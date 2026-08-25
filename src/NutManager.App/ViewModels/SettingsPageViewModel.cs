using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NutManager.App.Localization;
using NutManager.App.Services;
using NutManager.Core.Agent;
using NutManager.Core.Models;
using NutManager.Core.Services;
using NutManager.Core.Validation;

namespace NutManager.App.ViewModels;

public sealed partial class SettingsPageViewModel : PageViewModel
{
    private readonly IApplicationSettingsStore? _settingsStore;
    private readonly IManagedNutServerProfileStore? _profileStore;
    private readonly ManagedNutServerProfileUpdateService? _profileMutator;
    private readonly IRemoteCredentialStore? _credentialStore;
    private readonly NutAgentCredentialCoordinator? _agentCredentials;
    private int _agentCredentialGeneration;
    private readonly IManagedNutConnectionTester? _connectionTester;
    private readonly Guid _runtimeProfileId;
    private readonly string _runtimeProfileName;
    private ApplicationSettings _confirmedSettings;
    private ManagedNutServerProfiles _confirmedProfiles;
    private Guid? _draftSourceId;
    private ManagedNutServerProfile? _draftBaseProfile;
    private PendingProfileAction? _pendingProfileAction;
    private ManagedNutServerProfile? _selectedManagedProfile;
    private ManagedProfileCardViewModel? _selectedProfileCard;
    private ManagedNutServerProfileValidationResult _profileValidation = new(null, []);
    private bool _isCreatingProfile;
    private bool _canPersistThemeAutomatically = true;
    private bool _canPersistProfiles = true;
    private bool _isApplyingVisualPreferences;
    private long _draftVersion;

    public SettingsPageViewModel()
        : this(new ApplicationSettings(), null, null, null)
    {
    }

    public SettingsPageViewModel(ApplicationSettings settings, IApplicationSettingsStore? store)
        : this(settings, store, null, null)
    {
    }

    public SettingsPageViewModel(
        ApplicationSettings settings,
        IApplicationSettingsStore? settingsStore,
        ManagedNutServerProfiles? profiles,
        IManagedNutServerProfileStore? profileStore,
        ManagedNutServerProfileUpdateService? profileMutator = null,
        IRemoteCredentialStore? credentialStore = null,
        IManagedNutConnectionTester? connectionTester = null,
        Guid? runtimeProfileId = null,
        INutManagedFileDetector? managedFileDetector = null,
        NutAgentCredentialCoordinator? agentCredentials = null)
        : base(
            Localize(settings, "Settings.Title"),
            Localize(settings, "Settings.Description"))
    {
        ArgumentNullException.ThrowIfNull(settings);
        _managedFileDetector = managedFileDetector;
        _settingsStore = settingsStore;
        _profileStore = profileStore;
        _profileMutator = profileStore is null ? null : profileMutator ?? new ManagedNutServerProfileUpdateService(profileStore);
        _credentialStore = credentialStore;
        _agentCredentials = agentCredentials;
        _connectionTester = connectionTester;
        _confirmedSettings = settings;
        _confirmedProfiles = profiles ?? ManagedNutServerProfiles.CreateLegacyProfile(settings);
        _runtimeProfileId = runtimeProfileId ?? _confirmedProfiles.ActiveProfileId;
        _runtimeProfileName = _confirmedProfiles.Profiles.Single(profile => profile.Id == _runtimeProfileId).Name;
        Localizer = new NutManagerLocalizer(settings.Language);

        ManagedProfiles = new ObservableCollection<ManagedNutServerProfile>(_confirmedProfiles.Profiles);
        ManagedProfileCards = new ObservableCollection<ManagedProfileCardViewModel>();
        ProfileDraft = new ManagedNutServerProfileDraftViewModel(_confirmedProfiles.ActiveProfile);
        ProfileDraft.PropertyChanged += OnProfileDraftPropertyChanged;
        _draftSourceId = _confirmedProfiles.ActiveProfileId;
        _draftBaseProfile = _confirmedProfiles.ActiveProfile;
        _selectedManagedProfile = _confirmedProfiles.ActiveProfile;

        ThemeOptions =
        [
            new ThemeOption(ThemePreference.System, Localizer.Get("Theme.System")),
            new ThemeOption(ThemePreference.Light, Localizer.Get("Theme.Light")),
            new ThemeOption(ThemePreference.Dark, Localizer.Get("Theme.Dark"))
        ];
        LanguageOptions =
        [
            new PresentationOption<UiLanguagePreference>(UiLanguagePreference.PtBr, Localizer.Get("Language.PtBr")),
            new PresentationOption<UiLanguagePreference>(UiLanguagePreference.EnUs, Localizer.Get("Language.EnUs"))
        ];
        SidebarOptions =
        [
            new PresentationOption<SidebarPreference>(SidebarPreference.Expanded, Localizer.Get("Sidebar.Expanded")),
            new PresentationOption<SidebarPreference>(SidebarPreference.Collapsed, Localizer.Get("Sidebar.Collapsed"))
        ];
        ManagementModeOptions =
        [
            new PresentationOption<NutManagementMode>(NutManagementMode.Local, Localizer.Get("Management.Local")),
            new PresentationOption<NutManagementMode>(NutManagementMode.Remote, Localizer.Get("Management.Remote"))
        ];
        AccessModeOptions =
        [
            new PresentationOption<ManagedNutServerAccessMode>(ManagedNutServerAccessMode.ReadOnly, Localizer.Get("Access.ReadOnly")),
            new PresentationOption<ManagedNutServerAccessMode>(ManagedNutServerAccessMode.Manage, Localizer.Get("Access.Manage"))
        ];
        ConfigurationTransportOptions =
        [
            new PresentationOption<RemoteConfigurationTransportKind>(RemoteConfigurationTransportKind.SshSftp, Localizer.Get("Transport.Sftp")),
            new PresentationOption<RemoteConfigurationTransportKind>(RemoteConfigurationTransportKind.Smb, Localizer.Get("Transport.Smb"))
        ];
        SshAuthenticationOptions =
        [
            new PresentationOption<SshAuthenticationMode>(SshAuthenticationMode.Password, Localizer.Get("SshAuth.Password")),
            new PresentationOption<SshAuthenticationMode>(SshAuthenticationMode.PrivateKey, Localizer.Get("SshAuth.PrivateKey"))
        ];
        SmbAuthenticationOptions =
        [
            new PresentationOption<SmbAuthenticationMode>(SmbAuthenticationMode.CurrentWindowsIdentity, Localizer.Get("SmbAuth.CurrentWindowsIdentity")),
            new PresentationOption<SmbAuthenticationMode>(SmbAuthenticationMode.ExplicitCredentials, Localizer.Get("SmbAuth.ExplicitCredentials"))
        ];

        AgentTransportOptions =
        [
            new PresentationOption<NutAgentTransportKind>(NutAgentTransportKind.NamedPipe, Localizer.Get("Agent.Transport.NamedPipe")),
            new PresentationOption<NutAgentTransportKind>(NutAgentTransportKind.Https, Localizer.Get("Agent.Transport.Https"))
        ];

        AgentAuthenticationOptions =
        [
            new PresentationOption<NutAgentAuthenticationMode>(NutAgentAuthenticationMode.CurrentWindowsIdentity, Localizer.Get("Agent.Auth.CurrentWindowsIdentity")),
            new PresentationOption<NutAgentAuthenticationMode>(NutAgentAuthenticationMode.AlternateWindowsAccount, Localizer.Get("Agent.Auth.AlternateWindowsAccount"))
        ];

        _isApplyingVisualPreferences = true;
        Apply(settings);
        SelectedThemeOption = ThemeOptions.Single(option => option.Preference == settings.Theme);
        SelectedLanguageOption = LanguageOptions.Single(option => option.Value == settings.Language);
        SelectedSidebarOption = SidebarOptions.Single(option => option.Value == settings.SidebarPreference);
        _isBackgroundTransparent = settings.BackgroundTransparency;
        _isApplyingVisualPreferences = false;
        RebuildProfileCards();
        RefreshProfileValidation();
    }

    public NutManagerLocalizer Localizer { get; private set; }

    public IReadOnlyList<ThemeOption> ThemeOptions { get; }

    public IReadOnlyList<PresentationOption<UiLanguagePreference>> LanguageOptions { get; }

    public IReadOnlyList<PresentationOption<SidebarPreference>> SidebarOptions { get; }

    public IReadOnlyList<PresentationOption<NutManagementMode>> ManagementModeOptions { get; }

    public IReadOnlyList<PresentationOption<ManagedNutServerAccessMode>> AccessModeOptions { get; }

    public IReadOnlyList<PresentationOption<RemoteConfigurationTransportKind>> ConfigurationTransportOptions { get; }

    public IReadOnlyList<PresentationOption<SshAuthenticationMode>> SshAuthenticationOptions { get; }

    public IReadOnlyList<PresentationOption<SmbAuthenticationMode>> SmbAuthenticationOptions { get; }

    public IReadOnlyList<PresentationOption<NutAgentTransportKind>> AgentTransportOptions { get; }

    public IReadOnlyList<PresentationOption<NutAgentAuthenticationMode>> AgentAuthenticationOptions { get; }

    public ObservableCollection<ManagedNutServerProfile> ManagedProfiles { get; }

    public ObservableCollection<ManagedProfileCardViewModel> ManagedProfileCards { get; }

    public ManagedNutServerProfileDraftViewModel ProfileDraft { get; }

    public ManagedNutServerProfile? SelectedManagedProfile
    {
        get => _selectedManagedProfile;
        set => RequestProfileSelection(value);
    }

