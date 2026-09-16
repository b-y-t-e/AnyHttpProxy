using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace AnyHttpProxy.Gateway;

/// <summary>Adres tego komputera w jednej z jego sieci.</summary>
public sealed record LocalNetwork(IPAddress Address, int PrefixLength, string InterfaceName)
{
    public string Key => Address.ToString();

    /// <summary>Podsieć w zapisie CIDR, np. 192.168.1.0/24.</summary>
    public string Subnet
    {
        get
        {
            var bytes = Address.GetAddressBytes();
            for (var i = 0; i < bytes.Length; i++)
            {
                var bits = Math.Clamp(PrefixLength - i * 8, 0, 8);
                bytes[i] &= (byte)(0xFF << (8 - bits));
            }

            return $"{new IPAddress(bytes)}/{PrefixLength}";
        }
    }
}

/// <summary>Sieci, w których jest ten komputer - do wyboru, na których wystawiać porty.</summary>
public static class NetworkInventory
{
    public static IReadOnlyList<LocalNetwork> Read()
    {
        var result = new List<LocalNetwork>();

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    var address = unicast.Address;
                    if (IPAddress.IsLoopback(address)) continue;
                    if (address.AddressFamily == AddressFamily.InterNetworkV6
                        && (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast))
                        continue;
                    if (address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)) continue;

                    result.Add(new LocalNetwork(address, unicast.PrefixLength, nic.Name));
                }
            }
        }
        catch
        {
            // Bez listy interfejsów zostaje "wszystkie sieci" i localhost.
        }

        return result
            .OrderBy(n => n.Address.AddressFamily == AddressFamily.InterNetworkV6)
            .ThenBy(n => n.InterfaceName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
