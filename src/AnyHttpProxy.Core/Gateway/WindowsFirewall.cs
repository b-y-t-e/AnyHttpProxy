using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;

namespace AnyHttpProxy.Gateway;

/// <summary>Port, którego Windows Firewall nie wpuści z innych komputerów, i dlaczego.</summary>
public sealed record FirewallBlock(int Port, string Reason);

/// <summary>
/// Sprawdza reguły Windows Firewall (COM HNetCfg.FwPolicy2 - odczyt nie wymaga uprawnień administratora)
/// i dodaje reguły zezwalające przez podniesiony PowerShell. Ocena jest przybliżona: reguła blokująca
/// wygrywa z zezwalającą, a bez pasującej reguły decyduje domyślna akcja profilu.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsFirewall
{
    public const string RuleGroup = "AnyHttpProxy";

    private const int DirectionIn = 1;
    private const int ActionBlock = 0;
    private const int ActionAllow = 1;
    private const int ProtocolTcp = 6;
    private const int ProtocolAny = 256;

    public static IReadOnlyList<FirewallBlock> FindBlocked(IReadOnlyCollection<int> ports, string? programPath)
    {
        if (ports.Count == 0) return [];

        var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
        if (type is null) return [];

        dynamic policy = Activator.CreateInstance(type)!;
        int activeProfiles = policy.CurrentProfileTypes;

        var profiles = new List<int>();
        foreach (var profile in new[] { 1, 2, 4 })
        {
            if ((activeProfiles & profile) == 0) continue;
            if ((bool)policy.FirewallEnabled[profile]) profiles.Add(profile);
        }

        // Firewall wyłączony we wszystkich aktywnych profilach - nic nie blokuje.
        if (profiles.Count == 0) return [];

        var rules = new List<RuleView>();
        foreach (dynamic rule in policy.Rules)
        {
            try
            {
                if (!(bool)rule.Enabled || (int)rule.Direction != DirectionIn) continue;
                int protocol = rule.Protocol;
                if (protocol != ProtocolTcp && protocol != ProtocolAny) continue;

                rules.Add(new RuleView(
                    (string?)rule.Name ?? "",
                    (int)rule.Action,
                    (int)rule.Profiles,
                    protocol == ProtocolTcp ? (string?)rule.LocalPorts ?? "*" : "*",
                    (string?)rule.ApplicationName,
                    (string?)rule.serviceName,
                    IsNarrowed(rule)));
            }
            catch
            {
                // Pojedyncza nietypowa reguła nie psuje całej oceny.
            }
        }

        // 1 = zasady domeny (GPO) nadpisują lokalne reguły, 2 = GPO blokuje cały ruch przychodzący.
        // Wtedy reguły dodane tutaj nic nie zmienią - trzeba to powiedzieć wprost.
        int modifyState = 0;
        try { modifyState = policy.LocalPolicyModifyState; } catch { }
        var policyNote = modifyState switch
        {
            1 => "; domain policy ignores firewall rules added on this computer - ask the administrator to open the port",
            2 => "; domain policy blocks all incoming connections - ask the administrator to open the port",
            _ => "",
        };

        var blocked = new List<FirewallBlock>();
        foreach (var port in ports.Distinct().Order())
        {
            foreach (var profile in profiles)
            {
                var matching = rules.Where(r => r.Applies(port, profile, programPath)).ToList();

                if (matching.FirstOrDefault(r => r.Action == ActionBlock) is { } block)
                {
                    blocked.Add(new FirewallBlock(port, $"blocked by the firewall rule \"{block.Name}\"{policyNote}"));
                    break;
                }

                // Zawężona reguła (inny adres lokalny, interfejs, aplikacja ze Sklepu) nie wpuszcza naszego portu w całości.
                if (matching.Any(r => r.Action == ActionAllow && !r.Narrowed)) continue;

                if ((int)policy.DefaultInboundAction[profile] == ActionBlock)
                {
                    blocked.Add(new FirewallBlock(port, $"no firewall rule allows it ({ProfileName(profile)} network){policyNote}"));
                    break;
                }
            }
        }

        return blocked;
    }

    /// <summary>
    /// Włączone firewalle innych firm zarejestrowane w Centrum zabezpieczeń (np. ESET Zapora).
    /// Taki firewall filtruje ruch sam i reguły Windows Firewall nic mu nie mówią, więc ocena powyżej
    /// jest wtedy bez znaczenia. Na Windows Server Centrum zabezpieczeń nie istnieje - pusta lista.
    /// </summary>
    public static IReadOnlyList<string> OtherFirewalls()
    {
        var names = new List<string>();
        try
        {
            var type = Type.GetTypeFromProgID("WbemScripting.SWbemLocator");
            if (type is null) return names;

            dynamic locator = Activator.CreateInstance(type)!;
            dynamic service = locator.ConnectServer(".", "root\\SecurityCenter2");
            foreach (dynamic product in service.ExecQuery("SELECT displayName, productState FROM FirewallProduct"))
            {
                string name = product.displayName;
                int state = product.productState;
                // Drugi bajt productState: 0x10 = włączony.
                if ((state >> 8 & 0x10) != 0 && !name.Contains("Windows", StringComparison.OrdinalIgnoreCase))
                    names.Add(name);
            }
        }
        catch
        {
            // brak WMI / Centrum zabezpieczeń
        }

        return names;
    }

    /// <summary>
    /// Dodaje reguły wpuszczające TCP na podanych portach i usuwa reguły blokujące ten program
    /// (tworzy je Windows, gdy ktoś zamknie jego pytanie o dostęp). Wymaga zgody UAC.
    /// Zwraca false, gdy użytkownik odmówił albo skrypt się nie powiódł.
    /// </summary>
    public static async Task<bool> AllowAsync(IReadOnlyCollection<int> ports, string? programPath)
    {
        var script = new StringBuilder("$ErrorActionPreference = 'Stop'\n");

        if (programPath is { Length: > 0 })
        {
            script.Append("Get-NetFirewallApplicationFilter -Program ").Append(Quote(programPath))
                .Append(" -ErrorAction SilentlyContinue | Get-NetFirewallRule | Where-Object { $_.Action -eq 'Block' -and $_.Direction -eq 'Inbound' } | Remove-NetFirewallRule\n");
        }

        foreach (var port in ports.Distinct().Order())
        {
            var name = $"AnyHttpProxy gateway TCP {port}";
            script.Append("Get-NetFirewallRule -DisplayName ").Append(Quote(name))
                .Append(" -ErrorAction SilentlyContinue | Remove-NetFirewallRule\n");
            script.Append("New-NetFirewallRule -DisplayName ").Append(Quote(name))
                .Append(" -Group ").Append(Quote(RuleGroup))
                .Append(" -Direction Inbound -Action Allow -Protocol TCP -LocalPort ").Append(port)
                .Append(" -Profile Any | Out-Null\n");
        }

        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script.ToString()));
        var start = new ProcessStartInfo("powershell.exe",
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand {encoded}")
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        try
        {
            using var process = Process.Start(start);
            if (process is null) return false;
            await process.WaitForExitAsync().ConfigureAwait(false);
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false; // UAC odrzucone
        }
    }

    /// <summary>
    /// Reguła ograniczona do części ruchu: adresów lokalnych (np. Tailscale-In tylko na adres tailnetu),
    /// interfejsów albo kontenera aplikacji ze Sklepu (LocalUserOwner / LocalAppPackageId, bez ApplicationName).
    /// </summary>
    private static bool IsNarrowed(dynamic rule)
    {
        string? localAddresses = rule.LocalAddresses;
        if (localAddresses is { Length: > 0 } and not "*") return true;

        string? interfaceTypes = rule.InterfaceTypes;
        if (interfaceTypes is { Length: > 0 } and not "All") return true;

        try
        {
            object? interfaces = rule.Interfaces;
            if (interfaces is Array { Length: > 0 }) return true;
        }
        catch
        {
            // brak listy interfejsów
        }

        try
        {
            string? owner = rule.LocalUserOwner;
            string? package = rule.LocalAppPackageId;
            if (owner is { Length: > 0 } || package is { Length: > 0 }) return true;
        }
        catch
        {
            // starszy Windows bez INetFwRule3
        }

        return false;
    }

    private static string Quote(string text) => "'" + text.Replace("'", "''") + "'";

    private static string ProfileName(int profile) => profile switch
    {
        1 => "domain",
        2 => "private",
        4 => "public",
        _ => "current",
    };

    private sealed record RuleView(string Name, int Action, int Profiles, string LocalPorts, string? Application, string? Service, bool Narrowed)
    {
        public bool Applies(int port, int profile, string? programPath)
        {
            if ((Profiles & profile) == 0) return false;
            if (Service is { Length: > 0 } and not "*") return false;
            if (Application is { Length: > 0 }
                && !string.Equals(Application, programPath, StringComparison.OrdinalIgnoreCase))
                return false;

            return PortMatches(LocalPorts, port);
        }

        private static bool PortMatches(string ports, int port)
        {
            foreach (var raw in ports.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (raw == "*") return true;

                var dash = raw.IndexOf('-');
                if (dash > 0
                    && int.TryParse(raw[..dash], out var from)
                    && int.TryParse(raw[(dash + 1)..], out var to)
                    && port >= from && port <= to)
                    return true;

                if (int.TryParse(raw, out var single) && single == port) return true;
            }

            return false;
        }
    }
}
