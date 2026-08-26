using System.ComponentModel;
using System.Runtime.Versioning;
using System.ServiceProcess;
using NutManager.Core.Administration;
using NutManager.Core.Agent;
using NutManager.Infrastructure.Platform.Windows;

namespace NutManager.Infrastructure.Agent;

/// <summary>
/// Starts, stops and reads the one service the resolver pinned, through the local SCM.
///
/// The verbs are the managed <see cref="ServiceController"/> ones and nothing else. There is no
/// process termination here, and its absence is deliberate rather than pending: a service that will
/// not stop is a fact an operator needs to see, and killing the process behind the SCM's back turns a
/// reportable problem into an inconsistent machine — with the SCM still believing it owns a process
/// that no longer exists. So a stop that does not complete is reported as a stop that did not
/// complete.
///
/// Every failure leaves here as a value. An exception escaping into the application service would
/// bypass the audit write that follows the call.
/// </summary>
public sealed class WindowsNutAgentServiceController : INutServiceController
{
    // Windows error numbers, not Windows APIs, so they stay on the platform-neutral side where the
    // mapping that reads them compiles and tests without a platform guard.
    public const int ErrorAccessDenied = 5;
    public const int ErrorServiceAlreadyRunning = 1056;
    public const int ErrorServiceDoesNotExist = 1060;
    public const int ErrorServiceNotActive = 1062;
    public const int ErrorServiceRequestTimeout = 1053;

    private readonly TimeProvider _time;

    public WindowsNutAgentServiceController(TimeProvider? timeProvider = null) =>
        _time = timeProvider ?? TimeProvider.System;

    public Task<NutAgentServiceStatus> GetStatusAsync(NutServiceTarget target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(NutAgentServiceStatus.Unavailable(Environment.MachineName, _time.GetUtcNow()));
        }

        return WindowsAgentServiceControl.GetStatusAsync(target, _time, cancellationToken);
    }

    public Task<NutServiceControlOutcome> StartAsync(NutServiceTarget target, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (!OperatingSystem.IsWindows()) return Task.FromResult(Unsupported());
        return WindowsAgentServiceControl.ControlAsync(target, ServiceControllerStatus.Running, timeout, cancellationToken);
    }

    public Task<NutServiceControlOutcome> StopAsync(NutServiceTarget target, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (!OperatingSystem.IsWindows()) return Task.FromResult(Unsupported());
        return WindowsAgentServiceControl.ControlAsync(target, ServiceControllerStatus.Stopped, timeout, cancellationToken);
    }

    /// <summary>
    /// Preserves a safe, locale-independent description of a failed status query. The wire payload
    /// carries the numeric Windows code and a fixed category; exception messages are deliberately
    /// excluded because they are localized and may contain environmental detail.
    /// </summary>
    public static NutAgentServiceQueryFailure MapStatusFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var code = GetWin32ErrorCode(exception);
        var kind = exception is System.ServiceProcess.TimeoutException or System.TimeoutException ||
                   code == ErrorServiceRequestTimeout
            ? NutAgentServiceQueryFailureKind.TimedOut
            : code switch
            {
                ErrorAccessDenied => NutAgentServiceQueryFailureKind.AccessDenied,
                ErrorServiceDoesNotExist => NutAgentServiceQueryFailureKind.ServiceDoesNotExist,
                not null => NutAgentServiceQueryFailureKind.WindowsFailure,
                _ => NutAgentServiceQueryFailureKind.Unknown
            };

        var detail = kind switch
        {
            NutAgentServiceQueryFailureKind.AccessDenied => "The SCM refused the status query.",
            NutAgentServiceQueryFailureKind.ServiceDoesNotExist => "The pinned service no longer exists.",
            NutAgentServiceQueryFailureKind.TimedOut => "The service status query timed out.",
            NutAgentServiceQueryFailureKind.WindowsFailure => "The Windows service status query failed.",
            _ => "The service status query failed."
        };

        return new NutAgentServiceQueryFailure(kind, code, exception.GetType().Name, detail);
    }

    /// <summary>
    /// Turns a failed control call into an outcome, by numeric code rather than by message: the
    /// message is localized on the server and the mapping must not depend on its language.
    ///
    /// The two "already there" codes are races, not errors. The application service checks the state
    /// before acting, and a service can still move between that check and this call, so Windows saying
    /// "already running" to a start request is the requested state being true — reported as such.
    /// </summary>
    public static NutServiceControlOutcome MapFailure(Exception exception, bool startRequested, NutServiceState observed)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is System.ServiceProcess.TimeoutException)
        {
            return new NutServiceControlOutcome(
                NutAgentResultCode.TimedOut, observed, null, "The service did not reach the expected state in time.");
        }

        var code = GetWin32ErrorCode(exception);

        if (startRequested && code == ErrorServiceAlreadyRunning)
        {
            return new NutServiceControlOutcome(NutAgentResultCode.AlreadyInRequestedState, NutServiceState.Running, code);
        }

        if (!startRequested && code == ErrorServiceNotActive)
        {
            return new NutServiceControlOutcome(NutAgentResultCode.AlreadyInRequestedState, NutServiceState.Stopped, code);
        }

        var detail = code switch
        {
            // The agent runs as LocalSystem, so this means the service's own security descriptor
            // refuses it — a deployment fact worth naming rather than a generic failure.
            ErrorAccessDenied => "The SCM refused the control request.",
            ErrorServiceDoesNotExist => "The service no longer exists.",
            ErrorServiceRequestTimeout => "The service did not respond to the control request.",
            _ => $"The service control call failed ({exception.GetType().Name})."
        };

        return new NutServiceControlOutcome(NutAgentResultCode.ServiceControlFailed, observed, code, detail);
    }

    private static int? GetWin32ErrorCode(Exception exception) =>
        (exception as Win32Exception ?? exception.InnerException as Win32Exception)?.NativeErrorCode;

    private static NutServiceControlOutcome Unsupported() => new(
        NutAgentResultCode.Failed, NutServiceState.Unknown, null, "The agent only runs on Windows.");
}

