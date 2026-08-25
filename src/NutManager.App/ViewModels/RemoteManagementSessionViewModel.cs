using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using NutManager.App.Localization;
using NutManager.App.Services;
using NutManager.Core.Configuration;
using NutManager.Core.Models;
using NutManager.Core.Services;
using NutManager.Infrastructure.Configuration;

namespace NutManager.App.ViewModels;

public sealed partial class RemoteManagementSessionViewModel : ObservableObject, IAsyncDisposable
{
    private ManagedNutServerProfile _profile;
    private readonly IRemoteNutConfigurationTransport _transport;
    private readonly ManagedNutServerProfileUpdateService? _profileUpdater;
    private readonly IRemoteCredentialStore? _credentialStore;
    private readonly IWindowsCredentialPrompt? _credentialPrompt;
    private readonly NutManagerLocalizer _strings;
    private IRemoteNutConfigurationSession? _session;
    private RemoteNutDirectoryValidationResult? _directoryValidation;

    public RemoteManagementSessionViewModel(
        ManagedNutServerProfile profile,
        IRemoteNutConfigurationTransport transport,
        ManagedNutServerProfileUpdateService? profileUpdater = null,
        IRemoteCredentialStore? credentialStore = null,
        UiLanguagePreference language = UiLanguagePreference.PtBr,
        IWindowsCredentialPrompt? credentialPrompt = null)
    {
        if (profile.Management.Mode != NutManagementMode.Remote)
        {
            throw new ArgumentException("A remote profile is required.", nameof(profile));
        }

        _profile = profile;
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _profileUpdater = profileUpdater;
        _credentialStore = credentialStore;
        _credentialPrompt = credentialPrompt;
        _strings = new NutManagerLocalizer(language);
        DirectoryEntries = new ObservableCollection<RemoteNutDirectoryEntry>();
        CurrentDirectory = profile.Management.ConfigurationTransport == RemoteConfigurationTransportKind.Smb
            ? profile.Management.SmbConfigurationDirectory ?? profile.Management.SmbSharePath ?? string.Empty
            : profile.Management.RemoteConfigurationDirectory ?? string.Empty;
    }

    public event Action<INutConfigurationFilePipeline?, RemoteNutDirectoryValidationResult?, bool>? ConfigurationContextChanged;

    public ObservableCollection<RemoteNutDirectoryEntry> DirectoryEntries { get; }

    [ObservableProperty]
    private RemoteNutConnectionState _connectionState = RemoteNutConnectionState.Disconnected;

    [ObservableProperty]
    private string _currentDirectory;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private RemoteNutHostKeyInfo? _presentedHostKey;

    [ObservableProperty]
    private RemoteNutPlatform _platform = RemoteNutPlatform.Unknown;

    [ObservableProperty]
    private RemoteNutWriteCapabilityResult? _writeCapability;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private RemoteCredentialStoreStatus _storedCredentialStatus = RemoteCredentialStoreStatus.NotFound;

    public bool IsSshSftp => _profile.Management.ConfigurationTransport == RemoteConfigurationTransportKind.SshSftp;

    public bool IsSmb => _profile.Management.ConfigurationTransport == RemoteConfigurationTransportKind.Smb;

    /// <summary>
    /// SMB profiles bind the exact configuration location to the saved share. Administration may
    /// validate that location, but changing it belongs to the profile editor so there is only one
    /// persisted source of truth.
    /// </summary>
    public bool IsSmbDirectoryFixed => IsSmb;

    public bool UsesSmbExplicitCredentials => IsSmb && _profile.Management.SmbAuthenticationMode == SmbAuthenticationMode.ExplicitCredentials;

    public bool UsesSmbCurrentWindowsIdentity => IsSmb && !UsesSmbExplicitCredentials;

    public bool UsesSshPassword => IsSshSftp && _profile.Management.SshAuthenticationMode == SshAuthenticationMode.Password;

    public bool UsesSshPrivateKey => IsSshSftp && _profile.Management.SshAuthenticationMode == SshAuthenticationMode.PrivateKey;

    public string ConfigurationTransportText => IsSmb ? "SMB" : "SSH/SFTP";

    public string ManagementHost => _profile.Management.ManagementHost ?? L("Common.NotApplicable");

    public int SshPort => _profile.Management.SshPort;

    public string SshUsername => _profile.Management.SshUsername ?? L("Common.NotConfigured");

    public string SshAuthenticationModeText => _profile.Management.SshAuthenticationMode == SshAuthenticationMode.PrivateKey
        ? L("Authentication.PrivateKey")
        : L("Authentication.Password");

    public string SshPrivateKeyPath => _profile.Management.SshPrivateKeyPath ?? L("Common.NotConfigured.Feminine");

    public string? ConfiguredSshPrivateKeyPath => _profile.Management.SshPrivateKeyPath;

    public string TrustedHostKeyFingerprint => _profile.Management.TrustedHostKeyFingerprint ?? L("Common.NotConfigured.Feminine");

