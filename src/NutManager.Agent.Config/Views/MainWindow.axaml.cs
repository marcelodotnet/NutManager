using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace NutManager.Agent.Config.Views;

/// <summary>
/// The window.
///
/// Window-only actions live here: close the window and copy already-presented, non-secret endpoint or
/// certificate identifiers. Administrative behavior remains in the view model and infrastructure.
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();

    private async void OnCopyValueClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string value } || string.IsNullOrWhiteSpace(value)) return;

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;

        var item = new DataTransferItem();
        item.Set(DataFormat.Text, value);
        var data = new DataTransfer();
        data.Add(item);
        await clipboard.SetDataAsync(data);
    }
}
