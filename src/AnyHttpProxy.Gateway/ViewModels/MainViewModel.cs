using System.Collections.ObjectModel;
using AnyHttpProxy.Common;
using AnyHttpProxy.Gateway;
using AnyHttpProxy.UI;
using Avalonia.Media;
using Avalonia.Threading;

namespace AnyHttpProxy.GatewayApp.ViewModels;

/// <summary>Jedna z sieci komputera do wyboru, gdzie wystawiać porty.</summary>
public sealed class NetworkRow(LocalNetwork network, bool selected, Action changed) : ViewModelBase
{
    private bool _selected = selected;

    public string Key => network.Key;
    public string Label => $"{network.Subnet}  ·  {network.InterfaceName}  ·  {network.Address}";

    public bool IsSelected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value)) changed();
        }
    }
}

/// <summary>Usługa hosta i jej lokalny port. Port edytowany w polu działa dopiero po "Apply".</summary>
public sealed class MappingRow(MainViewModel owner, string hostId, MappingView view) : ViewModelBase
{
    private MappingView _view = view;
    private string _localPortText = view.LocalPort.ToString();

    public string HostId => hostId;
    public int RemotePort => _view.RemotePort;
    public string RemoteText => $":{_view.RemotePort}";
    public string Scheme => _view.Scheme;
    public IBrush SchemeAccent => _view.Scheme == "https" ? StateBrushes.Ok : StateBrushes.Soft;

    public string Detail => string.Join("  ·  ", new[] { _view.Process, _view.Server }
        .Where(part => !string.IsNullOrWhiteSpace(part)));

    public bool IsEnabled
    {
        get => _view.Enabled;
        set
        {
            if (value == _view.Enabled) return;
            owner.SetMapping(this, value, _view.LocalPort);
        }
    }

    public string LocalPortText
    {
        get => _localPortText;
        set
        {
            if (!Set(ref _localPortText, value)) return;
            OnPropertyChanged(nameof(IsPortDirty));
        }
    }

    public int LocalPort => _view.LocalPort;
    public bool IsPortDirty => _localPortText.Trim() != _view.LocalPort.ToString();

    public string StateText => _view.State switch
    {
        MappingState.Listening => _view.OpenConnections switch
        {
            0 => "listening",
            1 => "1 connection",
            var n => $"{n} connections",
        },
        MappingState.Conflict => "port busy",
        MappingState.NotShared => "not shared now",
        _ => "off",
    };

    public IBrush StateAccent => _view.State switch
    {
        MappingState.Listening => StateBrushes.Ok,
        MappingState.Conflict => StateBrushes.Danger,
        MappingState.NotShared => StateBrushes.Warn,
        _ => StateBrushes.Muted,
    };

    public bool HasProblem => _view.State == MappingState.Conflict;
    public string Problem => _view.Error ?? "";
    public bool CanOpen => _view.State == MappingState.Listening;
    public string Url => $"{_view.Scheme}://localhost:{_view.LocalPort}/";

    public void ApplyPort()
    {
        if (!int.TryParse(_localPortText.Trim(), out var port) || port is < 1 or > 65535)
        {
            owner.SetHint($"\"{_localPortText}\" is not a port number (1-65535).");
            return;
        }

        owner.SetMapping(this, enabled: true, port);
    }

    public void SuggestPort() => LocalPortText = PortForwarder.SuggestFreePort(_view.LocalPort).ToString();

    public void Update(MappingView view)
    {
        var wasDirty = IsPortDirty;
        _view = view;

        // Nie nadpisujemy portu, który użytkownik właśnie wpisuje.
        if (!wasDirty) _localPortText = view.LocalPort.ToString();

        OnPropertyChanged(string.Empty);
    }
}

public sealed class HostRow(HostView view) : ViewModelBase
{
    private HostView _view = view;
    private bool _busy;

    public string Id => _view.Id;
    public string Name => _view.Name;
    public string Machine => _view.Machine is { Length: > 0 } machine && machine != _view.Name ? machine : "";

    public ObservableCollection<MappingRow> Mappings { get; } = [];

    public string StatusText => (_view.Enabled, _view.IsOn, _view.IsConnected) switch
    {
        (false, _, _) => "turned off",
        (true, true, true) => "connected",
        (true, true, false) => "reconnecting",
        _ => "connecting",
    };

    public IBrush StatusAccent => (_view.Enabled, _view.IsConnected) switch
    {
        (false, _) => StateBrushes.Muted,
        (true, true) => StateBrushes.Ok,
        _ => StateBrushes.Warn,
    };

    public string ToggleText => _view.Enabled ? "Turn off" : "Turn on";
    public bool Enabled => _view.Enabled;
    public bool HasNoMappings => Mappings.Count == 0;

    public string MappingsSummary => Mappings.Count == 0
        ? ""
        : $"{Mappings.Count(m => m.CanOpen)} of {Mappings.Count} ports listening";

    public bool Busy
    {
        get => _busy;
        set
        {
            if (Set(ref _busy, value)) OnPropertyChanged(nameof(NotBusy));
        }
    }

    public bool NotBusy => !_busy;