/// <summary>
/// The Windows-typed half of the controller, behind one annotation for the reason T34 established:
/// the platform guard on the public method does not follow the call into a lambda.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsAgentServiceControl
{
    internal static Task<NutAgentServiceStatus> GetStatusAsync(NutServiceTarget target, TimeProvider time, CancellationToken cancellationToken) =>
        Task.Run(() => GetStatus(target, time, cancellationToken), cancellationToken);

    private static NutAgentServiceStatus GetStatus(NutServiceTarget target, TimeProvider time, CancellationToken cancellationToken)
    {
        var machine = Environment.MachineName;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var service = new ServiceController(target.ServiceName);
            service.Refresh();
            var state = WindowsRemoteServiceQuery.ToState(service.Status);
            var processId = TryGetProcessId(target.ServiceName);

            return new NutAgentServiceStatus(
                machine,
                target.ServiceName,
                target.DisplayName,
                state,
                processId,
                WindowsRemoteNutServiceProbe.ExecutableNameOf(target.BinaryPath),
                true,
                time.GetUtcNow());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // The pin is still valid — only this read failed — so the target is still reported as
            // validated, with the state it could not be shown to have left as Unknown. The safe
            // failure category and numeric Win32 code survive on the wire for diagnosis.
            return new NutAgentServiceStatus(
                machine, target.ServiceName, target.DisplayName, NutServiceState.Unknown, null,
                WindowsRemoteNutServiceProbe.ExecutableNameOf(target.BinaryPath), true, time.GetUtcNow(),
                WindowsNutAgentServiceController.MapStatusFailure(exception));
        }
    }

    /// <summary>
    /// Issues one control request and waits for the service to settle.
    ///
    /// Scheduled on <see cref="CancellationToken.None"/> on purpose. Once <c>Stop</c> has been called
    /// the machine is already changing, and abandoning the wait would only mean nobody observes the
    /// result — the service stops either way. Cancellation is honoured before the request, never
    /// after it.
    /// </summary>
    internal static Task<NutServiceControlOutcome> ControlAsync(
        NutServiceTarget target,
        ServiceControllerStatus desired,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        Task.Run(() => Control(target, desired, timeout, cancellationToken), CancellationToken.None);

    private static NutServiceControlOutcome Control(
        NutServiceTarget target,
        ServiceControllerStatus desired,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startRequested = desired == ServiceControllerStatus.Running;
        using var service = TryOpen(target.ServiceName, out var openFailure);
        if (service is null)
        {
            return WindowsNutAgentServiceController.MapFailure(openFailure!, startRequested, NutServiceState.Unknown);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (startRequested) service.Start();
            else service.Stop();

            service.WaitForStatus(desired, timeout);
            service.Refresh();

            return new NutServiceControlOutcome(NutAgentResultCode.Success, WindowsRemoteServiceQuery.ToState(service.Status));
        }
        catch (OperationCanceledException)
        {
            // Cancelled before the control request was issued, so nothing was changed.
            return new NutServiceControlOutcome(
                NutAgentResultCode.Failed, ObserveState(service), null, "The operation was cancelled before it was issued.");
        }
        catch (Exception exception)
        {
            // The observed state matters most here: a restart whose stop timed out must report where
            // the service actually is, not where the request wanted it.
            return WindowsNutAgentServiceController.MapFailure(exception, startRequested, ObserveState(service));
        }
    }

    private static ServiceController? TryOpen(string serviceName, out Exception? failure)
    {
        try
        {
            var service = new ServiceController(serviceName);
            service.Refresh();
            failure = null;
            return service;
        }
        catch (Exception exception)
        {
            failure = exception;
            return null;
        }
    }

    private static NutServiceState ObserveState(ServiceController service)
    {
        try
        {
            service.Refresh();
            return WindowsRemoteServiceQuery.ToState(service.Status);
        }
        catch (Exception)
        {
            return NutServiceState.Unknown;
        }
    }

    /// <summary>
    /// The process id from the local SCM, through the same query-only interop T34 introduced. A
    /// failure degrades to null: the service state is already known and is worth reporting without it.
    /// </summary>
    private static int? TryGetProcessId(string serviceName)
    {
        try
        {
            using var manager = WindowsServiceControlManagerInterop.OpenServiceControlManager(".");
            if (manager.IsInvalid) return null;

            using var service = WindowsServiceControlManagerInterop.OpenServiceForQuery(manager, serviceName);
            return service.IsInvalid ? null : WindowsServiceControlManagerInterop.TryGetProcessId(service);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
