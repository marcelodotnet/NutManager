namespace NutManager.Core.Agent;

/// <summary>
/// How a single fact about the installation reads.
///
/// Four states rather than a boolean, because the interesting cases are not pass and fail. HTTPS that
/// nobody configured is not broken; a missing operators group is not an error in the agent; and a
/// certificate store that could not be opened is a different thing again from a certificate that is
/// absent. Collapsing these is how a diagnostic screen starts lying.
/// </summary>
public enum AgentDiagnosticState
{
    /// <summary>Present, valid, and doing its job.</summary>
    Ready,

    /// <summary>Present but incomplete, or configured in a way that will not work as intended.</summary>
    Attention,

    /// <summary>Nothing has been set up. Not a fault — most installations never configure HTTPS.</summary>
    NotConfigured,

    /// <summary>The question could not be answered, or the answer was a failure.</summary>
    Error,
}

/// <summary>
/// One line of the diagnostics view. <paramref name="Key"/> is a stable identifier the UI localizes
/// against; <paramref name="Detail"/> is the already-resolved specific text — a version, a service
/// state, a reason.
/// </summary>
public sealed record AgentDiagnosticItem(string Key, AgentDiagnosticState State, string? Detail = null)
{
    public static AgentDiagnosticItem Ready(string key, string? detail = null) =>
        new(key, AgentDiagnosticState.Ready, detail);

    public static AgentDiagnosticItem Attention(string key, string? detail = null) =>
        new(key, AgentDiagnosticState.Attention, detail);

    public static AgentDiagnosticItem NotConfigured(string key, string? detail = null) =>
        new(key, AgentDiagnosticState.NotConfigured, detail);

    public static AgentDiagnosticItem Error(string key, string? detail = null) =>
        new(key, AgentDiagnosticState.Error, detail);
}

/// <summary>
/// What kind of security authority this machine is, which decides what "create a local group" means
/// here.
///
/// A domain controller has no independent SAM: the groups it appears to hold locally are directory
/// objects, and creating one there changes the domain rather than the computer. That is a materially
/// different act and the utility must say so before performing it, which is why this is a first-class
/// answer rather than something inferred afterwards from a failure.
/// </summary>
public enum AgentMachineRole
{
    /// <summary>Windows would not say. Treated as needing confirmation.</summary>
    Unknown = 0,
    StandaloneWorkstation = 1,
    MemberWorkstation = 2,
    StandaloneServer = 3,
    MemberServer = 4,

    /// <summary>Primary or backup domain controller. Creation reaches the directory.</summary>
    DomainController = 5,
}

/// <summary>
/// The Core-side mirror of Windows' <c>SID_NAME_USE</c>.
///
/// Deliberately a separate enum from Infrastructure's <c>WindowsAccountKind</c>: Core does not depend
/// on Infrastructure, and moving that existing public type would be a rename nobody asked for. The
/// adapter maps one onto the other in a single place.
/// </summary>
public enum AgentPrincipalKind
{
    Unknown = 0,
    User = 1,
    Group = 2,
    Domain = 3,
    Alias = 4,
    WellKnownGroup = 5,
    DeletedAccount = 6,
    Invalid = 7,
    Computer = 9,
}

/// <summary>The operators group as this machine currently holds it.</summary>
public sealed record AgentOperatorsGroupState(
    bool Exists,
    string GroupName,
    string? Sid,
    AgentMachineRole Role,
    string? Failure)
{
    /// <summary>
    /// Whether creating the group would reach beyond this computer. True on a domain controller, and
    /// true when Windows would not say what this machine is — an unknown authority is not one to
    /// create a security principal in without asking first.
    /// </summary>
    public bool CreationAffectsDirectory => Role is AgentMachineRole.DomainController or AgentMachineRole.Unknown;

    public static AgentOperatorsGroupState Missing(string groupName, AgentMachineRole role, string? failure = null) =>
        new(Exists: false, groupName, Sid: null, role, failure);
}

