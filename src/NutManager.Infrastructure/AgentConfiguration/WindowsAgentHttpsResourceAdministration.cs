using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NutManager.Core.Agent;

namespace NutManager.Infrastructure.AgentConfiguration;

/// <summary>
/// The HTTP.sys SSL binding, the URL reservation and the Windows Firewall rule that HTTPS needs.
///
/// These are the three things an administrator would otherwise create by hand with netsh, and by hand
/// is where HTTPS deployments go wrong: a binding on the wrong address, a reservation granted to a
/// person rather than to the service account, a firewall rule left behind after the port changed.
/// This class does all three through documented Windows APIs — HttpSetServiceConfiguration and the
/// firewall policy COM object — and never by building a command line. There is no netsh here, no
/// PowerShell, no sc, no cmd, and nowhere for a host name to become a process argument.
///
/// <para><b>Ownership is the governing rule.</b> Everything this class creates carries a marker only
/// NutManager writes: the SSL binding records <see cref="AgentHttpsResourceIdentity.HttpServiceAppId"/>
/// as its AppId, the firewall rule carries the product's rule name and grouping, and the URL
/// reservation is matched on its exact prefix together with the exact security descriptor this product
/// grants. Removal happens only when the marker matches. Anything else — a foreign owner, or a query
/// that failed and left the question unanswered — is skipped and reported, because deleting somebody
/// else's binding is not recoverable by clicking again.</para>
///
/// <para>The certificate is never touched. Not created, not imported, not deleted.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsAgentHttpsResourceAdministration : IAgentHttpsResourceAdministration
{
    // ---------------------------------------------------------------- HTTP.sys constants

    private const uint HttpInitializeConfig = 0x00000002;
    private const int HttpServiceConfigSslCertInfo = 1;
    private const int HttpServiceConfigUrlAclInfo = 2;
    private const int HttpServiceConfigQueryExact = 0;

    private const int ErrorSuccess = 0;
    private const int ErrorFileNotFound = 2;
    private const int ErrorAccessDenied = 5;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorAlreadyExists = 183;

    private const short AddressFamilyInternetwork = 2;

    /// <summary>Where HTTP.sys should look the certificate up. "MY" is LocalMachine\My.</summary>
    private const string CertificateStoreName = "MY";

    // ---------------------------------------------------------------- firewall constants

    private const string FirewallPolicyProgId = "HNetCfg.FwPolicy2";
    private const string FirewallRuleProgId = "HNetCfg.FWRule";

    private const int FirewallDirectionInbound = 1;
    private const int FirewallActionAllow = 1;
    private const int FirewallProtocolTcp = 6;
    private const int FirewallProfileAll = 0x7FFFFFFF;

    // ---------------------------------------------------------------- labels used in results

    private const string SslBindingLabel = "HTTP.sys SSL binding";
    private const string UrlReservationLabel = "HTTP.sys URL reservation";
    private const string FirewallRuleLabel = "Windows Firewall rule";

    public AgentHttpsResourceSnapshot Describe(AgentHttpsBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);

        return new AgentHttpsResourceSnapshot(
            DescribeSslBinding(binding),
            DescribeUrlReservation(binding),
            DescribeFirewallRule(binding));
    }

    /// <summary>
    /// Creates or updates all three resources.
    ///
    /// Ordered so the bounded rollback below has something to undo: reservation, then binding, then
    /// firewall rule. A failure at any point reverses exactly what this call created and nothing that
    /// was already there — a reservation that existed beforehand survives a failed Apply untouched,
    /// because removing it would break whatever put it there.
    /// </summary>
    public AgentHttpsResourceResult Apply(AgentHttpsBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);

        var applied = new List<string>();
        var skipped = new List<string>();

        // What existed before this call, so rollback can tell "we made it" from "it was already here".
        var before = Describe(binding);

        try
        {
            if (!before.UrlReservation.MayConfigure)
            {
                return AgentHttpsResourceResult.Failed(
                    before.UrlReservation.Ownership is AgentResourceOwnership.ForeignOwner
                        ? $"A URL reservation for {binding.Prefix} already exists and was not created by NutManager. " +
                          "Remove it with the tool that created it, or choose a different port."
                        : $"Ownership of the URL reservation for {binding.Prefix} could not be determined. " +
                          "No resource was changed.",
                    applied, skipped);
            }

            if (!before.SslBinding.MayConfigure)
            {
                return AgentHttpsResourceResult.Failed(
                    before.SslBinding.Ownership is AgentResourceOwnership.ForeignOwner
                        ? $"An SSL certificate is already bound to port {binding.Port} by another application. " +
                          "Choose a different port, or remove that binding with the tool that created it."
                        : $"Ownership of the SSL binding on port {binding.Port} could not be determined. " +
                          "No resource was changed.",
                    applied, skipped);
            }

            if (!TrySetUrlReservation(binding, out var reservationFailure))
            {
                return AgentHttpsResourceResult.Failed(reservationFailure!, applied, skipped);
            }

            if (before.UrlReservation.Ownership is AgentResourceOwnership.Absent) applied.Add(UrlReservationLabel);

            if (!TrySetSslBinding(binding, out var bindingFailure))
            {
                RollBack(binding, applied, before);
                return AgentHttpsResourceResult.Failed(bindingFailure!, [], skipped);
            }

            if (before.SslBinding.Ownership is AgentResourceOwnership.Absent) applied.Add(SslBindingLabel);

            if (!before.FirewallRule.MayConfigure)
            {
                // Foreign or unreadable: left exactly as it is. A failed ownership query must never
                // become permission to remove a rule by name.
                skipped.Add(before.FirewallRule.Ownership is AgentResourceOwnership.ForeignOwner
                    ? $"{FirewallRuleLabel} '{AgentHttpsResourceIdentity.FirewallRuleName}' already exists and is not NutManager's."
                    : $"{FirewallRuleLabel} ownership could not be determined and it was left unchanged.");
            }
            else if (!TryWriteFirewallRule(binding, out var firewallFailure))
            {
                RollBack(binding, applied, before);
                return AgentHttpsResourceResult.Failed(firewallFailure!, [], skipped);
            }
            else if (before.FirewallRule.Ownership is AgentResourceOwnership.Absent)
            {
                applied.Add(FirewallRuleLabel);
            }

            return AgentHttpsResourceResult.Success(applied, skipped);
        }
        catch (Exception exception)
        {
            RollBack(binding, applied, before);
            return AgentHttpsResourceResult.Failed($"The HTTPS resources could not be configured ({exception.GetType().Name}).", [], skipped);
        }
    }

    public AgentHttpsResourceResult Remove(AgentHttpsBinding binding, AgentHttpsCleanupRequest request)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(request);

        var removed = new List<string>();
        var skipped = new List<string>();
        var current = Describe(binding);

        if (request.RemoveFirewallRule)
        {
            RemoveOne(current.FirewallRule, FirewallRuleLabel, removed, skipped,
                () => TryDeleteFirewallRule(out var failure) ? null : failure);
        }

        if (request.RemoveSslBinding)
        {
            RemoveOne(current.SslBinding, SslBindingLabel, removed, skipped,
                () => TryDeleteSslBinding(binding, out var failure) ? null : failure);
        }

        if (request.RemoveUrlReservation)
        {
            RemoveOne(current.UrlReservation, UrlReservationLabel, removed, skipped,
                () => TryDeleteUrlReservation(binding, out var failure) ? null : failure);
        }

        return AgentHttpsResourceResult.Success(removed, skipped);
    }

    /// <summary>
    /// One removal, gated on ownership.
    ///
    /// The gate is <see cref="AgentResourceState.MayRemove"/>, true only for
    /// <see cref="AgentResourceOwnership.OwnedByNutManager"/>. Absent needs nothing done; foreign and
    /// unknown are both left in place and reported, because "this is not mine" and "I could not tell
    /// whose this is" have the same correct outcome.
    /// </summary>
    private static void RemoveOne(
        AgentResourceState state, string label, List<string> removed, List<string> skipped, Func<string?> remove)
    {
        switch (state.Ownership)
        {
            case AgentResourceOwnership.Absent:
                return;

            case AgentResourceOwnership.OwnedByNutManager:
                var failure = remove();
                if (failure is null) removed.Add(label);
                else skipped.Add(failure);
                return;

            case AgentResourceOwnership.ForeignOwner:
                skipped.Add($"{label} was left in place: it was not created by NutManager.");
                return;

            default:
                skipped.Add($"{label} was left in place: its owner could not be determined.");
                return;
        }
    }

    /// <summary>
    /// Undoes only what this Apply created.
    ///
    /// <paramref name="applied"/> holds the resources that did not exist beforehand, so a reservation
    /// or a rule that was already on the machine survives a failed Apply untouched. That is the bounded
    /// rollback this operation promises: it reverses its own changes and nothing else.
    /// </summary>
    private static void RollBack(AgentHttpsBinding binding, List<string> applied, AgentHttpsResourceSnapshot before)
    {
        foreach (var label in applied)
        {
            try
            {
                if (label == FirewallRuleLabel && before.FirewallRule.Ownership is AgentResourceOwnership.Absent)
                {
                    TryDeleteFirewallRule(out _);
                }
                else if (label == SslBindingLabel && before.SslBinding.Ownership is AgentResourceOwnership.Absent)
                {
                    TryDeleteSslBinding(binding, out _);
                }
                else if (label == UrlReservationLabel && before.UrlReservation.Ownership is AgentResourceOwnership.Absent)
                {
                    TryDeleteUrlReservation(binding, out _);
                }
            }
            catch (Exception)
            {
                // A rollback step that fails must not mask the original failure, which is the one the
                // operator needs to read. The resource state is reported honestly on the next refresh.
            }
        }

        applied.Clear();
    }

    // ---------------------------------------------------------------- SSL binding

    /// <summary>
    /// Who owns the certificate binding on this port.
    ///
    /// HTTP.sys stores the AppId verbatim and hands it back on query, so this is an exact proof rather
    /// than an inference: the GUID either is NutManager's or it is not.
    /// </summary>
    private static AgentResourceState DescribeSslBinding(AgentHttpsBinding binding)
    {
        var address = IntPtr.Zero;
        var output = IntPtr.Zero;

        try
        {
            if (!TryInitialize(out var initializeFailure))
            {
                return new AgentResourceState(AgentResourceOwnership.Unknown, initializeFailure);
            }

            address = AllocateSocketAddress(binding.Port);

            var query = new HttpServiceConfigSslQuery
            {
                QueryDesc = HttpServiceConfigQueryExact,
                KeyDesc = new HttpServiceConfigSslKey { IpPort = address },
                Token = 0,
            };

            var status = HttpQueryServiceConfiguration(IntPtr.Zero, HttpServiceConfigSslCertInfo,
                ref query, Marshal.SizeOf<HttpServiceConfigSslQuery>(), IntPtr.Zero, 0, out var length, IntPtr.Zero);

            if (status == ErrorFileNotFound) return AgentResourceState.Absent;
            if (status != ErrorInsufficientBuffer) return DescribeQueryFailure(status);

            output = Marshal.AllocHGlobal(length);
            status = HttpQueryServiceConfiguration(IntPtr.Zero, HttpServiceConfigSslCertInfo,
                ref query, Marshal.SizeOf<HttpServiceConfigSslQuery>(), output, length, out _, IntPtr.Zero);

            if (status == ErrorFileNotFound) return AgentResourceState.Absent;
            if (status != ErrorSuccess) return DescribeQueryFailure(status);

            var result = Marshal.PtrToStructure<HttpServiceConfigSslSet>(output);

            return result.ParamDesc.AppId == AgentHttpsResourceIdentity.HttpServiceAppId
                ? new AgentResourceState(AgentResourceOwnership.OwnedByNutManager, $"0.0.0.0:{binding.Port}")
                : new AgentResourceState(AgentResourceOwnership.ForeignOwner,
                    $"Port {binding.Port} is bound by another application (AppId {result.ParamDesc.AppId:B}).");
        }
        catch (Exception exception)
        {
            return new AgentResourceState(AgentResourceOwnership.Unknown, $"The SSL binding could not be read ({exception.GetType().Name}).");
        }
        finally
        {
            if (output != IntPtr.Zero) Marshal.FreeHGlobal(output);
            if (address != IntPtr.Zero) Marshal.FreeHGlobal(address);
            Terminate();
        }
    }

    private static bool TrySetSslBinding(AgentHttpsBinding binding, out string? failure)
    {
        var address = IntPtr.Zero;
        var hash = IntPtr.Zero;
        var store = IntPtr.Zero;

        try
        {
            if (!TryInitialize(out failure)) return false;

            address = AllocateSocketAddress(binding.Port);

            var thumbprint = Convert.FromHexString(AgentHttpsPrefixRules.NormalizeThumbprint(binding.CertificateThumbprint));
            hash = Marshal.AllocHGlobal(thumbprint.Length);
            Marshal.Copy(thumbprint, 0, hash, thumbprint.Length);

            store = Marshal.StringToHGlobalUni(CertificateStoreName);

            var configuration = new HttpServiceConfigSslSet
            {
                KeyDesc = new HttpServiceConfigSslKey { IpPort = address },
                ParamDesc = new HttpServiceConfigSslParam
                {
                    SslHashLength = (uint)thumbprint.Length,
                    SslHash = hash,
                    // The ownership stamp. Everything this class will later agree to delete is
                    // identified by this GUID coming back out of HTTP.sys.
                    AppId = AgentHttpsResourceIdentity.HttpServiceAppId,
                    SslCertStoreName = store,
                    DefaultCertCheckMode = 0,
                    DefaultRevocationFreshnessTime = 0,
                    DefaultRevocationUrlRetrievalTimeout = 0,
                    DefaultSslCtlIdentifier = IntPtr.Zero,
                    DefaultSslCtlStoreName = IntPtr.Zero,
                    DefaultFlags = 0,
                },
            };

            var status = HttpSetServiceConfiguration(IntPtr.Zero, HttpServiceConfigSslCertInfo,
                ref configuration, Marshal.SizeOf<HttpServiceConfigSslSet>(), IntPtr.Zero);

            if (status == ErrorAlreadyExists)
            {
                // HTTP.sys has no update operation: an existing binding is deleted and written again.
                // Reaching here means it was ours, because Apply refused a foreign binding before this.
                HttpDeleteServiceConfiguration(IntPtr.Zero, HttpServiceConfigSslCertInfo,
                    ref configuration, Marshal.SizeOf<HttpServiceConfigSslSet>(), IntPtr.Zero);

                status = HttpSetServiceConfiguration(IntPtr.Zero, HttpServiceConfigSslCertInfo,
                    ref configuration, Marshal.SizeOf<HttpServiceConfigSslSet>(), IntPtr.Zero);
            }

            if (status != ErrorSuccess)
            {
                failure = status == ErrorAccessDenied
                    ? "The SSL binding could not be written: access was denied. Run NutManager Agent Config as an administrator."
                    : $"The SSL binding for port {binding.Port} could not be written (Windows status {status}).";
                return false;
            }

            failure = null;
            return true;
        }
        catch (FormatException)
        {
            failure = "The certificate thumbprint is not a hexadecimal value.";
            return false;
        }
        catch (Exception exception)
        {
            failure = $"The SSL binding could not be written ({exception.GetType().Name}).";
            return false;
        }
        finally
        {
            if (store != IntPtr.Zero) Marshal.FreeHGlobal(store);
            if (hash != IntPtr.Zero) Marshal.FreeHGlobal(hash);
            if (address != IntPtr.Zero) Marshal.FreeHGlobal(address);
            Terminate();
        }
    }

    private static bool TryDeleteSslBinding(AgentHttpsBinding binding, out string? failure)
    {
        var address = IntPtr.Zero;

        try
        {
            if (!TryInitialize(out failure)) return false;

            address = AllocateSocketAddress(binding.Port);

            var configuration = new HttpServiceConfigSslSet
            {
                KeyDesc = new HttpServiceConfigSslKey { IpPort = address },
            };

            var status = HttpDeleteServiceConfiguration(IntPtr.Zero, HttpServiceConfigSslCertInfo,
                ref configuration, Marshal.SizeOf<HttpServiceConfigSslSet>(), IntPtr.Zero);

            if (status is ErrorSuccess or ErrorFileNotFound)
            {
                failure = null;
                return true;
            }

            failure = $"The SSL binding for port {binding.Port} could not be removed (Windows status {status}).";
            return false;
        }
        catch (Exception exception)
        {
            failure = $"The SSL binding could not be removed ({exception.GetType().Name}).";
            return false;
        }
        finally
        {
            if (address != IntPtr.Zero) Marshal.FreeHGlobal(address);
            Terminate();
        }
    }

    // ---------------------------------------------------------------- URL reservation

    /// <summary>
    /// Who owns the reservation for this exact prefix.
    ///
    /// There is no AppId on a URL reservation, so the marker is the pair this product writes: the exact
    /// prefix, and the exact security descriptor granting only LocalSystem. A reservation for our
    /// prefix carrying a different descriptor was granted by somebody else to somebody else, and is
    /// reported foreign rather than overwritten.
    /// </summary>
    private static AgentResourceState DescribeUrlReservation(AgentHttpsBinding binding)
    {
        var output = IntPtr.Zero;
        var prefix = IntPtr.Zero;

        try
        {
            if (!TryInitialize(out var initializeFailure))
            {
                return new AgentResourceState(AgentResourceOwnership.Unknown, initializeFailure);
            }

            prefix = Marshal.StringToHGlobalUni(binding.Prefix);

            var query = new HttpServiceConfigUrlAclQuery
            {
                QueryDesc = HttpServiceConfigQueryExact,
                KeyDesc = new HttpServiceConfigUrlAclKey { UrlPrefix = prefix },
                Token = 0,
            };

            var status = HttpQueryServiceConfiguration(IntPtr.Zero, HttpServiceConfigUrlAclInfo,
                ref query, Marshal.SizeOf<HttpServiceConfigUrlAclQuery>(), IntPtr.Zero, 0, out var length, IntPtr.Zero);

            if (status == ErrorFileNotFound) return AgentResourceState.Absent;
            if (status != ErrorInsufficientBuffer) return DescribeQueryFailure(status);

            output = Marshal.AllocHGlobal(length);
            status = HttpQueryServiceConfiguration(IntPtr.Zero, HttpServiceConfigUrlAclInfo,
                ref query, Marshal.SizeOf<HttpServiceConfigUrlAclQuery>(), output, length, out _, IntPtr.Zero);

            if (status == ErrorFileNotFound) return AgentResourceState.Absent;
            if (status != ErrorSuccess) return DescribeQueryFailure(status);

            var result = Marshal.PtrToStructure<HttpServiceConfigUrlAclSet>(output);
            var descriptor = result.ParamDesc.StringSecurityDescriptor == IntPtr.Zero
                ? null
                : Marshal.PtrToStringUni(result.ParamDesc.StringSecurityDescriptor);

            return string.Equals(descriptor, AgentHttpsResourceIdentity.UrlReservationSecurityDescriptor, StringComparison.OrdinalIgnoreCase)
                ? new AgentResourceState(AgentResourceOwnership.OwnedByNutManager, binding.Prefix)
                : new AgentResourceState(AgentResourceOwnership.ForeignOwner,
                    $"A reservation for {binding.Prefix} exists with a security descriptor NutManager did not write.");
        }
        catch (Exception exception)
        {
            return new AgentResourceState(AgentResourceOwnership.Unknown, $"The URL reservation could not be read ({exception.GetType().Name}).");
        }
        finally
        {
            if (output != IntPtr.Zero) Marshal.FreeHGlobal(output);
            if (prefix != IntPtr.Zero) Marshal.FreeHGlobal(prefix);
            Terminate();
        }
    }

    private static bool TrySetUrlReservation(AgentHttpsBinding binding, out string? failure)
    {
        var prefix = IntPtr.Zero;
        var descriptor = IntPtr.Zero;

        try
        {
            if (!TryInitialize(out failure)) return false;

            prefix = Marshal.StringToHGlobalUni(binding.Prefix);
            descriptor = Marshal.StringToHGlobalUni(AgentHttpsResourceIdentity.UrlReservationSecurityDescriptor);

            var configuration = new HttpServiceConfigUrlAclSet
            {
                KeyDesc = new HttpServiceConfigUrlAclKey { UrlPrefix = prefix },
                ParamDesc = new HttpServiceConfigUrlAclParam { StringSecurityDescriptor = descriptor },
            };

            var status = HttpSetServiceConfiguration(IntPtr.Zero, HttpServiceConfigUrlAclInfo,
                ref configuration, Marshal.SizeOf<HttpServiceConfigUrlAclSet>(), IntPtr.Zero);

            if (status == ErrorAlreadyExists)
            {
                // Already exactly what we would write. Apply refused a foreign descriptor before
                // reaching here, so this is our own reservation and there is nothing to change.
                failure = null;
                return true;
            }

            if (status != ErrorSuccess)
            {
                failure = status == ErrorAccessDenied
                    ? "The URL reservation could not be written: access was denied. Run NutManager Agent Config as an administrator."
                    : $"The URL reservation for {binding.Prefix} could not be written (Windows status {status}).";
                return false;
            }

            failure = null;
            return true;
        }
        catch (Exception exception)
        {
            failure = $"The URL reservation could not be written ({exception.GetType().Name}).";
            return false;
        }
        finally
        {
            if (descriptor != IntPtr.Zero) Marshal.FreeHGlobal(descriptor);
            if (prefix != IntPtr.Zero) Marshal.FreeHGlobal(prefix);
            Terminate();
        }
    }

    private static bool TryDeleteUrlReservation(AgentHttpsBinding binding, out string? failure)
    {
        var prefix = IntPtr.Zero;

        try
        {
            if (!TryInitialize(out failure)) return false;

            prefix = Marshal.StringToHGlobalUni(binding.Prefix);

            var configuration = new HttpServiceConfigUrlAclSet
            {
                KeyDesc = new HttpServiceConfigUrlAclKey { UrlPrefix = prefix },
            };

            var status = HttpDeleteServiceConfiguration(IntPtr.Zero, HttpServiceConfigUrlAclInfo,
                ref configuration, Marshal.SizeOf<HttpServiceConfigUrlAclSet>(), IntPtr.Zero);

            if (status is ErrorSuccess or ErrorFileNotFound)
            {
                failure = null;
                return true;
            }

            failure = $"The URL reservation for {binding.Prefix} could not be removed (Windows status {status}).";
            return false;
        }
        catch (Exception exception)
        {
            failure = $"The URL reservation could not be removed ({exception.GetType().Name}).";
            return false;
        }
        finally
        {
            if (prefix != IntPtr.Zero) Marshal.FreeHGlobal(prefix);
            Terminate();
        }
    }

    // ---------------------------------------------------------------- firewall

    /// <summary>
    /// Who owns the inbound rule.
    ///
    /// Matched on the product's fixed rule name, then confirmed by its grouping. A rule that happens to
    /// share the name but not the grouping belongs to something else and is never rewritten or removed
    /// — matching on the port alone, which is the obvious shortcut, is exactly how an unrelated rule
    /// gets deleted.
    /// </summary>
    private static AgentResourceState DescribeFirewallRule(AgentHttpsBinding binding)
    {
        try
        {
            var rule = FindFirewallRule(out var failure);
            if (failure is not null) return new AgentResourceState(AgentResourceOwnership.Unknown, failure);
            if (rule is null) return AgentResourceState.Absent;

            var grouping = GetProperty(rule, "Grouping") as string;
            if (!string.Equals(grouping, AgentHttpsResourceIdentity.FirewallRuleGroup, StringComparison.Ordinal))
            {
                return new AgentResourceState(AgentResourceOwnership.ForeignOwner,
                    $"A rule named '{AgentHttpsResourceIdentity.FirewallRuleName}' exists but is not grouped under NutManager.");
            }

            var ports = GetProperty(rule, "LocalPorts") as string;
            return new AgentResourceState(
                AgentResourceOwnership.OwnedByNutManager,
                $"TCP {ports ?? binding.Port.ToString(CultureInfo.InvariantCulture)}");
        }
        catch (Exception exception)
        {
            return new AgentResourceState(AgentResourceOwnership.Unknown, $"The firewall rule could not be read ({exception.GetType().Name}).");
        }
    }

    /// <summary>
    /// Writes the inbound rule, replacing our own previous one so a changed port does not leave the old
    /// opening behind.
    /// </summary>
    private static bool TryWriteFirewallRule(AgentHttpsBinding binding, out string? failure)
    {
        try
        {
            var policy = CreateFirewallPolicy(out failure);
            if (policy is null) return false;

            var rules = GetProperty(policy, "Rules");
            if (rules is null)
            {
                failure = "The Windows Firewall rule collection was not available.";
                return false;
            }

            // Ours by name and grouping, so removing before adding is a replacement rather than the
            // deletion of somebody else's rule: Apply refused a foreign rule before reaching here.
            TryRemoveFirewallRuleByName(rules);

            var ruleType = Type.GetTypeFromProgID(FirewallRuleProgId, throwOnError: false);
            if (ruleType is null)
            {
                failure = "The Windows Firewall COM interface is not available on this machine.";
                return false;
            }

            var rule = Activator.CreateInstance(ruleType);
            if (rule is null)
            {
                failure = "A Windows Firewall rule object could not be created.";
                return false;
            }

            SetProperty(rule, "Name", AgentHttpsResourceIdentity.FirewallRuleName);
            SetProperty(rule, "Description", AgentHttpsResourceIdentity.FirewallRuleDescription);
            SetProperty(rule, "Grouping", AgentHttpsResourceIdentity.FirewallRuleGroup);
            SetProperty(rule, "Protocol", FirewallProtocolTcp);
            SetProperty(rule, "LocalPorts", binding.Port.ToString(CultureInfo.InvariantCulture));
            SetProperty(rule, "Direction", FirewallDirectionInbound);
            SetProperty(rule, "Action", FirewallActionAllow);
            SetProperty(rule, "Profiles", FirewallProfileAll);
            SetProperty(rule, "Enabled", true);

            Invoke(rules, "Add", rule);

            failure = null;
            return true;
        }
        catch (Exception exception)
        {
            failure = $"The Windows Firewall rule could not be written ({exception.GetType().Name}).";
            return false;
        }
    }

    private static bool TryDeleteFirewallRule(out string? failure)
    {
        try
        {
            var policy = CreateFirewallPolicy(out failure);
            if (policy is null) return false;

            var rules = GetProperty(policy, "Rules");
            if (rules is null)
            {
                failure = "The Windows Firewall rule collection was not available.";
                return false;
            }

            TryRemoveFirewallRuleByName(rules);

            failure = null;
            return true;
        }
        catch (Exception exception)
        {
            failure = $"The Windows Firewall rule could not be removed ({exception.GetType().Name}).";
            return false;
        }
    }

    private static void TryRemoveFirewallRuleByName(object rules)
    {
        try
        {
            Invoke(rules, "Remove", AgentHttpsResourceIdentity.FirewallRuleName);
        }
        catch (Exception)
        {
            // Removing a rule that is not there throws, and that is the desired state already.
        }
    }

    private static object? FindFirewallRule(out string? failure)
    {
        var policy = CreateFirewallPolicy(out failure);
        if (policy is null) return null;

        var rules = GetProperty(policy, "Rules");
        if (rules is null)
        {
            failure = "The Windows Firewall rule collection was not available.";
            return null;
        }

        try
        {
            failure = null;
            return Invoke(rules, "Item", AgentHttpsResourceIdentity.FirewallRuleName);
        }
        catch (Exception)
        {
            // Item throws for a name that is not present, which is how absence arrives here.
            failure = null;
            return null;
        }
    }

    /// <summary>
    /// The firewall policy object.
    ///
    /// Reached through its ProgID and driven by name through IDispatch rather than through
    /// hand-declared COM interfaces. Declaring INetFwPolicy2 and INetFwRule means reproducing a dual
    /// interface's vtable exactly, and a member listed in the wrong order calls the wrong function on a
    /// firewall. A silent failure mode, about firewall rules, is what makes late binding the safer of
    /// the two here.
    /// </summary>
    private static object? CreateFirewallPolicy(out string? failure)
    {
        var policyType = Type.GetTypeFromProgID(FirewallPolicyProgId, throwOnError: false);
        if (policyType is null)
        {
            failure = "The Windows Firewall COM interface is not available on this machine.";
            return null;
        }

        var policy = Activator.CreateInstance(policyType);
        if (policy is null)
        {
            failure = "The Windows Firewall policy object could not be created.";
            return null;
        }

        failure = null;
        return policy;
    }

    private static object? GetProperty(object target, string name) =>
        target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, null, CultureInfo.InvariantCulture);

    private static void SetProperty(object target, string name, object value) =>
        target.GetType().InvokeMember(name, BindingFlags.SetProperty, null, target, [value], CultureInfo.InvariantCulture);

    private static object? Invoke(object target, string name, params object?[] arguments) =>
        target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, arguments, CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------- helpers

    private static AgentResourceState DescribeQueryFailure(int status) => status == ErrorAccessDenied
        ? new AgentResourceState(AgentResourceOwnership.Unknown, "Access was denied reading the HTTP.sys configuration.")
        : new AgentResourceState(AgentResourceOwnership.Unknown, $"The HTTP.sys configuration could not be read (Windows status {status}).");

    private static bool TryInitialize(out string? failure)
    {
        var version = new HttpApiVersion { Major = 1, Minor = 0 };
        var status = HttpInitialize(version, HttpInitializeConfig, IntPtr.Zero);

        if (status == ErrorSuccess)
        {
            failure = null;
            return true;
        }

        failure = $"The HTTP.sys configuration interface could not be opened (Windows status {status}).";
        return false;
    }

    private static void Terminate()
    {
        try
        {
            HttpTerminate(HttpInitializeConfig, IntPtr.Zero);
        }
        catch (Exception)
        {
            // Termination failing changes neither what the caller learned nor what was written.
        }
    }

    /// <summary>
    /// A SOCKADDR_IN for 0.0.0.0 on this port, which is how HTTP.sys keys a certificate binding.
    ///
    /// The wildcard address here is not the wildcard the prefix rules refuse. A certificate binding is
    /// keyed by address and port, and binding every local address is what lets the agent answer on the
    /// name its certificate carries. The <em>prefix</em> still names one explicit host, and the prefix
    /// is what decides which requests HTTP.sys routes to the agent at all.
    /// </summary>
    private static IntPtr AllocateSocketAddress(int port)
    {
        var address = new SockAddrIn
        {
            Family = AddressFamilyInternetwork,
            // Network byte order, which is the opposite of this machine's.
            Port = (ushort)System.Net.IPAddress.HostToNetworkOrder((short)port),
            Address = 0,
        };

        var buffer = Marshal.AllocHGlobal(Marshal.SizeOf<SockAddrIn>());
        Marshal.StructureToPtr(address, buffer, false);
        return buffer;
    }

    // ---------------------------------------------------------------- interop

    [DllImport("httpapi.dll", SetLastError = true)]
    private static extern int HttpInitialize(HttpApiVersion version, uint flags, IntPtr reserved);

    [DllImport("httpapi.dll", SetLastError = true)]
    private static extern int HttpTerminate(uint flags, IntPtr reserved);

    [DllImport("httpapi.dll", SetLastError = true)]
    private static extern int HttpSetServiceConfiguration(
        IntPtr handle, int configId, ref HttpServiceConfigSslSet configInformation, int configInformationLength, IntPtr overlapped);

    [DllImport("httpapi.dll", SetLastError = true)]
    private static extern int HttpSetServiceConfiguration(
        IntPtr handle, int configId, ref HttpServiceConfigUrlAclSet configInformation, int configInformationLength, IntPtr overlapped);

    [DllImport("httpapi.dll", SetLastError = true)]
    private static extern int HttpDeleteServiceConfiguration(
        IntPtr handle, int configId, ref HttpServiceConfigSslSet configInformation, int configInformationLength, IntPtr overlapped);

    [DllImport("httpapi.dll", SetLastError = true)]
    private static extern int HttpDeleteServiceConfiguration(
        IntPtr handle, int configId, ref HttpServiceConfigUrlAclSet configInformation, int configInformationLength, IntPtr overlapped);

    [DllImport("httpapi.dll", SetLastError = true)]
    private static extern int HttpQueryServiceConfiguration(
        IntPtr handle, int configId, ref HttpServiceConfigSslQuery input, int inputLength,
        IntPtr output, int outputLength, out int returnLength, IntPtr overlapped);

    [DllImport("httpapi.dll", SetLastError = true)]
    private static extern int HttpQueryServiceConfiguration(
        IntPtr handle, int configId, ref HttpServiceConfigUrlAclQuery input, int inputLength,
        IntPtr output, int outputLength, out int returnLength, IntPtr overlapped);

    [StructLayout(LayoutKind.Sequential)]
    private struct HttpApiVersion
    {
        public ushort Major;
        public ushort Minor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SockAddrIn
    {
        public short Family;
        public ushort Port;
        public uint Address;
        public long Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HttpServiceConfigSslKey
    {
        public IntPtr IpPort;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HttpServiceConfigSslParam
    {
        public uint SslHashLength;
        public IntPtr SslHash;
        public Guid AppId;
        public IntPtr SslCertStoreName;
        public uint DefaultCertCheckMode;
        public uint DefaultRevocationFreshnessTime;
        public uint DefaultRevocationUrlRetrievalTimeout;
        public IntPtr DefaultSslCtlIdentifier;
        public IntPtr DefaultSslCtlStoreName;
        public uint DefaultFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HttpServiceConfigSslSet
    {
        public HttpServiceConfigSslKey KeyDesc;
        public HttpServiceConfigSslParam ParamDesc;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HttpServiceConfigSslQuery
    {
        public int QueryDesc;
        public HttpServiceConfigSslKey KeyDesc;
        public uint Token;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HttpServiceConfigUrlAclKey
    {
        public IntPtr UrlPrefix;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HttpServiceConfigUrlAclParam
    {
        public IntPtr StringSecurityDescriptor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HttpServiceConfigUrlAclSet
    {
        public HttpServiceConfigUrlAclKey KeyDesc;
        public HttpServiceConfigUrlAclParam ParamDesc;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HttpServiceConfigUrlAclQuery
    {
        public int QueryDesc;
        public HttpServiceConfigUrlAclKey KeyDesc;
        public uint Token;
    }
}
