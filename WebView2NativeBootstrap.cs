using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Web.WebView2.Core;

namespace ZennoVibe.MailClient;

/// <summary>
/// WebView2Loader.dll is embedded in the executable. On every start the copy in
/// <c>data\runtime</c> is checked against the embedded original by SHA-256 and rewritten
/// when it is missing, truncated or tampered with, so the message viewer cannot be broken
/// by a stray file and cannot be hijacked by a swapped DLL.
/// </summary>
internal static class WebView2NativeBootstrap
{
    private const string LoaderFileName = "WebView2Loader.dll";
    private const string EmbeddedLoaderName = "ZennoVibe.WebView2Loader.win-x64.dll";
    private static IntPtr _loaderHandle;

    /// <summary>Why the loader could not be prepared; null when everything is fine.</summary>
    public static string? LastError { get; private set; }

    /// <summary>Never throws: a broken loader must degrade to plain-text mail, not stop startup.</summary>
    public static bool EnsureLoaderSearchPath()
    {
        if (_loaderHandle != IntPtr.Zero)
            return true;

        try
        {
            var loaderPath = EnsureLoaderFile();
            _loaderHandle = NativeLibrary.Load(loaderPath);

            // WebView2 imports this DLL by name. Returning the already loaded absolute-path
            // handle makes resolution independent of the current directory and PATH.
            NativeLibrary.SetDllImportResolver(
                typeof(CoreWebView2Environment).Assembly,
                (libraryName, _, _) =>
                    string.Equals(
                        Path.GetFileName(libraryName),
                        LoaderFileName,
                        StringComparison.OrdinalIgnoreCase)
                        ? _loaderHandle
                        : IntPtr.Zero);

            LastError = null;
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            AppLog.Fatal(
                "webview2.loader",
                "WebView2Loader.dll could not be prepared; HTML mail falls back to plain text.",
                ex);
            return false;
        }
    }

    private static string EnsureLoaderFile()
    {
        var runtimeDir = AppPaths.RuntimeDirectory;
        Directory.CreateDirectory(runtimeDir);
        var runtimeLoader = Path.Combine(runtimeDir, LoaderFileName);

        var expected = HashEmbeddedLoader();
        if (!File.Exists(runtimeLoader) || !HashFile(runtimeLoader).AsSpan().SequenceEqual(expected))
            ExtractEmbeddedLoader(runtimeLoader);

        if (!HashFile(runtimeLoader).AsSpan().SequenceEqual(expected))
            throw new IOException("WebView2Loader.dll in data\\runtime does not match the embedded copy after extraction.");

        return runtimeLoader;
    }

    private static Stream OpenEmbeddedLoader() =>
        Assembly
            .GetExecutingAssembly()
            .GetManifestResourceStream(EmbeddedLoaderName)
        ?? throw new FileNotFoundException(
            "The embedded WebView2 loader is missing from the application package.");

    private static byte[] HashEmbeddedLoader()
    {
        using var source = OpenEmbeddedLoader();
        return SHA256.HashData(source);
    }

    private static byte[] HashFile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return SHA256.HashData(stream);
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static void ExtractEmbeddedLoader(string destination)
    {
        using var source = OpenEmbeddedLoader();

        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var target = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                source.CopyTo(target);
            }

            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch
            {
                // A failed cleanup must not hide the actual extraction error.
            }
        }
    }
}