/// <summary>A name translated against Windows, or the reason it was not.</summary>
public sealed record AgentIdentityResolution(
    bool Resolved,
    string AccountName,
    string? Sid,
    AgentPrincipalKind Kind,
    string? Domain,
    string? Failure)
{
    /// <summary>
    /// Whether this is something that may be put into the operators group.
    ///
    /// Users and groups yes — an administrator who nests a domain group is doing an ordinary thing,
    /// and the agent already counts indirect membership. A computer, a domain, a deleted account or a
    /// name Windows could not classify: no. Membership of this group decides who may stop the service
    /// that keeps a UPS monitored, so a principal nobody can name is not one to add to it.
    /// </summary>
    public bool IsAddable =>
        Resolved && Kind is AgentPrincipalKind.User or AgentPrincipalKind.Group or AgentPrincipalKind.Alias;

    public static AgentIdentityResolution Unresolved(string accountName, string failure) =>
        new(Resolved: false, accountName, Sid: null, AgentPrincipalKind.Unknown, Domain: null, failure);
}

/// <summary>What happened when a member was added.</summary>
public enum AgentMembershipOutcome
{
    Added,

    /// <summary>Already in the group: the desired state, reached earlier. Not a failure.</summary>
    AlreadyMember,

    /// <summary>The name did not resolve, or resolved to something that may not be a member.</summary>
    Rejected,

    Failed,
}

public sealed record AgentMembershipResult(AgentMembershipOutcome Outcome, string AccountName, string? Detail = null)
{
    public bool Succeeded => Outcome is AgentMembershipOutcome.Added or AgentMembershipOutcome.AlreadyMember;
}

public sealed record AgentGroupCreationResult(bool Created, string GroupName, string? Sid, string? Failure);

/// <summary>
/// The one group this utility administers, and the operations it may perform on it.
///
/// The group name is not a parameter anywhere in this interface. It is the agent's own authorization
/// group, fixed by the agent's default, and a utility that could create or populate an arbitrary
/// local group would be a far larger security surface than this one is meant to be.
/// </summary>
public interface IAgentOperatorsGroupAdministration
{
    /// <summary>Read-only. Safe to call on every refresh.</summary>
    AgentOperatorsGroupState Describe();

    /// <summary>
    /// Creates the group. Only ever called from an explicit user action — never during a refresh,
    /// never as a side effect of an Apply, and never by the installer.
    /// </summary>
    AgentGroupCreationResult Create();

    /// <summary>Translates a name without changing anything.</summary>
    AgentIdentityResolution ResolveIdentity(string accountName);

    /// <summary>Adds a resolved principal to the group.</summary>
    AgentMembershipResult AddMember(string accountName);

    /// <summary>The members, so the screen can show back what it just changed.</summary>
    IReadOnlyList<string> ListMembers();
}

/// <summary>The service's own state, kept separate from every other fact about the installation.</summary>
public enum AgentServiceState
{
    /// <summary>Not registered on this machine at all.</summary>
    NotInstalled,
    Stopped,
    StartPending,
    Running,
    StopPending,
    Paused,

    /// <summary>Registered, but the SCM would not say. Never treated as running.</summary>
    Unknown,
}

/// <summary>
/// What the SCM says about NutManagerAgent.
///
/// Installed and running are separate fields because they are separate facts: the installer registers
/// the service and deliberately does not start it, so an installation reporting
/// <see cref="AgentServiceState.Stopped"/> is a correct one rather than a broken one.
/// </summary>
public sealed record AgentServiceSnapshot(
    AgentServiceState State,
    string? StartMode,
    string? Failure,
    AgentServiceStartType StartType = AgentServiceStartType.Unknown,
    string? Account = null,
    int? QueryErrorCode = null)
{
    public bool IsInstalled => State is not AgentServiceState.NotInstalled;

    public bool IsRunning => State is AgentServiceState.Running;

    public static AgentServiceSnapshot NotInstalled(string? failure = null) =>
        new(AgentServiceState.NotInstalled, StartMode: null, failure);
}

/// <summary>
/// How Windows says the service starts, as read.
///
/// Separate from <see cref="AgentServiceStartupPreference"/> because reading and writing are not the
/// same set: a service can be found Disabled, and the utility can observe and report that, but it
/// offers no way to put one there. Unknown is what an unreadable configuration reports, and it is
/// never treated as a value somebody chose.
/// </summary>
public enum AgentServiceStartType
{
    Unknown,
    Boot,
    System,
    Automatic,
    Manual,
    Disabled,
}

