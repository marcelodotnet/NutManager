using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Runtime.Versioning;
using NutManager.Core.Agent;
using NutManager.Infrastructure.AgentConfiguration;
using Xunit;

namespace NutManager.Tests;

[SupportedOSPlatform("windows")]
public sealed class T41AgentCertificateImportTests
{
    private const string Host = "gandalf.sbra.local";

    [Fact]
    public void UnprotectedPfxImportsWithItsPrivateKey()
    {
        using var file = CertificateFile.Pfx(password: string.Empty);
        var writer = new CapturingStoreWriter();
        var importer = new WindowsAgentCertificateImporter(writer);

        var result = importer.Import(file.Path, password: null);

        Assert.Equal(AgentCertificateImportOutcome.Imported, result.Outcome);
        Assert.True(Assert.IsType<AgentCertificateSummary>(result.Certificate).HasPrivateKey);
        Assert.Equal(1, writer.AddCalls);
    }

    [Fact]
    public void ProtectedPfxRequestsAPasswordAndThenImports()
    {
        using var file = CertificateFile.Pfx(password: "correct horse battery staple");
        var writer = new CapturingStoreWriter();
        var importer = new WindowsAgentCertificateImporter(writer);

        var first = importer.Import(file.Path, password: null);
        var second = importer.Import(file.Path, "correct horse battery staple");

        Assert.Equal(AgentCertificateImportOutcome.PasswordRequired, first.Outcome);
        Assert.Equal(AgentCertificateImportOutcome.Imported, second.Outcome);
        Assert.Equal(1, writer.AddCalls);
    }

    [Fact]
    public void P12UsesTheSameProtectedPkcs12Workflow()
    {
        using var file = CertificateFile.Pfx(password: "p12-password", extension: ".p12");
        var importer = new WindowsAgentCertificateImporter(new CapturingStoreWriter());

        Assert.Equal(AgentCertificateImportOutcome.PasswordRequired, importer.Import(file.Path, null).Outcome);
        Assert.Equal(AgentCertificateImportOutcome.Imported, importer.Import(file.Path, "p12-password").Outcome);
    }

    [Fact]
    public void IncorrectPfxPasswordNeverReachesTheStore()
    {
        using var file = CertificateFile.Pfx(password: "right-password");
        var writer = new CapturingStoreWriter();
        var importer = new WindowsAgentCertificateImporter(writer);

        var result = importer.Import(file.Path, "wrong-password");

        Assert.Equal(AgentCertificateImportOutcome.PasswordIncorrect, result.Outcome);
        Assert.Equal(0, writer.AddCalls);
        Assert.Null(result.Certificate);
    }

    [Fact]
    public void StoreFailureIsNotMisreportedAsAnIncorrectPassword()
    {
        using var file = CertificateFile.Pfx(password: "correct-password");
        var writer = new CapturingStoreWriter { Failure = new CryptographicException("store unavailable") };
        var importer = new WindowsAgentCertificateImporter(writer);

        var result = importer.Import(file.Path, "correct-password");

        Assert.Equal(AgentCertificateImportOutcome.Failed, result.Outcome);
        Assert.NotEqual(AgentCertificateImportOutcome.PasswordIncorrect, result.Outcome);
    }

    [Fact]
    public void CerAndCrtImportForInspectionButHaveNoPrivateKey()
    {
        using var cer = CertificateFile.Cer(".cer");
        using var crt = CertificateFile.Cer(".crt");
        var writer = new CapturingStoreWriter();
        var importer = new WindowsAgentCertificateImporter(writer);

        var cerResult = importer.Import(cer.Path, password: null);
        var crtResult = importer.Import(crt.Path, password: null);

        Assert.False(Assert.IsType<AgentCertificateSummary>(cerResult.Certificate).HasPrivateKey);
        Assert.False(Assert.IsType<AgentCertificateSummary>(crtResult.Certificate).HasPrivateKey);
        Assert.False(AgentCertificateRules.Evaluate(cerResult.Certificate!, Host, DateTimeOffset.UtcNow).IsUsable);
        Assert.Equal(2, writer.AddCalls);
    }

    [Fact]
    public void InvalidAndUnsupportedFilesAreRejectedBeforeStoreAccess()
    {
        using var invalid = CertificateFile.Raw(".cer", [0x01, 0x02, 0x03]);
        using var unsupported = CertificateFile.Raw(".pem", [0x01, 0x02, 0x03]);
        var writer = new CapturingStoreWriter();
        var importer = new WindowsAgentCertificateImporter(writer);

        Assert.Equal(AgentCertificateImportOutcome.InvalidFile, importer.Import(invalid.Path, null).Outcome);
        Assert.Equal(AgentCertificateImportOutcome.UnsupportedFile, importer.Import(unsupported.Path, null).Outcome);
        Assert.Equal(0, writer.AddCalls);
    }

