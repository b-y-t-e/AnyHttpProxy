using AnyHttpProxy.HostApp.ViewModels;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace AnyHttpProxy.HostApp.Views;

public partial class MainWindow : Window
{
    public MainWindow() => AvaloniaXamlLoader.Load(this);

    private MainViewModel? Model => DataContext as MainViewModel;

    /// <summary>Przyciski siedzą w szablonach wierszy, więc wiersz bierzemy z ich kontekstu.</summary>
    private static T? RowOf<T>(object? sender) where T : class => (sender as Control)?.DataContext as T;

    private async void OnInvite(object? sender, RoutedEventArgs e)
    {
        if (Model is null) return;
        await Model.InviteAsync();
    }

    private async void OnCopyInvite(object? sender, RoutedEventArgs e)
    {
        if (Model is null || Clipboard is null || Model.InviteCode.Length == 0) return;
        await Clipboard.SetTextAsync(Model.InviteCode);
        Model.NoteCopied();
    }

    private async void OnRescan(object? sender, RoutedEventArgs e)
    {
        if (Model is null) return;
        await Model.RescanAsync();
    }

    private async void OnRemoveGateway(object? sender, RoutedEventArgs e)
    {
        if (Model is null || RowOf<GatewayRow>(sender) is not { } row) return;
        await Model.RemoveGatewayAsync(row);
    }

    private async void OnOpenService(object? sender, RoutedEventArgs e)
    {
        if (RowOf<ServiceRow>(sender) is not { } row) return;
        await Launcher.LaunchUriAsync(new Uri(row.Url));
    }
}
