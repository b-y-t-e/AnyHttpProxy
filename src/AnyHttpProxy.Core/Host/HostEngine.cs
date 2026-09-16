using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using AnyHttpProxy.Protocol;
using AnyHttpProxy.Tunnel;
using Tailcat.Link;
using Tailcat.Link.Storage;

namespace AnyHttpProxy.Host;

/// <summary>Gateway sparowany z tym hostem.</summary>
public sealed record GatewayPeer(string Key, string Name, bool IsConnected, DateTimeOffset LastSeen, int OpenConnections);

/// <summary>
/// Strona HOST: skanuje lokalne usługi HTTP/HTTPS, podaje gatewayom katalog tych zaznaczonych
/// i łączy tunele z kanałów Tailcata do tych usług. Tunel dostaje wyłącznie port z katalogu -
/// gateway nie może poprosić o dowolny adres ani port.
/// </summary>
public sealed class HostEngine : IAsyncDisposable
{
    public static TimeSpan InvitationLifetime { get; } = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(5);

    private readonly string _root;
    private readonly string _settingsPath;
    private readonly HostSettings _settings;
    private readonly ServiceScanner _scanner = new();
    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private readonly ConcurrentDictionary<string, int> _openByPeer = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _gate = new();

    private ILinkHost? _host;
    private Task? _scanLoop;
    private IReadOnlyList<DetectedService> _services = [];
    private readonly string _catalogEpoch = Guid.NewGuid().ToString("N")[..8];
    private long _catalogVersion = 1;
    private string _catalogSignature = "";
    private TaskCompletionSource _catalogChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public HostEngine(string dataRoot)
    {
        _root = dataRoot;
        _settingsPath = Path.Combine(dataRoot, "host.json");
        _settings = HostSettings.Load(_settingsPath);
    }

    /// <summary>(rodzaj, treść) - do logu w oknie.</summary>
    public event Action<string, string>? Audit;

    public event Action? Changed;

    public bool IsRunning => _host is not null;
    public bool IsScanning { get; private set; }
    public bool HasScanned { get; private set; }

    /// <summary>Zaproszenie czekające na użycie; znika, gdy gateway je wykorzysta albo wygaśnie.</summary>
    public LinkInvitation? Invitation
    {
        get
        {
            var invitation = _invitation;
            return invitation is not null && invitation.ExpiresAt > DateTimeOffset.UtcNow ? invitation : null;
        }
    }

    private LinkInvitation? _invitation;

    public IReadOnlyList<DetectedService> Services => _services;

    public bool IsShared(int port)
    {
        lock (_gate) return !_settings.HiddenPorts.Contains(port);
    }

    public IReadOnlyList<GatewayPeer> Gateways => _host is null
        ? []
        : _host.Peers.Select(p => new GatewayPeer(
            p.Key.ToString(), NameOf(p), p.IsConnected, p.LastSeen,
            _openByPeer.GetValueOrDefault(p.Key.ToString()))).ToList();

    public async Task StartAsync()
    {
        if (_host is not null) return;

        Directory.CreateDirectory(_root);
        var options = new LinkOptions
        {
            Store = new FileLinkStore(Path.Combine(_root, "link"), SecretProtector.ForCurrentPlatform()),
            MaxPeers = 32,
            PairingWindow = InvitationLifetime,
        };
        options = Common.LinkTrace.Apply(options, "host");

        var host = await TailcatLink.HostManyAsync(Wire.AppName, options).ConfigureAwait(false);

        // Niewykorzystane zaproszenia z poprzedniego uruchomienia leżą w magazynie i dalej działają,
        // a okno ich nie pokazuje - kod, o którym nikt nie wie, nie powinien wpuszczać.
        foreach (var stale in host.Invitations.ToList())
            await host.RevokeInvitationAsync(stale.Id).ConfigureAwait(false);

        host.SetRequestHandler(new LinkPeerContentHandler(HandleCatalogAsync));
        host.OnChannel(Wire.UpChannel, HandleTunnelAsync);

        host.PeerJoined += (_, e) =>
        {
            // Kod jest jednorazowy - gdy biblioteka go już nie oferuje, nie ma czego pokazywać.
            if (_invitation is { } pending && host.Invitations.All(i => i.Id != pending.Id)) _invitation = null;

            Audit?.Invoke("link", $"{NameOf(e.Peer)}: connected");
            Changed?.Invoke();
        };
        host.PeerLeft += (_, e) =>
        {
            Audit?.Invoke("link", $"{NameOf(e.Peer)}: disconnected ({e.Reason})");
            Changed?.Invoke();
        };

        _host = host;
        Audit?.Invoke("link", "Link ready");
        Changed?.Invoke();

        _scanLoop = Task.Run(ScanLoopAsync);
    }

