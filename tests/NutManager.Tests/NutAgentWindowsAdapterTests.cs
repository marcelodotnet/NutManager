using System.ComponentModel;
using NutManager.Core.Administration;
using NutManager.Core.Agent;
using NutManager.Core.Models;
using NutManager.Core.Services;
using NutManager.Infrastructure.Agent;
using Xunit;

namespace NutManager.Tests;

/// <summary>
/// The Windows adapters behind the agent's four interfaces.
///
/// Every decision they make is pure and is tested here as such: no SCM is queried, no Event Log
/// source is touched, no group is looked up and no service is controlled. What the tests do assert is
/// the property the agent exists for — that a service is adopted only when its binary lives inside
/// the detected NUT installation, which is precisely the check T34 could not perform from across the
/// network.
/// </summary>
public sealed class NutAgentWindowsAdapterTests
{
    private const string Root = @"C:\Program Files\NUT";
    private const string NutServiceName = "Network UPS Tools";

    private static WindowsAgentServiceCandidate Candidate(string name, string? imagePath, string? display = null) =>
        new(name, display ?? name, imagePath);

    // ---------------------------------------------------------------- target selection

    [Fact]
    public void AServiceRunningABinaryInsideTheInstallationIsAdopted()
    {
        var resolution = WindowsNutServiceTargetResolver.Select(
            [Candidate(NutServiceName, @"""C:\Program Files\NUT\sbin\nut.exe"" -service")], Root);

        Assert.Equal(NutServiceTargetStatus.Resolved, resolution.Status);
        Assert.True(resolution.IsResolved);
        Assert.Equal(NutServiceName, resolution.Target!.ServiceName);
        Assert.Equal(NutAssociationConfidence.BinaryPath, resolution.Target.Confidence);
        Assert.Equal(@"C:\Program Files\NUT\sbin\nut.exe", resolution.Target.BinaryPath);
    }

    [Fact]
    public void AServiceThatOnlyBorrowsTheNutNameIsRefused()
    {
        // The anti name-squatting rule, and the whole reason the agent runs on the server: a service
        // may call itself anything, but it cannot move its binary inside the installation.
        var resolution = WindowsNutServiceTargetResolver.Select(
            [Candidate(NutServiceName, @"C:\Temp\evil.exe")], Root);

        Assert.Equal(NutServiceTargetStatus.ValidationFailed, resolution.Status);
        Assert.Null(resolution.Target);
        Assert.False(resolution.IsResolved);
    }

    [Fact]
    public void AServiceWhoseImagePathCannotBeReadIsNotAdoptedOnItsNameAlone()
    {
        // No image path means containment cannot be verified, and unverifiable is refused rather
        // than downgraded to a name match.
        var resolution = WindowsNutServiceTargetResolver.Select([Candidate(NutServiceName, null)], Root);

        Assert.Equal(NutServiceTargetStatus.ValidationFailed, resolution.Status);
        Assert.Null(resolution.Target);
    }

    [Fact]
    public void TwoValidatingServicesLeaveTheAgentWithoutAuthority()
    {
        var resolution = WindowsNutServiceTargetResolver.Select(
            [
                Candidate(NutServiceName, @"C:\Program Files\NUT\sbin\nut.exe"),
                Candidate("NUT", @"C:\Program Files\NUT\sbin\upsd.exe")
            ],
            Root);

        Assert.Equal(NutServiceTargetStatus.Ambiguous, resolution.Status);
        Assert.Null(resolution.Target);
        Assert.Equal([NutServiceName, "NUT"], resolution.Candidates);
    }

    [Fact]
    public void AMissingInstallationRootRefusesRatherThanFallingBackToTheName()
    {
        var resolution = WindowsNutServiceTargetResolver.Select(
            [Candidate(NutServiceName, @"C:\Program Files\NUT\sbin\nut.exe")], installationRoot: null);

        Assert.Equal(NutServiceTargetStatus.ValidationFailed, resolution.Status);
        Assert.Null(resolution.Target);
        Assert.Contains(NutServiceName, resolution.Candidates!);
    }

    [Fact]
    public void AMachineWithNeitherAnInstallationNorANutServiceReportsNotFound()
    {
        var resolution = WindowsNutServiceTargetResolver.Select(
            [Candidate("Spooler", @"C:\Windows\System32\spoolsv.exe")], installationRoot: null);

        Assert.Equal(NutServiceTargetStatus.NotFound, resolution.Status);
        Assert.Null(resolution.Target);
    }

    [Fact]
    public void AnInstallationWithNoServicePointingIntoItReportsNotFound()
    {
        var resolution = WindowsNutServiceTargetResolver.Select(
            [Candidate("Spooler", @"C:\Windows\System32\spoolsv.exe")], Root);

        Assert.Equal(NutServiceTargetStatus.NotFound, resolution.Status);
    }

    [Fact]
    public void NoCandidateAtAllIsNotFoundRatherThanAnError()
    {
        var resolution = WindowsNutServiceTargetResolver.Select([], Root);

        Assert.Equal(NutServiceTargetStatus.NotFound, resolution.Status);
    }

    [Fact]
    public async Task AnInstallationDetectorThatFailsLeavesTheAgentWithoutATarget()
    {
        var resolver = new WindowsNutServiceTargetResolver(new ThrowingDetector());

        var resolution = await resolver.ResolveAsync(default);

        // Returned before the SCM is ever enumerated, so the test touches no service on this machine.
        Assert.Equal(NutServiceTargetStatus.QueryFailed, resolution.Status);
        Assert.Null(resolution.Target);
    }

    // ---------------------------------------------------------------- revalidation

    [Fact]
    public void RevalidationAcceptsTheSameServiceRunningTheSameBinary()
    {
        var pinned = new NutServiceTarget(NutServiceName, NutServiceName, @"C:\Program Files\NUT\sbin\nut.exe", NutAssociationConfidence.BinaryPath);
        var current = WindowsNutServiceTargetResolver.Select(
            [Candidate(NutServiceName, @"C:\Program Files\NUT\sbin\nut.exe")], Root);

        var confirmed = WindowsNutServiceTargetResolver.Confirm(pinned, current);

        Assert.Equal(NutServiceTargetStatus.Resolved, confirmed.Status);
    }

    [Fact]
    public void RevalidationRefusesAServiceWhoseBinaryWasRepointed()
    {
        // Same name, different image. This is the substitution the revalidation step exists for:
        // without the binary comparison the agent would start whatever now sits behind the name.
        var pinned = new NutServiceTarget(NutServiceName, NutServiceName, @"C:\Program Files\NUT\sbin\nut.exe", NutAssociationConfidence.BinaryPath);
        var current = WindowsNutServiceTargetResolver.Select(
            [Candidate(NutServiceName, @"C:\Program Files\NUT\sbin\upsd.exe")], Root);

        var confirmed = WindowsNutServiceTargetResolver.Confirm(pinned, current);

        Assert.Equal(NutServiceTargetStatus.ValidationFailed, confirmed.Status);
        Assert.Null(confirmed.Target);
    }

    [Fact]
    public void RevalidationRefusesADifferentService()
    {
        var pinned = new NutServiceTarget(NutServiceName, NutServiceName, @"C:\Program Files\NUT\sbin\nut.exe", NutAssociationConfidence.BinaryPath);
        var current = WindowsNutServiceTargetResolver.Select(
            [Candidate("NUT", @"C:\Program Files\NUT\sbin\nut.exe")], Root);

        var confirmed = WindowsNutServiceTargetResolver.Confirm(pinned, current);

        Assert.Equal(NutServiceTargetStatus.ValidationFailed, confirmed.Status);
    }

    [Fact]
    public void RevalidationPassesAFailedResolutionThroughUnchanged()
    {
        var pinned = new NutServiceTarget(NutServiceName, NutServiceName, @"C:\Program Files\NUT\sbin\nut.exe", NutAssociationConfidence.BinaryPath);
        var current = WindowsNutServiceTargetResolver.Select([], Root);

        var confirmed = WindowsNutServiceTargetResolver.Confirm(pinned, current);

        Assert.Equal(NutServiceTargetStatus.NotFound, confirmed.Status);
    }

    // ---------------------------------------------------------------- control failure mapping

    [Fact]
    public void AControlCallThatTimesOutReportsWhereTheServiceActuallyIs()
    {
        // The exception this maps is itself Windows-only, so the guard the repository already uses
        // for platform-typed tests applies here too.
        if (!OperatingSystem.IsWindows()) return;

        var outcome = WindowsNutAgentServiceController.MapFailure(
            new System.ServiceProcess.TimeoutException(), startRequested: false, observed: NutServiceState.StopPending);

        Assert.Equal(NutAgentResultCode.TimedOut, outcome.Code);
        Assert.Equal(NutServiceState.StopPending, outcome.FinalState);
    }

    [Fact]
    public void WindowsSayingAlreadyRunningToAStartIsTheRequestedStateNotAFailure()
    {
        var outcome = WindowsNutAgentServiceController.MapFailure(
            new Win32Exception(WindowsNutAgentServiceController.ErrorServiceAlreadyRunning),
            startRequested: true,
            observed: NutServiceState.Unknown);

        Assert.Equal(NutAgentResultCode.AlreadyInRequestedState, outcome.Code);
        Assert.Equal(NutServiceState.Running, outcome.FinalState);
    }

    [Fact]
    public void WindowsSayingNotActiveToAStopIsTheRequestedState()
    {
        var outcome = WindowsNutAgentServiceController.MapFailure(
            new Win32Exception(WindowsNutAgentServiceController.ErrorServiceNotActive),
            startRequested: false,
            observed: NutServiceState.Unknown);

        Assert.Equal(NutAgentResultCode.AlreadyInRequestedState, outcome.Code);
        Assert.Equal(NutServiceState.Stopped, outcome.FinalState);
    }

    [Fact]
    public void TheAlreadyInStateCodesAreNotAcceptedForTheOppositeVerb()
    {
        // "Already running" answering a stop request is a genuine failure, and collapsing the two
        // directions would report a service as stopped while it runs.
        var outcome = WindowsNutAgentServiceController.MapFailure(
            new Win32Exception(WindowsNutAgentServiceController.ErrorServiceAlreadyRunning),
            startRequested: false,
            observed: NutServiceState.Running);

        Assert.Equal(NutAgentResultCode.ServiceControlFailed, outcome.Code);
        Assert.Equal(NutServiceState.Running, outcome.FinalState);
    }

    [Fact]
    public void ARefusedControlCallIsAControlFailureAndNotACallerAuthorizationVerdict()
    {
        // Unauthorized means "the caller is not an operator". The SCM refusing LocalSystem is a
        // different fact and must not be reported as the caller's fault.
        var outcome = WindowsNutAgentServiceController.MapFailure(
            new Win32Exception(WindowsNutAgentServiceController.ErrorAccessDenied),
            startRequested: true,
            observed: NutServiceState.Stopped);

        Assert.Equal(NutAgentResultCode.ServiceControlFailed, outcome.Code);
        Assert.Equal(WindowsNutAgentServiceController.ErrorAccessDenied, outcome.Win32ErrorCode);
    }

    [Fact]
    public void AWin32FailureWrappedByTheManagedLayerIsStillMappedByItsCode()
    {
        var outcome = WindowsNutAgentServiceController.MapFailure(
            new InvalidOperationException("service missing", new Win32Exception(WindowsNutAgentServiceController.ErrorServiceDoesNotExist)),
            startRequested: true,
            observed: NutServiceState.Unknown);

        Assert.Equal(NutAgentResultCode.ServiceControlFailed, outcome.Code);
        Assert.Equal(WindowsNutAgentServiceController.ErrorServiceDoesNotExist, outcome.Win32ErrorCode);
    }

    [Theory]
    [InlineData(
        WindowsNutAgentServiceController.ErrorAccessDenied,
        NutAgentServiceQueryFailureKind.AccessDenied)]
    [InlineData(
        WindowsNutAgentServiceController.ErrorServiceDoesNotExist,
        NutAgentServiceQueryFailureKind.ServiceDoesNotExist)]
    [InlineData(1722, NutAgentServiceQueryFailureKind.WindowsFailure)]
    public void AFailedStatusQueryKeepsItsNumericWindowsCodeAndSafeCategory(
        int win32Error,
        NutAgentServiceQueryFailureKind expected)
    {
        var failure = WindowsNutAgentServiceController.MapStatusFailure(new Win32Exception(win32Error));

        Assert.Equal(expected, failure.Kind);
        Assert.Equal(win32Error, failure.Win32ErrorCode);
        Assert.Equal(nameof(Win32Exception), failure.ExceptionType);
        Assert.DoesNotContain(new Win32Exception(win32Error).Message, failure.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AStatusQueryTimeoutIsDistinguishedWithoutParsingItsMessage()
    {
        var failure = WindowsNutAgentServiceController.MapStatusFailure(new TimeoutException("localized text"));

        Assert.Equal(NutAgentServiceQueryFailureKind.TimedOut, failure.Kind);
        Assert.Null(failure.Win32ErrorCode);
        Assert.Equal(nameof(TimeoutException), failure.ExceptionType);
        Assert.DoesNotContain("localized text", failure.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownStatusQueryFailureKeepsOnlyItsSafeType()
    {
        var failure = WindowsNutAgentServiceController.MapStatusFailure(
            new InvalidOperationException("environmental detail that must not travel"));

        Assert.Equal(NutAgentServiceQueryFailureKind.Unknown, failure.Kind);
        Assert.Null(failure.Win32ErrorCode);
        Assert.Equal(nameof(InvalidOperationException), failure.ExceptionType);
        Assert.DoesNotContain("environmental detail", failure.Detail, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- audit record

    [Fact]
    public void TheAuditRecordCarriesTheIdentityTheTransportEstablishedAndTheStateChange()
    {
        var text = WindowsEventLogAuditSink.FormatEntry(SampleEntry(NutAgentAuditKind.OperationSucceeded));

        Assert.Contains(@"SBRA\operator", text, StringComparison.Ordinal);
        Assert.Contains("NamedPipe", text, StringComparison.Ordinal);
        Assert.Contains(NutServiceName, text, StringComparison.Ordinal);
        Assert.Contains("Running -> Stopped", text, StringComparison.Ordinal);
        Assert.Contains("Restart", text, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryAuditKindHasItsOwnStableEventId()
    {
        var kinds = Enum.GetValues<NutAgentAuditKind>();
        var ids = kinds.Select(WindowsEventLogAuditSink.EventIdOf).ToArray();

        Assert.Equal(kinds.Length, ids.Distinct().Count());

        // Part of the agent's external contract: an administrator filters on these.
        Assert.Equal(1002, WindowsEventLogAuditSink.EventIdOf(NutAgentAuditKind.UnauthorizedAttempt));
        Assert.Equal(1011, WindowsEventLogAuditSink.EventIdOf(NutAgentAuditKind.OperationSucceeded));
    }

    [Fact]
    public void TheSecurityRelevantKindsAreRecordedAboveInformation()
    {
        Assert.True(WindowsEventLogAuditSink.IsFailureKind(NutAgentAuditKind.UnauthorizedAttempt));
        Assert.True(WindowsEventLogAuditSink.IsFailureKind(NutAgentAuditKind.SecurityStartupFailure));
        Assert.True(WindowsEventLogAuditSink.IsFailureKind(NutAgentAuditKind.TargetRevalidationFailure));
        Assert.True(WindowsEventLogAuditSink.IsFailureKind(NutAgentAuditKind.OperationFailed));
        Assert.False(WindowsEventLogAuditSink.IsFailureKind(NutAgentAuditKind.OperationSucceeded));
    }

    // ---------------------------------------------------------------- boundaries

    [Fact]
    public void TheControllerHasNoWayToKillAProcess()
    {
        var source = Repository.Read(Path.Combine(
            "src", "NutManager.Infrastructure", "Agent", "WindowsNutAgentServiceController.cs"));

        // A stop that will not complete is a fact an operator needs, not a problem to hide by
        // terminating the process behind the SCM's back.
        foreach (var forbidden in new[]
        {
            "Process.Kill", "TerminateProcess", "taskkill", "Process.Start", "ChangeServiceConfig",
            "DeleteService", "ExitProcess"
        })
        {
            Assert.DoesNotContain(forbidden, source, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void TheGroupLayerAsksAboutMembershipAndNeverChangesIt()
    {
        var source = Repository.Read(Path.Combine(
            "src", "NutManager.Infrastructure", "Agent", "WindowsAgentGroupInterop.cs"));

        foreach (var forbidden in new[]
        {
            "NetLocalGroupAdd", "NetLocalGroupSetMembers", "NetLocalGroupAddMembers", "NetLocalGroupDel",
            "NetUserAdd", "NetUserDel", "NetUserSetInfo", "NetUserSetGroups", "AdjustTokenPrivileges",
            "LogonUser", "ImpersonateLoggedOnUser"
        })
        {
            Assert.DoesNotContain(forbidden, source, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void TheAgentNeverCreatesItsOwnAuditSourceOrItsOwnOperatorsGroup()
    {
        var audit = Repository.Read(Path.Combine(
            "src", "NutManager.Infrastructure", "Agent", "WindowsEventLogAuditSink.cs"));
        var authorization = Repository.Read(Path.Combine(
            "src", "NutManager.Infrastructure", "Agent", "WindowsGroupAuthorization.cs"));

        // Both are deployment acts. An agent that can create them is an agent that can be made to.
        Assert.DoesNotContain("CreateEventSource", audit, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Delete(", audit, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("NetLocalGroupAdd", authorization, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AMissingOperatorsGroupNeverWidensIntoAdministrators()
    {
        var source = Repository.Read(Path.Combine(
            "src", "NutManager.Infrastructure", "Agent", "WindowsGroupAuthorization.cs"));

        // The fail-closed rule, asserted against the source because the alternative — a deployment
        // mistake quietly becoming an open door — cannot be observed from behaviour on a machine
        // where the group happens to exist.
        foreach (var forbidden in new[]
        {
            "S-1-5-32-544", "WindowsBuiltInRole", "BuiltinAdministratorsSid", "IsInRole(WindowsBuiltInRole"
        })
        {
            Assert.DoesNotContain(forbidden, source, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static NutAgentAuditEntry SampleEntry(NutAgentAuditKind kind) => new(
        kind,
        new DateTimeOffset(2026, 8, 16, 10, 30, 0, TimeSpan.Zero),
        Guid.Parse("11111111-2222-3333-4444-555555555555"),
        @"SBRA\operator",
        "NamedPipe",
        "GANDALF",
        NutAgentOperation.Restart,
        NutServiceName,
        NutServiceState.Running,
        NutServiceState.Stopped,
        NutAgentResultCode.Success,
        null,
        TimeSpan.FromMilliseconds(1200),
        null);

    private sealed class ThrowingDetector : ILocalNutInstallationDetector
    {
        public Task<NutInstallationInfo> DetectAsync(CancellationToken cancellationToken) =>
            throw new IOException("the installation could not be inspected");

        public Task<NutInstallationInfo> InspectDirectoryAsync(string installationOrConfigurationDirectory, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