    public string TrustedHostKeyAlgorithm => _profile.Management.TrustedHostKeyAlgorithm ?? L("Common.Unavailable");

    public string SmbSharePath => _profile.Management.SmbSharePath ?? L("Common.NotConfigured");

    public string SmbAuthenticationModeText => _profile.Management.SmbAuthenticationMode == SmbAuthenticationMode.ExplicitCredentials
        ? L("Authentication.ExplicitSessionCredentials")
        : L("Authentication.CurrentWindowsUser");

    public string SmbUsername => _profile.Management.SmbUsername ?? L("Common.NotApplicable");

    public bool IsConnected => _session is not null;

    public bool ShowsDirectoryBrowser => IsConnected && IsSshSftp;

    public bool IsDirectoryValidated => _directoryValidation?.IsValid == true;

    /// <summary>
    /// The validated directory result, read-only. Validation already lists which recognised NUT
    /// files are present, so file detection can read it instead of probing the share again.
    /// </summary>
    public RemoteNutDirectoryValidationResult? DirectoryValidation => _directoryValidation;

    public bool CanConnect => !IsBusy && !IsConnected && (IsSmb || (!string.IsNullOrWhiteSpace(_profile.Management.SshUsername) && (!UsesSshPrivateKey || !string.IsNullOrWhiteSpace(_profile.Management.SshPrivateKeyPath))));

    public bool HasStoredCredential => StoredCredentialStatus == RemoteCredentialStoreStatus.Success;

    public bool CanConnectWithStoredCredential => CanConnect && HasStoredCredential && GetCredentialKind() is not null;

    public bool CanForgetStoredCredential => !IsBusy && GetCredentialKind() is not null && _profileUpdater is not null;

    public string StoredCredentialText => GetCredentialKind() is null
        ? UsesSmbCurrentWindowsIdentity ? L("Remote.Credential.NotRequired") : L("Common.NotApplicable")
        : StoredCredentialStatus switch
        {
            RemoteCredentialStoreStatus.Success => L("Remote.Credential.Saved.Yes"),
            RemoteCredentialStoreStatus.NotFound => L("Remote.Credential.Saved.No"),
            RemoteCredentialStoreStatus.Unsupported or RemoteCredentialStoreStatus.CredentialStoreUnavailable => L("Remote.Credential.Saved.Unavailable"),
            _ => L("Remote.Credential.QueryFailed")
        };

    public bool CanDisconnect => !IsBusy && IsConnected;

    public bool CanTrustHostKey => IsSshSftp && !IsBusy && ConnectionState == RemoteNutConnectionState.HostKeyTrustRequired && PresentedHostKey is not null && _profileUpdater is not null;

    public bool CanBrowse => !IsBusy && IsConnected;

    public bool CanValidateDirectory => CanBrowse && !string.IsNullOrWhiteSpace(CurrentDirectory);

    public bool CanChooseDirectory => IsSshSftp && CanBrowse;

    public bool CanUseCurrentDirectory => IsSshSftp && CanBrowse && IsDirectoryValidated && _profileUpdater is not null;

    public bool CanProbeWriteCapability =>
        CanBrowse &&
        IsDirectoryValidated &&
        _profile.AccessMode == ManagedNutServerAccessMode.Manage &&
        WriteCapability is null;

    public bool CanReadConfiguration => IsDirectoryValidated;

    public bool CanEditConfiguration =>
        _profile.AccessMode == ManagedNutServerAccessMode.Manage &&
        WriteCapability is { IsSupported: true } && (IsSmb || Platform == RemoteNutPlatform.Windows);

    public bool IsWriteCapabilityUnverified => WriteCapability is null;

    /// <summary>
    /// Applies a saved access mode to the session's own copy of the profile.
    ///
    /// This is the one that actually decides whether configuration may be written: CanEditConfiguration
    /// requires the profile to say Manage, and the copy held here was taken at startup. Saving a profile
    /// as read-only therefore left write authorization standing until the application was restarted —
    /// the header said read-only while the session went on reporting the write capability as granted.
    ///
    /// Narrowing revokes immediately, which is the direction that matters. Widening grants nothing on
    /// its own: WriteCapability is the safe-write probe's result, it is untouched here, and a profile
    /// that has not been probed still reports the capability as unverified.
    /// </summary>
    public void ApplyAccessMode(ManagedNutServerAccessMode accessMode)
    {
        if (_profile.AccessMode == accessMode) return;

        _profile = new ManagedNutServerProfile(
            _profile.Id, _profile.Name, _profile.Monitoring, _profile.Management, accessMode);

        OnPropertyChanged(nameof(CanEditConfiguration));
        OnPropertyChanged(nameof(IsWriteCapabilitySupported));
        OnPropertyChanged(nameof(WriteCapabilityText));
        OnPropertyChanged(nameof(CanProbeWriteCapability));

        // The stored status message is dropped rather than kept.
        //
        // It holds whatever the last directory validation reported, and that validation ran under the
        // previous access mode — after switching to Manage the panel went on stating that the profile
        // is configured read-only, beside a header already saying otherwise. A sentence that is simply
        // untrue is worse than no sentence: the reader has no way to tell it is describing a past state.
        //
        // The cost is that an unrelated message — a host-key warning, a credential problem — is dropped
        // with it, because a stored string cannot be told apart from one about the access mode. Losing
        // a message the operator can regenerate by reconnecting is the cheaper mistake, and the panel
        // falls back to its neutral prompt rather than to silence.
        //
        // Nothing is re-validated here. Reaching the remote host as a side effect of saving a settings
        // form is not something a save should do.
        StatusMessage = null;
    }

