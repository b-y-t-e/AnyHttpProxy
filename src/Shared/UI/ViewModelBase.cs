using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using Avalonia.Threading;

namespace AnyHttpProxy.UI;

public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

/// <summary>Kolory stanu - te same wartości co w motywie, dla wiązań, które wybierają kolor w kodzie.</summary>
public static class StateBrushes
{
    public static IBrush Ok { get; } = Brush.Parse("#63D19B");
    public static IBrush Warn { get; } = Brush.Parse("#FFB454");
    public static IBrush Danger { get; } = Brush.Parse("#FF7B72");
    public static IBrush Muted { get; } = Brush.Parse("#5C6474");
    public static IBrush Soft { get; } = Brush.Parse("#8B95A7");
    public static IBrush Accent { get; } = Brush.Parse("#4C8DFF");
}

public sealed record LogEntry(string Time, string Kind, string Message)
{
    public IBrush Accent => Kind switch
    {
        "deny" => StateBrushes.Danger,
        "link" => StateBrushes.Accent,
        "pair" => StateBrushes.Ok,
        "share" or "port" or "net" => StateBrushes.Warn,
        _ => StateBrushes.Soft,
    };
}

/// <summary>Log w oknie: najnowsze na górze, można dopisywać z dowolnego wątku.</summary>
public sealed class LogList : ObservableCollection<LogEntry>
{
    private const int Limit = 300;

    public void Write(string kind, string message)
    {
        void Add()
        {
            Insert(0, new LogEntry(DateTime.Now.ToString("HH:mm:ss"), kind, message));
            while (Count > Limit) RemoveAt(Count - 1);
        }

        if (Dispatcher.UIThread.CheckAccess()) Add();
        else Dispatcher.UIThread.Post(Add);
    }
}

/// <summary>Nieobsłużony wyjątek trafia do ahp-error.log obok exe - okno znika bez śladu, a log zostaje.</summary>
public static class CrashLog
{
    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Write(e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) => e.SetObserved();
    }

    public static void Write(object error)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "ahp-error.log");
            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {error}{Environment.NewLine}");
        }
        catch
        {
            // Nie ma gdzie zapisać - trudno.
        }
    }
}
