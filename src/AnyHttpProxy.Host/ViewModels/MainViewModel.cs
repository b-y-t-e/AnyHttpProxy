using System.Collections.ObjectModel;
using AnyHttpProxy.Common;
using AnyHttpProxy.Host;
using AnyHttpProxy.UI;
using Avalonia.Media;
using Avalonia.Threading;

namespace AnyHttpProxy.HostApp.ViewModels;

/// <summary>Usługa na liście. Checkbox decyduje, czy gatewaye ją widzą.</summary>
public sealed class ServiceRow(HostEngine engine, DetectedService service) : ViewModelBase
{
    private DetectedService _service = service;
    private bool _shared = engine.IsShared(service.Port);

    public int Port => _service.Port;
    public string PortText => $":{_service.Port}";
    public string Scheme => _service.Scheme;
    public IBrush SchemeAccent => _service.Scheme == "https" ? StateBrushes.Ok : StateBrushes.Soft;

    public string Detail => string.Join("  ·  ", new[] { _service.Process, _service.Server, _service.BoundTo }
        .Where(part => !string.IsNullOrWhiteSpace(part)));

    public string Url => $"{_service.Scheme}://localhost:{_service.Port}/";

    public bool IsShared
    {
        get => _shared;
        set
        {
            if (!Set(ref _shared, value)) return;
            engine.SetShared(Port, value);
        }
    }

    public void Update(DetectedService service)
    {
        _service = service;
        _shared = engine.IsShared(service.Port);
        OnPropertyChanged(nameof(IsShared));
        OnPropertyChanged(nameof(Scheme));
        OnPropertyChanged(nameof(SchemeAccent));
        OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(Url));
    }
}

public sealed class GatewayRow(GatewayPeer peer) : ViewModelBase
{
    private GatewayPeer _peer = peer;

    public string Key => _peer.Key;
    public string Name => _peer.Name;

    public string StatusText => _peer.IsConnected
        ? _peer.OpenConnections switch
        {
            0 => "connected",
            1 => "connected  ·  1 open connection",
            var n => $"connected  ·  {n} open connections",
        }
        : _peer.LastSeen == default ? "not connected yet" : $"offline  ·  last seen {_peer.LastSeen.LocalDateTime:g}";

    public IBrush StatusAccent => _peer.IsConnected ? StateBrushes.Ok : StateBrushes.Muted;

    public void Update(GatewayPeer peer)
    {
        _peer = peer;
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusAccent));
    }
}

public sealed class MainViewModel : ViewModelBase
{
    private readonly HostEngine _engine = new(AppPaths.HostRoot);
    private readonly DispatcherTimer _timer;
    private bool _refreshQueued;

    private string _linkStatus = "starting link";
    private IBrush _linkAccent = StateBrushes.Warn;
    private string _hint = "Services found on this computer are shared by default. Untick what gateways should not see.";

    public MainViewModel()
    {
        _engine.Audit += Logs.Write;
        _engine.Changed += QueueRefresh;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();

        _ = StartAsync();
    }

    public string MachineName { get; } = Environment.MachineName;

    public ObservableCollection<ServiceRow> Services { get; } = [];
    public ObservableCollection<GatewayRow> Gateways { get; } = [];
    public LogList Logs { get; } = [];

    public string LinkStatus
    {
        get => _linkStatus;
        private set => Set(ref _linkStatus, value);
    }

    public IBrush LinkAccent
    {
        get => _linkAccent;
        private set => Set(ref _linkAccent, value);
    }

    public string Hint
    {
        get => _hint;
        private set => Set(ref _hint, value);
    }

    public bool IsRunning => _engine.IsRunning;
    public bool IsScanning => _engine.IsScanning;
    public bool HasServices => Services.Count > 0;
    public bool ShowNoServices => Services.Count == 0 && _engine.HasScanned;
    public bool ShowFirstScan => !_engine.HasScanned;

    public string ServicesSummary => Services.Count == 0
        ? ""
        : $"{Services.Count(s => s.IsShared)} of {Services.Count} shared";

    public bool HasGateways => Gateways.Count > 0;
    public bool HasNoGateways => Gateways.Count == 0;

    public string InviteCode => _engine.Invitation?.Code.Value ?? "";
    public bool HasInvite => _engine.Invitation is not null;

    public string InviteDetail => _engine.Invitation is { } invitation
        ? $"single-use  ·  valid until {invitation.ExpiresAt.LocalDateTime:HH:mm}  ·  paste it in AnyHttpProxy Gateway"
        : "";

    public async Task InviteAsync()
    {
        try
        {
            await _engine.InviteAsync();
            Hint = "Send the code to the other computer. It works once and expires in 15 minutes.";
        }
        catch (Exception ex)
        {
            Hint = $"Cannot create an invite code: {ex.Message}";
        }

        Refresh();
    }

    public async Task RemoveGatewayAsync(GatewayRow row)
    {
        await _engine.ForgetGatewayAsync(row.Key);
        Hint = $"Removed \"{row.Name}\". It needs a new invite code to come back.";
        Refresh();
    }

    public async Task RescanAsync()
    {
        Hint = "Checking every listening port again...";
        await _engine.RescanAsync(force: true);
        Hint = $"Scan finished: {Services.Count} HTTP services.";
    }

    public void NoteCopied() => Hint = "Invite code copied.";

    public void ShutdownBlocking()
    {
        _timer.Stop();
        Task.Run(async () => await _engine.DisposeAsync()).GetAwaiter().GetResult();
    }

    private async Task StartAsync()
    {
        try
        {
            await _engine.StartAsync();
            Dispatcher.UIThread.Post(() =>
            {
                LinkStatus = "link ready";
                LinkAccent = StateBrushes.Ok;
            });
        }
        catch (Exception ex)
        {
            Logs.Write("deny", $"Link failed to start: {ex.Message}");
            Dispatcher.UIThread.Post(() =>
            {
                LinkStatus = "link failed";
                LinkAccent = StateBrushes.Danger;
                Hint = $"The link did not start: {ex.Message}";
            });
        }
    }

    /// <summary>Zdarzenia silnika przychodzą seriami i z różnych wątków - zlewamy je w jedno odświeżenie.</summary>
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

    /// <summary>Wiersze aktualizowane w miejscu - lista nie mruga, a checkbox pod kursorem nie znika.</summary>
    private void Refresh()
    {
        RowSync.Sync(Services, _engine.Services, s => s.Port, r => r.Port,
            s => new ServiceRow(_engine, s), (r, s) => r.Update(s));
        RowSync.Sync(Gateways, _engine.Gateways, g => g.Key, r => r.Key,
            g => new GatewayRow(g), (r, g) => r.Update(g));

        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsScanning));
        OnPropertyChanged(nameof(HasServices));
        OnPropertyChanged(nameof(ShowNoServices));
        OnPropertyChanged(nameof(ShowFirstScan));
        OnPropertyChanged(nameof(ServicesSummary));
        OnPropertyChanged(nameof(HasGateways));
        OnPropertyChanged(nameof(HasNoGateways));
        OnPropertyChanged(nameof(InviteCode));
        OnPropertyChanged(nameof(HasInvite));
        OnPropertyChanged(nameof(InviteDetail));
    }
}
