using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace NutManager.Agent.Config.Views;

/// <summary>
/// The window.
///
/// Closing it is the one thing a button here does that no view model can express — a view model able
/// to close its own window would need a handle to it, and that is exactly the coupling this
/// code-behind exists to avoid. Everything else on this screen is a command.
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();
}
