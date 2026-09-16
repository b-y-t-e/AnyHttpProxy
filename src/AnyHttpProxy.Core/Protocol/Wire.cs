using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace AnyHttpProxy.Protocol;

/// <summary>
/// Stałe wspólne dla obu stron. <see cref="AppName"/> to tożsamość parowania Tailcata -
/// zmiana zrywa wszystkie istniejące sparowania.
/// </summary>
public static class Wire
{
    public const string AppName = "anyhttpproxy";

    /// <summary>Kanał gateway -> host: pierwsza ramka to <see cref="TunnelOpen"/>, dalej bajty od klienta.</summary>
    public const string UpChannel = "ahp.tcp.up";

    /// <summary>Kanał host -> gateway: pierwsza ramka to <see cref="TunnelReply"/>, dalej bajty od usługi.</summary>
    public const string DownChannel = "ahp.tcp.down";

    /// <summary>Jedna runda przez link musi wrócić w ~30 s, więc long-poll katalogu trzyma krócej.</summary>
    public static TimeSpan CatalogHold { get; } = TimeSpan.FromSeconds(20);

    /// <summary>Ile czasu zostawiamy na otwarcie obu kanałów i połączenie z usługą po stronie hosta.</summary>
    public static TimeSpan TunnelOpenTimeout { get; } = TimeSpan.FromSeconds(20);

    public static byte[] Encode<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Info<T>());

    public static T Decode<T>(ReadOnlyMemory<byte> bytes) =>
        JsonSerializer.Deserialize(bytes.Span, Info<T>()) ?? throw new InvalidDataException($"Empty {typeof(T).Name}.");

    private static JsonTypeInfo<T> Info<T>() => (JsonTypeInfo<T>)WireJson.Default.GetTypeInfo(typeof(T))!;
}

/// <summary>Usługa, którą host udostępnia - to, co gateway wystawia lokalnie.</summary>
public sealed record ServiceInfo(int Port, string Scheme, string? Process, string? Server);

/// <summary>
/// Gateway pyta o katalog. Ta sama wersja = host trzyma odpowiedź do zmiany (long-poll).
/// Wersja zawiera losowy przedrostek procesu hosta, więc po jego restarcie nie pokryje się ze starą.
/// </summary>
public sealed record CatalogRequest(string? Since);

public sealed record Catalog(string Version, string Machine, List<ServiceInfo> Services);

public sealed record TunnelOpen(string Id, int Port);

public sealed record TunnelReply(string Id, bool Ok, string? Error);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CatalogRequest))]
[JsonSerializable(typeof(Catalog))]
[JsonSerializable(typeof(TunnelOpen))]
[JsonSerializable(typeof(TunnelReply))]
internal sealed partial class WireJson : JsonSerializerContext;