    [Fact]
    public void ImportedCertificateStillUsesExistingHostValidityAndEkuRules()
    {
        using var incompatible = CertificateFile.Pfx(
            password: string.Empty,
            dnsName: "other.sbra.local");
        using var expired = CertificateFile.Pfx(
            password: string.Empty,
            notBefore: DateTimeOffset.UtcNow.AddDays(-10),
            notAfter: DateTimeOffset.UtcNow.AddDays(-1));
        using var clientOnly = CertificateFile.Pfx(
            password: string.Empty,
            supportsServerAuthentication: false);
        var importer = new WindowsAgentCertificateImporter(new CapturingStoreWriter());

        var incompatibleSummary = Assert.IsType<AgentCertificateSummary>(
            importer.Import(incompatible.Path, null).Certificate);
        var expiredSummary = Assert.IsType<AgentCertificateSummary>(importer.Import(expired.Path, null).Certificate);
        var clientOnlySummary = Assert.IsType<AgentCertificateSummary>(importer.Import(clientOnly.Path, null).Certificate);

        Assert.Contains(AgentCertificateRules.Evaluate(incompatibleSummary, Host, DateTimeOffset.UtcNow).Problems,
            problem => problem.Contains("does not name", StringComparison.Ordinal));
        Assert.Contains(AgentCertificateRules.Evaluate(expiredSummary, Host, DateTimeOffset.UtcNow).Problems,
            problem => problem.Contains("expired", StringComparison.Ordinal));
        Assert.Contains(AgentCertificateRules.Evaluate(clientOnlySummary, Host, DateTimeOffset.UtcNow).Problems,
            problem => problem.Contains("server authentication", StringComparison.Ordinal));
    }

    [Fact]
    public void PasswordHasNoPersistenceSurface()
    {
        var documentProperties = typeof(AgentTransportConfigurationDocument).GetProperties();
        var resultProperties = typeof(AgentCertificateImportResult).GetProperties();

        Assert.DoesNotContain(documentProperties, property =>
            property.Name.Contains("Password", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(resultProperties, property =>
            property.Name.Contains("Password", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class CapturingStoreWriter : IWindowsAgentCertificateStoreWriter
    {
        public int AddCalls { get; private set; }

        public Exception? Failure { get; init; }

        public AgentCertificateSummary Add(X509Certificate2 certificate)
        {
            AddCalls++;
            if (Failure is not null) throw Failure;
            return WindowsAgentCertificateCatalog.Describe(certificate);
        }
    }

    private sealed class CertificateFile : IDisposable
    {
        private CertificateFile(string extension, byte[] contents)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"NutManager-T41-{Guid.NewGuid():N}{extension}");
            File.WriteAllBytes(Path, contents);
        }

        public string Path { get; }

        public static CertificateFile Pfx(
            string password,
            string dnsName = Host,
            bool supportsServerAuthentication = true,
            DateTimeOffset? notBefore = null,
            DateTimeOffset? notAfter = null,
            string extension = ".pfx") =>
            new(extension, CreateCertificate(
                X509ContentType.Pkcs12,
                password,
                dnsName,
                supportsServerAuthentication,
                notBefore,
                notAfter));

        public static CertificateFile Cer(string extension) =>
            new(extension, CreateCertificate(
                X509ContentType.Cert,
                password: null,
                Host,
                supportsServerAuthentication: true,
                notBefore: null,
                notAfter: null));

        public static CertificateFile Raw(string extension, byte[] contents) => new(extension, contents);

        private static byte[] CreateCertificate(
            X509ContentType contentType,
            string? password,
            string dnsName,
            bool supportsServerAuthentication,
            DateTimeOffset? notBefore,
            DateTimeOffset? notAfter)
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest(
                $"CN={dnsName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));

            var usages = new OidCollection
            {
                new(supportsServerAuthentication ? "1.3.6.1.5.5.7.3.1" : "1.3.6.1.5.5.7.3.2"),
            };
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usages, false));

            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName(dnsName);
            request.CertificateExtensions.Add(names.Build());

            using var certificate = request.CreateSelfSigned(
                notBefore ?? DateTimeOffset.UtcNow.AddDays(-1),
                notAfter ?? DateTimeOffset.UtcNow.AddDays(30));
            return certificate.Export(contentType, password);
        }

        public void Dispose()
        {
            if (File.Exists(Path)) File.Delete(Path);
        }
    }
}
