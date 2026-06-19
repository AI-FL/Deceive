using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Forms;

namespace Deceive;

internal static class Utils
{
    internal static string DeceiveVersion
    {
        get
        {
            var version = Assembly.GetEntryAssembly()?.GetName().Version;
            if (version is null)
                return "v0.0.0";
            return "v" + version.Major + "." + version.Minor + "." + version.Build;
        }
    }

    private static IEnumerable<Process> GetProcesses()
    {
        var riotCandidates = Process.GetProcessesByName(Process.GetCurrentProcess().ProcessName)
            .Where(process => process.Id != Process.GetCurrentProcess().Id).ToList();
        riotCandidates.AddRange(Process.GetProcessesByName("LeagueClient"));
        riotCandidates.AddRange(Process.GetProcessesByName("LoR"));
        riotCandidates.AddRange(Process.GetProcessesByName("VALORANT-Win64-Shipping"));
        riotCandidates.AddRange(Process.GetProcessesByName("RiotClientServices"));
        return riotCandidates;
    }

    public static Process? GetRiotClientProcess() => Process.GetProcessesByName("RiotClientServices").FirstOrDefault();

    public static bool IsClientRunning() => GetProcesses().Any();

    public static void KillProcesses()
    {
        try
        {
            foreach (var process in GetProcesses())
            {
                process.Refresh();
                if (process.HasExited)
                    continue;
                process.Kill();
                process.WaitForExit();
            }
        }
        catch (Win32Exception ex)
        {
            if (ex.NativeErrorCode == -2147467259 || ex.ErrorCode == -2147467259 || ex.ErrorCode == 5 ||
                ex.NativeErrorCode == 5)
            {
                MessageBox.Show(
                    "Deceive could not stop existing Riot processes because it does not have the right permissions. Please relaunch this application as an administrator and try again.",
                    StartupHandler.DeceiveTitle,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error,
                    MessageBoxDefaultButton.Button1
                );
                Environment.Exit(0);
            }

            throw ex;
        }
    }

    public static string? GetRiotClientPath()
    {
        var installPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Riot Games/RiotClientInstalls.json");
        if (!File.Exists(installPath))
            return null;

        try
        {
            var data = JsonSerializer.Deserialize<JsonNode>(File.ReadAllText(installPath));
            var rcPaths = new List<string?>
                { data?["rc_default"]?.ToString(), data?["rc_live"]?.ToString(), data?["rc_beta"]?.ToString() };

            return rcPaths.FirstOrDefault(File.Exists);
        }
        catch
        {
            return null;
        }
    }

    // Generates or loads a self-signed TLS certificate for the local XMPP proxy.
    // On first run (or after expiry), generates a new cert, installs it into
    // CurrentUser\Root (Windows shows a one-time trust dialog), and caches it
    // at %AppData%\Deceive\localhostCert.pfx. No external network calls.
    public static X509Certificate2? GetOrCreateProxyCertificate()
    {
        var cached = Persistence.GetCachedCertificate();
        if (cached is not null && cached.NotAfter > DateTime.Now.AddDays(30))
        {
            Trace.WriteLine($"Using cached certificate valid until {cached.NotAfter}.");
            return cached;
        }

        try
        {
            Trace.WriteLine("Generating self-signed proxy certificate.");

            using var rsa = RSA.Create(2048);
            var req = new CertificateRequest(
                $"CN={ConfigProxy.LocalhostDomain}",
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);

            req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
            req.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
            req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));

            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName(ConfigProxy.LocalhostDomain);
            san.AddIpAddress(IPAddress.Loopback);
            req.CertificateExtensions.Add(san.Build());

            var rawCert = req.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddYears(2));

            var pfxBytes = rawCert.Export(X509ContentType.Pfx);
            rawCert.Dispose();

            var cert = new X509Certificate2(pfxBytes, (string?)null, X509KeyStorageFlags.EphemeralKeySet);
            EnsureCertTrusted(cert);
            Persistence.SetCachedCertificate(pfxBytes);

            Trace.WriteLine($"Certificate generated, valid until {cert.NotAfter}.");
            return cert;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Failed to generate certificate: {ex}");
            return null;
        }
    }

    private static void EnsureCertTrusted(X509Certificate2 cert)
    {
        var publicOnly = new X509Certificate2(cert.RawData);
        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);

        var existing = store.Certificates.Find(X509FindType.FindByThumbprint, cert.Thumbprint, false);
        if (existing.Count > 0)
        {
            Trace.WriteLine("Certificate already present in CurrentUser\\Root.");
            return;
        }

        store.Add(publicOnly);
        Trace.WriteLine("Certificate installed to CurrentUser\\Root.");
    }

    private static bool DeceiveLocalhostResolves()
    {
        try
        {
            var addresses = Dns.GetHostAddresses(ConfigProxy.LocalhostDomain);
            if (addresses.Any(addr => addr.ToString() == "127.0.0.1"))
                return true;
        }
        catch
        {
            // intentionally empty
        }
        return false;
    }

    public static void EnsureLocalhostResolution()
    {
        if (DeceiveLocalhostResolves())
            return;

        MessageBox.Show(
            $"DNS resolution failed for {ConfigProxy.LocalhostDomain}.\n\n" +
            "Add the following line to C:\\Windows\\System32\\drivers\\etc\\hosts (requires Administrator):\n" +
            $"    127.0.0.1 {ConfigProxy.LocalhostDomain}\n\n" +
            "Alternatively, switch your DNS server to 1.1.1.1 (Cloudflare) or 8.8.8.8 (Google).\n\n" +
            "Deceive cannot start until this is resolved.",
            StartupHandler.DeceiveTitle,
            MessageBoxButtons.OK,
            MessageBoxIcon.Error,
            MessageBoxDefaultButton.Button1
        );

        Environment.Exit(0);
    }
}
