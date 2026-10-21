<div align="center">
<img src="docs/icon.png" width="96" alt="">

# PingWhen

**Stop babysitting progress bars: a toast or a Telegram ping when the job is done.**

**English** · [Русский](README.ru.md)

[![Release](https://img.shields.io/github/v/release/lojekq/PingWhen?sort=semver)](https://github.com/lojekq/PingWhen/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/lojekq/PingWhen/total)](https://github.com/lojekq/PingWhen/releases)
[![CI](https://github.com/lojekq/PingWhen/actions/workflows/ci.yml/badge.svg)](https://github.com/lojekq/PingWhen/actions/workflows/ci.yml)
![Windows 11](https://img.shields.io/badge/Windows-11-0078D4)
[![License](https://img.shields.io/github/license/lojekq/PingWhen)](LICENSE)

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/main-dark-en.png">
  <img src="docs/main-light-en.png" width="820" alt="PingWhen: active watchers">
</picture>
</div>

## Why

A render, a model download, a build or a training run takes 40 minutes, and you keep alt-tabbing back to check.
PingWhen watches the process, window, file or load for you and pings your PC, or your phone when you are away
or in a fullscreen game. Your AI agents get the same engine: "Claude finished" and "Claude needs permission" reach your phone.

## Features

- **Any window, one hotkey** — `Ctrl+Alt+W`: process exit (with exit code), window closed or title changed, file stopped growing, CPU/GPU/disk/network gone quiet.
- **Terminal-aware** — watches the command running inside Windows Terminal or VS Code, not the terminal itself.
- **Region watch** — draw a rectangle on a window: "stopped changing", "changed" or "the text 100% appeared" (Windows OCR), even behind other windows.
- **PC or phone** — a toast while you are at the PC; a message from your own Telegram bot or ntfy when you are away or in a fullscreen game, optionally with a screenshot.
- **Built for AI agents** — an MCP server and Claude Code hooks: "done" and "needs your answer" reach the phone; opt-in Allow once / Deny buttons answer the permission prompt from Telegram.
- **Every time, deadlines, flashing** — repeat a watcher ("every time ffmpeg exits"), get "not done in 30 min", or wait for a taskbar button to flash.
- **Local-only** — no account, no telemetry, no admin rights, no author's server. The bot is yours; tokens are encrypted with Windows DPAPI.

## Screenshots

| | |
|---|---|
| ![New watcher for a Windows Terminal command](docs/new-dark-en.png) | ![History](docs/history-dark-en.png) |
| ![Agents: MCP command and hooks](docs/agents-light-en.png) | ![Settings: Telegram bot](docs/telegram-light-en.png) |

## Download

1. **[Download PingWhen.exe](https://github.com/lojekq/PingWhen/releases/latest/download/PingWhen.exe)** (latest release). One file, no admin rights.
2. Smaller build: [`PingWhen-fd.zip`](https://github.com/lojekq/PingWhen/releases/latest/download/PingWhen-fd.zip) (needs [.NET Desktop Runtime 10](https://dotnet.microsoft.com/download/dotnet/10.0)).
3. Optional check: `gh attestation verify <file> -R lojekq/PingWhen` or compare with `SHA256SUMS.txt`.

Requirements: Windows 11 x64. Windows 10 22H2 should work but is not tested.

## First run

1. Run the downloaded exe from anywhere. Windows may show a SmartScreen warning first (see FAQ).
2. PingWhen offers to install itself: **Install** copies it to `%LOCALAPPDATA%\Programs\PingWhen\PingWhen.exe`, adds a Start menu shortcut, autostart and an entry in Settings → Apps; no admin rights. The agent commands point there.
   **Run without installing** keeps it portable. To update, run a newer exe the same way: it closes the running PingWhen, replaces the installed one and starts it.
3. PingWhen lives in the tray. Press `Ctrl+Alt+W` in the window you want to watch, or use **Tell me when…** in the tray menu.

**Phone (Telegram, two minutes).** In Telegram open [@BotFather](https://t.me/BotFather), send `/newbot`, pick a name and a username, copy the token.
In PingWhen: Settings → Telegram → paste the token → **Check** → **Pair** → **Open in Telegram** → **Start** (or send the 8-character code to the bot from your phone).
The bot only sends, and only to the chat that sent the code. Use a separate bot for each PC.
Where Telegram is blocked, turn on **ntfy** in Settings: a random topic on ntfy.sh or your own server, subscribe by scanning the QR code in the ntfy app.

**Region of a window:** New watcher → the window → "Region stops changing" (or "changes", "Text appears in the region") → **Select region…** → drag a rectangle over the progress bar.
The window may be covered by others; a minimized window pauses the watch. Text recognition uses the Windows OCR languages you have installed.

**From scripts:** `npm run build && PingWhen.exe --notify "Build finished"`; title and body: `PingWhen.exe --notify "Title" "Body"`.

## AI agents (MCP and hooks)

Claude Code, Codex CLI, Cursor and Claude Desktop start `PingWhen.exe --mcp`: a tiny stdio relay to the tray over a per-user named pipe, so agents and the window share the same watchers.
The Agents page shows these commands with your real path and a Copy button:

```powershell
claude mcp add --scope user --transport stdio pingwhen -- "C:\Users\<you>\AppData\Local\Programs\PingWhen\PingWhen.exe" --mcp
claude mcp list
```

Tools: `notify`, `watch_process` (by pid, name or command line substring), `watch_file`, `watch_window` (closed, title, flashed), `watch_quiet`, `list_watchers`, `cancel_watcher`; watch tools take `repeat` and `deadline_minutes`.
Limits: 6 notifications a minute per session and 60 an hour; 20 watchers per session, 50 in total; agent watchers expire after 24 h.

Hooks bring "<folder>: Claude finished" and "<folder>: needs your answer" even while Claude waits for permission and cannot call a tool. Add to `~/.claude/settings.json` (the Agents page has it with your path):

```json
{ "hooks": {
  "Stop":             [{ "hooks": [{ "type": "command", "command": "C:\\Users\\<you>\\AppData\\Local\\Programs\\PingWhen\\PingWhen.exe", "args": ["--hook"], "timeout": 10 }] }],
  "Notification":     [{ "hooks": [{ "type": "command", "command": "C:\\Users\\<you>\\AppData\\Local\\Programs\\PingWhen\\PingWhen.exe", "args": ["--hook"], "timeout": 10 }] }],
  "UserPromptSubmit": [{ "hooks": [{ "type": "command", "command": "C:\\Users\\<you>\\AppData\\Local\\Programs\\PingWhen\\PingWhen.exe", "args": ["--hook"], "timeout": 10 }] }]
}}
```

There is no ping while the session's terminal or editor is the active window and you are at the PC. `UserPromptSubmit` lets PingWhen skip "finished" for short turns (Agents page). `--hook` always exits 0 and prints nothing, so it never stops Claude.

**Answer from the phone (opt-in).** Settings → Telegram → "Answer Claude Code's permission prompts from the phone" adds a `PermissionRequest` hook (timeout 150) to the JSON.
While you are away, the prompt comes to Telegram with **Allow once** and **Deny**; no answer in 2 minutes and Claude shows its usual dialog. Only your paired account can press; there is no "always allow" and no text input.

## How it works

One tray process keeps the watchers, samples windows, files and performance counters once a second and waits on process handles; with no watchers it does nothing.
A router decides between a toast and the phone: it looks at keyboard and mouse idle time and at fullscreen games or presentations.
The phone channel is your own bot: PingWhen calls the Bot API directly, pairs with your chat by a one-time code and from then on only sends.

## Settings

| Setting | Default | What it does |
|---|---|---|
| Telegram | not set up | Your bot's token (DPAPI), paired chat, Test, Unpair |
| ntfy | off | Server (ntfy.sh or yours), random topic, optional token (DPAPI), Test, QR to subscribe |
| Screenshots to the phone | off | Watchers with a screenshot also send it through Telegram or ntfy; off = pictures stay in toasts |
| Answer permission prompts from the phone | off | Telegram buttons Allow once / Deny for Claude Code, only while you are away |
| I'm away after | 3 min | Auto route: the phone gets a message after this long without input |
| Phone during fullscreen games and presentations | on | Auto route: the phone also gets it while a game or presentation is fullscreen |
| Hotkey | `Ctrl+Alt+W` | New watcher for the active window |
| Claude's reply on the phone | off | Agents page: the first 150 characters of Claude's last message in the "finished" message |
| "Claude finished" only after turns of at least | 0 s | Agents page: skip short turns (needs the `UserPromptSubmit` hook) |
| Language, theme | Windows | English or Russian (after a restart), light or dark |
| Start with Windows | on after install | Starts in the tray |
| Start menu shortcut | on after install | A shortcut in the Start menu |
| Installation | — | Install, or Uninstall when installed |
| Check for updates | on | Once a day asks GitHub about a new release, downloads nothing |

Per watcher: **Route** Auto / PC only / Phone only / PC and phone, **Don't let the PC sleep while waiting**, **Wait for child processes too** (installers), **Every time**, **Screenshot of the window in the notification** (off; in the toast, on the phone only with "Screenshots to the phone"), **Tell me if it is not done within N minutes**. Tray: **Do not disturb for 1 h** mutes the phone, toasts stay.
Data lives in `%APPDATA%\PingWhen`: `settings.json`, `watchers.json`, `history.jsonl` (last 500 events), `logs\`.

## FAQ

<details><summary>Windows protected your PC / SmartScreen warning</summary>

The exe is not code-signed yet, so Windows does not know the publisher. Click **More info → Run anyway**.
Every file is built by GitHub Actions from this repository; you can check it with `gh attestation verify` or `SHA256SUMS.txt`.
Smart App Control, if it is on, blocks unsigned files with no way to allow one; the only option is to turn it off.
</details>

<details><summary>My antivirus flags it</summary>

It is a false positive: no packer, no injection or keyboard hooks, it installs only into your user folder (the hotkey is `RegisterHotKey`). It reads other processes' names and exit codes, which some heuristics dislike.
Please report it to your antivirus vendor and open an issue with the detection name and the SHA256 of the file.
</details>

<details><summary>Telegram is blocked in my country</summary>

The toast on the PC always works. Turn on ntfy in Settings (ntfy.sh or your own server). A VPN or proxy set in Windows is used automatically for both.
</details>

<details><summary>Region watch shows a yellow border or no text is found</summary>

Windows draws a yellow border around a captured window on builds older than Windows 11 24H2; on newer builds PingWhen turns it off.
"Text appears" needs a Windows OCR language (Settings → Time &amp; language → Language: a language with optical character recognition). A minimized window gives no picture: the watch pauses.
</details>

<details><summary>Is answering Claude from the phone safe?</summary>

Whoever controls your Telegram account can approve your agent's actions, so it is off by default. A press counts only from your paired account, only once and only while the request waits; there is no "always allow".
</details>

<details><summary>My bot token leaked</summary>

In @BotFather send `/revoke` for the bot, paste the new token in Settings → Telegram and pair again. Old messages stay in your chat; nobody else can send to it through PingWhen.
</details>

<details><summary>"Another program or another PC already polls this bot"</summary>

Pairing reads the bot's updates, which only one program at a time can do. Use one bot per PC; if the bot has a webhook, create a separate bot for PingWhen (PingWhen never deletes a webhook).
</details>

<details><summary>Updating while agents are connected</summary>

Just run the newer exe: the installed one is moved aside even while `--mcp` relays use it, and agents get the new version with their next session.
</details>

<details><summary>How do I uninstall?</summary>

Settings → **Uninstall**, or Windows Settings → Apps → PingWhen → Uninstall. You choose whether settings and logs go too.
PingWhen does not edit Claude Code's files: remove the MCP server yourself (`claude mcp remove pingwhen`) and the PingWhen hooks from `~/.claude/settings.json`.
Without installing: About → **Remove data and autostart**, then delete the exe.
</details>

## Privacy

No telemetry, no account, no author's server. Network requests:
- `https://api.telegram.org/bot<your token>/…` — only when sending a message, checking the token, pairing or waiting for a permission button (getMe, getUpdates, sendMessage, sendPhoto, answerCallbackQuery, editMessageText). Messages go through Telegram's servers: watcher titles, window titles, process and file names; Claude's reply text, screenshots (two switches: on the watcher and "Screenshots to the phone") and permission requests only if you turn them on.
- Your ntfy server (`https://ntfy.sh` by default) — only if ntfy is on: the same texts, screenshots if turned on. Anyone who knows the topic can read it.
- `GET https://api.github.com/repos/lojekq/PingWhen/releases/latest` once a day; turn it off in Settings.

The agent pipe `\\.\pipe\PingWhen-<your SID>` is open only to your Windows account. Agents can notify and watch (metadata only); they cannot read files or change settings.
Logs contain no window titles, paths, command lines or tokens.

## Build from source

```powershell
winget install Microsoft.DotNet.SDK.10
git clone https://github.com/lojekq/PingWhen
cd PingWhen
dotnet run --project src/PingWhen
dotnet test
```

## License

MIT © 2026 lojekq
