using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace ZennoVibe.MailClient.Core;

internal enum ActivationStatus
{
    /// <summary>The code is valid for this machine.</summary>
    Ok,

    /// <summary>The code is not a valid signature, or is signed for another machine.</summary>
    Invalid
}

/// <summary>
/// Hardware-bound activation. The distributed exe is born unactivated: without a valid code it
/// refuses to run. A code is the owner's signature over this machine's id (see
/// <see cref="LicenseFormat"/>), checked against the public key embedded below. Copying the exe
/// to another machine changes the machine id, so no existing code fits and the owner — the only
/// holder of the private key — has to issue a new one. This stops copying the binary; it cannot
/// stop someone rebuilding it from the public source, which is an accepted limitation.
/// </summary>
internal static class Activation
{
    // Public half of the owner's signing key (ECDSA nistP256, SubjectPublicKeyInfo, base64).
    // The private half lives only with the licence generator and is never shipped.
    private const string PublicKeyB64 =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEk+ARgGmFilmOwnIRM75d5vicKlmkV2BHIIZprzOtFVHJHf+0Sj7DBvyqYR1dbPCh5aPIQTj/Jm+XmrHp/r3IDQ==";

    // Mixed into the machine id so the shown value is not the raw Windows MachineGuid.
    private const string MachineIdSalt = "ZennoVibe.MailClient.Activation.v1";

    private const string ActivationFileName = "mailclient.activation";

    private static string ActivationPath => Path.Combine(AppPaths.DataDirectory, ActivationFileName);

    private static string? _machineIdCache;

    /// <summary>Normalised machine id (hex, no separators). Stable across reboots.</summary>
    public static string MachineId()
    {
        if (_machineIdCache != null)
            return _machineIdCache;

        var seed = ReadMachineGuid() ?? FallbackSeed();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(MachineIdSalt + "|" + seed));
        // 10 bytes -> 20 hex chars -> shown as five groups of four.
        var sb = new StringBuilder(20);
        for (var i = 0; i < 10; i++)
            sb.Append(hash[i].ToString("X2"));

        _machineIdCache = sb.ToString();
        return _machineIdCache;
    }

    /// <summary>Machine id grouped for display.</summary>
    public static string MachineIdDisplay() => LicenseFormat.FormatMachineId(MachineId());

    /// <summary>True when a stored code checks out for this machine.</summary>
    public static bool IsActivated()
    {
        try
        {
            if (!File.Exists(ActivationPath))
                return false;

            var code = File.ReadAllText(ActivationPath);
            return Verify(code) == ActivationStatus.Ok;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Checks a code against this machine without storing it.</summary>
    public static ActivationStatus Verify(string code)
    {
        if (!LicenseFormat.TryDecodeSignature(code, out var signature))
            return ActivationStatus.Invalid;

        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(PublicKeyB64), out _);
            var message = LicenseFormat.MessageFor(MachineId());
            var ok = ecdsa.VerifyData(message, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            return ok ? ActivationStatus.Ok : ActivationStatus.Invalid;
        }
        catch
        {
            return ActivationStatus.Invalid;
        }
    }

    /// <summary>Verifies a code and, on success, stores it so later starts pass automatically.</summary>
    public static ActivationStatus Activate(string code)
    {
        var status = Verify(code);
        if (status != ActivationStatus.Ok)
            return status;

        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            File.WriteAllText(ActivationPath, code.Trim());
        }
        catch
        {
            // The code is valid; a failed write only means the next start asks again.
        }

        return ActivationStatus.Ok;
    }

    private static string? ReadMachineGuid()
    {
        try
        {
            using var key = RegistryKey
                .OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            return key?.GetValue("MachineGuid") as string;
        }
        catch
        {
            return null;
        }
    }

    private static string FallbackSeed() =>
        Environment.MachineName + "|" + Environment.ProcessorCount + "|" +
        Environment.GetFolderPath(Environment.SpecialFolder.Windows);
}
