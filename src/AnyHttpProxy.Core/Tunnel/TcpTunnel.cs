using System.Net.Sockets;
using Tailcat.Link;

namespace AnyHttpProxy.Tunnel;

/// <summary>
/// Jedno połączenie TCP przeniesione przez parę jednokierunkowych kanałów Tailcata.
/// Kanał kończy się jednakowo przy zamknięciu i przy zerwaniu, więc każda ramka niesie bajt typu:
/// dane albo "zerwane". Koniec kanału bez "zerwane" to zwykłe zamknięcie jednej strony (half-close) -
/// dzięki temu urwana odpowiedź nigdy nie udaje kompletnej.
/// </summary>
public static class TcpTunnel
{
    private const byte DataFrame = 0;
    private const byte AbortFrame = 1;
    private const int BufferSize = 256 * 1024;

    /// <summary>
    /// Przepycha bajty w obie strony, aż obie się zamkną albo jedna padnie. Gniazdo i nadajnik należą
    /// do tunelu; odbiornik i jego wyliczacz zostają u handlera kanału, który żyje tak długo jak tunel.
    /// </summary>
    public static async Task RunAsync(
        Socket socket,
        ILinkChannelWriter writer,
        ILinkChannelReader reader,
        IAsyncEnumerator<ReadOnlyMemory<byte>> frames,
        CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var sessionEnded = false;
        void OnClosed(object? sender, ChannelClosedEventArgs e) =>
            sessionEnded |= e.Reason == ChannelCloseReason.SessionEnded;
        reader.Closed += OnClosed;

        var aborted = 0;
        async Task AbortAsync()
        {
            if (Interlocked.Exchange(ref aborted, 1) != 0) return;

            cts.Cancel();
            Reset(socket);
            try { await writer.SendAsync(new[] { AbortFrame }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)); }
            catch { /* druga strona i tak dowie się z końca kanału albo sesji */ }
            await Quietly(writer.DisposeAsync);
            await Quietly(reader.DisposeAsync);
        }

        async Task UpAsync()
        {
            var buffer = new byte[BufferSize];
            while (true)
            {
                var read = await socket.ReceiveAsync(buffer.AsMemory(1), SocketFlags.None, cts.Token);
                if (read == 0) break;

                // Kopia, bo bufor wraca do gniazda, zanim transport skończy z ramką.
                var frame = buffer.AsSpan(0, read + 1).ToArray();
                frame[0] = DataFrame;
                await writer.SendAsync(frame, cts.Token);
            }

            // Klient skończył wysyłać: koniec kanału = FIN po drugiej stronie.
            await writer.DisposeAsync();
        }

        async Task DownAsync()
        {
            while (await frames.MoveNextAsync())
            {
                var frame = frames.Current;
                if (frame.IsEmpty) continue;
                if (frame.Span[0] == AbortFrame) throw new IOException("The other side dropped the connection.");

                await socket.SendAsync(frame[1..], SocketFlags.None, cts.Token);
            }

            if (sessionEnded) throw new IOException("The link session ended.");
            socket.Shutdown(SocketShutdown.Send);
        }

        var up = UpAsync();
        var down = DownAsync();

        try
        {
            var first = await Task.WhenAny(up, down);
            if (!first.IsCompletedSuccessfully) await AbortAsync();

            try { await Task.WhenAll(up, down); }
            catch { await AbortAsync(); }
        }
        finally
        {
            reader.Closed -= OnClosed;
            socket.Dispose();
            await Quietly(writer.DisposeAsync);
        }
    }

    /// <summary>Zerwanie po stronie gniazda: RST zamiast FIN, żeby klient nie wziął urwanych danych za całość.</summary>
    public static void Reset(Socket socket)
    {
        try
        {
            socket.LingerState = new LingerOption(true, 0);
            socket.Close();
        }
        catch
        {
            // Już zamknięte.
        }
    }

    private static async Task Quietly(Func<ValueTask> action)
    {
        try { await action(); }
        catch { /* kanał już nie żyje */ }
    }
}
