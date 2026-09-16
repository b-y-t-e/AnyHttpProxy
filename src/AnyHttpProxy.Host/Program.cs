using AnyHttpProxy.Common;
using AnyHttpProxy.UI;
using Avalonia;

namespace AnyHttpProxy.HostApp;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Dwie kopie biłyby się o tożsamość węzła Tailcata i plik ustawień.
        if (!SingleInstance.TryAcquire("ahp-host"))
        {
            SingleInstance.WarnAlreadyRunning("AnyHttpProxy Host", "AnyHttpProxy Host is already running on this computer.");
            return 1;
        }

        CrashLog.Install();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

        // Zamknięte okno = martwy proces: nic nie może dalej wpuszczać tuneli w tle.
        Environment.Exit(0);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
