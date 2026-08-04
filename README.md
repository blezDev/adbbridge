# AdbBridge

## About

A single Windows app that lets Android Studio, running on a Cloud PC with no USB
access, see a phone plugged into a separate Local PC.

One `AdbBridge.exe`, copied to both machines. On launch it asks what this PC is:

- **Host** — the PC with the phone plugged into it over USB.
- **Companion** — the PC Android Studio runs on.

The Companion screen continuously shows two independent, auto-updating facts:
- **Host: connected / unreachable / checking…** — is the tunnel actually reaching the
  Host's real adb server right now.
- **Phone: forwarded / not detected / present but not ready** — is a device actually
  showing up through that connection, and in what state.

Tokens are encrypted at rest with Windows DPAPI and never passed as plain command-line
arguments to ngrok/ssh, and Pinggy's SSH host key is pinned via a real known_hosts file
instead of disabling verification. On startup the app checks GitHub Releases for a newer
version and shows a dismissible banner with a link if one exists — it only reads the
latest release's tag, never downloads or replaces anything automatically.

## Prerequisites

- **.NET 8 SDK** on whichever machine you build on — https://dotnet.microsoft.com/download/dotnet/8.0
  (this repo builds a standalone `.exe`, so the *other* machine only needs the published
  `.exe`, not the SDK or even the .NET runtime).
- Local PC (Host role): `adb.exe` from any platform-tools install, plus **one** tunnel
  provider:
  - **ngrok** — account + authtoken (free plan is fine) — https://dashboard.ngrok.com/get-started/your-authtoken,
    and `ngrok.exe`. Free-plan addresses change on every restart (handled automatically,
    but you'll re-paste into the Companion screen each time).
  - **Pinggy** — account + token — https://dashboard.pinggy.io. Uses the OpenSSH client
    that ships with Windows 10/11 (`C:\Windows\System32\OpenSSH\ssh.exe`), no extra
    binary needed. A **Pinggy Pro** token gives a persistent address that never changes
    across Host restarts, which free-tier ngrok can't do.
- Cloud PC (Companion role): the `adb.exe` path Android Studio actually uses (its
  bundled SDK platform-tools, e.g. `...\AppData\Local\Android\Sdk\platform-tools\adb.exe`).

## Build

```bash
dotnet build AdbBridge.sln -c Release
```

To produce a standalone, single-file executable (no .NET runtime needed on the target
machine):

```bash
dotnet publish src/AdbBridge.App/AdbBridge.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

The published `AdbBridge.exe` lands under
`src/AdbBridge.App/bin/Release/net8.0-windows/win-x64/publish/` — copy that one file to
both the Local PC and the Cloud PC. Prebuilt releases are also available under
[Releases](../../releases).

## Usage

Launch `AdbBridge.exe` on both machines. Pick **Host** on the Local PC (phone attached)
and **Companion** on the Cloud PC (Android Studio). Configure each once — settings are
saved for next time — then leave both running (minimize to tray) for the session.

## How to Use

1. Launch `AdbBridge.exe` on both machines. Pick **Host** on the Local PC (phone
   attached) and **Companion** on the Cloud PC (Android Studio).

**Host screen (Local PC):**
1. Fill in the `adb.exe` path, pick a **Tunnel provider** (ngrok or Pinggy), and fill in
   that provider's path/token fields (saved after first run).
2. Click **Start Sharing**. Once the status panel shows the tunnel connected, copy the
   address shown (e.g. `0.tcp.ngrok.io:12345` or `xxxxx.a.pinggy.link:40527`).
3. Confirm your phone shows up in the local device list.

**Companion screen (Cloud PC):**
1. Launch this *before* opening Android Studio (or restart Studio after connecting).
2. Confirm the `adb.exe` path matches the one Android Studio uses.
3. Paste the address from the Host screen and click **Connect**.
4. Watch the status panel: wait for **Host: connected** and **Phone: forwarded —
   `<model>`**. That confirms the relay + tunnel are working end-to-end before you
   even touch Android Studio.
5. Open Android Studio — it should detect the device automatically.

Leave the app running (minimize to tray) on both machines for the rest of the session.
With ngrok free plan or a Pinggy free tunnel, the address changes on every Host restart
— re-copy the new address into the Companion screen and hit Connect again; everything
else recovers automatically. With a **Pinggy Pro** token the address is persistent, so
this step goes away entirely.

**Run at Windows startup:** checking this box on either screen registers
`AdbBridge.exe host` or `AdbBridge.exe companion` (not the bare exe) in the Windows
startup Run key, so a relaunch after login goes straight to that screen instead of the
role picker. On the Companion screen, also checking **Auto-connect on startup** will
reconnect automatically using the last address you entered, without any click.

## Known limitations

- **No fully automatic address handoff on free tiers.** Unless you're on Pinggy Pro (or
  a paid ngrok reserved address), the tunnel address changes on restart and there's no
  domain to publish a stable one to, so you'll re-paste the address into the Companion
  screen. A natural follow-up (not built) is having the Host screen publish its address
  somewhere the Companion screen can poll — e.g. a private GitHub Gist — if this becomes
  annoying on a free plan.
- **Cloudflare Tunnel isn't wired in yet.** `ITunnelProvider` is designed so it can be
  added later, but raw TCP through Cloudflare needs Zero Trust + a domain you don't
  currently have. `CloudflareTunnelProvider` is a documented stub for when that changes.
- **Pinggy's SSH login prompt.** Pinggy's tunnel endpoint doesn't do real password auth,
  but `ssh.exe` occasionally still waits on a prompt when it can't attach a real
  terminal; `PinggyTunnelProvider` sends a blank line on connect to get past this. If a
  particular OpenSSH build still hangs, check the Host screen's log — the raw ssh output
  is echoed there.
