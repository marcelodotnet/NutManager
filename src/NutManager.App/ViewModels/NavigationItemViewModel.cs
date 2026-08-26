using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace NutManager.App.ViewModels;

public sealed partial class NavigationItemViewModel : ObservableObject
{
    public NavigationItemViewModel(AppPage page, string title, ICommand navigateCommand)
    {
        Page = page;
        _title = title;
        NavigateCommand = navigateCommand;
    }

    public AppPage Page { get; }

    [ObservableProperty]
    private string _title;

    public bool IsOverview => Page == AppPage.Overview;
    public bool IsDevices => Page == AppPage.Devices;
    public bool IsAdministration => Page == AppPage.Administration;
    public bool IsDiagnostics => Page == AppPage.Diagnostics;
    public bool IsSettings => Page == AppPage.Settings;
    public bool IsAbout => Page == AppPage.About;

    public ICommand NavigateCommand { get; }

    [ObservableProperty]
    private bool _isSelected;
}
