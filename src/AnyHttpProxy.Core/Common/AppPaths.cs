namespace AnyHttpProxy.Common;

/// <summary>Katalogi danych: %APPDATA%\ahp-host i %APPDATA%\ahp-gateway.</summary>
public static class AppPaths
{
    public static string HostRoot => For("ahp-host");
    public static string GatewayRoot => For("ahp-gateway");

    public static string For(string app) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), app);
}
