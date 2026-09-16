using AnyHttpProxy.Common;
using AnyHttpProxy.UI;
using Avalonia;

namespace AnyHttpProxy.GatewayApp;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Dwie kopie biłyby się o te same porty i plik ustawień.
        if (!SingleInstance.TryAcquire("ahp-gateway"))
        {
            SingleInstance.WarnAlreadyRunning("AnyHttpProxy Gateway", "AnyHttpProxy Gateway is already running on this computer.");
            return 1;
        }

        CrashLog.Install();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

        // Zamknięte okno = martwy proces: żaden port nie zostaje otwarty w tle.
        Environment.Exit(0);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
