using System.Text;

namespace ZennoVibe.MailClient.Core;

/// <summary>
/// The wire format shared by the client and the licence generator. Both sides must agree
/// byte for byte, so this file is compiled into both projects (the generator links it).
///
/// An activation code is the owner's ECDSA (nistP256, SHA-256, IEEE P1363) signature over the
/// bytes of <c>"ZVMC1:" + machineId</c>, where <c>machineId</c> is the normalised machine id
/// the client shows. The code carries no machine id of its own: the client always rebuilds the
/// message from its own machine, so a code signed for machine A fails on machine B.
/// </summary>
internal static class LicenseFormat
{
    public const string MessagePrefix = "ZVMC1:";

    /// <summary>The exact bytes that get signed and verified for a machine id.</summary>
    public static byte[] MessageFor(string machineId) =>
        Encoding.UTF8.GetBytes(MessagePrefix + NormalizeMachineId(machineId));

    /// <summary>Strips spaces and dashes and upper-cases, so display grouping never matters.</summary>
    public static string NormalizeMachineId(string shown)
    {
        if (string.IsNullOrEmpty(shown))
            return "";

        var sb = new StringBuilder(shown.Length);
        foreach (var ch in shown)
        {
            if (ch is '-' or ' ' or '\t' or '\r' or '\n')
                continue;
            sb.Append(char.ToUpperInvariant(ch));
        }

        return sb.ToString();
    }

    /// <summary>Groups a hex machine id into blocks of four for readability.</summary>
    public static string FormatMachineId(string normalized)
    {
        normalized = NormalizeMachineId(normalized);
        var sb = new StringBuilder(normalized.Length + normalized.Length / 4);
        for (var i = 0; i < normalized.Length; i++)
        {
            if (i > 0 && i % 4 == 0)
                sb.Append('-');
            sb.Append(normalized[i]);
        }

        return sb.ToString();
    }

    public static string EncodeSignature(byte[] signature) =>
        Base64UrlEncode(signature);

    public static bool TryDecodeSignature(string code, out byte[] signature)
    {
        signature = Array.Empty<byte>();
        if (string.IsNullOrWhiteSpace(code))
            return false;

        try
        {
            signature = Base64UrlDecode(code.Trim());
            return signature.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string text)
    {
        var s = text.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }

        return Convert.FromBase64String(s);
    }
}
