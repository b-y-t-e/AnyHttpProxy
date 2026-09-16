using System.Collections.Concurrent;
using System.Net;
using AnyHttpProxy.Common;

namespace AnyHttpProxy.Host;

/// <summary>Usługa HTTP/HTTPS wykryta na tym komputerze.</summary>
public sealed record DetectedService(
    int Port,
    string Scheme,
    IPAddress ConnectAddress,
    int? Pid,
    string? Process,
    string? Server,
    string BoundTo);

/// <summary>
/// Znajduje porty TCP, które mówią HTTP albo HTTPS. Sonduje tylko nowe gniazda (port + proces + adres):
/// pozytywne wyniki trzyma, dopóki gniazdo istnieje, negatywne sprawdza ponownie co jakiś czas -
/// serwer deweloperski często nasłuchuje chwilę przed tym, zanim zacznie odpowiadać.
/// </summary>
public sealed class ServiceScanner
{
    private static readonly TimeSpan NegativeRetry = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<(int Port, int? Pid, string Address), CachedProbe> _cache = new();
    private readonly int _ownPid = Environment.ProcessId;

    private sealed record CachedProbe(ProbeResult? Result, DateTimeOffset At);

    public async Task<IReadOnlyList<DetectedService>> ScanAsync(bool force, CancellationToken cancellationToken)
    {
        var sockets = ListenerTable.Read().Where(s => s.Pid != _ownPid).ToList();
        var now = DateTimeOffset.UtcNow;

        var candidates = sockets
            .GroupBy(s => s.Port)
            .Select(group => (Port: group.Key, Target: PickTarget(group.ToList()), Sockets: group.ToList()))
            .ToList();

        using var gate = new SemaphoreSlim(24);
        var probes = candidates.Select(async candidate =>
        {
            var key = (candidate.Port, candidate.Target.Pid, candidate.Target.Address.ToString());
            if (!force && _cache.TryGetValue(key, out var cached)
                && (cached.Result is not null || now - cached.At < NegativeRetry))
                return (candidate, Result: cached.Result);

            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var result = await HttpProbe.ProbeAsync(candidate.Target.Address, candidate.Port, cancellationToken).ConfigureAwait(false);
                _cache[key] = new CachedProbe(result, DateTimeOffset.UtcNow);
                return (candidate, Result: result);
            }
            finally
            {
                gate.Release();
            }
        });

        var results = await Task.WhenAll(probes).ConfigureAwait(false);

        // Gniazda, które zniknęły, nie zostają w pamięci podręcznej.
        var live = candidates.Select(c => (c.Port, c.Target.Pid, c.Target.Address.ToString())).ToHashSet();
        foreach (var key in _cache.Keys.Where(k => !live.Contains(k))) _cache.TryRemove(key, out _);

        return results
            .Where(r => r.Result is not null)
            .Select(r => new DetectedService(
                r.candidate.Port,
                r.Result!.Scheme,
                r.candidate.Target.Address,
                r.candidate.Target.Pid,
                ListenerTable.ProcessName(r.candidate.Target.Pid),
                r.Result.Server,
                string.Join(", ", r.candidate.Sockets.Select(s => Describe(s.Address)).Distinct())))
            .OrderBy(s => s.Port)
            .ToList();
    }

    /// <summary>
    /// Pod jaki adres łączyć się z usługą. Loopback, gdy tylko się da - wtedy host nie wychodzi
    /// ruchem na sieć; adres konkretnego interfejsu tylko wtedy, gdy usługa słucha wyłącznie na nim.
    /// </summary>
    private static ListeningSocket PickTarget(List<ListeningSocket> sockets)
    {
        foreach (var socket in sockets)
        {
            if (socket.Address.Equals(IPAddress.Any) || socket.Address.Equals(IPAddress.Loopback))
                return socket with { Address = IPAddress.Loopback };
        }

        foreach (var socket in sockets)
        {
            if (socket.Address.Equals(IPAddress.IPv6Any) || socket.Address.Equals(IPAddress.IPv6Loopback))
                return socket with { Address = IPAddress.IPv6Loopback };
        }

        return sockets[0];
    }

    private static string Describe(IPAddress address) =>
        address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) ? "all interfaces"
        : IPAddress.IsLoopback(address) ? "localhost"
        : address.ToString();
}
