using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using NutManager.Agent.Config.ViewModels;
using NutManager.Core.Agent;
using NutManager.Core.Models;

namespace NutManager.Agent.Config.Views;

/// <summary>
/// The window.
///
/// Window-only actions live here: language-menu selection, file picking and copying already-presented,
/// non-secret endpoint identifiers. Administrative behavior remains in the view model and infrastructure.
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Avalonia resolves Default against Windows, so the variant the window ends up in is only
        // known once it exists - and it can change underneath the window if somebody switches the
        // system theme while it is open. Both cases arrive here, and the glyph follows.
        ActualThemeVariantChanged += (_, _) => PublishEffectiveTheme();

        DataContextChanged += OnDataContextChanged;

        // A toast counts down on a task. Closing the window while one is in flight would leave that
        // delay to resume against a view model whose window is gone.
        Closed += (_, _) => (DataContext as AgentConfigViewModel)?.CancelTransientFeedback();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is not AgentConfigViewModel viewModel) return;

        // The saved preference is applied before the first paint, so the window opens in the theme
        // the operator last chose rather than opening in one and correcting itself.
        ApplyTheme(viewModel.SelectedTheme);
        viewModel.ThemeChanged += ApplyTheme;
        PublishEffectiveTheme();
    }

    /// <summary>
    /// Hands the chosen theme to Avalonia, using the desktop application's own mapping.
    ///
    /// System maps to Default rather than to a guess, which is what lets "follow Windows" keep
    /// following Windows after the window is open instead of freezing at whatever it was.
    /// </summary>
    private void ApplyTheme(ThemePreference preference)
    {
        if (Application.Current is not { } application) return;

        application.RequestedThemeVariant = preference switch
        {
            ThemePreference.Light => ThemeVariant.Light,
            ThemePreference.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }

    private void PublishEffectiveTheme() =>
        (DataContext as AgentConfigViewModel)?.UpdateEffectiveTheme(ActualThemeVariant == ThemeVariant.Dark);

    /// <summary>
    /// Copies the value the button carries, and reports what actually happened.
    ///
    /// The result is passed to the view model rather than assumed: a clipboard the platform refuses -
    /// locked by another process, or absent entirely on a session with no top level - has to say so.
    /// Announcing a copy that did not happen is worse than announcing nothing, because the operator
    /// then pastes whatever was there before.
    /// </summary>
    private async void OnCopyValueClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string value } || string.IsNullOrWhiteSpace(value)) return;

        var viewModel = DataContext as AgentConfigViewModel;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;

        if (clipboard is null)
        {
            viewModel?.ReportEndpointCopy(succeeded: false);
            return;
        }

        try
        {
            var item = new DataTransferItem();
            item.Set(DataFormat.Text, value);
            var data = new DataTransfer();
            data.Add(item);
            await clipboard.SetDataAsync(data);

            viewModel?.ReportEndpointCopy(succeeded: true);
        }
        catch (Exception)
        {
            // The clipboard is shared with every other process on the desktop and can be held by one
            // of them. That is a transient failure of a convenience, reported in the same transient
            // way the success is - never a dialog, and never a stack trace on screen.
            viewModel?.ReportEndpointCopy(succeeded: false);
        }
    }

    private async void OnImportCertificateClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AgentConfigViewModel viewModel || TopLevel.GetTopLevel(this) is not { } topLevel)
        {
            return;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = viewModel.Strings["Https.Import.DialogTitle"],
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(viewModel.Strings["Https.Import.FileType"])
                {
                    Patterns = ["*.pfx", "*.p12", "*.cer", "*.crt"],
                },
            ],
        });

        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) return;

        var result = await viewModel.ImportCertificateAsync(path, password: null);
        if (result.Outcome is not AgentCertificateImportOutcome.PasswordRequired) return;

        var dialog = new CertificatePasswordDialog(viewModel.Strings);
        string? password = await dialog.ShowDialog<string?>(this);

        try
        {
            if (password is not null)
            {
                await viewModel.ImportCertificateAsync(path, password);
            }
        }
        finally
        {
            // Strings are immutable, so their bytes cannot be overwritten. Dropping the only caller
            // reference immediately after the loader returns is the strongest useful cleanup here;
            // the dialog itself clears its TextBox before it closes.
            password = null;
        }
    }
}
