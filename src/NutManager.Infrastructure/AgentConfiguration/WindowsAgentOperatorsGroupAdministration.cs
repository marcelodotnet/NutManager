using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using NutManager.Core.Agent;
using NutManager.Infrastructure.Agent;

namespace NutManager.Infrastructure.AgentConfiguration;

/// <summary>
/// The operators group, as the local configuration utility is allowed to change it.
///
/// This file exists separately from <see cref="WindowsAgentGroupInterop"/> on purpose, and the split
/// is a security boundary rather than an organisational preference. That file is the agent service's
/// view of Windows and holds only calls that read; this one holds the two that write, and it is
/// compiled into a utility a person launches behind a consent prompt. The long-running privileged
/// service therefore never gains the ability to create a security principal or grant anyone
/// membership, whatever a caller might talk it into.
///
/// Three rules hold throughout:
///
///   - The group name is never a parameter from outside. It is the agent's own authorization group,
///     and a utility that could populate an arbitrary local group is a different product.
///   - Membership is granted by SID. LOCALGROUP_MEMBERS_INFO_0 takes the binary SID rather than a
///     name, so the principal that was resolved and inspected is exactly the principal that is added
///     — a name resolved twice can resolve to two different things.
///   - Nothing here is called during a refresh. Creation and membership happen only from a click.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsAgentOperatorsGroupAdministration : IAgentOperatorsGroupAdministration
{
    private const int NerrSuccess = 0;
    private const int NerrGroupExists = 2223;
    private const int ErrorAliasExists = 1379;
    private const int ErrorMemberInAlias = 1378;
    private const int ErrorAccessDenied = 5;
    private const int LocalGroupInfoLevelComment = 1;
    private const int LocalGroupMembersInfoLevelSid = 0;
    private const int LocalGroupMembersInfoLevelDomainAndName = 3;
    private const int MaxPreferredLength = -1;
    private const int DsRolePrimaryDomainInfoBasic = 1;

    private const string GroupComment =
        "Members may administer the NUT service through the NutManager Agent.";

    private readonly string _groupName;
    private readonly IWindowsLocalSecurityDatabase _database;

    public WindowsAgentOperatorsGroupAdministration()
        : this(WindowsGroupAuthorization.DefaultGroupName, new WindowsAgentGroupInterop())
    {
    }

    /// <summary>
    /// The database is injectable for the same reason it is on the agent's authorization: a member
    /// server and a domain controller differ in what Windows answers, not in what this class does with
    /// the answer, and one test machine can only ever be one of them.
    /// </summary>
    internal WindowsAgentOperatorsGroupAdministration(string groupName, IWindowsLocalSecurityDatabase database)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupName);
        ArgumentNullException.ThrowIfNull(database);

        _groupName = groupName;
        _database = database;
    }

    public string GroupName => _groupName;

    public AgentOperatorsGroupState Describe()
    {
        var role = ReadMachineRole();

        // Exactly the agent's own resolution: prove the name is a group this machine's local database
        // holds, then translate it, then insist the result really is a group. Asking the same question
        // the same way is what stops the utility reporting a group the agent will not accept.
        var (sid, failure) = WindowsLocalGroupResolution.Resolve(_groupName, _database);

        return sid is null
            ? AgentOperatorsGroupState.Missing(_groupName, role, failure)
            : new AgentOperatorsGroupState(Exists: true, _groupName, sid.Value, role, Failure: null);
    }

    /// <summary>
    /// Creates the group in this machine's local database.
    ///
    /// NULL for the server name is the documented way of saying "this computer", and it is what makes
    /// the same call correct on a workstation, on a member server and on a domain controller. On a DC
    /// there is no independent SAM, so this reaches the directory — which is why
    /// <see cref="AgentOperatorsGroupState.CreationAffectsDirectory"/> exists and the caller confirms
    /// before ever getting here.
    /// </summary>
    public AgentGroupCreationResult Create()
    {
        var info = new LocalGroupInfo1 { Name = _groupName, Comment = GroupComment };

        try
        {
            var status = NetLocalGroupAdd(null, LocalGroupInfoLevelComment, ref info, out _);

            if (status is NerrGroupExists or ErrorAliasExists)
            {
                // Somebody else created it, or it was there all along. The desired state either way, so
                // resolve and report success rather than an error about a race nobody lost.
                var existing = Describe();
                return existing.Exists
                    ? new AgentGroupCreationResult(Created: false, _groupName, existing.Sid, Failure: null)
                    : new AgentGroupCreationResult(false, _groupName, null, $"The group '{_groupName}' already exists but could not be resolved.");
            }

            if (status != NerrSuccess)
            {
                return new AgentGroupCreationResult(false, _groupName, null, DescribeStatus(status, $"The group '{_groupName}' could not be created"));
            }

            // Read it back rather than assume. "The create call returned success" and "the agent can
            // pin this group" are two different claims, and only the second one matters.
            var created = Describe();
            return created.Exists
                ? new AgentGroupCreationResult(Created: true, _groupName, created.Sid, Failure: null)
                : new AgentGroupCreationResult(false, _groupName, null, created.Failure ?? $"The group '{_groupName}' was created but could not be resolved.");
        }
        catch (Exception exception)
        {
            return new AgentGroupCreationResult(false, _groupName, null, $"The group '{_groupName}' could not be created ({exception.GetType().Name}).");
        }
    }

    public AgentIdentityResolution ResolveIdentity(string accountName)
    {
        if (string.IsNullOrWhiteSpace(accountName))
        {
            return AgentIdentityResolution.Unresolved(accountName ?? string.Empty, "No account name was supplied.");
        }

        var trimmed = accountName.Trim();
        var (sid, kind, domain, failure) = _database.LookupAccount(trimmed);

        if (string.IsNullOrWhiteSpace(sid))
        {
            return AgentIdentityResolution.Unresolved(trimmed, failure ?? $"'{trimmed}' could not be translated to a SID.");
        }

        return new AgentIdentityResolution(Resolved: true, trimmed, sid, ToPrincipalKind(kind), domain, Failure: null);
    }

    /// <summary>
    /// Adds a principal to the group, by SID.
    ///
    /// The name is resolved and classified first, and one that does not resolve is refused here rather
    /// than handed to Windows to fail on: "that account does not exist on this machine or in its
    /// domain" is a better answer than a NetAPI status number. Already being a member is reported as
    /// the success it is — the administrator wanted the account in the group, and it is.
    /// </summary>
    public AgentMembershipResult AddMember(string accountName)
    {
        var resolution = ResolveIdentity(accountName);

        if (!resolution.Resolved)
        {
            return new AgentMembershipResult(AgentMembershipOutcome.Rejected, resolution.AccountName, resolution.Failure);
        }

        if (!resolution.IsAddable)
        {
            return new AgentMembershipResult(
                AgentMembershipOutcome.Rejected,
                resolution.AccountName,
                $"'{resolution.AccountName}' resolved to a {resolution.Kind} rather than a user or a group.");
        }

        var identifier = new SecurityIdentifier(resolution.Sid!);
        var binary = new byte[identifier.BinaryLength];
        identifier.GetBinaryForm(binary, 0);

        var sidBuffer = Marshal.AllocHGlobal(binary.Length);
        try
        {
            Marshal.Copy(binary, 0, sidBuffer, binary.Length);
            var member = new LocalGroupMembersInfo0 { Sid = sidBuffer };

            var status = NetLocalGroupAddMembers(null, _groupName, LocalGroupMembersInfoLevelSid, ref member, 1);

            return status switch
            {
                NerrSuccess => new AgentMembershipResult(AgentMembershipOutcome.Added, resolution.AccountName),
                ErrorMemberInAlias => new AgentMembershipResult(AgentMembershipOutcome.AlreadyMember, resolution.AccountName),
                _ => new AgentMembershipResult(
                    AgentMembershipOutcome.Failed,
                    resolution.AccountName,
                    DescribeStatus(status, $"'{resolution.AccountName}' could not be added to '{_groupName}'")),
            };
        }
        catch (Exception exception)
        {
            return new AgentMembershipResult(
                AgentMembershipOutcome.Failed,
                resolution.AccountName,
                $"'{resolution.AccountName}' could not be added to '{_groupName}' ({exception.GetType().Name}).");
        }
        finally
        {
            Marshal.FreeHGlobal(sidBuffer);
        }
    }

    /// <summary>
    /// The group's direct members, resolved back to names for display.
    ///
    /// Direct only. The agent authorizes on indirect membership as well, but a list that silently
    /// expanded nested groups would show accounts this screen did not add and cannot remove.
    /// </summary>
    public IReadOnlyList<string> ListMembers()
    {
        var buffer = IntPtr.Zero;

        try
        {
            var status = NetLocalGroupGetMembers(
                null, _groupName, LocalGroupMembersInfoLevelDomainAndName,
                out buffer, MaxPreferredLength, out var read, out _, IntPtr.Zero);

            if (status != NerrSuccess || buffer == IntPtr.Zero || read <= 0) return [];

            var names = new List<string>(read);
            var size = Marshal.SizeOf<LocalGroupMembersInfo3>();
            for (var index = 0; index < read; index++)
            {
                var entry = Marshal.PtrToStructure<LocalGroupMembersInfo3>(IntPtr.Add(buffer, index * size));
                if (!string.IsNullOrWhiteSpace(entry.DomainAndName)) names.Add(entry.DomainAndName);
            }

            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }
        catch (Exception)
        {
            // A membership list that cannot be read is a display problem, not a security one. The
            // screen shows nothing rather than claiming the group is empty.
            return [];
        }
        finally
        {
            if (buffer != IntPtr.Zero) NetApiBufferFree(buffer);
        }
    }

    /// <summary>
    /// What kind of security authority this machine is.
    ///
    /// DsRoleGetPrimaryDomainInformation is the documented answer to "does this computer have its own
    /// SAM". Anything unreadable becomes <see cref="AgentMachineRole.Unknown"/>, which the caller
    /// treats as needing confirmation — guessing "ordinary workstation" is exactly the wrong direction
    /// when the consequence is writing to a directory.
    /// </summary>
    internal static AgentMachineRole ReadMachineRole()
    {
        var buffer = IntPtr.Zero;

        try
        {
            if (DsRoleGetPrimaryDomainInformation(null, DsRolePrimaryDomainInfoBasic, out buffer) != 0 || buffer == IntPtr.Zero)
            {
                return AgentMachineRole.Unknown;
            }

            var info = Marshal.PtrToStructure<DsRolePrimaryDomainInfoBasicStruct>(buffer);

            return info.MachineRole switch
            {
                0 => AgentMachineRole.StandaloneWorkstation,
                1 => AgentMachineRole.MemberWorkstation,
                2 => AgentMachineRole.StandaloneServer,
                3 => AgentMachineRole.MemberServer,
                // Backup and primary domain controller mean the same thing here: no independent local
                // SAM, so what looks like a local group is a directory object.
                4 or 5 => AgentMachineRole.DomainController,
                _ => AgentMachineRole.Unknown,
            };
        }
        catch (Exception)
        {
            return AgentMachineRole.Unknown;
        }
        finally
        {
            if (buffer != IntPtr.Zero) DsRoleFreeMemory(buffer);
        }
    }

    private static AgentPrincipalKind ToPrincipalKind(WindowsAccountKind kind) => kind switch
    {
        WindowsAccountKind.User => AgentPrincipalKind.User,
        WindowsAccountKind.Group => AgentPrincipalKind.Group,
        WindowsAccountKind.Domain => AgentPrincipalKind.Domain,
        WindowsAccountKind.Alias => AgentPrincipalKind.Alias,
        WindowsAccountKind.WellKnownGroup => AgentPrincipalKind.WellKnownGroup,
        WindowsAccountKind.DeletedAccount => AgentPrincipalKind.DeletedAccount,
        WindowsAccountKind.Invalid => AgentPrincipalKind.Invalid,
        WindowsAccountKind.Computer => AgentPrincipalKind.Computer,
        _ => AgentPrincipalKind.Unknown,
    };

    private static string DescribeStatus(int status, string prefix) => status switch
    {
        ErrorAccessDenied => $"{prefix}: access was denied. Run NutManager Agent Config as an administrator.",
        _ => $"{prefix} (Windows status {status}).",
    };

    // ---------------------------------------------------------------- interop

    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int NetLocalGroupAdd(
        [MarshalAs(UnmanagedType.LPWStr)] string? serverName,
        int level,
        ref LocalGroupInfo1 buffer,
        out int parameterError);

    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int NetLocalGroupAddMembers(
        [MarshalAs(UnmanagedType.LPWStr)] string? serverName,
        [MarshalAs(UnmanagedType.LPWStr)] string groupName,
        int level,
        ref LocalGroupMembersInfo0 buffer,
        int totalEntries);

    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int NetLocalGroupGetMembers(
        [MarshalAs(UnmanagedType.LPWStr)] string? serverName,
        [MarshalAs(UnmanagedType.LPWStr)] string groupName,
        int level,
        out IntPtr buffer,
        int preferredMaximumLength,
        out int entriesRead,
        out int totalEntries,
        IntPtr resumeHandle);

    [DllImport("Netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);

    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int DsRoleGetPrimaryDomainInformation(
        [MarshalAs(UnmanagedType.LPWStr)] string? server,
        int infoLevel,
        out IntPtr buffer);

    [DllImport("Netapi32.dll")]
    private static extern void DsRoleFreeMemory(IntPtr buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LocalGroupInfo1
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string Name;
        [MarshalAs(UnmanagedType.LPWStr)] public string Comment;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LocalGroupMembersInfo0
    {
        public IntPtr Sid;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LocalGroupMembersInfo3
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string DomainAndName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DsRolePrimaryDomainInfoBasicStruct
    {
        public int MachineRole;
        public uint Flags;
        public IntPtr DomainNameFlat;
        public IntPtr DomainNameDns;
        public IntPtr DomainForestName;
        public Guid DomainGuid;
    }
}
