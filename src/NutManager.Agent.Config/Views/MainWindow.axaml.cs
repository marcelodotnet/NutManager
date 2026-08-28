using Avalonia.Controls;
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
    public MainWindow() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnSelectPortugueseClicked(object? sender, RoutedEventArgs e) =>
        SelectLanguage(UiLanguagePreference.PtBr);

    private void OnSelectEnglishClicked(object? sender, RoutedEventArgs e) =>
        SelectLanguage(UiLanguagePreference.EnUs);

    private void SelectLanguage(UiLanguagePreference language)
    {
        if (DataContext is AgentConfigViewModel viewModel) viewModel.SelectedLanguage = language;
    }

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