    public ManagedProfileCardViewModel? SelectedProfileCard
    {
        get => _selectedProfileCard;
        set => RequestProfileSelection(value?.Profile);
    }

    public PresentationOption<NutManagementMode> SelectedManagementModeOption
    {
        get => ManagementModeOptions.Single(option => option.Value == ProfileDraft.ManagementMode);
        set => ProfileDraft.ManagementMode = value.Value;
    }

    public PresentationOption<ManagedNutServerAccessMode> SelectedAccessModeOption
    {
        get => AccessModeOptions.Single(option => option.Value == ProfileDraft.AccessMode);
        set => ProfileDraft.AccessMode = value.Value;
    }

    public PresentationOption<RemoteConfigurationTransportKind> SelectedConfigurationTransportOption
    {
        get => ConfigurationTransportOptions.Single(option => option.Value == ProfileDraft.ConfigurationTransport);
        set => ProfileDraft.ConfigurationTransport = value.Value;
    }

    public PresentationOption<SshAuthenticationMode> SelectedSshAuthenticationOption
    {
        get => SshAuthenticationOptions.Single(option => option.Value == ProfileDraft.SshAuthenticationMode);
        set => ProfileDraft.SshAuthenticationMode = value.Value;
    }

    public PresentationOption<SmbAuthenticationMode> SelectedSmbAuthenticationOption
    {
        get => SmbAuthenticationOptions.Single(option => option.Value == ProfileDraft.SmbAuthenticationMode);
        set => ProfileDraft.SmbAuthenticationMode = value.Value;
    }

    public PresentationOption<NutAgentTransportKind> SelectedAgentTransportOption
    {
        get => AgentTransportOptions.Single(option => option.Value == ProfileDraft.AgentTransport);
        set => ProfileDraft.AgentTransport = value.Value;
    }

    public PresentationOption<NutAgentAuthenticationMode> SelectedAgentAuthenticationOption
    {
        get => AgentAuthenticationOptions.Single(option => option.Value == ProfileDraft.AgentAuthentication);
        set => ProfileDraft.AgentAuthentication = value.Value;
    }

    // ==================== Windows agent labels ====================

    public string AgentSectionText => Localizer.Get("Agent.Section");
    public string AgentTransportText => Localizer.Get("Agent.Transport");
    public string AgentHttpsEndpointText => Localizer.Get("Agent.HttpsEndpoint");
    public string AgentHttpsEndpointInvalidText => Localizer.Get("Agent.HttpsEndpoint.Invalid");
    public string AgentAuthenticationText => Localizer.Get("Agent.Authentication");
    public string AgentAccountText => Localizer.Get("Agent.Account");
    public string AgentNamedPipeNoticeText => Localizer.Get("Agent.NamedPipe.Notice");
    public string AgentAlternateAccountNoticeText => Localizer.Get("Agent.AlternateAccount.Notice");

    /// <summary>
    /// The account the profile carries, or a statement that none is configured. Never a secret, and
    /// never anything from which one could be inferred.
    /// </summary>
    // ==================== Windows agent credential ====================

    /// <summary>
    /// True while the credential dialog is open or the handshake is running. One at a time: two
    /// prompts racing to publish into the same draft is how an operator ends up with an account
    /// they did not pick.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAuthenticateAgentCredential))]
    [NotifyPropertyChangedFor(nameof(CanForgetAgentCredential))]
    private bool _isAuthenticatingAgentCredential;

