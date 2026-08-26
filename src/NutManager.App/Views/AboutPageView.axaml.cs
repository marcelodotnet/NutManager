using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using NutManager.App.ViewModels;

namespace NutManager.App.Views;

public partial class AboutPageView : UserControl
{
    public AboutPageView() => InitializeComponent();

    private async void CopySupportInformationButton_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is not AboutPageViewModel viewModel ||
            TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        try
        {
            await clipboard.SetTextAsync(viewModel.CreateSupportInformation());
            viewModel.ReportCopyResult(succeeded: true);
        }
        catch
        {
            viewModel.ReportCopyResult(succeeded: false);
        }
    }
}
