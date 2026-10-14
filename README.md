# MailClient

A Windows desktop client for Yahoo Mail and AOL Mail: IMAP for reading, SMTP for sending, OAuth or app-password sign-in, and a proxy on every connection.

The source is published so that users can verify what the program does. See [LICENSE](LICENSE). The user manual is [README.txt](README.txt), also shown inside the program under **Settings → Documentation**.

## Download and verify

Binaries are on the [Releases](../../releases) page. Every release is built by GitHub Actions from the tagged commit; the release notes link to the build log.

Each release contains `MailClient-vX.Y.Z-win-x64.zip` (the program with `README.txt` and the `data\` templates), `MailClient.exe` on its own, and `SHA256SUMS.txt`.

```powershell
Get-FileHash .\MailClient.exe -Algorithm SHA256
```

The hash must match the line in `SHA256SUMS.txt`. The binary is not code-signed; Windows SmartScreen warns on first start.

Requirements: Windows 10 or 11 x64, [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) x64, a proxy.

## Build from source

With the .NET 8 SDK installed, run from the repository root:

```powershell
dotnet publish ZennoVibe.MailClient.csproj -c Release
```

The output is `dist-alpha\MailClient.exe` with `README.txt` and `data\`. The release workflow runs the same command: [.github/workflows/release.yml](.github/workflows/release.yml).

## Security review

All network code is in these files:

| File | Role |
| --- | --- |
| `Core/MailSite.cs` | The only server addresses: `imap.mail.yahoo.com:993`, `smtp.mail.yahoo.com:465`, `imap.aol.com:993`, `smtp.aol.com:465` |
| `Core/YahooAolTokenClient.cs` | OAuth token refresh at `api.login.{yahoo,aol}.com`, through the account proxy |
| `Core/ProxySpec.cs` | Proxy line parsing, HTTP / SOCKS clients |
| `Core/NetworkGuard.cs` | Refuses any connection without a proxy |
| `Services/ImapSyncService.cs`, `Services/ImapSyncSession.cs` | IMAP connect, sign-in, folder and header sync; the server is only read |
| `Services/MessageBodyLoader.cs`, `Services/AttachmentDownloadService.cs` | Body and attachment download on demand |
| `Services/SmtpSendService.cs`, `Services/OutgoingMessageBuilder.cs` | SMTP over TLS, port 465; message assembly |
| `Services/OAuthAuthenticator.cs`, `Services/AppPasswordAuthenticator.cs`, `Services/TokenService.cs` | XOAUTH2 and app-password sign-in, token lifetime |
| `Services/ProxiedMediaFetcher.cs` | Remote images, through the proxy, only when **Load media** is on |
| `WebView2Host.cs`, `WebViewGuard.cs` | Message viewer: WebView2 with a dead proxy and a request filter, so mail content cannot reach the network |
| `LinkBrowserWindow.xaml.cs`, `MessageLinks.cs` | Clicked links open in a WebView2 bound to the account proxy |
| `WebView2NativeBootstrap.cs` | Verifies `data\runtime\WebView2Loader.dll` against the embedded copy at start |

What the code guarantees:

- No telemetry, update checks or other servers. The only hosts are the Yahoo and AOL ones above.
- No proxy, no connection: `NetworkGuard` throws before any socket is opened.
- IMAP is read-only; no flags are changed, nothing is moved or deleted on the server.
- The database (`Data/MailDatabase.cs`) is LiteDB with AES. Its random password is protected with Windows DPAPI for the current user (`Data/DatabaseKeyStore.cs`), so a copied `data\` folder does not open elsewhere.
- Only crashes are logged (`AppLog.cs`), with IPv4 addresses redacted.
- The OAuth client IDs in `YahooAolTokenClient.cs` are the public ones Mozilla Thunderbird ships; Yahoo and AOL issue no per-developer keys for IMAP clients.

## License

Source-available for audit, see [LICENSE](LICENSE). Contact: [@Ptereks](https://t.me/Ptereks).
