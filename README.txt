# Yahoo & AOL Mail Client 1.11.17

Reads Yahoo and AOL mailboxes over IMAP and sends through their SMTP. Every connection goes through a proxy; without one the program refuses to connect. Windows 10 or 11, 64-bit.

## Setup

Install the **.NET 8 Desktop Runtime**, x64 (`winget install Microsoft.DotNet.DesktopRuntime.8`):
<https://dotnet.microsoft.com/download/dotnet/8.0>

`data\account.txt` — one account per line. Column order is set in **Settings → Format box**; the default is

```
mail:password:phone:access_token:refresh_token
```

The last column is the refresh token (OAuth sign-in) or, if the account uses one, an app password; the program tells them apart. With neither, the account signs in with the password column. A plain `mail:password` line works too.

`data\proxy.txt` — one proxy per line:

```
host:port
host:port:user:pass
user:pass:host:port
user:pass@host:port
socks5://user:pass@host:port
```

Prefix `http`, `https`, `socks5`, `socks5h`, `socks4` or `socks4a`; no prefix means HTTP, then SOCKS5 if that fails. Separators: `: ; | ,` space or tab. `#` lines and bad lines are skipped.

Both paths can be changed in **Settings → Files**.

## Activation

The program is bound to one computer. The first start shows an activation window with a **Machine ID**. Send that ID to the owner, paste the **activation code** you get back and press **Activate**; later starts open straight away.

A code fits only that machine. A copy on another computer, a reinstalled Windows or a major hardware change needs a new code from the owner. To reset activation, delete `data\mailclient.activation`.

## First run

`MailClient.exe` → activate → gear → **Import accounts** → **Sync all** → **Save**. Select an account, pick a folder, open a message with a double-click or Enter. Only one copy runs per folder.

## Main window

**Title bar:** sync all, import accounts, delete the selected account, export accounts (all, or only synced ones), Settings.

**Accounts.** The sync icon updates one account. Green border: last sync succeeded; red: it failed (hover for the error). **P** marks priority accounts. Ctrl/Shift-click and Ctrl+A select several; the search box filters by email. Double-click an account for its **Profile**: a note and the proxy it uses.

Right-click: **Copy email address**, **Sync selected**, **Priority sync**, **Change proxy...**, **Export selected...**, **Create group from selected...**, **Add selected to group**, **Delete account**.

**Groups.** **New group**, **Move selected** (to a group or back to Ungrouped), **Delete group** (keep or delete its accounts).

**Folders.** Inbox, Bulk and Spam arrive first and open while the rest still syncs. **All** shows every message of the account.

**Messages.** Click a header to sort by From, Subject or Date. A paperclip marks attachments. Right-click: **Open**, **Reply**, **Copy sender**, **View source**, **Download attachments**. Search covers downloaded mail from every account, not the server.

**Preview.** The selected message renders below the list; a one-time code in it becomes a **Copy 123456** button. The toolbar copies the sender, replies, downloads attachments and opens the message in its own window.

**Address book.** Every address seen, with message count and last date. Filter by address or note, write notes in the Note column, copy with the icon or right-click.

**Status bar.** Account count, last action, `synced / total`, sync progress.

### Keyboard

| Key | Action |
| --- | --- |
| J / K | Next / previous message |
| Enter | Open the message in a window |
| R | Reply |
| Ctrl+C | Copy the sender (message list focused) |
| Ctrl+F | Jump to search |
| Ctrl+A | Select all accounts or all messages |
| F5 | Sync the selected accounts, or all |

## Sync

**Threads** (1–500): accounts synced at once; priority accounts go first. **Messages** (10–2000): how many of the newest messages the first sync keeps per account; later syncs only add new mail. Stored mail is never deleted and the server is never modified. **Cancel sync** in Settings stops a running pass.

Sync downloads headers and the bodies of the five newest messages per account; other bodies load when opened and are kept. Dates are UTC. An access token lives an hour and is refreshed through the account proxy when needed; the status bar says so.

The status shows the time of each phase: a slow **connect** is the proxy, a slow **token** is OAuth. Accounts are spread across the proxy list unless a proxy is pinned in the Profile or with **Change proxy**.

## Reading mail

**Open** shows the message in its own window with buttons to copy addresses, copy a one-time code and download attachments. **View source** shows the raw message.

Attachments save to `data\attachments\<account email>\` and are never opened by the program. Messages over 200 MB are skipped.

Remote content is blocked unless **Load media** is on; then it loads through the account proxy. Scripts, frames, forms and downloads stay off. Links open in a window inside the program through the account proxy, never directly; right-click a link for **Copy link** or **Open link**. A SOCKS proxy with a password cannot be used for links, so they fail instead of leaking.

## Sending mail

Mail goes out through the account's proxy over TLS SMTP, as text plus HTML, the way Yahoo Mail or AOL Mail sends it.

**Reply** (arrow button, or R) sends from the selected account with the quoted original collapsed below; it lands in the same conversation. **Compose** (enable in Settings) adds a pane for a new message; select the account first, **To** takes several addresses separated by commas or semicolons.

**Attachments:** the paper-clip next to **Send**, up to 25 MB per message in total.

Closing the program while a message is sending asks to wait, cancel or stay open.

## Settings

**Actions:** Import accounts, Sync all, Cancel sync, About, Documentation (this file), Export database.

**Threads**, **Messages**: see Sync. **Compose**: shows the new-message pane. **Load media**: loads images and styles through the account proxy; off keeps the real IP hidden from mail content.

**Files:** account file and proxy file paths. **Account format:** drag tags into the **Format box** to set the column order; **Additional tags** add optional columns. The same format is used for import and export.

**Export database** writes an unencrypted copy of the whole database (accounts, tokens, mail). Drop that `.db` over `data\DB\mailclient.db` and the next start encrypts it again.

Settings are saved with **Save**.

## Data

Everything is in `data\`. The log is `data\mailclient.log`; the previous run is kept as `mailclient.log.previous-run`. Send both with any bug report. Passwords and tokens are never written to it.

`data\DB\mailclient.db` is encrypted and bound to this Windows user: a copied `data\` folder will not open elsewhere, so move mail with **Export database**. `data\mailclient.activation` holds the activation code; it is useless on another computer.

Every connection — IMAP, SMTP, token refresh, media, links — goes through the account's proxy. Without a proxy nothing connects.