    public void Update(HostView view, MainViewModel owner)
    {
        _view = view;
        RowSync.Sync(Mappings, view.Mappings, m => m.RemotePort, r => r.RemotePort,
            m => new MappingRow(owner, view.Id, m), (r, m) => r.Update(m));

        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Machine));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusAccent));
        OnPropertyChanged(nameof(ToggleText));
        OnPropertyChanged(nameof(Enabled));
        OnPropertyChanged(nameof(HasNoMappings));
        OnPropertyChanged(nameof(MappingsSummary));
    }
}

public sealed class MainViewModel : ViewModelBase
{
    private static readonly TimeSpan FirewallInterval = TimeSpan.FromSeconds(20);

    private readonly GatewayEngine _engine = new(AppPaths.GatewayRoot);
    private readonly DispatcherTimer _timer;
    private bool _refreshQueued;

    private string _newCode = "";
    private string _newName = "";
    private bool _adding;
    private string _hint = "Paste the invite code from AnyHttpProxy Host on the computer whose services you want here.";

    private bool _allNetworks;
    private bool _networkDirty;

    private string _firewallText = "";
    private bool _firewallBusy;
    private DateTimeOffset _firewallCheckedAt;
    private IReadOnlyList<int> _blockedPorts = [];

    public MainViewModel()
    {
        _engine.Audit += Logs.Write;
        _engine.Changed += QueueRefresh;

        LoadNetworks();
        _engine.Start();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            Refresh();
            if (DateTimeOffset.UtcNow - _firewallCheckedAt > FirewallInterval) _ = CheckFirewallAsync();
        };
        _timer.Start();

