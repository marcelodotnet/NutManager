using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using NutManager.Agent.Config.Localization;

namespace NutManager.Agent.Config.Views;

/// <summary>
/// A short-lived masked password prompt for protected PKCS#12 files.
///
/// The password never becomes a binding or view-model property. The TextBox is cleared before the
/// modal result is returned, and cancellation returns no value at all.
/// </summary>
internal sealed class CertificatePasswordDialog : Window
{
    private readonly TextBox _passwordBox;

    public CertificatePasswordDialog(AgentConfigStrings strings)
    {
        ArgumentNullException.ThrowIfNull(strings);

        Title = strings["Https.Import.PasswordTitle"];
        Width = 390;
        Height = 205;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ResolveBrush("NutWindowOpaqueBrush", Brushes.Black);

        _passwordBox = new TextBox
        {
            PasswordChar = '●',
            MinHeight = 34,
        };
        AutomationProperties.SetName(_passwordBox, strings["Https.Import.Password"]);
        _passwordBox.KeyDown += OnPasswordKeyDown;

        var importButton = new Button
        {
            Content = strings["Https.Import"],
            MinWidth = 92,
            Classes = { "nut-primary" },
        };
        importButton.Click += OnImportClicked;

        var cancelButton = new Button
        {
            Content = strings["Action.Cancel"],
            MinWidth = 84,
        };
        cancelButton.Click += OnCancelClicked;

        Content = new Border
        {
            Margin = new Thickness(12),
            Padding = new Thickness(16),
            CornerRadius = new CornerRadius(12),
            Background = ResolveBrush("NutSurface1Brush", Brushes.Black),
            BorderBrush = ResolveBrush("NutBorderBrush", Brushes.Gray),
            BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock
                    {
                        Text = strings["Https.Import.PasswordPrompt"],
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = ResolveBrush("NutTextPrimaryBrush", Brushes.White),
                    },
                    _passwordBox,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { importButton, cancelButton },
                    },
                },
            },
        };

        Opened += (_, _) => _passwordBox.Focus();
        Closed += (_, _) => _passwordBox.Text = string.Empty;
    }

    private IBrush ResolveBrush(string key, IBrush fallback) =>
        Application.Current is { } application &&
        application.TryGetResource(key, application.ActualThemeVariant, out var value) &&
        value is IBrush brush
            ? brush
            : fallback;

    private void OnPasswordKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter)
        {
            Complete();
            e.Handled = true;
        }
        else if (e.Key is Key.Escape)
        {
            Cancel();
            e.Handled = true;
        }
    }

    private void OnImportClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Complete();

    private void OnCancelClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Cancel();

    private void Complete()
    {
        var password = _passwordBox.Text ?? string.Empty;
        _passwordBox.Text = string.Empty;
        Close(password);
    }

    private void Cancel()
    {
        _passwordBox.Text = string.Empty;
        Close(null);
    }
}
