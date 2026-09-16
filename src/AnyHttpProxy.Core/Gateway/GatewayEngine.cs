using System.Net;
using System.Net.Sockets;
using AnyHttpProxy.Protocol;

namespace AnyHttpProxy.Gateway;

public enum MappingState
{
    /// <summary>Wyłączone ręcznie albo cały host wyłączony.</summary>
    Off,

    /// <summary>Host przestał udostępniać tę usługę.</summary>
    NotShared,

    Listening,

    /// <summary>Lokalnego portu nie da się zająć - trzeba zmienić port albo wyłączyć.</summary>
    Conflict,
}

public sealed record MappingView(
    int RemotePort,
    string Scheme,
    string? Process,
    string? Server,
    int LocalPort,
    bool Enabled,
    MappingState State,
    string? Error,
    int OpenConnections);

public sealed record HostView(
    string Id,
    string Name,
    string? Machine,
    bool Enabled,
    bool IsOn,
    bool IsConnected,
    IReadOnlyList<MappingView> Mappings);

/// <summary>
/// Strona GATEWAY: trzyma linki do hostów i dla każdej udostępnionej usługi nasłuch na lokalnym porcie -
/// domyślnie tym samym co na hoście. Zajęty port nie blokuje reszty: mapowanie przechodzi w konflikt
/// i czeka na inny port albo wyłączenie.
/// </summary>
public sealed class GatewayEngine : IAsyncDisposable
{
    private readonly string _root;
    private readonly string _settingsPath;
    private readonly GatewaySettings _settings;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, HostRuntime> _hosts = [];

    public GatewayEngine(string dataRoot)
    {
        _root = dataRoot;
        _settingsPath = Path.Combine(dataRoot, "gateway.json");
        _settings = GatewaySettings.Load(_settingsPath);
    }

    public event Action<string, string>? Audit;
    public event Action? Changed;

    public bool ListenOnAllNetworks
    {
        get { lock (_gate) return _settings.ListenOnAllNetworks; }
    }

    public IReadOnlyList<string> ListenAddresses
    {
        get { lock (_gate) return _settings.ListenAddresses.ToList(); }
    }

    /// <summary>Czy porty są widoczne poza tym komputerem - tylko wtedy firewall ma znaczenie.</summary>
    public bool ExposesBeyondLocalhost
    {
        get { lock (_gate) return _settings.ListenOnAllNetworks || _settings.ListenAddresses.Count > 0; }
    }

    /// <summary>Podnosi zapisane hosty: nasłuchy od razu (z ostatniego katalogu), linki w tle.</summary>
    public void Start()
    {
        List<HostEntry> entries;
        lock (_gate) entries = _settings.Hosts.ToList();

        foreach (var entry in entries)
        {
            var runtime = CreateRuntime(entry);
            lock (_gate)
            {
                _hosts[entry.Id] = runtime;
                ApplyMappings(runtime);
            }

            if (entry.Enabled) _ = StartLinkAsync(runtime, null);
        }

        Changed?.Invoke();
    }

    public IReadOnlyList<HostView> Snapshot()
    {
        lock (_gate)
        {
            return _settings.Hosts.Select(entry =>
            {
                var runtime = _hosts.GetValueOrDefault(entry.Id);
                return new HostView(
                    entry.Id,
                    entry.Name,
                    entry.Machine,
                    entry.Enabled,
                    runtime?.Connection.IsOn ?? false,
                    runtime?.Connection.IsConnected ?? false,
                    entry.Services
                        .OrderBy(s => s.RemotePort)
                        .Select(s => DescribeMapping(entry, runtime, s))
                        .ToList());
            }).ToList();
        }
    }

    /// <summary>Paruje nowy host kodem z jego okna. Przy błędzie nic nie zostaje na dysku.</summary>
    public async Task AddHostAsync(string code, string name)
    {
        var entry = new HostEntry { Id = Guid.NewGuid().ToString("N")[..12], Name = name, Enabled = true };
        var runtime = CreateRuntime(entry);

        // Rejestracja przed startem: pierwszy katalog przychodzi zaraz po połączeniu i musi mieć dokąd trafić.
        lock (_gate) _hosts[entry.Id] = runtime;

        try
        {
            await runtime.Connection.StartAsync(code).ConfigureAwait(false);
        }
        catch
        {
            lock (_gate)
            {
                _hosts.Remove(entry.Id);
                StopAllForwarders(runtime);
            }

            await runtime.Connection.DisposeAsync().ConfigureAwait(false);
            TryDeleteStore(entry.Id);
            throw;
        }

        lock (_gate)
        {
            _settings.Hosts.Add(entry);
            Save();
        }

        Audit?.Invoke("pair", $"{name}: paired");
        Changed?.Invoke();
    }