    public async Task<LinkInvitation> InviteAsync()
    {
        var host = _host ?? throw new InvalidOperationException("Link is not running.");

        if (_invitation is { } previous)
            await host.RevokeInvitationAsync(previous.Id).ConfigureAwait(false);

        _invitation = await host.InviteAsync(new InvitationRequest
        {
            Label = "gateway",
            Lifetime = InvitationLifetime,
            SingleUse = true,
        }).ConfigureAwait(false);

        Audit?.Invoke("link", $"Invite code valid until {_invitation.ExpiresAt.LocalDateTime:HH:mm}");
        Changed?.Invoke();
        return _invitation;
    }

    /// <summary>Odpina gateway: traci dostęp, a powrót wymaga nowego kodu.</summary>
    public async Task ForgetGatewayAsync(string key)
    {
        if (_host?.Peers.FirstOrDefault(p => p.Key.ToString() == key) is not { } peer) return;

        await _host.ForgetPeerAsync(peer).ConfigureAwait(false);
        Audit?.Invoke("link", $"{NameOf(peer)}: removed, coming back needs a new code");
        Changed?.Invoke();
    }

    public void SetShared(int port, bool shared)
    {
        lock (_gate)
        {
            var changed = shared ? _settings.HiddenPorts.Remove(port) : _settings.HiddenPorts.Add(port);
            if (!changed) return;
            _settings.Save(_settingsPath);
        }

        Audit?.Invoke("share", shared ? $":{port} shared" : $":{port} hidden from gateways");
        PublishCatalog();
        Changed?.Invoke();
    }

