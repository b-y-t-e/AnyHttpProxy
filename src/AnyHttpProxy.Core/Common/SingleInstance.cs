using System.Runtime.InteropServices;

namespace AnyHttpProxy.Common;

/// <summary>
/// Jedna kopia aplikacji na sesję użytkownika: dwa hosty biłyby się o tożsamość węzła Tailcata,
/// dwa gatewaye o te same porty i plik ustawień.
/// </summary>
public static class SingleInstance
{
    private static Mutex? _held;

    public static bool TryAcquire(string name)
    {
        var mutex = new Mutex(initiallyOwned: false, $"Local\\{name}");

        bool acquired;
        try
        {
            acquired = mutex.WaitOne(TimeSpan.Zero, exitContext: false);
        }
        catch (AbandonedMutexException)
        {
            // Poprzednia kopia padła bez zwolnienia - i tak jesteśmy teraz jedyni.
            acquired = true;
        }

        if (!acquired)
        {
            mutex.Dispose();
            return false;
        }

        _held = mutex;
        return true;
    }

    public static void WarnAlreadyRunning(string title, string message)
    {
        if (!OperatingSystem.IsWindows()) return;

        try { MessageBox(IntPtr.Zero, message, title, 0x00000040 /* MB_ICONINFORMATION */); }
        catch { /* brak USER32 - i tak kończymy */ }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);
}