    public async Task RemoveHostAsync(string id)
    {
        HostRuntime? runtime;
        HostEntry? entry;
        lock (_gate)
        {
            entry = _settings.Hosts.FirstOrDefault(h => h.Id == id);
            if (entry is null) return;

            _hosts.Remove(id, out runtime);
            _settings.Hosts.Remove(entry);
            if (runtime is not null) StopAllForwarders(runtime);
            Save();
        }

        if (runtime is not null) await runtime.Connection.DisposeAsync().ConfigureAwait(false);
        TryDeleteStore(id);

        Audit?.Invoke("link", $"{entry.Name}: removed with its pairing, adding it again needs a new code");
        Changed?.Invoke();
    }

    /// <summary>Wyłączony host: link w dół i wszystkie jego porty zwolnione. Sparowanie zostaje.</summary>
    public async Task SetHostEnabledAsync(string id, bool enabled)
    {
        HostRuntime? runtime;
        lock (_gate)
        {
            if (!_hosts.TryGetValue(id, out runtime)) return;
            runtime.Entry.Enabled = enabled;
            ApplyMappings(runtime);
            Save();
        }

        if (enabled) await StartLinkAsync(runtime, null).ConfigureAwait(false);
        else await runtime.Connection.StopAsync().ConfigureAwait(false);

        Audit?.Invoke("link", $"{runtime.Entry.Name}: turned {(enabled ? "on" : "off")}");
        Changed?.Invoke();
    }

    public void RenameHost(string id, string name)
    {
        lock (_gate)
        {
            if (!_hosts.TryGetValue(id, out var runtime) || string.IsNullOrWhiteSpace(name)) return;
            runtime.Entry.Name = name.Trim();
            runtime.Connection.Name = runtime.Entry.Name;
            Save();
        }

        Changed?.Invoke();
    }

    /// <summary>Zmienia lokalny port i/lub włącza-wyłącza jedno mapowanie. Zwraca błąd, gdy portu nie da się zająć.</summary>
    public string? SetMapping(string hostId, int remotePort, bool enabled, int localPort)
    {
        string? error;
        lock (_gate)
        {
            if (!_hosts.TryGetValue(hostId, out var runtime)) return null;
            if (runtime.Entry.Services.FirstOrDefault(s => s.RemotePort == remotePort) is not { } mapping) return null;

            var portChanged = mapping.LocalPort != localPort;
            mapping.Enabled = enabled;
            mapping.LocalPort = localPort;

            // Zmiana portu = nowy nasłuch, nawet jeśli poprzedni był w konflikcie.
            if (portChanged)
            {
                StopForwarder(runtime, remotePort);
                runtime.Errors.Remove(remotePort);
            }
            ApplyMapping(runtime, mapping);
            Save();

            error = runtime.Errors.GetValueOrDefault(remotePort);
            var what = !enabled ? "off" : error is null ? $"on local port {localPort}" : $"local port {localPort}: {error}";
            Audit?.Invoke(error is null ? "port" : "deny", $"{runtime.Entry.Name} :{remotePort} -> {what}");
        }

        Changed?.Invoke();
        return error;
    }

    /// <summary>Ponawia zajęcie portów w konflikcie - np. gdy inny program właśnie zwolnił port.</summary>
    public void RetryConflicts()
    {
        lock (_gate)
        {
            foreach (var runtime in _hosts.Values)
            {
                runtime.Errors.Clear();
                ApplyMappings(runtime);
            }
        }

        Changed?.Invoke();
    }

    /// <summary>Nowy zestaw adresów nasłuchu - wszystkie porty są zajmowane od nowa.</summary>
    public void SetListenAddresses(bool allNetworks, IEnumerable<string> addresses)
    {
        lock (_gate)
        {
            _settings.ListenOnAllNetworks = allNetworks;
            _settings.ListenAddresses = addresses.Distinct().ToList();
            Save();

            foreach (var runtime in _hosts.Values)
            {
                StopAllForwarders(runtime);
                ApplyMappings(runtime);
            }
        }

        Audit?.Invoke("net", allNetworks
            ? "Listening on all networks"
            : ListenAddresses.Count == 0 ? "Listening on localhost only" : $"Listening on localhost and {string.Join(", ", ListenAddresses)}");
        Changed?.Invoke();
    }

