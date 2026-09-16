using AnyHttpProxy.GatewayApp.ViewModels;
using AnyHttpProxy.GatewayApp.Views;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace AnyHttpProxy.GatewayApp;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = new MainViewModel();
            desktop.MainWindow = new MainWindow { DataContext = viewModel };

            // Czekanie na zatrzymanie linku idzie wątkiem roboczym - blokowanie UI na async zakleszcza dyspozytor.
            desktop.ShutdownRequested += (_, _) => viewModel.ShutdownBlocking();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
