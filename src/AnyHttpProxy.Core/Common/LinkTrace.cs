using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging;
using Tailcat.Keys;
using Tailcat.Link;
using Tailcat.Link.Transport;
using Tailcat.Net;

namespace AnyHttpProxy.Common;

/// <summary>
/// Diagnostyka linku: gdy zmienna AHP_LINK_LOG wskazuje plik, wszystko, co loguje Tailcat (także warstwy
/// pod linkiem), trafia tam z poziomu Debug. Bez zmiennej - nic.
/// </summary>
public static class LinkTrace
{
    public static ILoggerFactory? Create(string tag)
    {
        var path = Environment.GetEnvironmentVariable("AHP_LINK_LOG");
        return string.IsNullOrWhiteSpace(path) ? null : new FileLoggerFactory(path, tag);
    }

    /// <summary>Dokłada do opcji log i obserwatora węzła (ścieżki, relay, datagramy), gdy diagnostyka jest włączona.</summary>
    public static LinkOptions Apply(LinkOptions options, string tag)
    {
        if (Create(tag) is not FileLoggerFactory factory) return options;

        // AHP_FORCE_RELAY1=1 odtwarza na jednej maszynie to, co dzieje się z hostem bez QUIC.
        var relay1Only = Environment.GetEnvironmentVariable("AHP_FORCE_RELAY1") == "1";
        return options with
        {
            LoggerFactory = factory,
            Gateway = new TailcatNodeGatewayFactory
            {
                Observer = new Observer(factory),
                Transports = relay1Only ? [PeerTransport.Relay1] : null,
            },
        };
    }

    private sealed class Observer : ITailcatObserver
    {
        private readonly FileLoggerFactory _log;
        private readonly ConcurrentDictionary<string, int> _datagrams = new();

        public Observer(FileLoggerFactory log)
        {
            _log = log;
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    await Task.Delay(1000);
                    var counts = _datagrams.ToArray();
                    _datagrams.Clear();
                    _log.Write(counts.Length == 0 ? "datagrams/s: none" : "datagrams/s: " + string.Join(", ", counts.Select(c => $"{c.Key}={c.Value}")));
                }
            });
        }

        public void RelayConnected(int regionId) => _log.Write($"relay connected region {regionId}");
        public void RelayReconnected(int regionId, int attempt) => _log.Write($"relay RECONNECTED region {regionId} attempt {attempt}");
        public void HandshakeStarted(NodePublic peer, int peerRegionId) => _log.Write($"handshake started, peer region {peerRegionId}");
        public void HandshakeCompleted(NodePublic peer, TimeSpan elapsed) => _log.Write($"handshake completed in {elapsed.TotalMilliseconds:F0} ms");
        public void HandshakeFailed(NodePublic peer, string reason) => _log.Write($"handshake FAILED: {reason}");
        public void PathChanged(NodePublic peer, PeerPath path) => _log.Write($"path -> {path}");
        public void EndpointsDiscovered(IReadOnlyList<IPEndPoint> endpoints) => _log.Write($"endpoints: {string.Join(", ", endpoints)}");
        public void DirectProbeSent(NodePublic peer, IPEndPoint candidate) => _datagrams.AddOrUpdate("probe-out", 1, (_, n) => n + 1);
        public void DatagramArrived(IPEndPoint from, int bytes, string kind) => _datagrams.AddOrUpdate(kind, 1, (_, n) => n + 1);

        // To, czego relay DERP nie przekazywał wcześniej wyżej - bez tego host znikający z relaya wyglądał jak cisza.
        public void RelayPeerGone(int regionId, NodePublic peer, Tailcat.Derp.DerpPeerGoneReason reason) =>
            _log.Write($"relay region {regionId}: peer {peer} GONE ({reason})");

        public void RelayHealth(int regionId, string problem) =>
            _log.Write(problem.Length == 0 ? $"relay region {regionId}: healthy again" : $"relay region {regionId}: UNHEALTHY - {problem}");

        public void RelayRestarting(int regionId, TimeSpan reconnectIn, TimeSpan tryFor) =>
            _log.Write($"relay region {regionId}: restarting, reconnect in {reconnectIn.TotalSeconds:F1} s");
    }

    private sealed class FileLoggerFactory(string path, string tag) : ILoggerFactory, ILogger
    {
        private readonly Lock _gate = new();

        public ILogger CreateLogger(string categoryName) => this;
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Write($"{logLevel,-11} {formatter(state, exception)}"
                + (exception is null ? "" : $" | {exception.GetType().Name}: {exception.Message}"));
        }

        public void Write(string text)
        {
            var line = $"{DateTime.Now:HH:mm:ss.fff} [{tag}] {text}";
            try
            {
                lock (_gate) File.AppendAllText(path, line + Environment.NewLine);
            }
            catch
            {
                // Diagnostyka nie może psuć linku.
            }
        }
    }
}
