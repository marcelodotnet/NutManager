using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using NutManager.Core.Agent;

namespace NutManager.Infrastructure.AgentConfiguration;

/// <summary>
/// Imports one operator-selected certificate into <c>LocalMachine\My</c> without invoking a shell.
///
/// PKCS#12 private keys are loaded into the machine key set and persisted before the certificate is
/// added to the store. CER/CRT files are also accepted so an administrator can inspect them in the
/// Config utility, but the shared certificate rules continue to reject them for HTTPS when no private
/// key is present.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsAgentCertificateImporter : IAgentCertificateImporter
{
    private const int ErrorInvalidPasswordHResult = unchecked((int)0x80070056);

    /// <summary>E_ACCESSDENIED, which is what the store itself reports when it is opened for writing.</summary>
    private const int ErrorAccessDeniedHResult = unchecked((int)0x80070005);

    /// <summary>
    /// NTE_PERM. The machine key set reports a refusal with its own code rather than E_ACCESSDENIED,
    /// and both messages read "Access denied" — so classifying on the message would work in English
    /// and quietly stop working on a Portuguese server. The codes are the same everywhere.
    /// </summary>
    private const int KeySetAccessDeniedHResult = unchecked((int)0x80090010);

    private readonly IWindowsAgentCertificateStoreWriter _store;
    private readonly X509KeyStorageFlags _pkcs12KeyStorageFlags;

    public WindowsAgentCertificateImporter()
        : this(
            new LocalMachineAgentCertificateStoreWriter(),
            X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet)
    {
    }

    internal WindowsAgentCertificateImporter(IWindowsAgentCertificateStoreWriter store)
        : this(store, X509KeyStorageFlags.EphemeralKeySet)
    {
    }

    internal WindowsAgentCertificateImporter(
        IWindowsAgentCertificateStoreWriter store,
        X509KeyStorageFlags pkcs12KeyStorageFlags)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _pkcs12KeyStorageFlags = pkcs12KeyStorageFlags;
    }

    public AgentCertificateImportResult Import(string path, string? password)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return AgentCertificateImportResult.From(AgentCertificateImportOutcome.InvalidFile);
        }

        var extension = Path.GetExtension(path);
        var isPkcs12 = extension.Equals(".pfx", StringComparison.OrdinalIgnoreCase) ||
                       extension.Equals(".p12", StringComparison.OrdinalIgnoreCase);
        var isCertificate = extension.Equals(".cer", StringComparison.OrdinalIgnoreCase) ||
                            extension.Equals(".crt", StringComparison.OrdinalIgnoreCase);

        if (!isPkcs12 && !isCertificate)
        {
            return AgentCertificateImportResult.From(AgentCertificateImportOutcome.UnsupportedFile);
        }

        X509Certificate2 certificate;

        try
        {
            certificate = isPkcs12
                ? X509CertificateLoader.LoadPkcs12FromFile(
                    path,
                    password ?? string.Empty,
                    _pkcs12KeyStorageFlags)
                : X509CertificateLoader.LoadCertificateFromFile(path);
        }
        catch (CryptographicException exception) when (
            isPkcs12 && password is null && IsPasswordFailure(exception))
        {
            // A protected PFX is retried only after the UI obtains a password from its masked dialog.
            // No password value enters this result or any persisted state.
            return AgentCertificateImportResult.From(AgentCertificateImportOutcome.PasswordRequired);
        }
        catch (CryptographicException exception) when (
            isPkcs12 && IsPasswordFailure(exception))
        {
            return AgentCertificateImportResult.From(AgentCertificateImportOutcome.PasswordIncorrect);
        }
        catch (CryptographicException exception) when (IsAccessFailure(exception))
        {
            // The file parsed. Persisting its private key into the machine key set is what Windows
            // refused, and calling that an invalid certificate would send an administrator to fix a
            // file that was never broken.
            return Denied(exception);
        }
        catch (CryptographicException exception)
        {
            return AgentCertificateImportResult.From(
                AgentCertificateImportOutcome.InvalidFile, Describe(exception));
        }
        catch (UnauthorizedAccessException exception)
        {
            return Denied(exception);
        }
        catch (IOException exception)
        {
            return AgentCertificateImportResult.From(
                AgentCertificateImportOutcome.Failed, Describe(exception));
        }

        using (certificate)
        {
            try
            {
                return AgentCertificateImportResult.Imported(_store.Add(certificate));
            }
            catch (CryptographicException exception) when (IsAccessFailure(exception))
            {
                return Denied(exception);
            }
            catch (UnauthorizedAccessException exception)
            {
                return Denied(exception);
            }
            catch (CryptographicException exception)
            {
                return AgentCertificateImportResult.From(
                    AgentCertificateImportOutcome.Failed, Describe(exception));
            }
            catch (IOException exception)
            {
                return AgentCertificateImportResult.From(
                    AgentCertificateImportOutcome.Failed, Describe(exception));
            }
        }
    }

    private static AgentCertificateImportResult Denied(Exception exception) =>
        AgentCertificateImportResult.From(AgentCertificateImportOutcome.AccessDenied, Describe(exception));

    /// <summary>
    /// What goes in the technical detail: the exception type and its HRESULT, never its message.
    ///
    /// A cryptographic exception's message is written by the platform and can name a file path, a key
    /// container or a store; the type and the code identify the fault just as well for a support log
    /// and cannot carry a password, a key or a path with them.
    /// </summary>
    private static string Describe(Exception exception) =>
        $"{exception.GetType().Name} (0x{exception.HResult:X8})";

    private static bool IsPasswordFailure(CryptographicException exception) =>
        exception.HResult == ErrorInvalidPasswordHResult;

    private static bool IsAccessFailure(Exception exception) =>
        exception.HResult is ErrorAccessDeniedHResult or KeySetAccessDeniedHResult;
}

/// <summary>Small seam that keeps normal tests away from the real machine store.</summary>
internal interface IWindowsAgentCertificateStoreWriter
{
    AgentCertificateSummary Add(X509Certificate2 certificate);
}

[SupportedOSPlatform("windows")]
internal sealed class LocalMachineAgentCertificateStoreWriter : IWindowsAgentCertificateStoreWriter
{
    public AgentCertificateSummary Add(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);

        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);
        store.Add(certificate);
        return WindowsAgentCertificateCatalog.Describe(certificate);
    }
}