        Refresh();
        Logs.Write("app", $"AnyHttpProxy Gateway - {Hosts.Count} saved hosts");
    }

    public ObservableCollection<HostRow> Hosts { get; } = [];
    public ObservableCollection<NetworkRow> Networks { get; } = [];
    public LogList Logs { get; } = [];

    public string NewCode
    {
        get => _newCode;
        set => Set(ref _newCode, value);
    }

    public string NewName
    {
        get => _newName;
        set => Set(ref _newName, value);
    }

    public bool NotAdding => !_adding;

    public string Hint
    {
        get => _hint;
        private set => Set(ref _hint, value);
    }

    public bool HasHosts => Hosts.Count > 0;
    public bool HasNoHosts => Hosts.Count == 0;

    public string HostsSummary => Hosts.Count == 0
        ? "no hosts"
        : $"{Hosts.Count(h => h.StatusText == "connected")} of {Hosts.Count} hosts connected";

    // --- sieci ---

    public bool AllNetworks
    {
        get => _allNetworks;
        set
        {
            if (!Set(ref _allNetworks, value)) return;
            OnPropertyChanged(nameof(ChosenNetworks));
            MarkNetworkDirty();
        }
    }

    public bool ChosenNetworks
    {
        get => !_allNetworks;
        set => AllNetworks = !value;
    }

    public bool IsNetworkDirty
    {
        get => _networkDirty;
        private set => Set(ref _networkDirty, value);
    }

    public string NetworkSummary => _engine.ListenOnAllNetworks
        ? "all networks of this computer"
        : _engine.ListenAddresses.Count == 0
            ? "this computer only (localhost)"
            : $"localhost and {string.Join(", ", _engine.ListenAddresses)}";

    // --- firewall ---

    public string FirewallText
    {
        get => _firewallText;
        private set
        {
            if (Set(ref _firewallText, value)) OnPropertyChanged(nameof(ShowFirewall));
        }
    }

    public bool ShowFirewall => _firewallText.Length > 0;

    public bool FirewallNotBusy => !_firewallBusy;

    // --- akcje ---

    public async Task AddAsync()
    {
        // Podwójne kliknięcie dałoby dwa równoległe parowania tym samym kodem - i dwa wpisy tego samego hosta.
        if (_adding) return;

        var code = NewCode.Trim();
        if (code.Length == 0)
        {
            Hint = "Paste an invite code first.";
            return;
        }

        var name = NewName.Trim();
        if (name.Length == 0) name = $"host {Hosts.Count + 1}";

        _adding = true;
        OnPropertyChanged(nameof(NotAdding));
        Hint = $"Pairing with \"{name}\"...";

        try
        {
            await Task.Run(() => _engine.AddHostAsync(code, name));
            NewCode = "";
            NewName = "";
            Hint = $"Paired with \"{name}\". Its services appear below and listen on the same ports here.";
        }
        catch (Exception ex)
        {
            Hint = $"Pairing failed: {ex.Message}";
            Logs.Write("deny", $"{name}: {ex.Message}");
        }
        finally
        {
            _adding = false;
            OnPropertyChanged(nameof(NotAdding));
            Refresh();
        }
    }

    public async Task ToggleHostAsync(HostRow row)
    {
        row.Busy = true;
        try { await Task.Run(() => _engine.SetHostEnabledAsync(row.Id, !row.Enabled)); }
        finally { row.Busy = false; }
        Refresh();
    }

    public async Task RemoveHostAsync(HostRow row)
    {
        row.Busy = true;
        await Task.Run(() => _engine.RemoveHostAsync(row.Id));
        Hint = $"Removed \"{row.Name}\". Adding it again needs a new invite code.";
        Refresh();
    }

    public void SetMapping(MappingRow row, bool enabled, int localPort)
    {
        var error = _engine.SetMapping(row.HostId, row.RemotePort, enabled, localPort);
        Hint = error is null
            ? enabled ? $":{row.RemotePort} is available here on port {localPort}." : $":{row.RemotePort} is turned off here."
            : $"Port {localPort} cannot be used: {error} Pick another port or turn this service off.";

        row.LocalPortText = localPort.ToString();
        Refresh();
        _ = CheckFirewallAsync();
    }

    public void RetryConflicts()
    {
        _engine.RetryConflicts();
        Hint = "Tried busy ports again.";
        Refresh();
    }

    public void ApplyNetworks()
    {
        var chosen = Networks.Where(n => n.IsSelected).Select(n => n.Key).ToList();
        _engine.SetListenAddresses(_allNetworks, _allNetworks ? [] : chosen);
        IsNetworkDirty = false;
        Hint = $"Ports now listen on {NetworkSummary}.";
        OnPropertyChanged(nameof(NetworkSummary));
        Refresh();
        _ = CheckFirewallAsync();
    }

    public void ReloadNetworks()
    {
        LoadNetworks();
        Hint = "Network list refreshed.";
    }

    public async Task AllowFirewallAsync()
    {
        if (!OperatingSystem.IsWindows() || _blockedPorts.Count == 0) return;

        SetFirewallBusy(true);
        try
        {
            var ok = await WindowsFirewall.AllowAsync(_blockedPorts, Environment.ProcessPath);
            Logs.Write(ok ? "net" : "deny", ok
                ? $"Firewall rules added for ports {string.Join(", ", _blockedPorts)}"
                : "Firewall rules were not added (cancelled or failed)");
            Hint = ok ? "Firewall rules added." : "Firewall rules were not added - the admin prompt was cancelled or failed.";
        }
        finally
        {
            SetFirewallBusy(false);
        }

        await CheckFirewallAsync();
    }

    public async Task CheckFirewallAsync()
    {
        _firewallCheckedAt = DateTimeOffset.UtcNow;

        if (!OperatingSystem.IsWindows() || !_engine.ExposesBeyondLocalhost)
        {
            _blockedPorts = [];
            FirewallText = "";
            return;
        }

        var ports = _engine.ListeningPorts();
        try
        {
            var blocked = await Task.Run(() => OperatingSystem.IsWindows()
                ? WindowsFirewall.FindBlocked(ports, Environment.ProcessPath)
                : []);
            _blockedPorts = blocked.Select(b => b.Port).ToList();
            FirewallText = blocked.Count switch
            {
                0 => "",
                1 => $"Windows Firewall blocks port {blocked[0].Port} for other computers: {blocked[0].Reason}. This computer can still use it.",
                _ => $"Windows Firewall blocks ports {string.Join(", ", _blockedPorts)} for other computers ({blocked[0].Reason}). This computer can still use them.",
            };
        }
        catch (Exception ex)
        {
            _blockedPorts = [];
            FirewallText = "";
            Logs.Write("net", $"Cannot read firewall rules: {ex.Message}");
        }
    }

    public void SetHint(string text) => Hint = text;

    public void ShutdownBlocking()
    {
        _timer.Stop();
        Task.Run(async () => await _engine.DisposeAsync()).GetAwaiter().GetResult();
    }

    private void SetFirewallBusy(bool busy)
    {
        _firewallBusy = busy;
        OnPropertyChanged(nameof(FirewallNotBusy));
    }

    private void LoadNetworks()
    {
        var chosen = _engine.ListenAddresses.ToHashSet();
        Networks.Clear();
        foreach (var network in NetworkInventory.Read())
            Networks.Add(new NetworkRow(network, chosen.Contains(network.Key), MarkNetworkDirty));

        _allNetworks = _engine.ListenOnAllNetworks;
        OnPropertyChanged(nameof(AllNetworks));
        OnPropertyChanged(nameof(ChosenNetworks));
        OnPropertyChanged(nameof(NetworkSummary));
        IsNetworkDirty = false;
    }

    private void MarkNetworkDirty()
    {
        var chosen = Networks.Where(n => n.IsSelected).Select(n => n.Key).ToHashSet();
        IsNetworkDirty = _allNetworks != _engine.ListenOnAllNetworks
            || !_allNetworks && !chosen.SetEquals(_engine.ListenAddresses);
    }

    private void QueueRefresh()
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _refreshQueued = false;
            Refresh();
        });
    }

    private void Refresh()
    {
        RowSync.Sync(Hosts, _engine.Snapshot(), h => h.Id, r => r.Id,
            h =>
            {
                var row = new HostRow(h);
                row.Update(h, this);
                return row;
            },
            (r, h) => r.Update(h, this));

        OnPropertyChanged(nameof(HasHosts));
        OnPropertyChanged(nameof(HasNoHosts));
        OnPropertyChanged(nameof(HostsSummary));
    }
}