    public bool IsWriteCapabilitySupported => CanEditConfiguration;

    public bool IsWriteCapabilityRejected => WriteCapability is { IsSupported: false };

    public string ConnectionStateText => ConnectionState switch
    {
        RemoteNutConnectionState.Disconnected => L("Remote.Connection.Disconnected"),
        RemoteNutConnectionState.Connecting => L("Remote.Connection.Connecting"),
        RemoteNutConnectionState.HostKeyTrustRequired => L("Remote.Connection.HostKeyTrustRequired"),
        RemoteNutConnectionState.Connected => L("Remote.Connection.Connected"),
        RemoteNutConnectionState.Validating => L("Remote.Connection.Validating"),
        RemoteNutConnectionState.Ready => L("Remote.Connection.Ready"),
        RemoteNutConnectionState.AuthenticationFailed => L("Remote.Connection.AuthenticationFailed"),
        RemoteNutConnectionState.HostKeyMismatch => L("Remote.Connection.HostKeyMismatch"),
        RemoteNutConnectionState.AccessDenied => L("Remote.Connection.AccessDenied"),
        RemoteNutConnectionState.Timeout => L("Remote.Connection.Timeout"),
        _ => L("Remote.Connection.Failed")
    };

    public string ReadCapabilityText => CanReadConfiguration ? L("Common.Available") : L("Remote.Capability.ValidateForRead");

    /// <summary>Whether the profile itself permits writing. Independent of any session state.</summary>
    public bool IsManageProfile => _profile.AccessMode == ManagedNutServerAccessMode.Manage;

    /// <summary>
    /// What the write capability currently is, kept strictly distinct from what the profile allows.
    /// A management profile whose session has not been probed yet is not read-only, and saying so
    /// contradicted the access mode shown beside it.
    /// </summary>
    public string WriteCapabilityText => CanEditConfiguration
        ? IsSmb ? L("Remote.Capability.SmbVerified") : L("Remote.Capability.SshVerified")
        : !IsManageProfile
            ? L("Remote.Capability.ReadOnlyProfile")
            : WriteCapability?.Message ?? (IsSmb
                ? L("Remote.Capability.SmbProbeRequired")
                : L("Remote.Capability.SshProbeRequired"));

    public bool IsWriteCapabilityCritical => !string.IsNullOrWhiteSpace(WriteCapability?.CleanupPath);

    public string WriteCapabilityCriticalText => L("Remote.Capability.CriticalCleanup");

    public async Task ConnectWithPasswordAsync(ReadOnlyMemory<char> password, bool rememberCredential = false, CancellationToken cancellationToken = default)
    {
        if (IsSmb)
        {
            if (_profile.Management.SmbAuthenticationMode != SmbAuthenticationMode.ExplicitCredentials)
            {
                await ConnectWithCurrentWindowsIdentityAsync(cancellationToken);
                return;
            }

            if (password.IsEmpty)
            {
                StatusMessage = L("Remote.Message.EnterSmbPassword");
                return;
            }

            var connected = await ConnectSmbAsync(password, username: null, cancellationToken);
            if (connected && rememberCredential)
            {
                await SaveCredentialAfterSuccessfulConnectionAsync(RemoteCredentialKind.SmbPassword, password, cancellationToken);
            }
            return;
        }

        if (!UsesSshPassword)
        {
            StatusMessage = L("Remote.Message.RequiresPrivateKey");
            return;
        }

        if (password.IsEmpty)
        {
            StatusMessage = L("Remote.Message.EnterSessionCredential");
            return;
        }

        var sshConnected = await ConnectSshAsync(new RemoteNutPasswordAuthentication(password), cancellationToken);
        if (sshConnected && rememberCredential)
        {
            await SaveCredentialAfterSuccessfulConnectionAsync(RemoteCredentialKind.SshPassword, password, cancellationToken);
        }
    }

    public async Task ConnectWithPrivateKeyAsync(string keyPath, ReadOnlyMemory<char> passphrase = default, bool rememberPassphrase = false, CancellationToken cancellationToken = default)
    {
        if (!IsSshSftp || !UsesSshPrivateKey)
        {
            StatusMessage = L("Remote.Message.RequiresPassword");
            return;
        }

        if (string.IsNullOrWhiteSpace(keyPath))
        {
            StatusMessage = L("Remote.Message.SelectPrivateKey");
            return;
        }

        var connected = await ConnectSshAsync(new RemoteNutPrivateKeyAuthentication(keyPath, passphrase), cancellationToken);
        if (connected && rememberPassphrase && !passphrase.IsEmpty && string.Equals(keyPath, _profile.Management.SshPrivateKeyPath, StringComparison.Ordinal))
        {
            await SaveCredentialAfterSuccessfulConnectionAsync(RemoteCredentialKind.SshPrivateKeyPassphrase, passphrase, cancellationToken);
        }
    }

