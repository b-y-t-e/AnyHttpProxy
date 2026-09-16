using AnyHttpProxy.GatewayApp.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace AnyHttpProxy.GatewayApp.Views;

public partial class MainWindow : Window
{
    public MainWindow() => AvaloniaXamlLoader.Load(this);

    private MainViewModel? Model => DataContext as MainViewModel;

    /// <summary>Przyciski siedzą w szablonach wierszy, więc wiersz bierzemy z ich kontekstu.</summary>
    private static T? RowOf<T>(object? sender) where T : class => (sender as Control)?.DataContext as T;

    private async void OnAdd(object? sender, RoutedEventArgs e)
    {
        if (Model is null) return;
        await Model.AddAsync();
    }

    private async void OnToggleHost(object? sender, RoutedEventArgs e)
    {
        if (Model is null || RowOf<HostRow>(sender) is not { } row) return;
        await Model.ToggleHostAsync(row);
    }

    private async void OnRemoveHost(object? sender, RoutedEventArgs e)
    {
        if (Model is null || RowOf<HostRow>(sender) is not { } row) return;
        await Model.RemoveHostAsync(row);
    }

    private void OnApplyPort(object? sender, RoutedEventArgs e) => RowOf<MappingRow>(sender)?.ApplyPort();

    private void OnPortKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || RowOf<MappingRow>(sender) is not { } row) return;
        e.Handled = true;
        row.ApplyPort();
    }

    private void OnSuggestPort(object? sender, RoutedEventArgs e) => RowOf<MappingRow>(sender)?.SuggestPort();

    private void OnDisableMapping(object? sender, RoutedEventArgs e)
    {
        if (Model is null || RowOf<MappingRow>(sender) is not { } row) return;
        Model.SetMapping(row, enabled: false, row.LocalPort);
    }

    private async void OnOpenMapping(object? sender, RoutedEventArgs e)
    {
        if (RowOf<MappingRow>(sender) is not { } row) return;
        await Launcher.LaunchUriAsync(new Uri(row.Url));
    }

    private void OnRetryConflicts(object? sender, RoutedEventArgs e) => Model?.RetryConflicts();

    private void OnApplyNetworks(object? sender, RoutedEventArgs e) => Model?.ApplyNetworks();

    private void OnReloadNetworks(object? sender, RoutedEventArgs e) => Model?.ReloadNetworks();

    private async void OnCheckFirewall(object? sender, RoutedEventArgs e)
    {
        if (Model is null) return;
        await Model.CheckFirewallAsync();
    }

    private async void OnAllowFirewall(object? sender, RoutedEventArgs e)
    {
        if (Model is null) return;
        await Model.AllowFirewallAsync();
    }
}
