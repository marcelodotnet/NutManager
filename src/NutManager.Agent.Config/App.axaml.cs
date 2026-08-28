using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using NutManager.Agent.Config.Presentation;
using NutManager.Agent.Config.Localization;
using NutManager.Agent.Config.ViewModels;
using NutManager.Agent.Config.Views;
using NutManager.Infrastructure.AgentConfiguration;

namespace NutManager.Agent.Config;

/// <summary>
/// Composition, and only composition.
///
/// Every rule this utility applies lives in NutManager.Core and every Windows call it makes lives in
/// NutManager.Infrastructure. What happens here is that the real adapters are handed to the view
/// model — which is also why the view model can be tested against fakes, with no group, certificate
/// store, firewall or service anywhere near the test run.
/// </summary>
public sealed class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // After the dictionaries are composed, exactly as the desktop does it: the catalog's
        // hand-authored drawings are the fallback, and Material Icons replaces the ones it carries.
        // A shield in this window is then the same shield the desktop draws.
        AgentConfigIcons.Apply(this);
    }

    [SupportedOSPlatform("windows")]
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = new AgentConfigViewModel(
                new WindowsAgentConfigurationStore(),
                new WindowsAgentOperatorsGroupAdministration(),
                new WindowsAgentServiceAdministration(),
                new WindowsAgentHttpsResourceAdministration(),
                new WindowsAgentCertificateCatalog(),
                new WindowsAgentRuntimeInventory(),
                certificateImporter: new WindowsAgentCertificateImporter(),
                preferences: new AgentConfigUiPreferences());

            desktop.MainWindow = new MainWindow { DataContext = viewModel };

            // Started after the window exists so a slow query — the certificate store on a domain
            // member can take a moment — shows an open window rather than nothing at all.
            //
            // The result is observed rather than discarded. A bare `_ = RefreshAsync()` swallows any
            // exception into a dropped task, and the visible symptom is a window that opens with empty
            // sections and no explanation — exactly the state an operator would report as "it shows
            // nothing". A failure to read the machine belongs on the screen.
            viewModel.RefreshAsync().ContinueWith(
                task => viewModel.ReportStartupFailure(task.Exception),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.FromCurrentSynchronizationContext());
        }

        base.OnFrameworkInitializationCompleted();
    }
}