    /// <summary>Skan na żądanie; <paramref name="force"/> sonduje od nowa także porty już znane.</summary>
    public async Task RescanAsync(bool force)
    {
        await _scanLock.WaitAsync(_stop.Token).ConfigureAwait(false);
        IsScanning = true;
        Changed?.Invoke();

        try
        {
            var found = (await _scanner.ScanAsync(force, _stop.Token).ConfigureAwait(false))
                // Porty gatewaya na tym samym komputerze to tunele do innych maszyn - wystawienie ich dalej robiłoby pętle.
                .Where(s => !string.Equals(s.Process, "ahp-gateway", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var before = _services;
            var firstScan = !HasScanned;
            _services = found;
            HasScanned = true;

            // Pierwszy skan i tak widać w oknie jako listę - do logu idzie tylko liczba.
            if (firstScan) Audit?.Invoke("scan", $"Found {found.Count} HTTP services");
            else ReportDifferences(before, found);
            PublishCatalog();
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Audit?.Invoke("scan", $"Scan failed: {ex.Message}");
        }
        finally
        {
            IsScanning = false;
            _scanLock.Release();
            Changed?.Invoke();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();

        if (_host is { } host)
        {
            _host = null;
            await host.DisposeAsync().ConfigureAwait(false);
        }

        if (_scanLoop is not null)
        {
            try { await _scanLoop.ConfigureAwait(false); }
            catch { /* zatrzymane */ }
        }
    }

    private async Task ScanLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            await RescanAsync(force: false).ConfigureAwait(false);
            try { await Task.Delay(ScanInterval, _stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void ReportDifferences(IReadOnlyList<DetectedService> before, IReadOnlyList<DetectedService> after)
    {
        var old = before.ToDictionary(s => s.Port);
        var now = after.ToDictionary(s => s.Port);

        foreach (var service in after.Where(s => !old.ContainsKey(s.Port)))
            Audit?.Invoke("scan", $"New: {service.Scheme} :{service.Port}{Suffix(service.Process)}");
        foreach (var service in before.Where(s => !now.ContainsKey(s.Port)))
            Audit?.Invoke("scan", $"Gone: {service.Scheme} :{service.Port}{Suffix(service.Process)}");
    }

    private static string Suffix(string? process) => process is null ? "" : $" ({process})";

    /// <summary>Nowa wersja katalogu budzi gatewaye czekające w long-pollu.</summary>
    private void PublishCatalog()
    {
        var shared = SharedServices();
        var signature = string.Join(";", shared.Select(s => $"{s.Port}/{s.Scheme}/{s.Process}/{s.Server}"));

        TaskCompletionSource wake;
        lock (_gate)
        {
            if (signature == _catalogSignature) return;
            _catalogSignature = signature;
            _catalogVersion++;
            wake = _catalogChanged;
            _catalogChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        wake.TrySetResult();
    }

    private string CatalogVersion => $"{_catalogEpoch}:{_catalogVersion}";

    private List<ServiceInfo> SharedServices()
    {
        lock (_gate)
        {
            return _services
                .Where(s => !_settings.HiddenPorts.Contains(s.Port))
                .Select(s => new ServiceInfo(s.Port, s.Scheme, s.Process, s.Server))
                .ToList();
        }
    }

    private async Task<LinkContent> HandleCatalogAsync(ILinkPeer peer, IncomingTransfer request, CancellationToken ct)
    {
        var ask = Wire.Decode<CatalogRequest>(await request.ReadAllBytesAsync(ct).ConfigureAwait(false));

        Task changed;
        string version;
        lock (_gate)
        {
            version = CatalogVersion;
            changed = _catalogChanged.Task;
        }

        // Gateway ma już tę wersję - trzymamy odpowiedź do zmiany, ale krócej niż limit rundy linku.
        if (ask.Since == version)
        {
            try { await changed.WaitAsync(Wire.CatalogHold, ct).ConfigureAwait(false); }
            catch (TimeoutException) { }
        }

        List<ServiceInfo> services;
        lock (_gate)
        {
            version = CatalogVersion;
            services = SharedServices();
        }

        return LinkContent.FromBytes(Wire.Encode(new Catalog(version, Environment.MachineName, services)));
    }

    private async Task HandleTunnelAsync(ILinkPeer peer, ILinkChannelReader reader, CancellationToken ct)
    {
        await using var frames = reader.ReadAllAsync(ct).GetAsyncEnumerator(ct);
        if (!await frames.MoveNextAsync().ConfigureAwait(false)) return;

        TunnelOpen open;
        try { open = Wire.Decode<TunnelOpen>(frames.Current); }
        catch { return; }

        ILinkChannelWriter down;
        try
        {
            down = await peer.OpenChannelAsync(Wire.DownChannel, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Audit?.Invoke("deny", $"{NameOf(peer)}: cannot open return channel for :{open.Port} - {ex.Message}");
            return;
        }

        DetectedService? service;
        lock (_gate) service = _services.FirstOrDefault(s => s.Port == open.Port && !_settings.HiddenPorts.Contains(s.Port));

        if (service is null)
        {
            await RefuseAsync(down, open, $"Port {open.Port} is not shared by this computer.").ConfigureAwait(false);
            return;
        }

        Socket socket;
        try
        {
            socket = await ConnectAsync(service.ConnectAddress, service.Port, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await RefuseAsync(down, open, $"Cannot reach port {open.Port} on the host: {ex.Message}").ConfigureAwait(false);
            return;
        }

        var key = peer.Key.ToString();
        _openByPeer.AddOrUpdate(key, 1, (_, n) => n + 1);
        try
        {
            await down.SendAsync(Wire.Encode(new TunnelReply(open.Id, true, null)), ct).ConfigureAwait(false);
            await TcpTunnel.RunAsync(socket, down, reader, frames, ct).ConfigureAwait(false);
        }
        catch
        {
            TcpTunnel.Reset(socket);
            await down.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _openByPeer.AddOrUpdate(key, 0, (_, n) => Math.Max(0, n - 1));
        }
    }

    private static async Task RefuseAsync(ILinkChannelWriter down, TunnelOpen open, string error)
    {
        try { await down.SendAsync(Wire.Encode(new TunnelReply(open.Id, false, error))).ConfigureAwait(false); }
        catch { /* gateway i tak zamknie połączenie po czasie */ }
        finally { await down.DisposeAsync().ConfigureAwait(false); }
    }

    private static async Task<Socket> ConnectAsync(IPAddress address, int port, CancellationToken ct)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));

        try
        {
            await socket.ConnectAsync(address, port, timeout.Token).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    internal static string NameOf(ILinkPeer peer) =>
        string.IsNullOrWhiteSpace(peer.Name) ? "unknown gateway" : peer.Name;
}