    /// <summary>Lokalne porty, które faktycznie nasłuchują - dla sprawdzenia firewalla.</summary>
    public IReadOnlyList<int> ListeningPorts()
    {
        lock (_gate)
        {
            return _hosts.Values
                .SelectMany(h => h.Forwarders.Values.Select(f => f.Port))
                .Distinct()
                .Order()
                .ToList();
        }
    }

    public async ValueTask DisposeAsync()
    {
        List<HostRuntime> runtimes;
        lock (_gate)
        {
            runtimes = _hosts.Values.ToList();
            foreach (var runtime in runtimes) StopAllForwarders(runtime);
            Save();
        }

        await Task.WhenAll(runtimes.Select(r => r.Connection.DisposeAsync().AsTask())).ConfigureAwait(false);
    }

    private HostRuntime CreateRuntime(HostEntry entry)
    {
        var connection = new HostConnection(entry.Id, StoreRoot(entry.Id)) { Name = entry.Name };
        var runtime = new HostRuntime(entry, connection);

        connection.Audit += (kind, message) => Audit?.Invoke(kind, message);
        connection.Changed += () => Changed?.Invoke();
        connection.CatalogReceived += catalog => OnCatalog(runtime, catalog);
        return runtime;
    }

    private async Task StartLinkAsync(HostRuntime runtime, string? code)
    {
        try
        {
            await runtime.Connection.StartAsync(code).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Audit?.Invoke("deny", $"{runtime.Entry.Name}: {ex.Message}");
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// Katalog od hosta: nowe usługi dostają port taki jak na hoście i są włączone, znikające przechodzą
    /// w "nie udostępnione" z zachowaniem ustawień.
    /// </summary>
    private void OnCatalog(HostRuntime runtime, Catalog catalog)
    {
        lock (_gate)
        {
            if (!_hosts.ContainsKey(runtime.Entry.Id)) return;

            var entry = runtime.Entry;
            entry.Machine = catalog.Machine;
            var offered = catalog.Services.ToDictionary(s => s.Port);

            foreach (var service in catalog.Services)
            {
                var mapping = entry.Services.FirstOrDefault(s => s.RemotePort == service.Port);
                if (mapping is null)
                {
                    mapping = new MappingEntry { RemotePort = service.Port, LocalPort = service.Port, Enabled = true };
                    entry.Services.Add(mapping);
                    Audit?.Invoke("port", $"{entry.Name}: new {service.Scheme} :{service.Port}{(service.Process is null ? "" : $" ({service.Process})")}");
                }

                mapping.Scheme = service.Scheme;
                mapping.Process = service.Process;
                mapping.Server = service.Server;
                mapping.Offered = true;
            }

            foreach (var mapping in entry.Services.Where(s => s.Offered && !offered.ContainsKey(s.RemotePort)))
            {
                mapping.Offered = false;
                Audit?.Invoke("port", $"{entry.Name}: :{mapping.RemotePort} no longer shared");
            }

            ApplyMappings(runtime);
            Save();
        }

        Changed?.Invoke();
    }

    /// <summary>Wiele zajętych portów naraz (typowe przy pierwszym katalogu) to jedna linia w logu, nie lawina.</summary>
    private void ApplyMappings(HostRuntime runtime)
    {
        var failed = runtime.Entry.Services
            .Where(mapping => ApplyMapping(runtime, mapping))
            .ToList();

        if (failed.Count == 1)
        {
            Audit?.Invoke("deny", $"{runtime.Entry.Name} :{failed[0].RemotePort}: {runtime.Errors[failed[0].RemotePort]}");
        }
        else if (failed.Count > 1)
        {
            Audit?.Invoke("deny", $"{runtime.Entry.Name}: {failed.Count} ports are busy on this computer " +
                $"({string.Join(", ", failed.Select(m => m.LocalPort))}) - change the local port or turn them off");
        }
    }

    /// <summary>
    /// Doprowadza jeden nasłuch do stanu z ustawień. Wywoływane pod blokadą.
    /// Zwraca true, gdy port właśnie okazał się zajęty - log pisze wywołujący.
    /// </summary>
    private bool ApplyMapping(HostRuntime runtime, MappingEntry mapping)
    {
        var shouldRun = runtime.Entry.Enabled && mapping.Enabled && mapping.Offered;
        var existing = runtime.Forwarders.GetValueOrDefault(mapping.RemotePort);

        if (!shouldRun)
        {
            StopForwarder(runtime, mapping.RemotePort);
            runtime.Errors.Remove(mapping.RemotePort);
            return false;
        }

        var addresses = BindAddresses();
        if (existing is not null)
        {
            if (existing.Port == mapping.LocalPort && existing.Addresses.SequenceEqual(addresses)) return false;
            StopForwarder(runtime, mapping.RemotePort);
        }
        else if (runtime.Errors.ContainsKey(mapping.RemotePort))
        {
            // Zgłoszony konflikt nie jest ponawiany przy każdym katalogu - dopiero po zmianie portu albo "Retry".
            return false;
        }

        var remotePort = mapping.RemotePort;
        var connection = runtime.Connection;
        var forwarder = PortForwarder.TryStart(
            mapping.LocalPort, addresses, (client, ct) => connection.ServeAsync(client, remotePort, ct), out var error);

        if (forwarder is null)
        {
            runtime.Errors[remotePort] = error ?? "Cannot listen on this port.";
            return true;
        }

        runtime.Forwarders[remotePort] = forwarder;
        return false;
    }

    private static void StopForwarder(HostRuntime runtime, int remotePort)
    {
        if (runtime.Forwarders.Remove(remotePort, out var forwarder)) forwarder.Dispose();
    }

    private static void StopAllForwarders(HostRuntime runtime)
    {
        foreach (var remotePort in runtime.Forwarders.Keys.ToList()) StopForwarder(runtime, remotePort);
        runtime.Errors.Clear();
    }

    /// <summary>
    /// Adresy nasłuchu. Pierwszy (IPv4) jest wymagany - jego zajętość to konflikt; IPv6 dokładamy, jeśli się da.
    /// Localhost jest zawsze, bo gateway służy przede wszystkim temu komputerowi.
    /// </summary>
    private IReadOnlyList<IPAddress> BindAddresses()
    {
        if (_settings.ListenOnAllNetworks) return [IPAddress.Any, IPAddress.IPv6Any];

        var list = new List<IPAddress> { IPAddress.Loopback };
        foreach (var text in _settings.ListenAddresses)
        {
            if (IPAddress.TryParse(text, out var address) && address.AddressFamily == AddressFamily.InterNetwork)
                list.Add(address);
        }

        list.Add(IPAddress.IPv6Loopback);
        foreach (var text in _settings.ListenAddresses)
        {
            if (IPAddress.TryParse(text, out var address) && address.AddressFamily == AddressFamily.InterNetworkV6)
                list.Add(address);
        }

        return list;
    }

    private static MappingView DescribeMapping(HostEntry entry, HostRuntime? runtime, MappingEntry mapping)
    {
        var forwarder = runtime?.Forwarders.GetValueOrDefault(mapping.RemotePort);
        var error = runtime?.Errors.GetValueOrDefault(mapping.RemotePort);

        var state = !entry.Enabled || !mapping.Enabled ? MappingState.Off
            : !mapping.Offered ? MappingState.NotShared
            : forwarder is not null ? MappingState.Listening
            : error is not null ? MappingState.Conflict
            : MappingState.Off;

        return new MappingView(
            mapping.RemotePort, mapping.Scheme, mapping.Process, mapping.Server,
            mapping.LocalPort, mapping.Enabled, state, error, forwarder?.OpenConnections ?? 0);
    }

    private void Save() => _settings.Save(_settingsPath);

    private string StoreRoot(string id) => Path.Combine(_root, "links", id);

    private void TryDeleteStore(string id)
    {
        try
        {
            var path = StoreRoot(id);
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Sparowanie zostanie na dysku, ale host i tak znika z listy.
        }
    }

    private sealed class HostRuntime(HostEntry entry, HostConnection connection)
    {
        public HostEntry Entry { get; } = entry;
        public HostConnection Connection { get; } = connection;
        public Dictionary<int, PortForwarder> Forwarders { get; } = [];
        public Dictionary<int, string> Errors { get; } = [];
    }
}
