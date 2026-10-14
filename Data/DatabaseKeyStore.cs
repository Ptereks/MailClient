using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace ZennoVibe.MailClient.Data;

/// <summary>
/// Holds the AES password of the LiteDB file. The password is random and is stored next to
/// the database protected by Windows DPAPI for the current user, so copying the
/// <c>data</c> folder to another machine or user account does not expose the mailboxes.
/// </summary>
internal static class DatabaseKeyStore
{
    private const string KeyFileName = "mailclient.key";

    public static string KeyPath(string databaseDirectory) =>
        Path.Combine(databaseDirectory, KeyFileName);

    /// <summary>Returns null when no key file exists yet.</summary>
    public static string? TryLoad(string databaseDirectory)
    {
        var path = KeyPath(databaseDirectory);
        if (!File.Exists(path))
            return null;

        byte[] plain;
        try
        {
            plain = Unprotect(File.ReadAllBytes(path));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "The database key in " + path + " cannot be read by this Windows user. " +
                "The database was encrypted under another user account or machine. " +
                "Use an unencrypted export from Settings on the original machine, or delete the " +
                "data\\DB folder to start with an empty database.",
                ex);
        }

        return Convert.ToBase64String(plain);
    }

    public static string Create(string databaseDirectory)
    {
        Directory.CreateDirectory(databaseDirectory);
        var raw = RandomNumberGenerator.GetBytes(32);
        var path = KeyPath(databaseDirectory);
        var temporary = path + ".tmp";

        File.WriteAllBytes(temporary, Protect(raw));
        File.Move(temporary, path, overwrite: true);
        return Convert.ToBase64String(raw);
    }

    private static byte[] Protect(byte[] data) => Transform(data, protect: true);

    private static byte[] Unprotect(byte[] data) => Transform(data, protect: false);

    private static byte[] Transform(byte[] data, bool protect)
    {
        var input = new DataBlob { cbData = data.Length, pbData = Marshal.AllocHGlobal(data.Length) };
        var output = new DataBlob();

        try
        {
            Marshal.Copy(data, 0, input.pbData, data.Length);

            var ok = protect
                ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref output);

            if (!ok)
                throw new CryptographicException(Marshal.GetLastWin32Error());

            var result = new byte[output.cbData];
            Marshal.Copy(output.pbData, result, 0, output.cbData);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(input.pbData);
            if (output.pbData != IntPtr.Zero)
                LocalFree(output.pbData);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string? description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        ref DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        ref DataBlob dataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);
}