    public async Task ConnectWithCurrentWindowsIdentityAsync(CancellationToken cancellationToken = default)
    {
        if (!IsSmb)
        {
            return;
        }

        await ConnectSmbAsync(default, username: null, cancellationToken);
    }

    public async Task RefreshStoredCredentialStatusAsync(CancellationToken cancellationToken = default)
    {
        var kind = GetCredentialKind();
        if (kind is null || _credentialStore is null)
        {
            StoredCredentialStatus = kind is null ? RemoteCredentialStoreStatus.NotFound : RemoteCredentialStoreStatus.Unsupported;
            return;
        }

        var result = await _credentialStore.ContainsAsync(_profile.Id, kind.Value, cancellationToken);
        StoredCredentialStatus = result.Status;
    }

    public async Task ConnectWithStoredCredentialAsync(CancellationToken cancellationToken = default)
    {
        var kind = GetCredentialKind();
        if (!CanConnectWithStoredCredential || kind is null || _credentialStore is null)
        {
            return;
        }

        using var read = await _credentialStore.ReadAsync(_profile.Id, kind.Value, cancellationToken);
        StoredCredentialStatus = read.Status;
        if (!read.IsSuccess || read.Secret is null)
        {
            StatusMessage = read.Message ?? L("Remote.Message.ProtectedCredentialUnavailable");
            return;
        }

        if (kind == RemoteCredentialKind.SshPrivateKeyPassphrase)
        {
            var keyPath = _profile.Management.SshPrivateKeyPath;
            if (string.IsNullOrWhiteSpace(keyPath))
            {
                StatusMessage = L("Remote.Message.ConfigurePrivateKeyFirst");
                return;
            }

            await ConnectSshAsync(new RemoteNutPrivateKeyAuthentication(keyPath, read.Secret.Memory), cancellationToken);
            return;
        }

        if (kind == RemoteCredentialKind.SmbPassword)
        {
            await ConnectSmbAsync(read.Secret.Memory, username: null, cancellationToken);
            return;
        }

        await ConnectSshAsync(new RemoteNutPasswordAuthentication(read.Secret.Memory), cancellationToken);
    }

    // ==================== Windows-native SMB credentials ====================

    /// <summary>
    /// The window the credential dialog should belong to. Set by the view; a zero handle still
    /// works, the dialog is simply not owned.
    /// </summary>
    public nint OwnerWindowHandle { get; set; }

    public bool CanUseWindowsCredentialPrompt => UsesSmbExplicitCredentials && _credentialPrompt is not null && !IsBusy;

    /// <summary>The signed-in account, or null before one has been chosen. Never a secret.</summary>
    public string? SmbCredentialIdentity => UsesSmbExplicitCredentials ? _profile.Management.SmbUsername : null;

    public bool HasSmbCredentialIdentity => !string.IsNullOrWhiteSpace(SmbCredentialIdentity);

