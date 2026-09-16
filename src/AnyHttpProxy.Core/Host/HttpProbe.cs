using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace AnyHttpProxy.Host;

public sealed record ProbeResult(string Scheme, string? Server);

/// <summary>
/// Sprawdza, czy port mówi HTTP. Najpierw TLS (serwer HTTP na ClientHello od razu odpowiada błędem),
/// potem czyste HTTP. Wysyłamy HEAD /, więc aplikacja widzi co najwyżej jedno lekkie żądanie.
/// </summary>
public static class HttpProbe
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(3);

    public static async Task<ProbeResult?> ProbeAsync(IPAddress address, int port, CancellationToken cancellationToken)
    {
        var tls = await TryAsync(address, port, useTls: true, cancellationToken).ConfigureAwait(false);
        if (tls is not null) return tls;

        return await TryAsync(address, port, useTls: false, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ProbeResult?> TryAsync(IPAddress address, int port, bool useTls, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectTimeout + ReplyTimeout);

        try
        {
            using var client = new TcpClient(address.AddressFamily);
            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token))
            {
                connect.CancelAfter(ConnectTimeout);
                await client.ConnectAsync(address, port, connect.Token).ConfigureAwait(false);
            }

            Stream stream = client.GetStream();
            if (useTls)
            {
                var ssl = new SslStream(stream, leaveInnerStreamOpen: false, (_, _, _, _) => true);
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = "localhost",
                    ApplicationProtocols = [SslApplicationProtocol.Http11],
                }, timeout.Token).ConfigureAwait(false);
                stream = ssl;
            }

            var host = address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
            var request = Encoding.ASCII.GetBytes(
                $"HEAD / HTTP/1.1\r\nHost: {host}:{port}\r\nUser-Agent: ahp-probe\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(request, timeout.Token).ConfigureAwait(false);

            var head = await ReadHeadAsync(stream, timeout.Token).ConfigureAwait(false);
            if (!head.StartsWith("HTTP/", StringComparison.Ordinal)) return null;

            return new ProbeResult(useTls ? "https" : "http", Header(head, "Server"));
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string> ReadHeadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var filled = 0;

        while (filled < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(filled), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            filled += read;

            // Pierwsze bajty wystarczą, żeby odrzucić protokół, który HTTP nie jest (SSH, bazy danych).
            if (filled >= 5 && !buffer.AsSpan(0, 5).SequenceEqual("HTTP/"u8)) break;
            if (buffer.AsSpan(0, filled).IndexOf("\r\n\r\n"u8) >= 0) break;
        }

        return Encoding.Latin1.GetString(buffer, 0, filled);
    }

    private static string? Header(string head, string name)
    {
        foreach (var line in head.Split("\r\n"))
        {
            var colon = line.IndexOf(':');
            if (colon > 0 && line.AsSpan(0, colon).Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                return line[(colon + 1)..].Trim() is { Length: > 0 } value ? value : null;
        }

        return null;
    }
}
