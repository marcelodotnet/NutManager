using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NutManager.Core.Agent;

namespace NutManager.Infrastructure.AgentConfiguration;

/// <summary>
/// Registers NutManagerAgent with the service control manager, and can register nothing else.
///
/// Every value CreateService takes is a constant in this file or is derived from the running process:
/// the service name, the display name, the type, the start type, the error control, the account and
/// the command line. Nothing reaches this class from the window, and there is no overload that would
/// let it. A class that accepted a name and a path would be a general-purpose service installer that
/// happens to be reachable from an elevated GUI, which is a different and much larger thing to review.
///
/// The binary it registers is the process that is running. It is not looked up in the registry, not
/// read from configuration and not passed in - which is what makes "install" unable to point the name
/// at somebody else's executable. It is validated before use all the same: a host that cannot say
/// where it lives, or that is not the expected apphost, refuses rather than registering a guess.
///
/// It creates the service stopped. Starting it is a separate operator action, exactly as it is after
/// the product installer runs.
///
/// This is deliberately not everything the MSI does. The installer also owns the Event Log source, as
/// a registry key it may later remove, and the agent refuses to run without it rather than creating
/// its own - that fail-closed boundary is the reason this class does not create it either. Registering
/// the service is the part an application may honestly own.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsAgentServiceInstallation
{
    /// <summary>The one service, and the same name the product installer registers.</summary>
    internal const string ServiceName = WindowsAgentServiceAdministration.ServiceName;

    /// <summary>What services.msc shows, matching the installer's display name.</summary>
    internal const string DisplayName = "NutManager Agent";

    /// <summary>The only argument. The host resolves service mode from it and nothing else.</summary>
    internal const string ServiceArgument = "--service";

    /// <summary>The apphost this is allowed to register, by name.</summary>
    internal const string HostFileName = "NutManager.Agent.exe";

    private const uint ScManagerCreateService = 0x0002;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceWin32OwnProcess = 0x00000010;
    private const uint ServiceAutoStart = 0x00000002;
    private const uint ServiceErrorNormal = 0x00000001;

    internal const int ErrorServiceExists = 1073;
    internal const int ErrorServiceMarkedForDelete = 1072;

    private readonly Func<string?> _hostPath;
    private readonly Func<string, bool> _fileExists;

    internal WindowsAgentServiceInstallation()
        : this(() => Environment.ProcessPath, File.Exists)
    {
    }

    /// <summary>Test seam for the path rules only. The SCM call itself is never faked.</summary>
    internal WindowsAgentServiceInstallation(Func<string?> hostPath, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(hostPath);
        ArgumentNullException.ThrowIfNull(fileExists);

        _hostPath = hostPath;
        _fileExists = fileExists;
    }

    /// <summary>
    /// The command line the service would be registered with, or null when the host cannot be trusted.
    ///
    /// Separated from the registration so the rules that decide what may be registered can be
    /// exercised without a service control manager: this is the whole of the "arbitrary executable"
    /// question, and it is answered here.
    /// </summary>
    internal string? ResolveImagePath()
    {
        var path = _hostPath();

        if (string.IsNullOrWhiteSpace(path)) return null;

        // A relative path would resolve against whatever directory the SCM happens to use.
        if (!Path.IsPathFullyQualified(path)) return null;

        if (!string.Equals(Path.GetFileName(path), HostFileName, StringComparison.OrdinalIgnoreCase))
        {
            // Running under a different host - a test runner, a debugger stub, a renamed copy. The
            // name is the only thing that ties the process to the product, so a mismatch refuses.
            return null;
        }

        if (!_fileExists(path)) return null;

        // Quoted, because Program Files has a space in it and an unquoted image path lets Windows
        // try C:\Program.exe first. That is a well-known unquoted-service-path weakness and not one
        // this product is going to introduce from a button.
        return $"\"{path}\" {ServiceArgument}";
    }

    internal AgentServiceInstallation Install()
    {
        if (ResolveImagePath() is not { } imagePath)
        {
            return AgentServiceInstallation.Failed(
                $"The {ServiceName} service was not registered because the running program could not be " +
                $"identified as {HostFileName}.");
        }

        var manager = IntPtr.Zero;
        var service = IntPtr.Zero;

        try
        {
            manager = OpenSCManagerW(null, null, ScManagerCreateService);
            if (manager == IntPtr.Zero)
            {
                return Failure("the Service Control Manager could not be opened", Marshal.GetLastWin32Error());
            }

            service = CreateServiceW(
                manager,
                ServiceName,
                DisplayName,
                ServiceQueryStatus,
                ServiceWin32OwnProcess,
                ServiceAutoStart,
                ServiceErrorNormal,
                imagePath,
                lpLoadOrderGroup: null,
                lpdwTagId: IntPtr.Zero,
                lpDependencies: null,
                // Null is LocalSystem. Naming any other account would mean a password, and the
                // authorization model deliberately does not have one: the agent decides a caller's
                // rights by group membership rather than by whoever it runs as.
                lpServiceStartName: null,
                lpPassword: null);

            if (service != IntPtr.Zero) return AgentServiceInstallation.Installed;

            var error = Marshal.GetLastWin32Error();

            // Somebody registered it between the button being enabled and being pressed. Reported as
            // what it is, and never followed by a change to the service that is already there.
            if (error == ErrorServiceExists) return AgentServiceInstallation.AlreadyInstalled;

            return Failure("the service could not be registered", error);
        }
        catch (Exception exception)
        {
            return AgentServiceInstallation.Failed(
                $"The {ServiceName} service could not be registered ({exception.GetType().Name}).");
        }
        finally
        {
            if (service != IntPtr.Zero) CloseServiceHandle(service);
            if (manager != IntPtr.Zero) CloseServiceHandle(manager);
        }
    }

    private static AgentServiceInstallation Failure(string operation, int error) =>
        AgentServiceInstallation.Failed(
            $"The {ServiceName} service {operation} (Win32 error {error}: {new Win32Exception(error).Message}).",
            error);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr OpenSCManagerW(string? machineName, string? databaseName, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr CreateServiceW(
        IntPtr manager,
        string lpServiceName,
        string lpDisplayName,
        uint dwDesiredAccess,
        uint dwServiceType,
        uint dwStartType,
        uint dwErrorControl,
        string lpBinaryPathName,
        string? lpLoadOrderGroup,
        IntPtr lpdwTagId,
        string? lpDependencies,
        string? lpServiceStartName,
        string? lpPassword);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
