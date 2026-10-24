using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace InkvoltPdf.Core;

/// <summary>
/// Digital IDs (certificates with private keys) for signing: the user's Windows certificate store (software keys, smart
/// cards, Windows Hello), .pfx files, and self-signed personal IDs.
/// </summary>
public static class DigitalIds
{
    public const string DocumentSigningEku = "1.3.6.1.4.1.311.10.3.12";
    private const string EmailProtectionEku = "1.3.6.1.5.5.7.3.4";

    /// <summary>
    /// Certificates in CurrentUser\My that can sign documents: a private key (RSA or EC), currently valid, and key usage
    /// absent or allowing digital signatures / non-repudiation. The caller disposes them.
    /// </summary>
    public static List<X509Certificate2> ListUsable(StoreName store = StoreName.My, StoreLocation location = StoreLocation.CurrentUser)
    {
        using var s = new X509Store(store, location);
        s.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        var list = new List<X509Certificate2>();
        foreach (var c in s.Certificates)
        {
            if (IsUsable(c)) list.Add(c);
            else c.Dispose();
        }
        return [.. list.OrderBy(c => c.GetNameInfo(X509NameType.SimpleName, false), StringComparer.CurrentCultureIgnoreCase)];
    }

    public static bool IsUsable(X509Certificate2 c)
    {
        var now = DateTime.Now;
        if (!c.HasPrivateKey || c.NotBefore > now || c.NotAfter < now) return false;
        var alg = c.GetKeyAlgorithm();
        if (alg is not ("1.2.840.113549.1.1.1" or "1.2.840.10045.2.1")) return false; // RSA, EC
        var ku = c.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault();
        if (ku is not null && (ku.KeyUsages & (X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation)) == 0) return false;
        // An extended key usage list, when present, must allow document or email signing (this leaves out device and
        // login certificates such as Entra ID's MS-Organization-Access, which only allow client authentication).
        var eku = c.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
        return eku is null || eku.EnhancedKeyUsages.Cast<Oid>().Any(o => o.Value is DocumentSigningEku or EmailProtectionEku or AdobeAuthenticDocumentsEku or AnyEku);
    }

    private const string AdobeAuthenticDocumentsEku = "1.2.840.113583.1.1.5";
    private const string AnyEku = "2.5.29.37.0";

    /// <summary>The ID with a private key in a .pfx/.p12 file (kept in memory only).</summary>
    public static X509Certificate2 LoadPfx(byte[] pfx, string? password)
    {
        X509Certificate2Collection all;
        try
        {
            all = X509CertificateLoader.LoadPkcs12Collection(pfx, password, X509KeyStorageFlags.EphemeralKeySet);
        }
        catch (CryptographicException ex)
        {
            throw new SigningException(ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase)
                ? "The password for this digital ID file is incorrect." : $"This file isn't a usable digital ID: {ex.Message}", ex);
        }
        var signer = all.FirstOrDefault(c => c.HasPrivateKey);
        foreach (var c in all)
            if (!ReferenceEquals(c, signer)) c.Dispose();
        return signer ?? throw new SigningException("This digital ID file holds no private key.");
    }

    /// <summary>
    /// A new self-signed personal ID (RSA 3072, SHA-256, valid 5 years, Document Signing + Email Protection EKUs) as a
    /// .pfx protected by <paramref name="password"/>.
    /// </summary>
    public static byte[] CreatePersonalPfx(string name, string? email, string? organization, string password, int years = 5)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new SigningException("A name is required.");
        var dn = new X500DistinguishedNameBuilder();
        dn.AddCommonName(name.Trim());
        if (!string.IsNullOrWhiteSpace(organization)) dn.AddOrganizationName(organization.Trim());
        if (!string.IsNullOrWhiteSpace(email)) dn.AddEmailAddress(email.Trim());
        using var rsa = RSA.Create(3072);
        var req = new CertificateRequest(dn.Build(), rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(DocumentSigningEku), new Oid(EmailProtectionEku)], false));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        if (!string.IsNullOrWhiteSpace(email))
        {
            var san = new SubjectAlternativeNameBuilder();
            san.AddEmailAddress(email.Trim());
            req.CertificateExtensions.Add(san.Build());
        }
        var now = DateTimeOffset.UtcNow;
        using var cert = req.CreateSelfSigned(now.AddMinutes(-10), now.AddYears(years));
        // AES-256 with PBKDF2 (not the legacy 3DES/SHA-1 default): the backup file may be copied anywhere.
        return cert.ExportPkcs12(Pkcs12ExportPbeParameters.Pbes2Aes256Sha256, password);
    }

    /// <summary>
    /// Imports a .pfx into CurrentUser\My (key persisted, not exportable unless <paramref name="exportable"/>) and
    /// returns the stored ID.
    /// </summary>
    public static X509Certificate2 Install(byte[] pfx, string password, bool exportable = false)
    {
        var flags = X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.UserKeySet | (exportable ? X509KeyStorageFlags.Exportable : 0);
        var cert = X509CertificateLoader.LoadPkcs12(pfx, password, flags);
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        store.Add(cert);
        return cert;
    }

    /// <summary>A one-line description for pickers: "Name (issuer, expires 2031-10-22)".</summary>
    public static string Describe(X509Certificate2 c)
    {
        var name = c.GetNameInfo(X509NameType.SimpleName, false);
        var issuer = c.GetNameInfo(X509NameType.SimpleName, true);
        var self = c.SubjectName.RawData.AsSpan().SequenceEqual(c.IssuerName.RawData);
        return $"{name} ({(self ? "self-signed" : issuer)}, expires {c.NotAfter:yyyy-MM-dd})";
    }
}
