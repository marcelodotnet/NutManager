namespace NutManager.Core.Services;

/// <summary>
/// Tracks the one-session result of the remote safe-write capability probe.
/// The caller supplies a normalized, validated remote directory; this type deliberately
/// does not apply host filesystem path semantics.
/// </summary>
public sealed class RemoteSafeWriteCapabilityState
{
    private readonly StringComparer _directoryComparer;
    private string? _verifiedConfigurationDirectory;
    private bool _terminallyInvalidated;

    public RemoteSafeWriteCapabilityState(StringComparer? directoryComparer = null) =>
        _directoryComparer = directoryComparer ?? StringComparer.Ordinal;

    public bool IsValidFor(string normalizedConfigurationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedConfigurationDirectory);
        return !_terminallyInvalidated && _directoryComparer.Equals(
            _verifiedConfigurationDirectory,
            normalizedConfigurationDirectory);
    }

    /// <summary>
    /// Starts a new explicit probe. A normal failed or cancelled probe leaves no verified
    /// capability, while an indeterminate write outcome remains terminal for the session.
    /// </summary>
    public bool TryBeginProbe()
    {
        if (_terminallyInvalidated)
        {
            return false;
        }

        _verifiedConfigurationDirectory = null;
        return true;
    }

    public bool TryCompleteProbe(string normalizedConfigurationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedConfigurationDirectory);
        if (_terminallyInvalidated)
        {
            return false;
        }

        _verifiedConfigurationDirectory = normalizedConfigurationDirectory;
        return true;
    }

    /// <summary>
    /// Clears a completed probe after the profile's write intent changes, while keeping the
    /// session eligible for a new explicit probe. A terminal invalidation remains terminal.
    /// </summary>
    public void ClearVerification() => _verifiedConfigurationDirectory = null;

    /// <summary>
    /// Makes the session permanently ineligible for remote writes after an indeterminate
    /// commit or rollback outcome. Only a new transport session may establish a new capability.
    /// </summary>
    public void InvalidateSession()
    {
        _verifiedConfigurationDirectory = null;
        _terminallyInvalidated = true;
    }
}