/// <summary>
/// What an operator may choose, which is two things.
///
/// Disabled is deliberately absent. The switch on the settings page means "start with Windows or
/// not", and a service left Disabled cannot be started even deliberately afterwards - turning a
/// preference off must not take away the operator's ability to start the agent by hand.
/// </summary>
public enum AgentServiceStartupPreference
{
    Automatic,
    Manual,
}

public sealed record AgentServiceOutcome(bool Succeeded, AgentServiceState State, string? Failure);

/// <summary>
/// Start, stop and restart, for exactly one service.
///
/// There is no service-name parameter anywhere in this interface, and that is the security boundary
/// rather than a convenience: a method that took a name would be generic SCM administration reachable
/// from a text box. NutManagerAgent is the only service this utility touches, and the NUT service in
/// particular is never named, started, stopped or restarted by anything behind this contract.
/// </summary>
public interface IAgentServiceAdministration
{
    AgentServiceSnapshot Describe();

    Task<AgentServiceOutcome> StartAsync(CancellationToken cancellationToken);

    Task<AgentServiceOutcome> StopAsync(CancellationToken cancellationToken);

    Task<AgentServiceOutcome> RestartAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Changes how the service starts, and changes nothing else.
    ///
    /// It does not start a stopped service and does not stop a running one: the start type is what
    /// Windows does at boot, and an operator changing a boot preference has not asked for anything to
    /// happen now. The pair is deliberately narrow - two values, one service, no name parameter.
    /// </summary>
    Task<AgentServiceOutcome> SetStartupAsync(
        AgentServiceStartupPreference preference, CancellationToken cancellationToken);

    /// <summary>
    /// Registers NutManagerAgent with the service control manager.
    ///
    /// Deliberately takes nothing. Every value a generic service installer would accept - the name,
    /// the executable, the command line, the account, the start type - is fixed by the implementation,
    /// so there is no parameter through which this window could be made to register a different
    /// service or point an existing name at a different binary. A method with those parameters would
    /// be an arbitrary service installer with a button in front of it.
    ///
    /// It registers and stops there. The service is created stopped, and starting it stays the
    /// operator explicit action through <see cref="StartAsync"/> - the same boundary that keeps the
    /// product installer from starting what it installs.
    /// </summary>
    Task<AgentServiceInstallation> InstallAsync(CancellationToken cancellationToken);
}

/// <summary>What registering the service did, or why it did not.</summary>
public enum AgentServiceInstallOutcome
{
    /// <summary>The service was created. It is registered and stopped.</summary>
    Installed,

    /// <summary>
    /// A service of that name was already registered, so nothing was created.
    ///
    /// Never an overwrite. An existing NutManagerAgent belongs to whoever installed it, and silently
    /// repointing its image path from a button labelled "install" would be a repair nobody asked for
    /// - on a service this product may not own.
    /// </summary>
    AlreadyInstalled,

    Failed,
}

/// <summary>
/// The result of a registration attempt, with the Win32 error kept rather than flattened.
///
/// The code survives because the failures that matter here are distinguishable only by it: access
/// denied is a different problem from a service marked for deletion, and both read as "could not
/// install" without it.
/// </summary>
public sealed record AgentServiceInstallation(
    AgentServiceInstallOutcome Outcome,
    string? Failure = null,
    int? ErrorCode = null)
{
    public bool Succeeded => Outcome is AgentServiceInstallOutcome.Installed;

    public static AgentServiceInstallation Installed { get; } = new(AgentServiceInstallOutcome.Installed);

    public static AgentServiceInstallation AlreadyInstalled { get; } =
        new(AgentServiceInstallOutcome.AlreadyInstalled);

    public static AgentServiceInstallation Failed(string failure, int? errorCode = null) =>
        new(AgentServiceInstallOutcome.Failed, failure, errorCode);
}

/// <summary>
/// Opens the product's own project page, and nothing else.
///
/// There is deliberately no URL parameter. A launcher that took one would be a general way to make
/// this process open an arbitrary target, reachable from anywhere the interface is injected; the one
/// address this product links to is a constant in the implementation, so there is nothing to pass and
/// nothing to get wrong. It is not a browser, a shell, or a file opener.
/// </summary>
public interface IAgentProjectPageLauncher
{
    /// <summary>The address the About surface displays, so the text and the target cannot disagree.</summary>
    string ProjectPageUrl { get; }

    /// <summary>Hands the project page to the operator's default browser. Failures are reported, not thrown.</summary>
    bool OpenProjectPage();
}
