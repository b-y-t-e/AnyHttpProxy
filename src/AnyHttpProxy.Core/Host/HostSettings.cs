using System.Text.Json.Serialization;
using AnyHttpProxy.Common;

namespace AnyHttpProxy.Host;

/// <summary>
/// Ustawienia hosta. Domyślnie udostępniane jest wszystko, więc zapisujemy tylko porty odznaczone -
/// nowa usługa, której nikt jeszcze nie widział, trafia do gatewayów od razu.
/// </summary>
public sealed class HostSettings
{
    public SortedSet<int> HiddenPorts { get; set; } = [];

    public static HostSettings Load(string path) =>
        JsonFile.Load(path, HostSettingsJson.Default.HostSettings, () => new HostSettings());

    public void Save(string path) => JsonFile.Save(path, this, HostSettingsJson.Default.HostSettings);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(HostSettings))]
internal sealed partial class HostSettingsJson : JsonSerializerContext;