    /// <summary>
    /// Connects SMB the way the profile says it should be connected, without ever asking for a
    /// password in a NutManager control. Current Windows identity goes straight through; an
    /// explicit account reuses its protected credential when there is one and otherwise opens the
    /// Windows dialog.
    /// </summary>
    public async Task ConnectSmbAsync(CancellationToken cancellationToken = default)
    {
        if (!IsSmb)
        {
            return;
        }

        if (UsesSmbCurrentWindowsIdentity)
        {
            // The current session's token is the credential. Nothing is read from the credential
            // store, and no prompt is shown, even if an old profile still has a stored secret.
            await ConnectWithCurrentWindowsIdentityAsync(cancellationToken);
            return;
        }

        await RefreshStoredCredentialStatusAsync(cancellationToken);
        if (HasStoredCredential)
        {
            await ConnectWithStoredCredentialAsync(cancellationToken);
            return;
        }

        await SignInWithWindowsCredentialAsync(cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Restores a saved SMB context without prompting. Current Windows identity can be reused
    /// directly; an explicit account is reused only when its protected credential already exists.
    /// A successful connection is followed by read-only validation of the exact saved directory.
    /// The write-capability probe remains explicit because it creates temporary remote files.
    /// </summary>
    public async Task TryConnectAndValidateConfiguredSmbAsync(CancellationToken cancellationToken = default)
    {
        if (!IsSmb || IsConnected || IsBusy)
        {
            return;
        }

        if (UsesSmbCurrentWindowsIdentity)
        {
            await ConnectWithCurrentWindowsIdentityAsync(cancellationToken);
        }
        else
        {
            await RefreshStoredCredentialStatusAsync(cancellationToken);
            if (!HasStoredCredential)
            {
                StatusMessage = L("Remote.Message.AutoConnectCredentialRequired");
                return;
            }

            await ConnectWithStoredCredentialAsync(cancellationToken);
        }

        // Every successful transport connection validates its configured directory before it
        // returns. No second request is necessary here.
    }

    /// <summary>
    /// Opens the Windows credential dialog and, only if the share actually accepts what came back,
    /// records the account and honours the dialog's remember choice.
    ///
    /// The order matters: a credential is proven before it is kept, so a mistyped password can
    /// never replace a working stored one. On any failure the existing credential is left exactly
    /// as it was.
    /// </summary>
    public async Task<bool> SignInWithWindowsCredentialAsync(bool replaceExisting = false, CancellationToken cancellationToken = default)
    {
        if (!UsesSmbExplicitCredentials || _credentialPrompt is null)
        {
            return false;
        }

        using var prompted = await _credentialPrompt.RequestAsync(
            new WindowsCredentialPromptRequest(
                L("Credential.Prompt.Caption"),
                string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    L("Credential.Prompt.Message"),
                    _profile.Management.SmbSharePath ?? string.Empty),
                _profile.Management.SmbUsername,
                OwnerWindowHandle),
            cancellationToken);

        switch (prompted.Status)
        {
            case WindowsCredentialPromptStatus.Cancelled:
                // Nothing is touched: not the profile, not the stored secret, not the session.
                StatusMessage = L("Credential.Prompt.Cancelled");
                return false;
            case WindowsCredentialPromptStatus.Unsupported:
                StatusMessage = L("Credential.Prompt.WindowsOnly");
                return false;
            case WindowsCredentialPromptStatus.Failed:
                StatusMessage = L("Credential.Prompt.Failed");
                return false;
        }

        var connected = await ConnectSmbAsync(prompted.Secret!.Memory, prompted.Username!, cancellationToken);
        if (!connected)
        {
            // The share refused it. Whatever was stored before is still stored and still valid.
            if (replaceExisting)
            {
                StatusMessage = L("Credential.Prompt.KeptPrevious");
            }

            return false;
        }

        await RecordSignedInAccountAsync(prompted.Username!, cancellationToken);
        if (prompted.Remember)
        {
            await SaveCredentialAfterSuccessfulConnectionAsync(
                RemoteCredentialKind.SmbPassword, prompted.Secret.Memory, cancellationToken);
        }
        else
        {
            // Deliberately not persisted: the credential lives only as long as this session.
            StatusMessage = L("Credential.Prompt.SessionOnly");
        }

        return true;
    }

    /// <summary>
    /// Replaces the credential. The dialog runs first and the new credential has to work before
    /// the old one is discarded.
    /// </summary>
    public Task<bool> ChangeWindowsCredentialAsync(CancellationToken cancellationToken = default) =>
        SignInWithWindowsCredentialAsync(replaceExisting: true, cancellationToken);

    /// <summary>
    /// Stores the account Windows returned as ordinary profile metadata. Only the name; the
    /// password stays in the credential store.
    /// </summary>
    private async Task RecordSignedInAccountAsync(string username, CancellationToken cancellationToken)
    {
        if (_profileUpdater is null ||
            string.Equals(_profile.Management.SmbUsername, username, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var saved = await _profileUpdater.SaveSmbAccountAsync(_profile, username, cancellationToken);
        if (saved is not null)
        {
            _profile = saved;
            OnPropertyChanged(nameof(SmbUsername));
            OnPropertyChanged(nameof(SmbCredentialIdentity));
            OnPropertyChanged(nameof(HasSmbCredentialIdentity));
        }
    }

    public async Task ForgetStoredCredentialAsync(CancellationToken cancellationToken = default)
    {
        var kind = GetCredentialKind();
        if (!CanForgetStoredCredential || kind is null || _profileUpdater is null)
        {
            return;
        }

        var result = await _profileUpdater.ForgetCredentialAsync(_profile.Id, kind.Value, cancellationToken);
        StoredCredentialStatus = result.IsSuccess ? RemoteCredentialStoreStatus.NotFound : result.Status;
        StatusMessage = result.IsSuccess ? L("Remote.Message.CredentialRemoved") : result.Message ?? L("Remote.Message.CredentialRemoveFailed");
    }

    public async Task TrustPresentedHostKeyAsync(CancellationToken cancellationToken = default)
    {
        if (!CanTrustHostKey || PresentedHostKey is null || _profileUpdater is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var updated = await _profileUpdater.TrustHostKeyAsync(_profile, PresentedHostKey.Algorithm, PresentedHostKey.Fingerprint, cancellationToken);
            StatusMessage = updated is null
                ? L("Remote.Message.HostKeyProfileChanged")
                : L("Remote.Message.HostKeyTrusted");
            if (updated is not null)
            {
                _profile = updated;
                ConnectionState = RemoteNutConnectionState.Disconnected;
                PresentedHostKey = null;
                NotifyProfileMetadataChanged();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusMessage = L("Remote.Message.HostKeyTrustCancelled");
        }
        catch
        {
            StatusMessage = L("Remote.Message.HostKeySaveFailed");
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task BrowseDirectoryAsync(string directory, CancellationToken cancellationToken = default)
    {
        if (!CanBrowse || _session is null)
        {
            return;
        }

        var listed = false;
        IsBusy = true;
        try
        {
            var listing = await _session.BrowseDirectoryAsync(directory, cancellationToken);
            CurrentDirectory = listing.CurrentPath;
            DirectoryEntries.Clear();
            foreach (var entry in listing.Entries)
            {
                DirectoryEntries.Add(entry);
            }

            InvalidateDirectoryValidation();
            StatusMessage = null;
            listed = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusMessage = L("Remote.Message.BrowseCancelled");
        }
        catch
        {
            StatusMessage = L("Remote.Message.BrowseFailed");
        }
        finally
        {
            IsBusy = false;
        }

        if (listed)
        {
            await ValidateCurrentDirectoryAsync(cancellationToken);
        }
    }

    public async Task BrowseParentAsync(CancellationToken cancellationToken = default)
    {
        if (_session is null || string.IsNullOrWhiteSpace(CurrentDirectory))
        {
            return;
        }

        var parent = _session.PathPolicy.GetParentDirectory(CurrentDirectory);
        if (parent is not null)
        {
            await BrowseDirectoryAsync(parent, cancellationToken);
        }
    }

    public string CombineConfigurationFilePath(string directory, string fileName)
    {
        if (_session is null)
        {
            throw new InvalidOperationException("A remote configuration session is required to compose a configuration path.");
        }

        return _session.PathPolicy.CombineDirectChild(directory, fileName);
    }

    public Task BrowseChildAsync(RemoteNutDirectoryEntry? entry, CancellationToken cancellationToken = default) =>
        entry is { IsDirectory: true, IsSymbolicLink: false }
            ? BrowseDirectoryAsync(entry.FullPath, cancellationToken)
            : Task.CompletedTask;

    public async Task ValidateCurrentDirectoryAsync(CancellationToken cancellationToken = default)
    {
        if (!CanValidateDirectory || _session is null)
        {
            return;
        }

        IsBusy = true;
        ConnectionState = RemoteNutConnectionState.Validating;
        try
        {
            var validation = await _session.ValidateConfigurationDirectoryAsync(CurrentDirectory, cancellationToken);
            _directoryValidation = validation;
            CurrentDirectory = validation.Directory;
            if (!validation.IsValid)
            {
                ConnectionState = validation.Status switch
                {
                    RemoteNutTransportStatus.AccessDenied => RemoteNutConnectionState.AccessDenied,
                    RemoteNutTransportStatus.Timeout => RemoteNutConnectionState.Timeout,
                    _ => RemoteNutConnectionState.Failed
                };
                StatusMessage = validation.Message;
                NotifyConfigurationContextChanged();
                return;
            }

            ConnectionState = RemoteNutConnectionState.Ready;
            StatusMessage = validation.Message;
            NotifyConfigurationContextChanged();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ConnectionState = RemoteNutConnectionState.Connected;
            StatusMessage = L("Remote.Message.ValidationCancelled");
        }
        catch
        {
            ConnectionState = RemoteNutConnectionState.Failed;
            StatusMessage = L("Remote.Message.ValidationFailed");
            InvalidateDirectoryValidation();
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task UseCurrentDirectoryAsync(CancellationToken cancellationToken = default)
    {
        if (!CanUseCurrentDirectory || _profileUpdater is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var updated = await _profileUpdater.SaveRemoteDirectoryAsync(_profile, CurrentDirectory, cancellationToken);
            StatusMessage = updated is null
                ? L("Remote.Message.DirectoryProfileChanged")
                : L("Remote.Message.DirectorySaved");
            if (updated is not null)
            {
                _profile = updated;
                NotifyProfileMetadataChanged();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusMessage = L("Remote.Message.DirectorySaveCancelled");
        }
        catch
        {
            StatusMessage = L("Remote.Message.DirectorySaveFailed");
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task ProbeWriteCapabilityAsync(CancellationToken cancellationToken = default)
    {
        if (!CanProbeWriteCapability || _session is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            WriteCapability = await _session.ProbeSafeWriteCapabilityAsync(CurrentDirectory, cancellationToken);
            Platform = WriteCapability.Platform;
            StatusMessage = WriteCapability.Message;
            NotifyConfigurationContextChanged();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusMessage = L("Remote.Message.WriteProbeCancelled");
        }
        catch
        {
            WriteCapability = new RemoteNutWriteCapabilityResult(false, Platform, message: L("Remote.Message.WriteProbeFailed"));
            NotifyConfigurationContextChanged();
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task DisconnectAsync()
    {
        var session = _session;
        _session = null;
        if (session is not null)
        {
            await session.DisposeAsync();
        }

        DirectoryEntries.Clear();
        InvalidateDirectoryValidation();
        ConnectionState = RemoteNutConnectionState.Disconnected;
        StatusMessage = L("Remote.Message.Disconnected");
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync();

    public void InvalidateWriteCapabilityAfterUncertainOutcome()
    {
        WriteCapability = new RemoteNutWriteCapabilityResult(
            false,
            Platform,
            message: L("Remote.Message.OutcomeUnknown"));
        OnPropertyChanged(nameof(CanEditConfiguration));
        OnPropertyChanged(nameof(WriteCapabilityText));
    }

    private async Task<bool> ConnectSshAsync(RemoteNutAuthentication authentication, CancellationToken cancellationToken)
    {
        if (!CanConnect || !IsSshSftp)
        {
            StatusMessage = L("Remote.Message.ConfigureSshUser");
            return false;
        }

        var connected = false;
        IsBusy = true;
        ConnectionState = RemoteNutConnectionState.Connecting;
        StatusMessage = null;
        try
        {
            var result = await _transport.ConnectAsync(
                new RemoteNutConnectionRequest(
                    _profile.Id,
                    ManagementHost,
                    SshPort,
                    _profile.Management.SshUsername!,
                    _profile.Management.TrustedHostKeyFingerprint,
                    authentication),
                cancellationToken);
            AcceptConnectionResult(result, _profile.Management.RemoteConfigurationDirectory);
            connected = result.State == RemoteNutConnectionState.Connected && result.Session is not null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ConnectionState = RemoteNutConnectionState.Disconnected;
            StatusMessage = L("Remote.Message.ConnectionCancelled");
            return false;
        }
        catch
        {
            ConnectionState = RemoteNutConnectionState.ConnectionFailed;
            StatusMessage = L("Remote.Message.ConnectionFailed");
            return false;
        }
        finally
        {
            IsBusy = false;
        }

        if (connected && !string.IsNullOrWhiteSpace(CurrentDirectory))
        {
            await ValidateCurrentDirectoryAsync(cancellationToken);
        }

        return connected;
    }

    /// <param name="username">
    /// The account to authenticate as. It is passed in rather than read from the profile because a
    /// freshly prompted credential has to be proven before its account is recorded.
    /// </param>
    private async Task<bool> ConnectSmbAsync(ReadOnlyMemory<char> password, string? username, CancellationToken cancellationToken)
    {
        if (!CanConnect || !IsSmb)
        {
            return false;
        }

        var connected = false;
        IsBusy = true;
        ConnectionState = RemoteNutConnectionState.Connecting;
        StatusMessage = null;
        try
        {
            var management = _profile.Management;
            var result = await _transport.ConnectAsync(
                new SmbRemoteNutConnectionRequest(
                    _profile.Id,
                    management.SmbSharePath!,
                    management.SmbAuthenticationMode,
                    username ?? management.SmbUsername,
                    password,
                    _profile.AccessMode == ManagedNutServerAccessMode.Manage),
                cancellationToken);
            AcceptConnectionResult(result, management.SmbConfigurationDirectory ?? management.SmbSharePath);
            connected = result.State == RemoteNutConnectionState.Connected && result.Session is not null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ConnectionState = RemoteNutConnectionState.Disconnected;
            StatusMessage = L("Remote.Message.SmbConnectionCancelled");
            return false;
        }
        catch
        {
            ConnectionState = RemoteNutConnectionState.ConnectionFailed;
            StatusMessage = L("Remote.Message.SmbConnectionFailed");
            return false;
        }
        finally
        {
            IsBusy = false;
        }

        if (connected && !string.IsNullOrWhiteSpace(CurrentDirectory))
        {
            await ValidateCurrentDirectoryAsync(cancellationToken);
        }

        return connected;
    }

    private async Task SaveCredentialAfterSuccessfulConnectionAsync(RemoteCredentialKind kind, ReadOnlyMemory<char> secret, CancellationToken cancellationToken)
    {
        if (_profileUpdater is null)
        {
            StatusMessage = L("Remote.Message.CredentialStoreUnavailable");
            return;
        }

        var result = await _profileUpdater.SaveCredentialForCurrentSessionAsync(_profile, kind, secret, cancellationToken);
        StoredCredentialStatus = result.Status;
        StatusMessage = result.IsSuccess
            ? L("Remote.Message.CredentialSaved")
            : result.Message ?? L("Remote.Message.CredentialSaveFailed");
    }

    private RemoteCredentialKind? GetCredentialKind()
    {
        if (UsesSshPassword)
        {
            return RemoteCredentialKind.SshPassword;
        }

        if (UsesSshPrivateKey && !string.IsNullOrWhiteSpace(_profile.Management.SshPrivateKeyPath))
        {
            return RemoteCredentialKind.SshPrivateKeyPassphrase;
        }

        return UsesSmbExplicitCredentials ? RemoteCredentialKind.SmbPassword : null;
    }

    private string L(string key) => _strings.Get(key);

    private void AcceptConnectionResult(RemoteNutConnectionResult result, string? initialDirectory)
    {
        ConnectionState = result.State;
        PresentedHostKey = IsSshSftp ? result.HostKey : null;
        StatusMessage = result.Message;
        if (result.Session is null)
        {
            return;
        }

        _session = result.Session;
        CurrentDirectory = initialDirectory ?? result.Session.HomeDirectory;
        Platform = result.Session.Platform;
        WriteCapability = null;
        DirectoryEntries.Clear();
        _directoryValidation = null;
        NotifyConfigurationContextChanged();
    }

    private void InvalidateDirectoryValidation()
    {
        _directoryValidation = null;
        WriteCapability = null;
        NotifyConfigurationContextChanged();
    }

    private void NotifyConfigurationContextChanged()
    {
        var pipeline = _session is not null && _directoryValidation?.IsValid == true
            ? new RemoteNutConfigurationFilePipeline(_session, _directoryValidation.Directory, CanEditConfiguration)
            : null;
        ConfigurationContextChanged?.Invoke(pipeline, _directoryValidation, CanEditConfiguration);
        OnPropertyChanged(nameof(IsDirectoryValidated));
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(ShowsDirectoryBrowser));
        OnPropertyChanged(nameof(CanReadConfiguration));
        OnPropertyChanged(nameof(CanEditConfiguration));
        OnPropertyChanged(nameof(ReadCapabilityText));
        OnPropertyChanged(nameof(WriteCapabilityText));
    }

    private void NotifyProfileMetadataChanged()
    {
        OnPropertyChanged(nameof(ManagementHost));
        OnPropertyChanged(nameof(SshPort));
        OnPropertyChanged(nameof(SshUsername));
        OnPropertyChanged(nameof(SshAuthenticationModeText));
        OnPropertyChanged(nameof(SshPrivateKeyPath));
        OnPropertyChanged(nameof(ConfiguredSshPrivateKeyPath));
        OnPropertyChanged(nameof(TrustedHostKeyFingerprint));
        OnPropertyChanged(nameof(TrustedHostKeyAlgorithm));
        OnPropertyChanged(nameof(SmbSharePath));
        OnPropertyChanged(nameof(SmbAuthenticationModeText));
        OnPropertyChanged(nameof(SmbUsername));
        OnPropertyChanged(nameof(IsSshSftp));
        OnPropertyChanged(nameof(IsSmb));
        OnPropertyChanged(nameof(ShowsDirectoryBrowser));
        OnPropertyChanged(nameof(UsesSmbExplicitCredentials));
        OnPropertyChanged(nameof(UsesSmbCurrentWindowsIdentity));
        OnPropertyChanged(nameof(UsesSshPassword));
        OnPropertyChanged(nameof(UsesSshPrivateKey));
        OnPropertyChanged(nameof(ConfigurationTransportText));
        OnPropertyChanged(nameof(CanConnect));
        OnPropertyChanged(nameof(CanTrustHostKey));
        OnPropertyChanged(nameof(CanConnectWithStoredCredential));
        OnPropertyChanged(nameof(CanForgetStoredCredential));
        OnPropertyChanged(nameof(StoredCredentialText));
    }

    partial void OnConnectionStateChanged(RemoteNutConnectionState value)
    {
        OnPropertyChanged(nameof(ConnectionStateText));
        OnPropertyChanged(nameof(CanConnect));
        OnPropertyChanged(nameof(CanDisconnect));
        OnPropertyChanged(nameof(CanTrustHostKey));
    }

    partial void OnCurrentDirectoryChanged(string value)
    {
        if (_directoryValidation is not null && !string.Equals(value, _directoryValidation.Directory, StringComparison.Ordinal))
        {
            InvalidateDirectoryValidation();
        }

        OnPropertyChanged(nameof(CanValidateDirectory));
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanConnect));
        OnPropertyChanged(nameof(CanDisconnect));
        OnPropertyChanged(nameof(CanTrustHostKey));
        OnPropertyChanged(nameof(CanBrowse));
        OnPropertyChanged(nameof(CanChooseDirectory));
        OnPropertyChanged(nameof(CanValidateDirectory));
        OnPropertyChanged(nameof(CanUseCurrentDirectory));
        OnPropertyChanged(nameof(CanProbeWriteCapability));
        OnPropertyChanged(nameof(CanConnectWithStoredCredential));
        OnPropertyChanged(nameof(CanForgetStoredCredential));
    }

    partial void OnWriteCapabilityChanged(RemoteNutWriteCapabilityResult? value)
    {
        OnPropertyChanged(nameof(CanProbeWriteCapability));
        OnPropertyChanged(nameof(CanEditConfiguration));
        OnPropertyChanged(nameof(IsWriteCapabilityUnverified));
        OnPropertyChanged(nameof(IsWriteCapabilitySupported));
        OnPropertyChanged(nameof(IsWriteCapabilityRejected));
        OnPropertyChanged(nameof(WriteCapabilityText));
        OnPropertyChanged(nameof(IsWriteCapabilityCritical));
    }

    partial void OnStoredCredentialStatusChanged(RemoteCredentialStoreStatus value)
    {
        OnPropertyChanged(nameof(HasStoredCredential));
        OnPropertyChanged(nameof(CanConnectWithStoredCredential));
        OnPropertyChanged(nameof(StoredCredentialText));
    }
}
