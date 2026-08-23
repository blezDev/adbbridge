# AdbBridge — project context

Windows WPF app (net8.0-windows) that lets Android Studio on a Cloud PC (no USB access)
see a phone plugged into a separate Local PC, by relaying the adb protocol over an
ngrok/Pinggy tunnel. One `AdbBridge.exe`, run as **Host** (phone attached) on one machine
and **Companion** (Android Studio) on the other. Full usage/build steps: [README.md](README.md).

Solution: `AdbBridge.sln` → `src/AdbBridge.App` (WPF UI) + `src/AdbBridge.Core` (relay,
tunnel, adb process management — no UI dependency).

## Current state (as of 2026-08-23, commit `b10e31c`)

Latest change adds tunneling support for Android Studio's **Device Mirroring / App
Inspection / Layout Inspector / Network-Database Inspector / JDWP debugging**. These
don't ride the normal adb protocol — `adb forward` makes the real adb server open a
*separate* TCP listener, and Studio connects to that port directly. Before this change,
only the adb server's own port was tunneled, so that direct connection found nothing on
the Companion side and the feature failed silently even though `adb forward` reported
success. Full explanation in [README.md § Device Mirroring and App Inspection](README.md#device-mirroring-and-app-inspection).

New pieces, all in `src/AdbBridge.Core/Relay/`:
- `AdbForwardWatcher.cs` — best-effort parser of the adb host protocol that detects
  `forward:`/`killforward` requests flowing through `TcpRelay`.
- `ForwardBroker.cs` — Host side. Listens on `127.0.0.1:5099` next to the real adb
  server; routes connections tagged with a `FWD:<port>\n` marker line to the matching
  `127.0.0.1:<port>` forward listener the adb server just opened.
- `ForwardPortRelay.cs` — Companion side. Watches `TcpRelay`'s forward events and opens
  a matching local listener for each one, tagging outbound connections with the marker.
- `RelayPump.cs` — shared bidirectional copy-loop helper used by both sides.
- `TcpRelay.cs` — existing class, extended to run traffic through `AdbForwardWatcher` and
  raise `ForwardEstablished`/`ForwardRemoved`/`ForwardsCleared`.

## What's been verified vs. what hasn't

**Verified (Host-side machine, this session):** `dotnet build AdbBridge.sln` is clean.
A throwaway console harness (not committed — lived in a scratch dir) wired up the real
`ForwardBroker` + `TcpRelay` + `ForwardPortRelay` classes over loopback TCP (standing in
for the tunnel) against the real, USB-attached device. Confirmed: plain `adb devices`
works through the relay; `adb forward tcp:0 tcp:19999` triggers `ForwardPortRelay` to
open a matching local listener within ~1s; a raw TCP client can connect through that
listener → marker protocol → `ForwardBroker` → the Host's real forwarded port,
end-to-end.

**Not yet verified — this is the Companion-side gap:**
- The real two-machine flow through an actual ngrok or Pinggy tunnel (only loopback was
  tested — tunnel providers themselves weren't touched by this change, but the marker
  protocol has never crossed a real tunnel).
- Android Studio actually attaching Device Mirroring / Layout Inspector / Network
  Inspector / a JDWP debug session through a live `AdbBridge.exe` Companion instance.
  Android Studio wasn't available in the Host-side test environment.
- Behavior under Studio's real usage pattern (multiple concurrent forwards, forwards
  torn down and re-created as Studio reconnects, `killforward`/`forward --remove`).

## If you're picking this up on the Companion PC

1. `git pull`, `dotnet build AdbBridge.sln -c Release`.
2. Run `AdbBridge.exe companion` here, `AdbBridge.exe host` on the machine with the
   phone. Connect them per the README's Usage section.
3. Once the Companion screen shows **Host: connected** and **Phone: forwarded**, open
   Android Studio and try Device Mirroring / Layout Inspector on the phone. Watch the
   Companion app's log panel — `ForwardPortRelay` logs each local listener it opens
   (`"Relaying 127.0.0.1:<port> through the tunnel for App Inspection / Device
   Mirroring."`), so silence there when Studio should be attaching is the first thing to
   check.
4. If a feature still fails silently, check whether `ForwardBroker`'s marker-line parsing
   (`ForwardBroker.cs`, `HandleForwardMarkerAsync`) is still matching — it's tied to
   `adb`'s current `forward:` response framing (see the class doc comment) and could
   break on an adb version bump.
