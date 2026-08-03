using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

/// <summary>
/// The relay's TLS certificate. It is kept between restarts: a fresh certificate on every start
/// means the browser warning can never be dismissed for good and no client can pin the relay.
/// </summary>
public static class RelayCertificate
{
    /// <summary>
    /// Reuse the certificate stored at <paramref name="certPath"/>, or issue one and keep it there.
    /// The password sits next to it in a file the OS protects the same way as the key itself — the
    /// point is stability across restarts, not secrecy from someone who already reads this directory.
    /// </summary>
    public static X509Certificate2 LoadOrCreate(string certPath, TextWriter? log = null)
    {
        log ??= TextWriter.Null;
        var passPath = certPath + ".pass";

        try
        {
            if (File.Exists(certPath) && File.Exists(passPath))
            {
                var stored = X509CertificateLoader.LoadPkcs12FromFile(certPath, File.ReadAllText(passPath));
                if (stored.NotAfter > DateTime.Now.AddDays(7)) return stored;
                log.WriteLine("[TLS] stored certificate expires soon — issuing a new one");
            }
        }
        catch (Exception ex)
        {
            log.WriteLine($"[TLS] cannot reuse stored certificate ({ex.Message}) — issuing a new one");
        }

        using var fresh = GenerateSelfSigned();
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var pfx = fresh.Export(X509ContentType.Pfx, password);
        try
        {
            File.WriteAllBytes(certPath, pfx);
            File.WriteAllText(passPath, password);
            log.WriteLine($"[TLS] certificate stored in {certPath} (thumbprint {fresh.Thumbprint})");
        }
        catch (Exception ex)
        {
            log.WriteLine($"[TLS] certificate not persisted ({ex.Message}) — the browser warning returns after a restart");
        }

        // Always hand back the PKCS#12 round-trip, never the object CreateSelfSigned returned: on
        // Windows that one carries an ephemeral key SChannel refuses, and every handshake fails.
        return X509CertificateLoader.LoadPkcs12(pfx, password);
    }

    public static X509Certificate2 GenerateSelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=RemoteCmd, O=NKS Hub",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));

        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName("localhost");
        sanBuilder.AddIpAddress(System.Net.IPAddress.Loopback);
        sanBuilder.AddIpAddress(System.Net.IPAddress.IPv6Loopback);
        request.CertificateExtensions.Add(sanBuilder.Build());

        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(5));
    }
}
