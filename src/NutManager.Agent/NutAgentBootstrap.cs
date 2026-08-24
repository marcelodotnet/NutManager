using System.Runtime.Versioning;
using System.Security.Principal;
using NutManager.Core.Agent;
using NutManager.Infrastructure.Agent;

namespace NutManager.Agent;

/// <summary>
/// Assembles the agent and checks the one assumption everything else rests on.
///
/// The agent is designed to run as LocalSystem, and that is not a deployment preference: the whole
/// security model says the caller's rights are decided by group membership rather than inherited from
/// whoever happens to have started the process. An agent running as some other account has a
/// different authority than the one that was reviewed, so the check is explicit and it fails closed.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class NutAgentBootstrap
{
    /// <summary>
    /// Whether this process is LocalSystem, with the account it actually is for the failure record.
    /// Any doubt is answered no — an identity that cannot be read is not an identity that passed.
    /// </summary>
    internal static (bool IsLocalSystem, string Account) VerifyAccount()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var isLocalSystem = identity.User is { } sid && sid.IsWellKnown(WellKnownSidType.LocalSystemSid);
            return (isLocalSystem, identity.Name);
        }
        catch (Exception exception)
        {
            return (false, $"(unreadable: {exception.GetType().Name})");
        }
    }

    /// <summary>
    /// Builds the agent from the Windows adapters. Composition is the only thing that happens here:
    /// every rule lives in <see cref="NutAgentApplicationService"/>, which is what keeps the two
    /// transports from developing separate opinions about what is allowed.
    /// </summary>
    internal static NutAgentComposition Create()
    {
        var authorization = new WindowsGroupAuthorization();
        var audit = new WindowsEventLogAuditSink();
        // The hardware inspector is supplied here, and supplying it is what makes the agent
        // advertise the capability: an installation assembled without one reports the operation as
        // absent rather than refusing it at call time.
        var service = new NutAgentApplicationService(
            new WindowsNutServiceTargetResolver(),
            new WindowsNutAgentServiceController(),
            audit,
            authorization,
            hardwareInspector: new WindowsNutAgentHardwareInspector());

        return new NutAgentComposition(service, new NutAgentRequestDispatcher(service), authorization, audit);
    }
}

/// <summary>The assembled agent. Held together only so the host can start and stop it as one thing.</summary>
[SupportedOSPlatform("windows")]
internal sealed record NutAgentComposition(
    NutAgentApplicationService Service,
    NutAgentRequestDispatcher Dispatcher,
    WindowsGroupAuthorization Authorization,
    WindowsEventLogAuditSink Audit);
