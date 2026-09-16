using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace AnyHttpProxy.Common;

/// <summary>Gniazdo nasłuchujące TCP. PID znany tylko na Windowsie.</summary>
public sealed record ListeningSocket(IPAddress Address, int Port, int? Pid);

/// <summary>
/// Lista portów TCP, na których ktoś nasłuchuje. Na Windowsie z PID-em właściciela (GetExtendedTcpTable),
/// żeby pokazać nazwę procesu i odsiać własne porty; gdzie indziej - samo IPGlobalProperties.
/// </summary>
public static class ListenerTable
{
    public static IReadOnlyList<ListeningSocket> Read()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                return [.. ReadWindows(AfInet), .. ReadWindows(AfInet6)];
            }
            catch
            {
                // Spadamy na wersję bez PID-ów.
            }
        }

        return IPGlobalProperties.GetIPGlobalProperties()
            .GetActiveTcpListeners()
            .Select(e => new ListeningSocket(e.Address, e.Port, null))
            .ToList();
    }

    public static string? ProcessName(int? pid)
    {
        if (pid is null or 0) return null;
        if (pid == 4) return "System";

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid.Value);
            return process.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int TcpTableOwnerPidListener = 3;

    private static List<ListeningSocket> ReadWindows(int family)
    {
        var size = 0;
        _ = GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, TcpTableOwnerPidListener, 0);

        for (var attempt = 0; attempt < 4; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var result = GetExtendedTcpTable(buffer, ref size, false, family, TcpTableOwnerPidListener, 0);
                if (result == 122 /* ERROR_INSUFFICIENT_BUFFER */) continue;
                if (result != 0) throw new InvalidOperationException($"GetExtendedTcpTable failed ({result}).");

                return family == AfInet ? ParseV4(buffer) : ParseV6(buffer);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        throw new InvalidOperationException("GetExtendedTcpTable kept growing.");
    }

    // MIB_TCPROW_OWNER_PID: state, localAddr, localPort, remoteAddr, remotePort, pid - 6 x DWORD.
    private static List<ListeningSocket> ParseV4(IntPtr table)
    {
        var count = Marshal.ReadInt32(table);
        var list = new List<ListeningSocket>(count);

        for (var i = 0; i < count; i++)
        {
            var row = table + 4 + i * 24;
            var address = new IPAddress((uint)Marshal.ReadInt32(row, 4));
            var port = NetworkPort(Marshal.ReadInt32(row, 8));
            var pid = Marshal.ReadInt32(row, 20);
            list.Add(new ListeningSocket(address, port, pid));
        }

        return list;
    }

    // MIB_TCP6ROW_OWNER_PID: localAddr[16], scope, localPort, remoteAddr[16], scope, remotePort, state, pid.
    private static List<ListeningSocket> ParseV6(IntPtr table)
    {
        var count = Marshal.ReadInt32(table);
        var list = new List<ListeningSocket>(count);
        var bytes = new byte[16];

        for (var i = 0; i < count; i++)
        {
            var row = table + 4 + i * 56;
            Marshal.Copy(row, bytes, 0, 16);
            var address = new IPAddress(bytes);
            var port = NetworkPort(Marshal.ReadInt32(row, 20));
            var pid = Marshal.ReadInt32(row, 52);
            list.Add(new ListeningSocket(address, port, pid));
        }

        return list;
    }

    /// <summary>Port leży w DWORD w kolejności sieciowej, w dwóch najmłodszych bajtach.</summary>
    private static int NetworkPort(int raw) => ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);
}
