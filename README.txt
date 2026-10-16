# Yahoo & AOL Mail Client 1.11.9

Reads Yahoo and AOL mailboxes over IMAP and sends through their SMTP. A refresh token signs in with OAuth (XOAUTH2); an account without one uses the password column as an app password. Every connection goes through a proxy; without one the program refuses to connect. Windows 10 or 11, 64-bit.

## Setup

Install the **.NET 8 Desktop Runtime**, x64 (`winget install Microsoft.DotNet.DesktopRuntime.8`):
<https://dotnet.microsoft.com/download/dotnet/8.0>

`data\account.txt` — one account per line. Column order is set in **Settings → Format box**; the default is

```
mail:password:phone:access_token:refresh_token
```

A short last field with `~001~` is a refresh token. A last field of 16 letters (spaces allowed) is an app password: the password column stays the account password, and sign-in uses the app password. A line of just `mail:password` signs in with that password. Leave the last field empty and put the app password in the password column when you have no separate account password.

`data\proxy.txt` — one proxy per line:

```
host:port
host:port:user:pass
user:pass:host:port
user:pass@host:port
socks5://user:pass@host:port
```

Optional prefix: `http`, `https`, `socks5`, `socks5h`, `socks4`, `socks4a`. No prefix means HTTP, then SOCKS5 if that fails. Separators: `: ; | ,` space or tab. `#` comments and bad lines are skipped.

Both paths can be changed in **Settings → Files**.

## First run

`MailClient.exe` → gear → **Import accounts** → **Sync all** → **Save**. Select an account, pick a folder, open a message with a double-click or Enter.

Only one copy runs per folder; a second start says so and exits.

## Main window

**Title bar:** sync all, import accounts, delete the selected account, export all accounts to a file (same column order and delimiter as Settings), Settings.

**Accounts.** The sync icon updates one account. A green border means the last sync succeeded, red means it failed (hover for the error). **P** marks priority accounts. Ctrl/Shift click and Ctrl+A select several; the search box filters by email. Double-click an account to open its **Profile**: a free-text note and the proxy this account uses, picked from the list or typed in (a new line is appended to the proxy file).

Right-click menu: **Copy email address**, **Sync selected**, **Priority sync** (on/off for the selection), **Change proxy...** (one proxy line for every selected account), **Export selected...**, **Create group from selected...**, **Add selected to group**, **Delete account**.

**Groups.** **New group** makes an empty group or one from the selection, **Move selected** moves accounts to a group or back to Ungrouped, **Delete group** asks whether to keep the accounts (they go to Ungrouped) or delete them too. Each group header also has its own delete button.

**Folders.** Inbox, Bulk and Spam arrive first and open while the rest of the folders still syncs. **All** clears the folder filter and shows every message of the account.

**Messages.** Click a column header to sort by From, Subject or Date. A paperclip marks attachments. Right-click: **Open**, **Reply**, **Copy sender**, **View source**, **Download attachments**. Search covers mail already downloaded from every account, not the server.

**Preview.** The selected message renders below the list. A one-time code in the text becomes a **Copy 123456** button. The toolbar copies the sender, replies, downloads attachments and opens the message in its own window. If the account's access token has expired, the preview says so under the loading circle while the token is refreshed; the folder sync that follows runs in the background.

**Address book.** Every address the program has seen, with how many messages it appeared in and when it was last seen. Filter by address or note, write a note straight in the Note column, copy the address with the icon or the right-click menu.

**Status bar.** Account count, the last action, `synced / total` (successful accounts only) and the sync progress.

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

**Threads** (1–500) is how many accounts run at once. Priority accounts go first. **Cancel sync** in Settings stops a running pass.

**Messages** (10–2000) is how many of the newest messages the first sync keeps per account, all folders together. Later syncs only add new mail. Stored mail is never deleted and the server is never modified.

Sync downloads headers only. The body loads the first time you open a message and is kept; the next five messages prefetch. Dates are UTC.

