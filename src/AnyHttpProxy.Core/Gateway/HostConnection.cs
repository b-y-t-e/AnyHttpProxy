using System.Collections.Concurrent;
using System.Net.Sockets;
using AnyHttpProxy.Protocol;
using AnyHttpProxy.Tunnel;
using Tailcat.Link;
using Tailcat.Link.Storage;

namespace AnyHttpProxy.Gateway;

/// <summary>
/// Link do jednego hosta: pobiera jego katalog usług (long-poll) i otwiera tunele do nich.
/// Każdy host ma własny katalog sparowania, więc gateway może być połączony z wieloma naraz.
/// </summary>
public sealed class HostConnection : IAsyncDisposable
{
    private static readonly TimeSpan PairingTimeout = TimeSpan.FromSeconds(60);

    private readonly string _storeRoot;
    private readonly ConcurrentDictionary<string, PendingTunnel> _pending = new();
    private readonly ConcurrentDictionary<int, DateTimeOffset> _lastRefusalLog = new();

    private ILink? _link;
    private CancellationTokenSource? _stop;
    private Task? _catalogLoop;

    public HostConnection(string id, string storeRoot)
    {
        Id = id;
        _storeRoot = storeRoot;
    }

    public string Id { get; }

    /// <summary>Nazwa do logu - ustawiana przez właściciela, bo użytkownik może ją zmienić.</summary>
    public string Name { get; set; } = "";

    public bool IsOn => _link is not null;
    public bool IsConnected => _link?.IsConnected ?? false;

    public event Action<string, string>? Audit;
    public event Action? Changed;
    public event Action<Catalog>? CatalogReceived;

