# Yahoo & AOL Mail Client

A Windows desktop client for Yahoo Mail and AOL Mail mailboxes: IMAP for reading, SMTP for sending, OAuth or app-password sign-in, and a proxy on every connection. It is built for people who manage many accounts and want to see exactly what the program does on the network.

This repository exists so customers can audit the source and match it to the binaries they receive. See [LICENSE](LICENSE): the code is published for review, not for reuse.

The user manual is [README.txt](README.txt); the program shows the same file under **Settings → Documentation**.

## Download and verify

Releases are on the [Releases](../../releases) page. Each release is built by GitHub Actions from the tagged commit — not on a developer machine — and the release notes link to the build log.

Each release carries:

- `MailClient-vX.Y.Z-win-x64.zip` — `MailClient.exe`, `README.txt`, and the `data\` templates (`account.txt`, `proxy.txt`, fonts)
- `MailClient.exe` — the same binary on its own
- `SHA256SUMS.txt` — hashes of both

Check a download in PowerShell:

```powershell
Get-FileHash .\MailClient.exe -Algorithm SHA256
```

The hash must match the line in `SHA256SUMS.txt`. The binary is not code-signed, so Windows SmartScreen shows a warning on first start.

Requirements: Windows 10 or 11 x64, [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) x64, and a working proxy.

## Build from source

Install the .NET 8 SDK, then from the repository root:

```powershell
dotnet publish ZennoVibe.MailClient.csproj -c Release
```

The output lands in `dist-alpha\`: a single `MailClient.exe` plus `README.txt` and `data\`. This is the same command the release workflow runs ([.github/workflows/release.yml](.github/workflows/release.yml)).

## Security review guide

Everything that touches the network lives in a handful of files. Reading them is a one-afternoon job.

| File | What it does |
| --- | --- |
| `Core/MailSite.cs` | The only server addresses in the program: `imap.mail.yahoo.com:993`, `smtp.mail.yahoo.com:465`, `imap.aol.com:993`, `smtp.aol.com:465` |
| `Core/YahooAolTokenClient.cs` | OAuth token refresh against `https://api.login.{yahoo,aol}.com/oauth2/get_token`, through the account proxy |
| `Core/ProxySpec.cs` | Parsing of proxy lines and creation of HTTP / SOCKS proxy clients |
| `Core/NetworkGuard.cs` | Refuses any mail or HTTP hop that has no proxy; redacts IPv4 addresses from the crash log |
| `Services/ImapSyncService.cs`, `Services/ImapSyncSession.cs` | IMAP connect, authenticate, folder listing, header fetch. Read-only: the server is never modified |
| `Services/MessageBodyLoader.cs`, `Services/AttachmentDownloadService.cs` | On-demand body and attachment download |
| `Services/SmtpSendService.cs`, `Services/OutgoingMessageBuilder.cs` | SMTP over TLS on port 465; text + HTML message assembly, attachments, threading headers |
| `Services/OAuthAuthenticator.cs`, `Services/AppPasswordAuthenticator.cs`, `Services/TokenService.cs` | XOAUTH2 / app-password sign-in and token lifetime |
| `Services/ProxiedMediaFetcher.cs` | Remote images in a message, fetched through the proxy only when **Load media** is on |
| `WebView2Host.cs`, `WebViewGuard.cs`, `WebViewContextMenu.cs` | The message viewer. Its WebView2 runs with a dead proxy (`127.0.0.1:1`) and a request filter, so page content cannot reach the network by itself |
| `LinkBrowserWindow.xaml.cs`, `MessageLinks.cs` | Clicked links open in a separate WebView2 configured with the account's proxy (`--proxy-server`, no QUIC, no WebRTC leak, no prefetch) |
| `WebView2NativeBootstrap.cs` | Checks `data\runtime\WebView2Loader.dll` against the copy embedded in the exe on every start |

What a reviewer should expect to confirm:

- **No telemetry, no update check, no home server.** There is no URL in the code other than the Yahoo/AOL hosts above and the author's Telegram link shown in **About**.
- **Fail closed.** `NetworkGuard` throws before any connection when no proxy is configured. A SOCKS proxy that needs a password cannot be used by WebView2, so links on such accounts fail instead of going direct.
- **Server read-only.** IMAP folders are opened for reading; no flags are set, nothing is moved or deleted.
- **Local storage.** `Data/MailDatabase.cs` is LiteDB with AES; the random database password is protected with Windows DPAPI for the current user (`Data/DatabaseKeyStore.cs`), so a copied `data\` folder does not open elsewhere. Account passwords and tokens live only in that database and in the user's own `account.txt`.
- **Logging.** `AppLog.cs` writes only crashes to `data\mailclient.log`, with IPv4 addresses redacted. Routine errors stay in the UI.
- **OAuth client keys.** `YahooAolTokenClient.cs` uses the public Yahoo/AOL client IDs that Mozilla Thunderbird ships in its open source; Yahoo and AOL issue no per-developer keys for IMAP clients, so every third-party client does the same.

## Project layout

```
ZennoVibe.MailClient.csproj   project file; Release = win-x64 single-file publish to dist-alpha\
App.xaml(.cs)                 startup, single-instance check, crash handler
MainWindow.xaml(.cs)          accounts, folders, message list, preview, compose pane
*Window.xaml(.cs)             Reply, Settings, Address book, Message, Link, Profile, dialogs
Core/                         protocol-level pieces: sites, proxies, OAuth, guards
Services/                     IMAP sync, body loading, SMTP, import/export
Data/                         LiteDB entities and database; data\ templates copied next to the exe
Themes/                       WPF styles and palette
Assets/                       icon
README.txt                    user manual, shipped with the program
```

## License and contact

Source-available for audit; see [LICENSE](LICENSE). Author: [@Ptereks](https://t.me/Ptereks) on Telegram.