    /// <summary>Whether a validated credential should be written when the profile is saved.</summary>
    [ObservableProperty]
    private bool _rememberAgentCredential = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AgentCredentialStatusText))]
    [NotifyPropertyChangedFor(nameof(CanForgetAgentCredential))]
    private NutAgentCredentialOutcome? _agentCredentialOutcome;

    /// <summary>The account most recently proven against the agent, held only for this session.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AgentCredentialStatusText))]
    [NotifyPropertyChangedFor(nameof(CanForgetAgentCredential))]
    private string? _validatedAgentAccount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AgentCredentialStatusText))]
    [NotifyPropertyChangedFor(nameof(CanForgetAgentCredential))]
    private bool _hasStoredAgentCredential;

    public string AgentAuthenticateText => Localizer.Get(
        HasStoredAgentCredential || ValidatedAgentAccount is not null ? "Agent.Credential.Change" : "Agent.Credential.Authenticate");

    /// <summary>
    /// The label over the agent's credential status. The account and the credential are separate
    /// facts — a profile can name an account it has no secret for — and showing the status under a
    /// heading that says "Account" made the two read as one.
    /// </summary>
    public string AgentCredentialLabel => Localizer.Get("Agent.Credential.Label");

    /// <summary>Names what the protected credential below it is actually for.</summary>
    public string ConfigurationCredentialLabel => Localizer.Get("Credential.Configuration.Label");

    public string AgentForgetText => Localizer.Get("Agent.Credential.Forget");
    public string AgentRememberText => Localizer.Get("Agent.Credential.Remember");
    public string AgentAuthenticatingText => Localizer.Get("Agent.Credential.Authenticating");

    /// <summary>Set by the View so the Windows dialog belongs to the application window.</summary>
    public nint CredentialPromptOwnerWindowHandle { get; set; }

    /// <summary>
    /// The dialog is not opened for a destination that cannot be used: an endpoint that will not
    /// validate is a prompt the operator would fill in for nothing.
    /// </summary>
    public bool CanAuthenticateAgentCredential =>
        _agentCredentials is not null &&
        ProfileDraft.UsesAgentAlternateAccount &&
        !ProfileDraft.HasInvalidAgentHttpsEndpoint &&
        !IsAuthenticatingAgentCredential;

    public bool CanForgetAgentCredential =>
        _agentCredentials is not null &&
        ProfileDraft.UsesAgentAlternateAccount &&
        !IsAuthenticatingAgentCredential &&
        (HasStoredAgentCredential || ValidatedAgentAccount is not null);

    /// <summary>
    /// What an operator can act on, and never anything a secret could be inferred from.
    ///
    /// A stored credential is reported as stored rather than as valid. It may have been changed on
    /// the server or expired since, and only a handshake settles that — claiming more than was
    /// proven is how a credential screen starts lying.
    /// </summary>
    public string AgentCredentialStatusText
    {
        get
        {
            if (!ProfileDraft.UsesAgentAlternateAccount) return Localizer.Get("Agent.Auth.CurrentWindowsIdentity");

            if (AgentCredentialOutcome is { } outcome && outcome != NutAgentCredentialOutcome.Validated &&
                outcome != NutAgentCredentialOutcome.Cancelled)
            {
                return Localizer.Get(outcome switch
                {
                    NutAgentCredentialOutcome.AccessDenied => "Agent.Credential.AccessDenied",
                    NutAgentCredentialOutcome.AgentUnavailable => "Agent.Credential.Unavailable",
                    NutAgentCredentialOutcome.HostUnreachable => "Agent.Credential.HostUnreachable",
                    NutAgentCredentialOutcome.TimedOut => "Agent.Credential.TimedOut",
                    NutAgentCredentialOutcome.ProtocolFailure => "Agent.Credential.ProtocolFailure",
                    NutAgentCredentialOutcome.PromptUnavailable => "Agent.Credential.PromptUnavailable",
                    _ => "Agent.Credential.Failed"
                });
            }

            if (HasStoredAgentCredential) return Localizer.Get("Agent.Credential.Stored");

            if (ValidatedAgentAccount is { } validated)
            {
                var key = RememberAgentCredential ? "Agent.Credential.ValidatedPending" : "Agent.Credential.ValidatedSession";
                return string.Format(System.Globalization.CultureInfo.CurrentCulture, Localizer.Get(key), validated);
            }

            return string.IsNullOrWhiteSpace(ProfileDraft.AgentUsername)
                ? Localizer.Get("Agent.Account.NotConfigured")
                : Localizer.Get("Agent.Credential.Missing");
        }
    }

    public string AgentAccountStatusText => ProfileDraft.UsesAgentAlternateAccount
        ? string.IsNullOrWhiteSpace(ProfileDraft.AgentUsername)
            ? Localizer.Get("Agent.Account.NotConfigured")
            : ProfileDraft.AgentUsername!
        : Localizer.Get("Agent.Auth.CurrentWindowsIdentity");

    public string AppearanceTitle => Localizer.Get("Appearance.Title");
    public string AppearanceThemeLabel => Localizer.Get("Appearance.Theme");
    public string AppearanceLanguageLabel => Localizer.Get("Appearance.Language");
    public string AppearanceSidebarLabel => Localizer.Get("Appearance.Sidebar");
    public string AppearanceTransparencyLabel => Localizer.Get("Appearance.Transparency");
    public string AppearanceTransparencyOn => Localizer.Get("Appearance.Transparency.On");
    public string AppearanceTransparencyOff => Localizer.Get("Appearance.Transparency.Off");
    public string AppearanceTransparencyDarkOnly => Localizer.Get("Appearance.Transparency.DarkOnly");
    public string RestartLanguageMessage => Localizer.Get("Appearance.RestartRequired");
    public string ManagedServersTitle => Localizer.Get("Profiles.Title");
    public string NewServerText => Localizer.Get("Profiles.NewServer");
    public string EditorTitle => IsCreatingProfile ? Localizer.Get("Profiles.NewServer") : Localizer.Get("Profiles.EditorTitle");
    public string MonitoringSectionTitle => Localizer.Get("Profiles.MonitoringSection");
    public string ManagementSectionTitle => Localizer.Get("Profiles.ManagementSection");
    public string TransportSectionTitle => Localizer.Get("Profiles.TransportSection");
    public string NameLabel => Localizer.Get("Profiles.Name");
    public string MonitoringHostLabel => Localizer.Get("Profiles.MonitoringHost");
    public string MonitoringPortLabel => Localizer.Get("Profiles.MonitoringPort");
    public string PreferredUpsLabel => Localizer.Get("Profiles.PreferredUps");
    public string ManagementModeLabel => Localizer.Get("Profiles.ManagementMode");
    public string AccessModeLabel => Localizer.Get("Profiles.AccessMode");
    public string TransportLabel => Localizer.Get("Profiles.Transport");
    public string ManagementHostLabel => Localizer.Get("Profiles.ManagementHost");
    public string SshPortLabel => Localizer.Get("Profiles.SshPort");
    public string SshUsernameLabel => Localizer.Get("Profiles.SshUsername");
    public string SshAuthenticationLabel => Localizer.Get("Profiles.SshAuthentication");
    public string PrivateKeyLabel => Localizer.Get("Profiles.PrivateKey");
    public string SelectPrivateKeyText => Localizer.Get("Profiles.SelectPrivateKey");
    public string SelectPrivateKeyDialogTitle => Localizer.Get("Profiles.SelectPrivateKeyDialog");
    public string PrivateKeyMetadataHelp => Localizer.Get("Profiles.PrivateKeyMetadataHelp");
    public string RemoteDirectoryLabel => Localizer.Get("Profiles.RemoteDirectory");
    public string TrustedHostKeyLabel => Localizer.Get("Profiles.TrustedHostKey");
    public string ForgetHostKeyText => Localizer.Get("Profiles.ForgetHostKey");
    public string ProtectedSecretHelp => Localizer.Get("Profiles.ProtectedSecretHelp");
    public string SmbShareLabel => Localizer.Get("Profiles.SmbShare");
    public string SmbAuthenticationLabel => Localizer.Get("Profiles.SmbAuthentication");
    public string SmbSecretHelp => Localizer.Get("Profiles.SmbSecretHelp");
    public string SmbLegacyDirectoryText => Localizer.Get("Smb.Legacy.DirectoryAdjustment");
    public string ManagedFilesLabel => Localizer.Get("Profiles.ManagedFiles");
    public string ManagedFilesHelp => Localizer.Get("Profiles.ManagedFiles.Help");
    public string ManagedFilesNoneWarning => Localizer.Get("Profiles.ManagedFiles.None");
    public string ManagedFilesDetectText => Localizer.Get("Profiles.ManagedFiles.Detect");

    private readonly INutManagedFileDetector? _managedFileDetector;

    [ObservableProperty]
    private bool _isDetectingManagedFiles;

    [ObservableProperty]
    private string? _managedFilesDetectionMessage;

    /// <summary>
    /// Detection needs somewhere to look. Without a detector, or with one that has no validated
    /// location yet, the action stays disabled and says why rather than pretending to check.
    /// </summary>
    public bool CanDetectManagedFiles =>
        !IsDetectingManagedFiles && _managedFileDetector is { CanDetect: true } && ProfileDraft is not null;

    public string ManagedFilesDetectHint => _managedFileDetector is { CanDetect: true }
        ? string.Empty
        : Localizer.Get("Profiles.ManagedFiles.DetectUnavailable");

    public bool HasManagedFilesDetectHint => !string.IsNullOrEmpty(ManagedFilesDetectHint);

    /// <summary>
    /// Looks for the supported files and applies what it found to the draft. This runs only when
    /// asked: nothing detects on page load, and the result lands in the draft where it still has to
    /// be saved, so the profile is never changed behind the administrator's back.
    /// </summary>
    [RelayCommand]
    private async Task DetectManagedFilesAsync(CancellationToken cancellationToken)
    {
        if (_managedFileDetector is null || ProfileDraft is null)
        {
            return;
        }

        IsDetectingManagedFiles = true;
        OnPropertyChanged(nameof(CanDetectManagedFiles));
        ManagedFilesDetectionMessage = Localizer.Get("Profiles.ManagedFiles.Detecting");
        try
        {
            var result = await _managedFileDetector.DetectAsync(cancellationToken);
            ManagedFilesDetectionMessage = result.Status switch
            {
                NutManagedFileDetectionStatus.Success when result.Count == 0 =>
                    Localizer.Get("Profiles.ManagedFiles.NoneFound"),
                NutManagedFileDetectionStatus.Success => string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    Localizer.Get("Profiles.ManagedFiles.Found"), result.Count),
                NutManagedFileDetectionStatus.Unavailable =>
                    Localizer.Get("Profiles.ManagedFiles.DetectUnavailable"),
                NutManagedFileDetectionStatus.Cancelled => null,
                _ => Localizer.Get("Profiles.ManagedFiles.DetectFailed")
            };

            if (result.IsSuccess)
            {
                ProfileDraft.SetManagedFiles(result.ToManagedFiles());
            }
        }
        finally
        {
            IsDetectingManagedFiles = false;
            OnPropertyChanged(nameof(CanDetectManagedFiles));
        }
    }

    public bool HasManagedFilesDetectionMessage => !string.IsNullOrWhiteSpace(ManagedFilesDetectionMessage);

    partial void OnManagedFilesDetectionMessageChanged(string? value) =>
        OnPropertyChanged(nameof(HasManagedFilesDetectionMessage));
    public string ForgetCredentialText => Localizer.Get("Profiles.ForgetCredential");
    public string SaveProfileText => Localizer.Get("Common.Save");
    public string DiscardProfileText => Localizer.Get("Common.Discard");
    public string ContinueEditingText => Localizer.Get("DirtyDraft.ContinueEditing");
    public string SaveAndContinueText => Localizer.Get("DirtyDraft.Save");
    public string DiscardAndContinueText => Localizer.Get("DirtyDraft.Discard");
    public string DirtyDraftTitle => Localizer.Get("DirtyDraft.Title");
    public string DirtyDraftMessage => Localizer.Get("DirtyDraft.Message");
    public string TestConnectionText => Localizer.Get("ConnectionTest.Action");
    public string ActivateProfileText => Localizer.Get("Profiles.Activate");
    public string DeleteProfileText => Localizer.Get("Profiles.Delete");
    public string GeneralSettingsTitle => Localizer.Get("Settings.GeneralTitle");
    public string ConnectionTimeoutLabel => Localizer.Get("Settings.ConnectionTimeout");
    public string PollingIntervalLabel => Localizer.Get("Settings.PollingInterval");
    public string SaveSettingsText => Localizer.Get("Settings.Save");
    public string SavingText => Localizer.Get("Common.Saving");
    public string SettingsSavedText => Localizer.Get("Settings.SaveSuccess");
    public string ProfileSavingText => Localizer.Get("Profiles.Saving");
    public string RuntimeProfileLabel => Localizer.Get("Profiles.RuntimeProfile");
    public string PersistedActiveProfileLabel => Localizer.Get("Profiles.PersistedActiveProfile");
    public string LocalManagementHelp => Localizer.Get("Profiles.LocalManagementHelp");
    public string RuntimeProfileName => _runtimeProfileName;
    public string PersistedActiveProfileName => _confirmedProfiles.ActiveProfile.Name;
    public string RestartRequiredTitle => Localizer.Get("Profiles.RestartRequiredTitle");
    public string RestartRequiredMessage => Localizer.Get("Profiles.RestartRequiredMessage");

    [ObservableProperty] private string _pollingIntervalSeconds = "5";
    [ObservableProperty] private string _connectionTimeoutSeconds = "5";

    /// <summary>
    /// Numeric presentation boundaries for the duration fields. The existing string properties
    /// remain the validation/persistence contract, while NumericUpDown prevents non-numeric input.
    /// </summary>
    public decimal? PollingIntervalSecondsValue
    {
        get => ParseNumericPresentationValue(PollingIntervalSeconds);
        set => PollingIntervalSeconds = FormatNumericPresentationValue(value);
    }

    public decimal? ConnectionTimeoutSecondsValue
    {
        get => ParseNumericPresentationValue(ConnectionTimeoutSeconds);
        set => ConnectionTimeoutSeconds = FormatNumericPresentationValue(value);
    }
    [ObservableProperty] private ThemeOption? _selectedThemeOption;
    [ObservableProperty] private PresentationOption<UiLanguagePreference>? _selectedLanguageOption;
    [ObservableProperty] private PresentationOption<SidebarPreference>? _selectedSidebarOption;

    /// <summary>
    /// Whether the window's backdrop is see-through. A plain switch rather than another dropdown:
    /// the other three preferences pick one of several values, this one is on or off, and
    /// PresentationOption is constrained to enums anyway.
    /// </summary>
    [ObservableProperty] private bool _isBackgroundTransparent = true;

    /// <summary>
    /// Whether the switch can be operated at all. The acrylic backdrop is only meaningful under the
    /// dark palette, so under the light one the control is disabled rather than silently doing
    /// nothing — and the hint beside it says why.
    /// </summary>
    [ObservableProperty] private bool _isTransparencyAvailable = true;

    /// <summary>
    /// What the switch shows, which is the backdrop actually in use rather than the stored choice.
    /// Under the light palette a disabled switch reading "on" would claim a transparency the window
    /// is not drawing, so it reads "off" there while <see cref="IsBackgroundTransparent"/> quietly
    /// keeps the preference for the return to dark. The setter is inert while the control is
    /// disabled, so nothing the user cannot reach can overwrite that preference.
    /// </summary>
    public bool IsTransparencyEffective
    {
        get => IsBackgroundTransparent && IsTransparencyAvailable;
        set
        {
            if (!IsTransparencyAvailable || value == IsTransparencyEffective) return;
            IsBackgroundTransparent = value;
        }
    }

    partial void OnIsTransparencyAvailableChanged(bool value) => OnPropertyChanged(nameof(IsTransparencyEffective));

    public void ApplyTransparencyAvailability(bool isEffectiveDark) => IsTransparencyAvailable = isEffectiveDark;
    [ObservableProperty] private bool _isSaving;
    [ObservableProperty] private string? _saveError;
    [ObservableProperty] private string? _loadError;
    [ObservableProperty] private bool _isSaved;
    [ObservableProperty] private bool _isSavingProfile;
    [ObservableProperty] private bool _isProfileSaved;
    [ObservableProperty] private string? _profileSaveError;
    [ObservableProperty] private string? _profileLoadError;
    [ObservableProperty] private string? _profileStatusMessage;
    [ObservableProperty] private RemoteCredentialStoreStatus _storedCredentialStatus = RemoteCredentialStoreStatus.NotFound;
    [ObservableProperty] private bool _isDirtyDraftDecisionVisible;
    [ObservableProperty] private bool _isTestingConnection;
    [ObservableProperty] private string? _connectionTestResultText;
    [ObservableProperty] private ProfileOperationTone _connectionTestTone = ProfileOperationTone.Neutral;
    [ObservableProperty] private ManagedNutConnectionTestStatus? _connectionTestStatus;

    public bool IsLanguageRestartRequired { get; private set; }

    public bool IsProfileDraftDirty => _isCreatingProfile || (_draftBaseProfile is not null && !ProfileDraft.Matches(_draftBaseProfile));

    public bool IsCreatingProfile => _isCreatingProfile;

    public bool CanPersistProfiles => _profileStore is not null && _canPersistProfiles && !IsSavingProfile;

    public bool CanSaveProfile => CanPersistProfiles && IsProfileDraftDirty && !_profileValidation.HasErrors;

    public bool CanSaveAll => !IsSaving && !IsSavingProfile &&
        (!IsProfileDraftDirty || CanSaveProfile);

    public bool CanDiscardAll => IsProfileDraftDirty || AreGeneralSettingsDirty;

    public bool CanDeleteSelectedProfile => CanPersistProfiles && SelectedManagedProfile is not null && ManagedProfiles.Count > 1 && SelectedManagedProfile.Id != _confirmedProfiles.ActiveProfileId;

    public bool CanActivateSelectedProfile => CanPersistProfiles && SelectedManagedProfile is not null && SelectedManagedProfile.Id != _confirmedProfiles.ActiveProfileId;

    public bool CanForgetTrustedHostKey => CanPersistProfiles && !IsProfileDraftDirty && SelectedManagedProfile is { Management.Mode: NutManagementMode.Remote, Management.TrustedHostKeyFingerprint: not null };

    public bool IsSelectedProfileActive => SelectedManagedProfile?.Id == _confirmedProfiles.ActiveProfileId;

    public string ActiveProfileName => _confirmedProfiles.ActiveProfile.Name;

    public bool IsActiveProfileRestartRequired => _confirmedProfiles.ActiveProfileId != _runtimeProfileId;

    public bool HasProfileLoadError => !string.IsNullOrWhiteSpace(ProfileLoadError);

    public bool HasProfileStatusMessage => !string.IsNullOrWhiteSpace(ProfileStatusMessage);

    /// <summary>
    /// Drops the "saved" banner on the way out.
    ///
    /// It is feedback for an action, not a property of the page, and an action that finished two
    /// screens ago has nothing left to report. Coming back to Settings and being told the settings
    /// were saved is confusing precisely because it is true — the user cannot tell whether it refers
    /// to what they just did or to something from ten minutes ago.
    ///
    /// Only the success messages are cleared, and only by matching the exact strings this page emits
    /// for them. Anything else in that field is a failure or a warning that has not been dealt with,
    /// and clearing it would hide a problem instead of tidying a banner.
    /// </summary>
    public override void OnDeactivated()
    {
        if (ProfileStatusMessage is not { } message) return;

        var transient =
            string.Equals(message, Localizer.Get("Profiles.SaveSuccess"), StringComparison.Ordinal) ||
            string.Equals(message, Localizer.Get("Profiles.DeleteSuccess"), StringComparison.Ordinal) ||
            string.Equals(message, Localizer.Get("Profiles.ActivateSuccess"), StringComparison.Ordinal) ||
            string.Equals(message, Localizer.Get("Settings.SaveSuccess"), StringComparison.Ordinal);

        if (transient) ProfileStatusMessage = null;
    }

    public bool HasProfileSaveError => !string.IsNullOrWhiteSpace(ProfileSaveError);

    public bool HasSaveError => !string.IsNullOrWhiteSpace(SaveError);

    public bool HasLoadError => !string.IsNullOrWhiteSpace(LoadError);

    public bool CanForgetStoredCredential => CanPersistProfiles && SelectedManagedProfile is not null && GetCredentialKind(SelectedManagedProfile) is not null;

    public bool CanTestConnection => _connectionTester is not null && !IsTestingConnection &&
        ManagedNutServerProfileValidator.ValidateHost(ProfileDraft.MonitoringHost).IsValid &&
        ManagedNutServerProfileValidator.ValidatePort(ProfileDraft.MonitoringPort).IsValid;

    public bool HasConnectionTestResult => !string.IsNullOrWhiteSpace(ConnectionTestResultText);

    public bool IsConnectionTestCritical => ConnectionTestTone == ProfileOperationTone.Critical && HasConnectionTestResult;

    public bool IsConnectionTestHealthy => ConnectionTestTone == ProfileOperationTone.Healthy && HasConnectionTestResult;

    public bool IsConnectionTestWarning => ConnectionTestTone == ProfileOperationTone.Warning && HasConnectionTestResult;

    public bool IsConnectionTestNeutral => ConnectionTestTone == ProfileOperationTone.Neutral && HasConnectionTestResult;

    public string StoredCredentialText => SelectedManagedProfile is { } profile && GetCredentialKind(profile) is { }
        ? StoredCredentialStatus switch
        {
            RemoteCredentialStoreStatus.Success => Localizer.Get("Credential.StoredYes"),
            RemoteCredentialStoreStatus.NotFound => Localizer.Get("Credential.StoredNo"),
            RemoteCredentialStoreStatus.Unsupported or RemoteCredentialStoreStatus.CredentialStoreUnavailable => Localizer.Get("Credential.Unavailable"),
            _ => Localizer.Get("Credential.QueryFailed")
        }
        : Localizer.Get("Credential.NotRequired");

    public IReadOnlyList<LocalizedValidationIssueViewModel> ProfileValidationIssues { get; private set; } = [];
    public IReadOnlyList<LocalizedValidationIssueViewModel> NameValidationIssues => IssuesFor(ManagedProfileFields.Name);
    public IReadOnlyList<LocalizedValidationIssueViewModel> MonitoringHostValidationIssues => IssuesFor(ManagedProfileFields.MonitoringHost);
    public IReadOnlyList<LocalizedValidationIssueViewModel> MonitoringPortValidationIssues => IssuesFor(ManagedProfileFields.MonitoringPort);
    public IReadOnlyList<LocalizedValidationIssueViewModel> PreferredUpsValidationIssues => IssuesFor(ManagedProfileFields.PreferredUpsName);
    public IReadOnlyList<LocalizedValidationIssueViewModel> ManagementHostValidationIssues => IssuesFor(ManagedProfileFields.ManagementHost);
    public IReadOnlyList<LocalizedValidationIssueViewModel> SshPortValidationIssues => IssuesFor(ManagedProfileFields.SshPort);
    public IReadOnlyList<LocalizedValidationIssueViewModel> SshUsernameValidationIssues => IssuesFor(ManagedProfileFields.SshUsername);
    public IReadOnlyList<LocalizedValidationIssueViewModel> SshPrivateKeyValidationIssues => IssuesFor(ManagedProfileFields.SshPrivateKeyPath);
    public IReadOnlyList<LocalizedValidationIssueViewModel> RemoteDirectoryValidationIssues => IssuesFor(ManagedProfileFields.RemoteConfigurationDirectory);
    public IReadOnlyList<LocalizedValidationIssueViewModel> SmbShareValidationIssues => IssuesFor(ManagedProfileFields.SmbSharePath);
    public IReadOnlyList<LocalizedValidationIssueViewModel> SmbDirectoryValidationIssues => IssuesFor(ManagedProfileFields.SmbConfigurationDirectory);
    public IReadOnlyList<LocalizedValidationIssueViewModel> SmbUsernameValidationIssues => IssuesFor(ManagedProfileFields.SmbUsername);

    public event Action<ThemePreference>? ThemeChanged;
    public event Action<SidebarPreference>? SidebarPreferenceChanged;
    public event Action<bool>? BackgroundTransparencyChanged;
    /// <summary>
    /// Raised only after the profile document has been persisted. Runtime consumers may use the
    /// confirmed profile to refresh presentation-only profile scope without treating an unsaved
    /// draft as authoritative.
    /// </summary>
    public event Action<ManagedNutServerProfile>? ProfilePersisted;

    [RelayCommand]
    private async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        IsSaving = true;
        IsSaved = false;
        SaveError = null;
        try
        {
            var settings = CreateSettings();
            if (_settingsStore is not null)
            {
                await _settingsStore.SaveAsync(settings, cancellationToken);
            }

            _confirmedSettings = settings;
            _canPersistThemeAutomatically = true;
            IsSaved = true;
            OnPropertyChanged(nameof(CanDiscardAll));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            SaveError = Localizer.Get("Settings.SaveError");
        }
        finally
        {
            IsSaving = false;
        }
    }

    [RelayCommand]
    private async Task SaveAllAsync(CancellationToken cancellationToken = default)
    {
        if (IsProfileDraftDirty && !await SaveProfileCoreAsync(cancellationToken))
        {
            return;
        }

        await SaveAsync(cancellationToken);

        // The unified action reports one success only. Keep non-success profile messages (for
        // example, a protected credential that could not be persisted) because those still need
        // the operator's attention.
        if (IsSaved && string.Equals(ProfileStatusMessage, Localizer.Get("Profiles.SaveSuccess"), StringComparison.Ordinal))
        {
            IsProfileSaved = false;
            ProfileStatusMessage = null;
        }
    }

    [RelayCommand]
    private void DiscardAll()
    {
        if (IsProfileDraftDirty)
        {
            DiscardProfileDraftCore();
        }

        PollingIntervalSeconds = _confirmedSettings.PollingInterval.TotalSeconds.ToString("0.##", CultureInfo.InvariantCulture);
        ConnectionTimeoutSeconds = _confirmedSettings.ConnectionTimeout.TotalSeconds.ToString("0.##", CultureInfo.InvariantCulture);
        IsSaved = false;
        IsProfileSaved = false;
        SaveError = null;
        ProfileSaveError = null;
        ProfileStatusMessage = null;
        OnPropertyChanged(nameof(CanDiscardAll));
    }

    [RelayCommand]
    private void NewServer()
    {
        if (QueueIfDirty(new PendingProfileAction(PendingProfileActionKind.NewProfile)))
        {
            return;
        }

        BeginCreate();
    }

    [RelayCommand]
    private async Task SaveProfileAsync(CancellationToken cancellationToken = default) =>
        _ = await SaveProfileCoreAsync(cancellationToken);

    private async Task<bool> SaveProfileCoreAsync(CancellationToken cancellationToken)
    {
        var mutator = _profileMutator;
        RefreshProfileValidation();
        if (mutator is null || !CanPersistProfiles)
        {
            ProfileSaveError = Localizer.Get("Profiles.PersistenceBlocked");
            return false;
        }

        if (_profileValidation.HasErrors || _profileValidation.Profile is null)
        {
            ProfileSaveError = Localizer.Get("Validation.Profile.FixErrors");
            return false;
        }

        IsSavingProfile = true;
        IsProfileSaved = false;
        ProfileSaveError = null;
        ProfileStatusMessage = null;
        try
        {
            var updated = _profileValidation.Profile;
            var document = _isCreatingProfile
                ? await mutator.CreateProfileAsync(updated, cancellationToken)
                : _draftBaseProfile is null
                    ? null
                    : await mutator.SaveExistingProfileAsync(_draftBaseProfile, updated, cancellationToken);
            if (document is null)
            {
                ProfileSaveError = Localizer.Get("Profiles.ConcurrentChange");
                return false;
            }

            var persistedProfile = document.Profiles.Single(profile => profile.Id == updated.Id);
            ApplyConfirmedProfiles(document, persistedProfile.Id);
            ProfilePersisted?.Invoke(persistedProfile);
            IsProfileSaved = true;
            ProfileStatusMessage = Localizer.Get("Profiles.SaveSuccess");

            // Only now, with the profile actually persisted, may a remembered credential be written.
            // The profile store and the Credential Manager are separate stores and there is no
            // transaction across them, so the order is chosen instead: the profile first, and a
            // failure to write the secret afterwards is reported rather than papered over.
            await CommitAgentCredentialAsync(updated.Id, cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ManagedProfilePersistenceAfterCredentialRemovalException)
        {
            ProfileSaveError = Localizer.Get("Profiles.SaveAfterCredentialRemovalFailed");
            return false;
        }
        catch (ManagedProfileCredentialRemovalException)
        {
            ProfileSaveError = Localizer.Get("Profiles.CredentialRemovalFailed");
            return false;
        }
        catch (Exception)
        {
            ProfileSaveError = Localizer.Get("Profiles.SaveFailed");
            return false;
        }
        finally
        {
            IsSavingProfile = false;
            NotifyProfilePropertiesChanged();
        }
    }

    [RelayCommand]
    private async Task DeleteSelectedProfileAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedManagedProfile is null)
        {
            return;
        }

        if (QueueIfDirty(new PendingProfileAction(PendingProfileActionKind.DeleteProfile, SelectedManagedProfile.Id)))
        {
            return;
        }

        await DeleteProfileCoreAsync(SelectedManagedProfile.Id, cancellationToken);
    }

    private async Task DeleteProfileCoreAsync(Guid profileId, CancellationToken cancellationToken)
    {
        var mutator = _profileMutator;
        if (mutator is null || !CanPersistProfiles || profileId == _confirmedProfiles.ActiveProfileId || ManagedProfiles.Count <= 1)
        {
            return;
        }

        try
        {
            var document = await mutator.DeleteProfileAsync(profileId, cancellationToken);
            if (document is null)
            {
                ProfileSaveError = Localizer.Get("Profiles.DeleteConcurrentChange");
                return;
            }

            ApplyConfirmedProfiles(document, document.ActiveProfileId);
            ProfileStatusMessage = Localizer.Get("Profiles.DeleteSuccess");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ManagedProfilePersistenceAfterCredentialRemovalException)
        {
            ProfileSaveError = Localizer.Get("Profiles.DeleteAfterCredentialRemovalFailed");
        }
        catch (ManagedProfileCredentialRemovalException)
        {
            ProfileSaveError = Localizer.Get("Profiles.DeleteCredentialRemovalFailed");
        }
        catch (Exception)
        {
            ProfileSaveError = Localizer.Get("Profiles.DeleteFailed");
        }
    }

    [RelayCommand]
    private async Task ActivateSelectedProfileAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedManagedProfile is null)
        {
            return;
        }

        if (QueueIfDirty(new PendingProfileAction(PendingProfileActionKind.ActivateProfile, SelectedManagedProfile.Id)))
        {
            return;
        }

        await ActivateProfileCoreAsync(SelectedManagedProfile.Id, cancellationToken);
    }

    private async Task ActivateProfileCoreAsync(Guid profileId, CancellationToken cancellationToken)
    {
        var mutator = _profileMutator;
        if (mutator is null || !CanPersistProfiles || profileId == _confirmedProfiles.ActiveProfileId)
        {
            return;
        }

        try
        {
            var document = await mutator.ActivateProfileAsync(profileId, cancellationToken);
            if (document is null)
            {
                ProfileSaveError = Localizer.Get("Profiles.ActivateConcurrentChange");
                return;
            }

            ApplyConfirmedProfiles(document, profileId);
            ProfileStatusMessage = Localizer.Get("Profiles.ActivateSuccess");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            ProfileSaveError = Localizer.Get("Profiles.ActivateFailed");
        }
    }

    [RelayCommand]
    private async Task SaveDirtyDraftAndContinueAsync(CancellationToken cancellationToken = default)
    {
        var pending = _pendingProfileAction;
        if (pending is null || !await SaveProfileCoreAsync(cancellationToken))
        {
            return;
        }

        ClearPendingProfileAction();
        await ExecutePendingProfileActionAsync(pending, cancellationToken);
    }

    [RelayCommand]
    private async Task DiscardDirtyDraftAndContinueAsync(CancellationToken cancellationToken = default)
    {
        var pending = _pendingProfileAction;
        if (pending is null)
        {
            return;
        }

        DiscardProfileDraftCore();
        ClearPendingProfileAction();
        await ExecutePendingProfileActionAsync(pending, cancellationToken);
    }

    [RelayCommand]
    private void ContinueEditing() => ClearPendingProfileAction();

    [RelayCommand]
    private void DiscardProfileDraft() => DiscardProfileDraftCore();

    [RelayCommand]
    private async Task TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        var tester = _connectionTester;
        var host = ManagedNutServerProfileValidator.ValidateHost(ProfileDraft.MonitoringHost);
        var port = ManagedNutServerProfileValidator.ValidatePort(ProfileDraft.MonitoringPort);
        if (tester is null || host.Value is null || port.HasErrors)
        {
            ConnectionTestStatus = ManagedNutConnectionTestStatus.Failed;
            ConnectionTestTone = ProfileOperationTone.Critical;
            ConnectionTestResultText = Localizer.Get("ConnectionTest.InvalidFields");
            return;
        }

        var version = _draftVersion;
        IsTestingConnection = true;
        ConnectionTestResultText = Localizer.Get("ConnectionTest.Running");
        ConnectionTestTone = ProfileOperationTone.Warning;
        try
        {
            var endpoint = new NutEndpoint(host.Value, port.Value, _confirmedSettings.ConnectionTimeout);
            var result = await tester.TestAsync(endpoint, NormalizeOptional(ProfileDraft.PreferredUpsName), cancellationToken);
            if (version != _draftVersion)
            {
                return;
            }

            ConnectionTestStatus = result.Status;
            ConnectionTestTone = result.Status switch
            {
                ManagedNutConnectionTestStatus.Success => ProfileOperationTone.Healthy,
                ManagedNutConnectionTestStatus.Cancelled => ProfileOperationTone.Neutral,
                _ => ProfileOperationTone.Critical
            };
            ConnectionTestResultText = Localizer.Get(result.Status switch
            {
                ManagedNutConnectionTestStatus.Success => "ConnectionTest.Success",
                ManagedNutConnectionTestStatus.EndpointUnreachable => "ConnectionTest.Unreachable",
                ManagedNutConnectionTestStatus.Timeout => "ConnectionTest.Timeout",
                ManagedNutConnectionTestStatus.ProtocolError => "ConnectionTest.ProtocolError",
                ManagedNutConnectionTestStatus.NoUpsDiscovered => "ConnectionTest.NoUps",
                ManagedNutConnectionTestStatus.PreferredUpsMissing => "ConnectionTest.PreferredUpsMissing",
                ManagedNutConnectionTestStatus.Cancelled => "ConnectionTest.Cancelled",
                _ => "ConnectionTest.Failed"
            });
        }
        finally
        {
            IsTestingConnection = false;
        }
    }

    [RelayCommand]
    private async Task ForgetTrustedHostKeyAsync(CancellationToken cancellationToken = default)
    {
        var profile = SelectedManagedProfile;
        if (_profileMutator is null || profile is null || !CanForgetTrustedHostKey)
        {
            return;
        }

        IsSavingProfile = true;
        ProfileSaveError = null;
        try
        {
            var updated = await _profileMutator.ForgetTrustedHostKeyAsync(profile, cancellationToken);
            var document = updated is null ? null : await _profileMutator.LoadCurrentAsync(cancellationToken);
            if (document is null || updated is null)
            {
                ProfileSaveError = Localizer.Get("Profiles.HostKeyConcurrentChange");
                return;
            }

            ApplyConfirmedProfiles(document, updated.Id);
            ProfileStatusMessage = Localizer.Get("Profiles.HostKeyForgotten");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            ProfileSaveError = Localizer.Get("Profiles.HostKeyForgetFailed");
        }
        finally
        {
            IsSavingProfile = false;
            NotifyProfilePropertiesChanged();
        }
    }

    /// <summary>
    /// Collects a credential and proves it against the agent before anything is remembered.
    ///
    /// The generation guard is the same one the monitor uses: a handshake can outlive the draft it
    /// was started for, and publishing a late answer into a profile the operator has since changed
    /// would attach an account to the wrong server.
    /// </summary>
    [RelayCommand]
    private async Task AuthenticateAgentCredentialAsync(CancellationToken cancellationToken = default)
    {
        if (_agentCredentials is null || !CanAuthenticateAgentCredential) return;

        var profileId = ProfileDraft.Id;
        var endpoint = ProfileDraft.AgentHttpsEndpoint!;
        var generation = ++_agentCredentialGeneration;

        IsAuthenticatingAgentCredential = true;
        try
        {
            var result = await _agentCredentials.AuthenticateAsync(
                profileId,
                endpoint,
                ProfileDraft.AgentUsername,
                Localizer.Get("Agent.Credential.PromptCaption"),
                Localizer.Get("Agent.Credential.PromptMessage"),
                CredentialPromptOwnerWindowHandle,
                cancellationToken);

            if (generation != _agentCredentialGeneration) return;

            // Cancelling changes nothing at all: not the draft, not the status, not the store.
            if (result.Outcome == NutAgentCredentialOutcome.Cancelled) return;

            AgentCredentialOutcome = result.Outcome;

            if (result.IsValidated)
            {
                ValidatedAgentAccount = result.Username;
                ProfileDraft.AgentUsername = result.Username;

                // An existing, unchanged profile already owns this exact endpoint/account binding.
                // Persist immediately when requested so closing the application after authenticating
                // cannot silently discard the validated credential. New profiles and edited agent
                // identities still wait for Save, which prevents orphaned or mis-bound secrets.
                if (RememberAgentCredential &&
                    _credentialStore is not null &&
                    CanPersistAgentCredentialImmediately(profileId, endpoint, result.Username!))
                {
                    var persisted = await _agentCredentials.PersistAsync(
                        profileId,
                        result.Username!,
                        _credentialStore,
                        cancellationToken);
                    HasStoredAgentCredential = persisted;
                    if (!persisted)
                    {
                        ProfileStatusMessage = Localizer.Get("Agent.Credential.SaveFailed");
                    }
                }
            }
        }
        finally
        {
            if (generation == _agentCredentialGeneration) IsAuthenticatingAgentCredential = false;
        }
    }

    private bool CanPersistAgentCredentialImmediately(Guid profileId, string endpoint, string username)
    {
        if (_isCreatingProfile || _draftBaseProfile?.Id != profileId)
        {
            return false;
        }

        var saved = _confirmedProfiles.Profiles.SingleOrDefault(profile => profile.Id == profileId);
        return saved is not null &&
            saved.Management.Agent.Transport == NutAgentTransportKind.Https &&
            saved.Management.Agent.Authentication == NutAgentAuthenticationMode.AlternateWindowsAccount &&
            string.Equals(saved.Management.Agent.HttpsEndpoint, endpoint, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(saved.Management.Agent.Username, username, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Removes the agent credential for this profile, and only that one. The SMB and SSH secrets
    /// authorize different things and are left exactly where they are.
    /// </summary>
    [RelayCommand]
    private async Task ForgetAgentCredentialAsync(CancellationToken cancellationToken = default)
    {
        if (_agentCredentials is null || !CanForgetAgentCredential) return;

        var profileId = ProfileDraft.Id;
        _agentCredentials.ForgetSession(profileId);
        ValidatedAgentAccount = null;
        AgentCredentialOutcome = null;

        if (_credentialStore is not null)
        {
            await _credentialStore.DeleteAsync(profileId, RemoteCredentialKind.WindowsAgentPassword, cancellationToken);
        }

        HasStoredAgentCredential = false;
        ProfileStatusMessage = Localizer.Get("Credential.ForgetSuccess");
    }

    /// <summary>
    /// Writes the validated credential once the profile it belongs to actually exists.
    ///
    /// Deliberately not done at authentication time: a credential stored for a profile the operator
    /// then cancels is an orphan in the Credential Manager that nobody will think to remove.
    /// </summary>
    private async Task CommitAgentCredentialAsync(Guid profileId, CancellationToken cancellationToken)
    {
        if (_agentCredentials is null || _credentialStore is null) return;
        if (!RememberAgentCredential || ValidatedAgentAccount is not { } account) return;

        var persisted = await _agentCredentials.PersistAsync(profileId, account, _credentialStore, cancellationToken);
        HasStoredAgentCredential = persisted;

        // The profile is already saved at this point, so claiming success would leave it pointing at
        // an account whose secret is only in memory. Saying so is the only honest report.
        if (!persisted) ProfileStatusMessage = Localizer.Get("Agent.Credential.SaveFailed");
    }

    /// <summary>Rebuilds the credential status when the selected profile changes.</summary>
    private async Task RefreshAgentCredentialStatusAsync(CancellationToken cancellationToken = default)
    {
        ValidatedAgentAccount = null;
        AgentCredentialOutcome = null;
        HasStoredAgentCredential = false;

        var profile = SelectedManagedProfile;
        if (profile is null || _credentialStore is null) return;

        if (_agentCredentials is not null && _agentCredentials.HasSessionCredential(profile.Id, out var session))
        {
            ValidatedAgentAccount = session;
        }

        var stored = await _credentialStore.ContainsAsync(profile.Id, RemoteCredentialKind.WindowsAgentPassword, cancellationToken);
        HasStoredAgentCredential = stored.IsSuccess;
    }

    [RelayCommand]
    private async Task ForgetStoredCredentialAsync(CancellationToken cancellationToken = default)
    {
        var profile = SelectedManagedProfile;
        var kind = profile is null ? null : GetCredentialKind(profile);
        if (_profileMutator is null || profile is null || kind is null || !CanForgetStoredCredential)
        {
            return;
        }

        var result = await _profileMutator.ForgetCredentialAsync(profile.Id, kind.Value, cancellationToken);
        StoredCredentialStatus = result.IsSuccess ? RemoteCredentialStoreStatus.NotFound : result.Status;
        ProfileStatusMessage = result.IsSuccess ? Localizer.Get("Credential.ForgetSuccess") : Localizer.Get("Credential.ForgetFailed");
    }

    public async Task RefreshStoredCredentialStatusAsync(CancellationToken cancellationToken = default)
    {
        var profile = SelectedManagedProfile;
        var kind = profile is null ? null : GetCredentialKind(profile);
        if (kind is null)
        {
            StoredCredentialStatus = RemoteCredentialStoreStatus.NotFound;
            return;
        }

        if (_credentialStore is null)
        {
            StoredCredentialStatus = RemoteCredentialStoreStatus.Unsupported;
            return;
        }

        var result = await _credentialStore.ContainsAsync(profile!.Id, kind.Value, cancellationToken);
        if (SelectedManagedProfile?.Id == profile.Id)
        {
            StoredCredentialStatus = result.Status;
        }
    }

    /// <summary>
    /// Both credential statuses, which is what startup actually needs.
    ///
    /// They are two lifecycles over two stores answering two questions — one authorizes reading the
    /// configuration files, the other authorizes controlling the service — and the bootstrap used to
    /// refresh only the first. The agent's status therefore stayed at its constructed default, so a
    /// profile whose password was sitting in the Credential Manager, and which the agent client had
    /// already used to connect, reported no credential until the operator happened to switch
    /// profiles. Selection refreshed both; opening the application did not.
    /// </summary>
    public async Task RefreshCredentialStatusesAsync(CancellationToken cancellationToken = default)
    {
        await RefreshStoredCredentialStatusAsync(cancellationToken);
        await RefreshAgentCredentialStatusAsync(cancellationToken);
    }

    public ApplicationSettings CreateSettings() => new(
        pollingInterval: TimeSpan.FromSeconds(double.Parse(PollingIntervalSeconds, CultureInfo.InvariantCulture)),
        connectionTimeout: TimeSpan.FromSeconds(double.Parse(ConnectionTimeoutSeconds, CultureInfo.InvariantCulture)),
        theme: SelectedThemeOption?.Preference ?? ThemePreference.System,
        mockMode: false,
        language: SelectedLanguageOption?.Value ?? UiLanguagePreference.PtBr,
        sidebarPreference: SelectedSidebarOption?.Value ?? SidebarPreference.Expanded);

    private bool AreGeneralSettingsDirty =>
        !string.Equals(
            PollingIntervalSeconds,
            _confirmedSettings.PollingInterval.TotalSeconds.ToString("0.##", CultureInfo.InvariantCulture),
            StringComparison.Ordinal) ||
        !string.Equals(
            ConnectionTimeoutSeconds,
            _confirmedSettings.ConnectionTimeout.TotalSeconds.ToString("0.##", CultureInfo.InvariantCulture),
            StringComparison.Ordinal);

    public void Apply(ApplicationSettings settings)
    {
        PollingIntervalSeconds = settings.PollingInterval.TotalSeconds.ToString("0.##", CultureInfo.InvariantCulture);
        ConnectionTimeoutSeconds = settings.ConnectionTimeout.TotalSeconds.ToString("0.##", CultureInfo.InvariantCulture);
        Localizer = new NutManagerLocalizer(settings.Language);
    }

    public async Task PersistThemeAsync(ThemePreference theme, CancellationToken cancellationToken = default)
    {
        if (_settingsStore is null || !_canPersistThemeAutomatically)
        {
            return;
        }

        var settings = CopyConfirmedSettings(theme: theme);
        try
        {
            await _settingsStore.SaveAsync(settings, cancellationToken);
            _confirmedSettings = settings;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            SaveError = Localizer.Get("Appearance.SaveError");
        }
    }

    public void SetLoadError(string message)
    {
        LoadError = message;
        _canPersistThemeAutomatically = false;
    }

    public void SetProfileLoadError(string message, bool blockPersistence = false)
    {
        ProfileLoadError = message;
        _canPersistProfiles = !blockPersistence;
        NotifyProfilePropertiesChanged();
    }

    public void ApplyTheme(ThemePreference theme)
    {
        var option = ThemeOptions.Single(option => option.Preference == theme);
        if (!Equals(SelectedThemeOption, option))
        {
            SelectedThemeOption = option;
        }
    }

    partial void OnSelectedThemeOptionChanged(ThemeOption? value)
    {
        if (value is not null)
        {
            ThemeChanged?.Invoke(value.Preference);
        }
    }

    public void ApplySidebarPreference(SidebarPreference preference)
    {
        var option = SidebarOptions.Single(option => option.Value == preference);
        if (!Equals(SelectedSidebarOption, option))
        {
            SelectedSidebarOption = option;
        }
    }

    partial void OnSelectedLanguageOptionChanged(PresentationOption<UiLanguagePreference>? value)
    {
        if (value is null || _isApplyingVisualPreferences)
        {
            return;
        }

        IsLanguageRestartRequired = value.Value != Localizer.Language;
        OnPropertyChanged(nameof(IsLanguageRestartRequired));
        _ = PersistVisualPreferencesAsync(value.Value, SelectedSidebarOption?.Value ?? SidebarPreference.Expanded);
    }

    partial void OnIsBackgroundTransparentChanged(bool value)
    {
        OnPropertyChanged(nameof(IsTransparencyEffective));
        if (_isApplyingVisualPreferences)
        {
            return;
        }

        BackgroundTransparencyChanged?.Invoke(value);
        _ = PersistVisualPreferencesAsync(
            SelectedLanguageOption?.Value ?? UiLanguagePreference.PtBr,
            SelectedSidebarOption?.Value ?? SidebarPreference.Expanded);
    }

    partial void OnSelectedSidebarOptionChanged(PresentationOption<SidebarPreference>? value)
    {
        if (value is null || _isApplyingVisualPreferences)
        {
            return;
        }

        SidebarPreferenceChanged?.Invoke(value.Value);
        _ = PersistVisualPreferencesAsync(SelectedLanguageOption?.Value ?? UiLanguagePreference.PtBr, value.Value);
    }

    public async Task PersistVisualPreferencesAsync(
        UiLanguagePreference language,
        SidebarPreference sidebarPreference,
        CancellationToken cancellationToken = default)
    {
        if (_settingsStore is null)
        {
            return;
        }

        var settings = CopyConfirmedSettings(
            language: language,
            sidebarPreference: sidebarPreference,
            backgroundTransparency: IsBackgroundTransparent);
        try
        {
            await _settingsStore.SaveAsync(settings, cancellationToken);
            _confirmedSettings = settings;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            SaveError = Localizer.Get("Appearance.SaveError");
        }
    }

    private ApplicationSettings CopyConfirmedSettings(
        ThemePreference? theme = null,
        UiLanguagePreference? language = null,
        SidebarPreference? sidebarPreference = null,
        bool? backgroundTransparency = null) => new(
            _confirmedSettings.SchemaVersion,
            _confirmedSettings.PollingInterval,
            _confirmedSettings.ConnectionTimeout,
            theme ?? _confirmedSettings.Theme,
            mockMode: false,
            language ?? _confirmedSettings.Language,
            sidebarPreference ?? _confirmedSettings.SidebarPreference,
            backgroundTransparency: backgroundTransparency ?? _confirmedSettings.BackgroundTransparency);

    private void RequestProfileSelection(ManagedNutServerProfile? value)
    {
        if (value?.Id == _selectedManagedProfile?.Id)
        {
            return;
        }

        if (value is not null && QueueIfDirty(new PendingProfileAction(PendingProfileActionKind.SelectProfile, value.Id)))
        {
            NotifySelectionChanged();
            return;
        }

        if (value is not null)
        {
            SelectProfile(value);
        }
    }

    private void SelectProfile(ManagedNutServerProfile profile)
    {
        _isCreatingProfile = false;
        _draftSourceId = profile.Id;
        _draftBaseProfile = profile;
        ProfileDraft.Apply(profile);
        _selectedManagedProfile = profile;
        _selectedProfileCard = ManagedProfileCards.FirstOrDefault(card => card.Profile.Id == profile.Id);
        ProfileSaveError = null;
        NotifySelectionChanged();
        NotifyProfilePropertiesChanged();
        _ = RefreshCredentialStatusesAsync();
    }

    private void BeginCreate()
    {
        ProfileDraft.CopyFrom(ManagedNutServerProfileDraftViewModel.CreateNew());
        _isCreatingProfile = true;
        _draftSourceId = null;
        _draftBaseProfile = null;
        _selectedManagedProfile = null;
        _selectedProfileCard = null;
        ProfileSaveError = null;
        ProfileStatusMessage = Localizer.Get("Profiles.NewServerHelp");
        NotifySelectionChanged();
        NotifyProfilePropertiesChanged();
        _ = RefreshCredentialStatusesAsync();
    }

    private void DiscardProfileDraftCore()
    {
        var profile = _draftSourceId is { } id
            ? ManagedProfiles.FirstOrDefault(candidate => candidate.Id == id)
            : _confirmedProfiles.ActiveProfile;
        if (profile is not null)
        {
            SelectProfile(profile);
        }
    }

    private bool QueueIfDirty(PendingProfileAction action)
    {
        if (!IsProfileDraftDirty)
        {
            return false;
        }

        _pendingProfileAction = action;
        IsDirtyDraftDecisionVisible = true;
        return true;
    }

    private void ClearPendingProfileAction()
    {
        _pendingProfileAction = null;
        IsDirtyDraftDecisionVisible = false;
    }

    private async Task ExecutePendingProfileActionAsync(PendingProfileAction action, CancellationToken cancellationToken)
    {
        switch (action.Kind)
        {
            case PendingProfileActionKind.NewProfile:
                BeginCreate();
                break;
            case PendingProfileActionKind.SelectProfile when action.ProfileId is { } selectedId:
                SelectProfile(ManagedProfiles.Single(profile => profile.Id == selectedId));
                break;
            case PendingProfileActionKind.DeleteProfile when action.ProfileId is { } deletedId:
                await DeleteProfileCoreAsync(deletedId, cancellationToken);
                break;
            case PendingProfileActionKind.ActivateProfile when action.ProfileId is { } activeId:
                await ActivateProfileCoreAsync(activeId, cancellationToken);
                break;
        }
    }

    private void ApplyConfirmedProfiles(ManagedNutServerProfiles document, Guid selectedId)
    {
        _confirmedProfiles = document;
        ManagedProfiles.Clear();
        foreach (var profile in document.Profiles)
        {
            ManagedProfiles.Add(profile);
        }

        RebuildProfileCards();
        SelectProfile(ManagedProfiles.Single(profile => profile.Id == selectedId));
    }

    private void RebuildProfileCards()
    {
        ManagedProfileCards.Clear();
        foreach (var profile in ManagedProfiles)
        {
            ManagedProfileCards.Add(new ManagedProfileCardViewModel(
                profile,
                $"{profile.Monitoring.Host}:{profile.Monitoring.Port.ToString(CultureInfo.InvariantCulture)}",
                Localizer.Get(profile.Management.Mode == NutManagementMode.Local ? "Management.Local" : "Management.Remote"),
                Localizer.Get(profile.AccessMode == ManagedNutServerAccessMode.Manage ? "Access.Manage" : "Access.ReadOnly"),
                profile.Management.Mode == NutManagementMode.Remote
                    ? Localizer.Get(profile.Management.ConfigurationTransport == RemoteConfigurationTransportKind.Smb ? "Transport.Smb" : "Transport.Sftp")
                    : null,
                profile.Id == _confirmedProfiles.ActiveProfileId,
                Localizer.Get("Profiles.ActiveBadge")));
        }

        _selectedProfileCard = _selectedManagedProfile is null
            ? null
            : ManagedProfileCards.FirstOrDefault(card => card.Profile.Id == _selectedManagedProfile.Id);
        NotifySelectionChanged();
    }

    private void OnProfileDraftPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        _draftVersion++;
        ConnectionTestResultText = null;
        ConnectionTestStatus = null;
        OnPropertyChanged(nameof(SelectedManagementModeOption));
        OnPropertyChanged(nameof(SelectedAccessModeOption));
        OnPropertyChanged(nameof(SelectedConfigurationTransportOption));
        OnPropertyChanged(nameof(SelectedSshAuthenticationOption));
        OnPropertyChanged(nameof(SelectedSmbAuthenticationOption));
        OnPropertyChanged(nameof(SelectedAgentTransportOption));
        OnPropertyChanged(nameof(SelectedAgentAuthenticationOption));
        OnPropertyChanged(nameof(AgentAccountStatusText));

        // The credential surface reads the draft — which account, which transport, whether the
        // endpoint is usable — so it has to be re-evaluated with it. Leaving these out is what left
        // "Sign in" disabled beside a perfectly valid endpoint.
        OnPropertyChanged(nameof(AgentCredentialStatusText));
        OnPropertyChanged(nameof(AgentAuthenticateText));
        OnPropertyChanged(nameof(CanAuthenticateAgentCredential));
        OnPropertyChanged(nameof(CanForgetAgentCredential));
        AuthenticateAgentCredentialCommand.NotifyCanExecuteChanged();
        ForgetAgentCredentialCommand.NotifyCanExecuteChanged();
        RefreshProfileValidation();
        NotifyProfilePropertiesChanged();
    }

    private void RefreshProfileValidation()
    {
        _profileValidation = ProfileDraft.Validate(_confirmedProfiles.Profiles);
        ProfileValidationIssues = _profileValidation.Issues
            .Select(issue => new LocalizedValidationIssueViewModel(
                issue.Field,
                issue.Code,
                issue.Severity,
                Localizer.Get(issue.ResourceKey)))
            .ToArray();
        OnPropertyChanged(nameof(ProfileValidationIssues));
        OnPropertyChanged(nameof(NameValidationIssues));
        OnPropertyChanged(nameof(MonitoringHostValidationIssues));
        OnPropertyChanged(nameof(MonitoringPortValidationIssues));
        OnPropertyChanged(nameof(PreferredUpsValidationIssues));
        OnPropertyChanged(nameof(ManagementHostValidationIssues));
        OnPropertyChanged(nameof(SshPortValidationIssues));
        OnPropertyChanged(nameof(SshUsernameValidationIssues));
        OnPropertyChanged(nameof(SshPrivateKeyValidationIssues));
        OnPropertyChanged(nameof(RemoteDirectoryValidationIssues));
        OnPropertyChanged(nameof(SmbShareValidationIssues));
        OnPropertyChanged(nameof(SmbDirectoryValidationIssues));
        OnPropertyChanged(nameof(SmbUsernameValidationIssues));
        OnPropertyChanged(nameof(CanSaveProfile));
        OnPropertyChanged(nameof(CanSaveAll));
        OnPropertyChanged(nameof(CanDiscardAll));
        OnPropertyChanged(nameof(CanTestConnection));
    }

    private IReadOnlyList<LocalizedValidationIssueViewModel> IssuesFor(string field) =>
        ProfileValidationIssues.Where(issue => issue.Field == field).ToArray();

    private void NotifyProfilePropertiesChanged()
    {
        OnPropertyChanged(nameof(IsProfileDraftDirty));
        OnPropertyChanged(nameof(IsCreatingProfile));
        OnPropertyChanged(nameof(EditorTitle));
        OnPropertyChanged(nameof(CanDeleteSelectedProfile));
        OnPropertyChanged(nameof(CanActivateSelectedProfile));
        OnPropertyChanged(nameof(CanForgetTrustedHostKey));
        OnPropertyChanged(nameof(CanPersistProfiles));
        OnPropertyChanged(nameof(CanSaveProfile));
        OnPropertyChanged(nameof(CanSaveAll));
        OnPropertyChanged(nameof(CanDiscardAll));
        OnPropertyChanged(nameof(IsSelectedProfileActive));
        OnPropertyChanged(nameof(ActiveProfileName));
        OnPropertyChanged(nameof(RuntimeProfileName));
        OnPropertyChanged(nameof(PersistedActiveProfileName));
        OnPropertyChanged(nameof(IsActiveProfileRestartRequired));
        OnPropertyChanged(nameof(CanForgetStoredCredential));
        OnPropertyChanged(nameof(StoredCredentialText));
    }

    partial void OnPollingIntervalSecondsChanged(string value)
    {
        IsSaved = false;
        OnPropertyChanged(nameof(PollingIntervalSecondsValue));
        OnPropertyChanged(nameof(CanDiscardAll));
    }

    partial void OnConnectionTimeoutSecondsChanged(string value)
    {
        IsSaved = false;
        OnPropertyChanged(nameof(ConnectionTimeoutSecondsValue));
        OnPropertyChanged(nameof(CanDiscardAll));
    }

    private static decimal? ParseNumericPresentationValue(string value) =>
        decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    private static string FormatNumericPresentationValue(decimal? value) =>
        value?.ToString("0.##", CultureInfo.InvariantCulture) ?? string.Empty;

    partial void OnIsSavingChanged(bool value) => OnPropertyChanged(nameof(CanSaveAll));

    partial void OnIsSavingProfileChanged(bool value) => OnPropertyChanged(nameof(CanSaveAll));

    private void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedManagedProfile));
        OnPropertyChanged(nameof(SelectedProfileCard));
    }

    partial void OnStoredCredentialStatusChanged(RemoteCredentialStoreStatus value) =>
        OnPropertyChanged(nameof(StoredCredentialText));

    partial void OnIsTestingConnectionChanged(bool value)
    {
        OnPropertyChanged(nameof(CanTestConnection));
    }

    partial void OnConnectionTestResultTextChanged(string? value) =>
        NotifyConnectionTestPresentationChanged();

    partial void OnConnectionTestToneChanged(ProfileOperationTone value) =>
        NotifyConnectionTestPresentationChanged();

    partial void OnProfileStatusMessageChanged(string? value) =>
        OnPropertyChanged(nameof(HasProfileStatusMessage));

    partial void OnProfileSaveErrorChanged(string? value) =>
        OnPropertyChanged(nameof(HasProfileSaveError));

    partial void OnSaveErrorChanged(string? value) =>
        OnPropertyChanged(nameof(HasSaveError));

    partial void OnLoadErrorChanged(string? value) =>
        OnPropertyChanged(nameof(HasLoadError));

    partial void OnProfileLoadErrorChanged(string? value) =>
        OnPropertyChanged(nameof(HasProfileLoadError));

    private static RemoteCredentialKind? GetCredentialKind(ManagedNutServerProfile profile)
    {
        var management = profile.Management;
        if (management.Mode != NutManagementMode.Remote)
        {
            return null;
        }

        if (management.ConfigurationTransport == RemoteConfigurationTransportKind.Smb)
        {
            return management.SmbAuthenticationMode == SmbAuthenticationMode.ExplicitCredentials
                ? RemoteCredentialKind.SmbPassword
                : null;
        }

        return management.SshAuthenticationMode == SshAuthenticationMode.PrivateKey && !string.IsNullOrWhiteSpace(management.SshPrivateKeyPath)
            ? RemoteCredentialKind.SshPrivateKeyPassphrase
            : RemoteCredentialKind.SshPassword;
    }

    private static string? NormalizeOptional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Localize(ApplicationSettings settings, string key) =>
        new NutManagerLocalizer(settings?.Language ?? UiLanguagePreference.PtBr).Get(key);

    private void NotifyConnectionTestPresentationChanged()
    {
        OnPropertyChanged(nameof(HasConnectionTestResult));
        OnPropertyChanged(nameof(IsConnectionTestCritical));
        OnPropertyChanged(nameof(IsConnectionTestHealthy));
        OnPropertyChanged(nameof(IsConnectionTestWarning));
        OnPropertyChanged(nameof(IsConnectionTestNeutral));
    }

    private sealed record PendingProfileAction(PendingProfileActionKind Kind, Guid? ProfileId = null);

    private enum PendingProfileActionKind
    {
        NewProfile,
        SelectProfile,
        DeleteProfile,
        ActivateProfile
    }
}

public sealed record LocalizedValidationIssueViewModel(
    string Field,
    string Code,
    ValidationSeverity Severity,
    string Message)
{
    public bool IsError => Severity == ValidationSeverity.Error;
    public bool IsWarning => Severity == ValidationSeverity.Warning;
    public bool IsInfo => Severity == ValidationSeverity.Info;
}

public sealed record ManagedProfileCardViewModel(
    ManagedNutServerProfile Profile,
    string Endpoint,
    string ManagementMode,
    string AccessMode,
    string? Transport,
    bool IsActive,
    string ActiveText)
{
    public string Name => Profile.Name;
    public bool HasTransport => !string.IsNullOrWhiteSpace(Transport);
}

public enum ProfileOperationTone
{
    Neutral,
    Healthy,
    Warning,
    Critical
}