    /// <summary>
    /// Podnosi link. Kod potrzebny tylko za pierwszym razem - potem sparowanie leży w katalogu tego hosta.
    /// Przy parowaniu czeka na potwierdzenie, żeby nie zgłosić sukcesu dla kodu, który host odrzucił.
    /// </summary>
    public async Task StartAsync(string? invitationCode = null)
    {
        if (_link is not null) return;

        Directory.CreateDirectory(_storeRoot);
        var options = new LinkOptions
        {
            Store = new FileLinkStore(_storeRoot, SecretProtector.ForCurrentPlatform()),
        };
        options = Common.LinkTrace.Apply(options, $"gw:{Name}");

        var request = new JoinRequest { DisplayName = Environment.MachineName };
        var link = invitationCode is { Length: > 0 }
            ? await TailcatLink.JoinAsync(Wire.AppName, invitationCode, request, options).ConfigureAwait(false)
            : await TailcatLink.JoinAsync(Wire.AppName, null, request, options).ConfigureAwait(false);

        link.OnChannel(Wire.DownChannel, HandleReturnChannelAsync);
        link.Connected += () =>
        {
            Audit?.Invoke("link", $"{Name}: connected");
            Changed?.Invoke();
        };
        link.Disconnected += reason =>
        {
            Audit?.Invoke("link", $"{Name}: disconnected ({reason})");
            Changed?.Invoke();
        };

        if (invitationCode is { Length: > 0 })
        {
            using var pairing = new CancellationTokenSource(PairingTimeout);
            try
            {
                await link.WaitUntilConnectedAsync(pairing.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await link.DisposeAsync().ConfigureAwait(false);
                throw new TimeoutException(
                    "The host did not confirm pairing. The code may have expired or was already used - ask for a new one.");
            }
        }

        _link = link;
        _stop = new CancellationTokenSource();
        _catalogLoop = Task.Run(() => CatalogLoopAsync(link, _stop.Token));
        Changed?.Invoke();
    }

    public async Task StopAsync()
    {
        var link = _link;
        var stop = _stop;
        var loop = _catalogLoop;
        _link = null;
        _stop = null;
        _catalogLoop = null;
        if (link is null) return;

        stop?.Cancel();
        await link.DisposeAsync().ConfigureAwait(false);
        if (loop is not null)
        {
            try { await loop.ConfigureAwait(false); }
            catch { /* zatrzymane */ }
        }

        foreach (var pending in _pending.Values) pending.Arrived.TrySetCanceled();
        Changed?.Invoke();
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    /// <summary>
    /// Przenosi przyjęte lokalnie połączenie do usługi <paramref name="remotePort"/> na hoście.
    /// Kolejność: rezerwacja id, kanał w górę z nagłówkiem, czekanie, aż host otworzy kanał powrotny.
    /// </summary>
    public async Task ServeAsync(Socket client, int remotePort, CancellationToken cancellationToken)
    {
        if (_link is not { } link)
        {
            TcpTunnel.Reset(client);
            return;
        }

        var id = Guid.NewGuid().ToString("N");
        var pending = new PendingTunnel();
        _pending[id] = pending;
        ILinkChannelWriter? up = null;

        try
        {
            ReturnChannel arrived;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(Wire.TunnelOpenTimeout);
                up = await link.OpenChannelAsync(Wire.UpChannel, timeout.Token).ConfigureAwait(false);
                await up.SendAsync(Wire.Encode(new TunnelOpen(id, remotePort)), timeout.Token).ConfigureAwait(false);
                arrived = await pending.Arrived.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            }

            if (!arrived.Reply.Ok)
            {
                LogRefusal(remotePort, arrived.Reply.Error ?? "refused by the host");
                TcpTunnel.Reset(client);
                await up.DisposeAsync().ConfigureAwait(false);
                return;
            }

            await TcpTunnel.RunAsync(client, up, arrived.Reader, arrived.Frames, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            TcpTunnel.Reset(client);
            if (up is not null)
            {
                try { await up.DisposeAsync().ConfigureAwait(false); }
                catch { /* kanał już nie żyje */ }
            }

            if (!cancellationToken.IsCancellationRequested)
                LogRefusal(remotePort, ex is OperationCanceledException ? "the host did not answer in time" : ex.Message);
        }
        finally
        {
            _pending.TryRemove(id, out _);
            pending.Done.TrySetResult();
        }
    }

    private async Task CatalogLoopAsync(ILink link, CancellationToken ct)
    {
        string? since = null;
        var failing = false;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var request = LinkContent.FromBytes(Wire.Encode(new CatalogRequest(since)));
                await using var reply = await link.RequestAsync(request, ct).ConfigureAwait(false);
                var catalog = Wire.Decode<Catalog>(await reply.ReadAllBytesAsync(ct).ConfigureAwait(false));

                since = catalog.Version;
                failing = false;
                CatalogReceived?.Invoke(catalog);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                if (!failing) Audit?.Invoke("link", $"{Name}: cannot read the service list - {ex.Message}");
                failing = true;

                try { await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    /// <summary>
    /// Kanał powrotny od hosta. Handler musi żyć tak długo jak tunel - kanał kończy się razem z nim -
    /// więc oddaje odbiornik czekającemu połączeniu i czeka, aż to się skończy.
    /// </summary>
    private async Task HandleReturnChannelAsync(ILinkChannelReader reader, CancellationToken ct)
    {
        await using var frames = reader.ReadAllAsync(ct).GetAsyncEnumerator(ct);
        if (!await frames.MoveNextAsync().ConfigureAwait(false)) return;

        TunnelReply reply;
        try { reply = Wire.Decode<TunnelReply>(frames.Current); }
        catch { return; }

        if (!_pending.TryRemove(reply.Id, out var pending)) return;
        if (!pending.Arrived.TrySetResult(new ReturnChannel(reader, frames, reply))) return;

        // Bez ct: gdy sesja się kończy, tunel wciąż czeka na ramkę z tego wyliczacza, a zwolnienie go
        // w trakcie MoveNextAsync rzuca NotSupportedException. Koniec sesji i tak kończy tunel,
        // a ServeAsync zawsze ustawia Done w finally.
        await pending.Done.Task.ConfigureAwait(false);
    }

    /// <summary>Odmowy potrafią przyjść seriami (przeglądarka ponawia) - do logu jedna na port co 15 s.</summary>
    private void LogRefusal(int remotePort, string reason)
    {
        var now = DateTimeOffset.UtcNow;
        if (_lastRefusalLog.TryGetValue(remotePort, out var last) && now - last < TimeSpan.FromSeconds(15)) return;

        _lastRefusalLog[remotePort] = now;
        Audit?.Invoke("deny", $"{Name} :{remotePort}: {reason}");
    }

    private sealed record ReturnChannel(
        ILinkChannelReader Reader, IAsyncEnumerator<ReadOnlyMemory<byte>> Frames, TunnelReply Reply);

    private sealed class PendingTunnel
    {
        public TaskCompletionSource<ReturnChannel> Arrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
