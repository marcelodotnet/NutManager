using System.Formats.Asn1;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using NutManager.Core.Agent;

namespace NutManager.Infrastructure.AgentConfiguration;

/// <summary>
/// The machine's own certificates, read so an administrator can pick one.
///
/// Read-only, with no member that imports, exports, generates, moves or deletes. The private key is
/// never touched: <see cref="X509Certificate2.HasPrivateKey"/> answers whether one is present, which
/// is the only thing this screen needs to know, and reading the key itself would put material in this
/// process that has no reason to be here.
///
/// Nothing in this file decides whether a certificate is acceptable. That is
/// <see cref="AgentCertificateRules"/>, which is pure and therefore testable against an expired
/// certificate, a wildcard mismatch and a missing key without any of those existing on the machine.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsAgentCertificateCatalog : IAgentCertificateCatalog
{
    /// <summary>The OID for TLS server authentication in an Extended Key Usage extension.</summary>
    private const string ServerAuthenticationOid = "1.3.6.1.5.5.7.3.1";

    private const string SubjectAlternativeNameOid = "2.5.29.17";

    /// <summary>dNSName, as tagged inside a SubjectAltName GeneralName.</summary>
    private const int DnsNameTag = 2;

    public IReadOnlyList<AgentCertificateSummary> List()
    {
        try
        {
            using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly);

            var summaries = new List<AgentCertificateSummary>();

            foreach (var certificate in store.Certificates)
            {
                using (certificate)
                {
                    // A certificate with no private key cannot terminate TLS, but it is listed anyway:
                    // an administrator who imported the wrong file needs to see it in the list and be
                    // told why it will not do, rather than wonder where it went.
                    summaries.Add(Describe(certificate));
                }
            }

            // Usable ones first, then by expiry. The certificate somebody is looking for is almost
            // always one that currently works.
            return
            [
                .. summaries
                    .OrderByDescending(summary => summary.HasPrivateKey && summary.SupportsServerAuthentication)
                    .ThenByDescending(summary => summary.NotAfter)
                    .ThenBy(summary => summary.DisplayName, StringComparer.OrdinalIgnoreCase)
            ];
        }
        catch (Exception)
        {
            // An unreadable store shows an empty list. The diagnostics view reports the store itself
            // separately, so this does not have to pretend to be an error path.
            return [];
        }
    }

    public AgentCertificateSummary? Find(string thumbprint)
    {
        if (!AgentHttpsPrefixRules.IsPlausibleThumbprint(thumbprint)) return null;

        var normalized = AgentHttpsPrefixRules.NormalizeThumbprint(thumbprint);

        try
        {
            using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly);

            foreach (var certificate in store.Certificates)
            {
                using (certificate)
                {
                    if (string.Equals(certificate.Thumbprint, normalized, StringComparison.OrdinalIgnoreCase))
                    {
                        return Describe(certificate);
                    }
                }
            }

            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static AgentCertificateSummary Describe(X509Certificate2 certificate) =>
        new(
            certificate.Thumbprint ?? string.Empty,
            certificate.Subject,
            certificate.Issuer,
            certificate.NotBefore,
            certificate.NotAfter,
            certificate.HasPrivateKey,
            SupportsServerAuthentication(certificate),
            ReadDnsNames(certificate));

    /// <summary>
    /// Whether the certificate may be used to authenticate a server.
    ///
    /// A certificate carrying no EKU extension at all is unrestricted, which the specification says
    /// means any purpose — so that counts as yes. One that carries an EKU and omits server
    /// authentication has been explicitly restricted away from this use, and HTTP.sys will refuse it.
    /// </summary>
    private static bool SupportsServerAuthentication(X509Certificate2 certificate)
    {
        var found = false;

        foreach (var extension in certificate.Extensions)
        {
            if (extension is not X509EnhancedKeyUsageExtension usage) continue;

            found = true;

            foreach (var oid in usage.EnhancedKeyUsages)
            {
                if (string.Equals(oid.Value, ServerAuthenticationOid, StringComparison.Ordinal)) return true;
            }
        }

        return !found;
    }

    /// <summary>
    /// The DNS names in the SubjectAltName extension.
    ///
    /// Parsed from the ASN.1 rather than read out of the extension's formatted display string: that
    /// string is localized and its layout differs between Windows versions, so matching a host against
    /// it would mean matching against a translation. The structure is a SEQUENCE of GeneralName, and
    /// dNSName is context tag 2.
    /// </summary>
    private static IReadOnlyList<string> ReadDnsNames(X509Certificate2 certificate)
    {
        foreach (var extension in certificate.Extensions)
        {
            if (!string.Equals(extension.Oid?.Value, SubjectAlternativeNameOid, StringComparison.Ordinal)) continue;

            try
            {
                var names = new List<string>();
                var reader = new AsnReader(extension.RawData, AsnEncodingRules.DER).ReadSequence();

                while (reader.HasData)
                {
                    var tag = reader.PeekTag();

                    if (tag.TagClass == TagClass.ContextSpecific && tag.TagValue == DnsNameTag)
                    {
                        names.Add(reader.ReadCharacterString(UniversalTagNumber.IA5String, tag));
                    }
                    else
                    {
                        // Every other GeneralName kind — an IP address, an email, a URI — is skipped
                        // rather than guessed at. Host matching is about DNS names.
                        reader.ReadEncodedValue();
                    }
                }

                return names;
            }
            catch (Exception)
            {
                // A malformed extension yields no names, which makes host matching fall back to the
                // common name. It never yields a match nobody could verify.
                return [];
            }
        }

        return [];
    }
}