The status times the phases **token**, **connect**, **folders**, **store**. A slow connect is the proxy; a slow token is OAuth. After **Sync all** those times are averages; each account tooltip keeps its own last run.

Accounts are spread across the proxy list unless a proxy is pinned in the account's Profile or with **Change proxy**. With one proxy, a failed account is retried once.

## Reading mail

**Open** shows the message in a separate window with From, To, date, the body, and buttons to copy the sender or recipient, copy a one-time code and download attachments. **View source** shows the raw message.

Attachments save to `data\attachments\<account email>\` and are never opened by the program. Windows treats them as browser downloads (SmartScreen, Office Protected View). File names from senders are sanitised. Messages over 200 MB are skipped.

The message view never opens its own connections. Remote content is blocked, or fetched through the account proxy when **Load media** is on; scripts, frames, forms, downloads and Save as stay off. Tracking pixels load only with media on.

A link you click opens in a window inside the program, through the proxy of that account; without a proxy it does not open. Right-click a link for **Copy link** or **Open link**, and selected text for **Copy**. The link window has Back, Reload and Copy address; downloads are refused there too. A SOCKS proxy with a password cannot be used by the browser, so such links fail instead of leaking. Links the message tries to follow by itself are blocked.

## Sending mail

Mail goes out through the proxy of the sending account, over SMTP with TLS (`smtp.mail.yahoo.com` or `smtp.aol.com`, port 465), with the same token or app password the account signs in with. Every message is text plus HTML, built the way Yahoo Mail or AOL Mail builds it, with a Message-ID of the account's domain and a UTC date, so it looks like webmail sent it.

**Reply** (the arrow button, or R) opens its own window and sends from the selected account. **To** is the sender, **Subject** is `Re:` plus the original subject. **Original message** keeps the quoted mail collapsed; it is included when you send, in Yahoo's "On …, … wrote:" form or AOL's "-----Original Message-----" block depending on the account, and the reply carries In-Reply-To and References so it lands in the same conversation. The window closes after a successful send.

**Compose** in Settings adds a pane for a new message at the bottom of the main window. Select the account first: that address is **From**. **To** takes one or more addresses, separated by commas or semicolons (`Name <address>` is fine). **Send** clears the pane when the mail goes out.

**Attachments.** The paper-clip button next to **Send** attaches files; the file dialog states the limit of 25 MB per message in total, and a file that would exceed it is refused with a warning. Attached files show as chips with a cross to remove them and a line with the running total. Files removed from disk before sending are dropped with a note.

Closing the program while a message is still sending asks to wait, cancel the send, or stay open.

## Settings

**Actions:** Import accounts, Sync all, Cancel sync, About, Documentation (this file), Export database.

**Threads** and **Messages** as described under Sync. **Compose** shows the new-message pane; it stays off until you turn it on, and Reply does not need it. **Load media** fetches images, fonts, video and stylesheets through the account proxy when a message is opened; off keeps the real IP hidden from mail content.

**Files:** paths of the account file and the proxy file.

**Account format.** Drag tags from **Available tags** into the **Format box** to set the column order of the account file; drag out or click × to remove. **Additional tags** are optional columns: `phone` and `access_token` are built in, and any name typed with Enter becomes a column too. The same format is used for import and export.

**Export database** writes an unencrypted copy of the whole database (accounts, tokens, mail). Drop that `.db` over `data\DB\mailclient.db` and the next start encrypts it again.

Settings are saved with **Save**.

## Data and safety

Everything is in `data\`. Routine errors stay in the UI. Only a crash is written to `data\mailclient.log` (the previous one is `mailclient.log.previous-crash`). IPv4 addresses in that log are redacted.

`data\DB\mailclient.db` is AES-encrypted. `data\DB\mailclient.key` is bound to this Windows user, so a copied `data\` folder will not open on another machine; move mail with **Export database**. On every start, `data\runtime\WebView2Loader.dll` is checked against the copy inside the exe. JetBrains Mono is in `data\runtime\` as well.

Every connection — IMAP, SMTP, token refresh, media, links — goes through the account's proxy. A connection without a proxy is refused rather than made directly.
