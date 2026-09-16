using System.Text.Json.Serialization;
using AnyHttpProxy.Common;

namespace AnyHttpProxy.Gateway;

public sealed class GatewaySettings
{
    /// <summary>Domyślnie porty są wystawiane we wszystkich sieciach komputera (0.0.0.0 i [::]).</summary>
    public bool ListenOnAllNetworks { get; set; } = true;

    /// <summary>Adresy interfejsów wybrane ręcznie, gdy nie wszystkie sieci. Localhost jest zawsze.</summary>
    public List<string> ListenAddresses { get; set; } = [];

    public List<HostEntry> Hosts { get; set; } = [];

    public static GatewaySettings Load(string path) =>
        JsonFile.Load(path, GatewaySettingsJson.Default.GatewaySettings, () => new GatewaySettings());

    public void Save(string path) => JsonFile.Save(path, this, GatewaySettingsJson.Default.GatewaySettings);
}

/// <summary>Sparowany host. Każdy ma własny katalog z tożsamością Tailcata - stąd wiele hostów naraz.</summary>
public sealed class HostEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;

    /// <summary>Nazwa komputera, którą podał host - tylko do wyświetlenia.</summary>
    public string? Machine { get; set; }

    public List<MappingEntry> Services { get; set; } = [];
}

/// <summary>
/// Usługa hosta i jej lokalny odpowiednik. Wpis zostaje także wtedy, gdy host przestał ją udostępniać,
/// żeby zmieniony port i wyłączenie przetrwały chwilowe zniknięcie usługi.
/// </summary>
public sealed class MappingEntry
{
    public int RemotePort { get; set; }
    public string Scheme { get; set; } = "http";
    public string? Process { get; set; }
    public string? Server { get; set; }
    public int LocalPort { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>Czy host udostępniał ją przy ostatnim katalogu.</summary>
    public bool Offered { get; set; } = true;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(GatewaySettings))]
internal sealed partial class GatewaySettingsJson : JsonSerializerContext;
