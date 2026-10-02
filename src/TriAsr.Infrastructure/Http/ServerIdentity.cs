using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace TriAsr.Infrastructure;

/// <summary>
/// The identity of a hosted server: a self-signed certificate made on this computer the first time it is needed and kept in the data folder
/// (<c>server-certificate.pem</c> and <c>server-key.pem</c>). Nobody vouches for it; clients recognise a server by its fingerprint, which they
/// are shown the first time they connect and which must be the same afterwards. Making a new one is how a server "forgets" its past.
/// </summary>
public sealed class ServerIdentity : IDisposable
{
    private ServerIdentity(X509Certificate2 certificate)
    {
        Certificate = certificate;
        Fingerprint = FingerprintOf(certificate);
    }

    public X509Certificate2 Certificate { get; }

    /// <summary>SHA-256 of the certificate, as 64 hexadecimal characters.</summary>
    public string Fingerprint { get; }

    /// <summary>The fingerprint as people compare it: groups of four, <c>A1B2-C3D4-...</c>.</summary>
    public string Readable => Format(Fingerprint);

    public static string FingerprintOf(X509Certificate certificate) => Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));

    /// <summary>Groups a fingerprint in fours (16 groups), which is how it is shown, spoken and compared.</summary>
    public static string Format(string fingerprint) =>
        string.Join('-', Enumerable.Range(0, fingerprint.Length / 4).Select(index => fingerprint.Substring(index * 4, 4)));

    /// <summary>Whether two fingerprints are the same, whatever the grouping and case they are written in.</summary>
    public static bool Same(string? first, string? second) =>
        first is not null && second is not null && Normalize(first).Length > 0 && Normalize(first) == Normalize(second);

    private static string Normalize(string fingerprint) => new(fingerprint.Where(char.IsAsciiHexDigit).Select(char.ToUpperInvariant).ToArray());

    private static string CertificatePath(string folder) => Path.Combine(folder, "server-certificate.pem");
    private static string KeyPath(string folder) => Path.Combine(folder, "server-key.pem");

    /// <summary>The identity kept in <paramref name="folder"/>; a new one when there is none, or when the files cannot be read.</summary>
    public static ServerIdentity LoadOrCreate(string folder)
    {
        Directory.CreateDirectory(folder);
        if (File.Exists(CertificatePath(folder)) && File.Exists(KeyPath(folder)))
        {
            try { return new ServerIdentity(Open(File.ReadAllText(CertificatePath(folder)), File.ReadAllText(KeyPath(folder)))); }
            catch (Exception error) when (error is CryptographicException or IOException or ArgumentException) { }
        }
        return Create(folder);
    }

    /// <summary>A new identity, replacing the one in <paramref name="folder"/>. Clients that knew the old one will refuse the new one until they are told to trust it.</summary>
    public static ServerIdentity Create(string folder)
    {
        Directory.CreateDirectory(folder);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Mockingbird Server", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddDnsName(Environment.MachineName);
        names.AddIpAddress(IPAddress.Loopback);
        names.AddIpAddress(IPAddress.IPv6Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
        var certificatePem = made.ExportCertificatePem();
        var keyPem = key.ExportPkcs8PrivateKeyPem();
        File.WriteAllText(KeyPath(folder), keyPem);
        File.WriteAllText(CertificatePath(folder), certificatePem);
        return new ServerIdentity(Open(certificatePem, keyPem));
    }

    /// <summary>The certificate with its key in a form Windows' TLS can use (a key that only lives in memory is not accepted for a server).</summary>
    private static X509Certificate2 Open(string certificatePem, string keyPem)
    {
        using var loaded = X509Certificate2.CreateFromPem(certificatePem, keyPem);
        return X509CertificateLoader.LoadPkcs12(loaded.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
    }

    public void Dispose() => Certificate.Dispose();
}
