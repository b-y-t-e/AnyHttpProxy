using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using AnyHttpProxy.Common;

namespace AnyHttpProxy.Gateway;

/// <summary>
/// Nasłuch na jednym lokalnym porcie (na wszystkich wybranych adresach). Każde przyjęte połączenie
/// idzie do <c>serve</c>, które przenosi je przez link do hosta.
/// </summary>
public sealed class PortForwarder : IDisposable
{
    /// <summary>Porty trzymane przez ten proces - żeby własny nasłuch nie wyglądał jak cudzy konflikt.</summary>
    private static readonly ConcurrentDictionary<int, int> HeldPorts = new();

    private readonly List<Socket> _listeners = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly Func<Socket, CancellationToken, Task> _serve;
    private int _open;

    private PortForwarder(int port, IReadOnlyList<IPAddress> addresses, Func<Socket, CancellationToken, Task> serve)
    {
        Port = port;
        Addresses = addresses;
        _serve = serve;
    }

    public int Port { get; }
    public IReadOnlyList<IPAddress> Addresses { get; }
    public int OpenConnections => _open;

    /// <summary>
    /// Zajmuje port albo zwraca powód, dla którego się nie da. Pierwszy adres na liście jest wymagany
    /// (IPv4), pozostałe - jeśli się uda (IPv6 bywa wyłączone).
    /// </summary>
    public static PortForwarder? TryStart(
        int port, IReadOnlyList<IPAddress> addresses, Func<Socket, CancellationToken, Task> serve, out string? error)
    {
        error = null;

        if (port is < 1 or > 65535)
        {
            error = "Port must be between 1 and 65535.";
            return null;
        }

        if (FindForeignListener(port) is { } busy)
        {
            error = busy;
            return null;
        }

        var forwarder = new PortForwarder(port, addresses, serve);

        for (var i = 0; i < addresses.Count; i++)
        {
            try
            {
                forwarder._listeners.Add(Listen(addresses[i], port));
            }
            catch (SocketException ex) when (i > 0 && ex.SocketErrorCode is not SocketError.AddressAlreadyInUse and not SocketError.AccessDenied)
            {
                // Dodatkowy adres (np. IPv6) nie jest dostępny - wystarczy reszta.
            }
            catch (SocketException ex)
            {
                forwarder.Dispose();
                error = ex.SocketErrorCode switch
                {
                    SocketError.AddressAlreadyInUse => $"Port {port} is already in use on this computer.",
                    SocketError.AccessDenied => $"Port {port} is reserved by the system or needs admin rights.",
                    SocketError.AddressNotAvailable => $"Address {addresses[i]} is no longer on this computer.",
                    _ => $"Cannot listen on {addresses[i]}:{port} - {ex.Message}",
                };
                return null;
            }
        }

        HeldPorts.AddOrUpdate(port, 1, (_, n) => n + 1);
        foreach (var listener in forwarder._listeners) _ = forwarder.AcceptLoopAsync(listener);
        return forwarder;
    }

    /// <summary>
    /// Podpowiedź wolnego portu przy konflikcie: najpierw łatwe do zapamiętania (port + 10000, kolejne numery),
    /// a jak nic z tego - dowolny wolny od systemu.
    /// </summary>
    public static int SuggestFreePort(int preferred)
    {
        var busy = new HashSet<int>(HeldPorts.Keys);
        try { busy.UnionWith(ListenerTable.Read().Select(s => s.Port)); }
        catch { /* bez tabeli sprawdzi to sam bind */ }

        var candidates = new List<int>();
        if (preferred + 10000 <= 65535) candidates.Add(preferred + 10000);
        candidates.AddRange(Enumerable.Range(preferred + 1, 20).Where(p => p <= 65535));

        foreach (var port in candidates.Where(p => !busy.Contains(p)))
        {
            if (CanBind(port)) return port;
        }

        using var any = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        any.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)any.LocalEndPoint!).Port;
    }

    private static bool CanBind(int port)
    {
        try
        {
            using var probe = Listen(IPAddress.Any, port);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_stop.IsCancellationRequested) return;
        _stop.Cancel();

        foreach (var listener in _listeners)
        {
            try { listener.Dispose(); }
            catch { /* już zamknięty */ }
        }

        if (_listeners.Count > 0 && HeldPorts.AddOrUpdate(Port, 0, (_, n) => n - 1) <= 0)
            HeldPorts.TryRemove(Port, out _);
    }

    private static Socket Listen(IPAddress address, int port)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            if (address.AddressFamily == AddressFamily.InterNetworkV6) socket.DualMode = false;

            // Na Windowsie bez tego można "dosiąść się" do portu, który ktoś trzyma na konkretnym adresie.
            if (OperatingSystem.IsWindows()) socket.ExclusiveAddressUse = true;

            socket.Bind(new IPEndPoint(address, port));
            socket.Listen(512);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static string? FindForeignListener(int port)
    {
        if (HeldPorts.ContainsKey(port)) return $"Port {port} is already used by another host in this gateway.";

        try
        {
            var owner = ListenerTable.Read().FirstOrDefault(s => s.Port == port && s.Pid != Environment.ProcessId);
            if (owner is null) return null;

            var name = ListenerTable.ProcessName(owner.Pid);
            return name is null
                ? $"Port {port} is already in use on this computer."
                : $"Port {port} is already in use by {name} (pid {owner.Pid}).";
        }
        catch
        {
            return null; // bez tabeli zostaje błąd z samego bind
        }
    }

    private async Task AcceptLoopAsync(Socket listener)
    {
        while (!_stop.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await listener.AcceptAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                if (_stop.IsCancellationRequested) return;
                continue;
            }

            client.NoDelay = true;
            _ = Task.Run(async () =>
            {
                Interlocked.Increment(ref _open);
                try { await _serve(client, _stop.Token).ConfigureAwait(false); }
                catch { /* serve samo zrywa połączenie */ }
                finally { Interlocked.Decrement(ref _open); }
            });
        }
    }
}
