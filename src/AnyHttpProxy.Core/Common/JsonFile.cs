using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace AnyHttpProxy.Common;

/// <summary>Ustawienia na dysku. Uszkodzony plik nie blokuje startu, zapis idzie przez plik tymczasowy.</summary>
public static class JsonFile
{
    public static T Load<T>(string path, JsonTypeInfo<T> type, Func<T> fallback)
    {
        try
        {
            if (!File.Exists(path)) return fallback();
            return JsonSerializer.Deserialize(File.ReadAllBytes(path), type) ?? fallback();
        }
        catch
        {
            return fallback();
        }
    }

    public static void Save<T>(string path, T value, JsonTypeInfo<T> type)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(value, type));
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            // Brak zapisu ustawień nie jest powodem, żeby przerywać pracę.
        }
    }
}
