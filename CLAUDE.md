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

## Diagnostic session on the Companion PC (2026-08-23, later same day)

Live-tested against a real Host (phone attached there) over a Pinggy tunnel with Android
Studio's Device Mirroring. Findings, cheapest-to-verify first:

- **`LogSink` now also writes to a file**, since the in-memory log panel isn't inspectable
  outside the running UI: `%LOCALAPPDATA%\AdbBridge\logs\{host,companion}.log`, fresh each
  run. `HostView`/`CompanionView` now construct it as `new LogSink("host")` /
  `new LogSink("companion")`. Check `host.log` on the Host PC with the same approach.
- **The forward-tunneling fix is confirmed working end-to-end.** Classic `adb forward`
  cases (Transport/profiler daemon, `host-serial:<serial>:forward:tcp:<N>;localabstract:
  AndroidStudioTransport`) get caught, relayed, and Studio's `TransportProxy successfully
  created for device` confirms real data flowed through Companion → tunnel → Host →
  device and back.
- **`host:kill` guard also confirmed firing** — `companion.log` shows `Blocked a host:kill
  request...` right after Studio's own adb housekeeping tries it.
- **Device Mirroring's screen-sharing-agent still fails** (`ScreenSharingAgent ...
  terminated`, exit code 0, ~7-9s after start; Studio then throws `TimeoutException:
  Device agent is not responding`). `AdbForwardWatcher` was extended to log every adb
  service string a connection requests (`ServiceObserved` event → `TcpRelay` logs `"adb
  service request: {service}"`) specifically to find out whether the mirroring agent's
  connection uses a separate `forward:` (which the code already handles) or is multiplexed
  directly over the existing `host:transport:<serial>` connection via a bare
  `localabstract:screen-sharing-agent-<N>` follow-up request (which would need different
  handling — there's no separate local port to tunnel; the raw byte stream would need to
  ride the same relayed connection instead). **This was mid-investigation when the session
  ended** — check `companion.log` for a `localabstract:screen-sharing-agent-` line to
  settle it.
- Measured round-trip latency through the Pinggy tunnel for a single `host:version`
  request: **~760ms.**

## Root cause found: Device Mirroring uses `adb reverse`, not `adb forward` (2026-08-23, later still)

The multiplex theory above was wrong. With `ServiceObserved` logging every request,
`companion.log` caught the actual sequence:

```
reverse:forward:localabstract:screen-sharing-agent-51553;tcp:51553
shell,v2:CLASSPATH=/data/local/tmp/.studio/screen-sharing-agent.jar app_process ... --socket=screen-sharing-agent-51553 --flags=32
...
reverse:killforward:localabstract:screen-sharing-agent-51553   (~11s later, after the agent gave up)
```

**`adb reverse` is the mirror image of `adb forward`, and structurally incompatible with
a two-machine bridge the same way `adb forward` originally was — but harder to fix.**
`reverse:forward:<remote>;<local>` tells the adb server "when the device connects to its
own `<remote>` (here, `localabstract:screen-sharing-agent-51553`), tunnel that back to
`<local>` (`tcp:51553`) **on this machine**" — where "this machine" means whichever one
runs the *real* adb server. Studio (on Companion) opens its receiving listener on
`127.0.0.1:51553` on Companion, but the real adb server that processes the command is on
Host. When the device later connects, Host's real adb server dials `127.0.0.1:51553` on
**Host**, finds nothing (Studio's listener is on the other machine entirely), and the
agent times out waiting for a connection that can never arrive.

Unlike `adb forward`, there's no listener to wrap here — the real adb server is the one
*dialing out*, and that can't be intercepted directly. The fix is a pooled reverse
tunnel, implemented in this commit:

- **`ForwardBroker.cs` (Host)** — extended with a second marker, `"REV:<port>\n"`
  (disambiguated from `"FWD:<port>\n"` and real adb traffic the same zero-lookahead way:
  peek one byte, real adb frames never start with uppercase). On seeing `REV:<port>`, it
  binds a stand-in `TcpListener` on `127.0.0.1:<port>` itself (idempotent — one per port)
  and parks the tunnel connection in a `Channel<TcpClient>` pool keyed by port. When the
  real adb server actually dials that port (delivering a genuine device-originated
  reverse-connect), the accept loop dequeues one pooled connection, writes `"GO\n"` into
  it, and bridges bytes between the two.
- **`ReversePoolRelay.cs` (Companion, new file)** — watches `TcpRelay` for two new events
  (`ReverseForwardEstablished`/`ReverseForwardRemoved`/`ReverseForwardsCleared`, added to
  both `AdbForwardWatcher` and `TcpRelay`, mirroring the existing forward ones but keyed
  by the device-side remote spec for removal, since `reverse:killforward:<remote>` names
  the remote, not the port). For each registered reverse mapping it keeps
  `PoolSizePerPort = 2` tunnel connections open on the Host at all times: each one opens
  a raw connection through the tunnel, sends `"REV:<port>\n"`, then blocks waiting to
  read exactly `"GO\n"`. Only once that arrives does it dial `127.0.0.1:<port>` on
  Companion (Studio's real listener) and start bridging — so Studio never sees a
  connection attempt that isn't backed by a real device-originated one. Each slot
  replenishes itself in a loop after being consumed or dying.
- `AdbForwardWatcher.ClassifyService` now also recognizes `reverse:forward:`/
  `reverse:killforward:`/`reverse:killforward-all` — these are per-device requests (sent
  after a `host:transport:<serial>` select, like `shell:`/`sync:`), never `host:`-prefixed,
  so they're checked before the existing host: family and don't interfere with it.

**Not yet verified — this is exactly where you're picking this up:**
- Whether the pooled reverse tunnel actually works end-to-end. Companion has this build
  running now (companion machine, this session) but **Host does not yet** — the whole
  point of this handoff is to get it built and running there.
- Whether `PoolSizePerPort = 2` is enough (Device Mirroring likely only needs one at a
  time, but concurrent inspector features might want more) and whether
  `ReversePairTimeout = 5s` in `ForwardBroker` is long enough given the ~760ms tunnel
  round-trip for the pool to even have a connection ready.
- General robustness under Studio's real reconnect churn (repeated
  `reverse:forward`/`killforward` cycles as mirroring is retried).

**To pick this up on the Host PC:**
1. `git pull`, `dotnet build AdbBridge.sln -c Debug` (or Release).
2. Run `AdbBridge.exe host` here (phone attached), start sharing as usual.
3. On the Companion PC, reconnect (this build is already running there as of this
   commit). Trigger Device Mirroring in Studio.
4. Watch `host.log` (`%LOCALAPPDATA%\AdbBridge\logs\host.log`) for `Reverse-tunnel
   stand-in listener bound on 127.0.0.1:<port>...` and `Reverse relay for port <port>
   failed: ...` / silence (success) lines. Watch `companion.log` for `Pooling
   reverse-tunnel connections for 127.0.0.1:<port>...`. Correlate both against
   `idea.log`'s `Executing adb shell ... screen-sharing-agent-<N>` / `ScreenSharingAgent
   ... terminated` timing.
5. If it still fails, the first thing to check is whether `PairReverseConnectionAsync`
   in `ForwardBroker.cs` ever finds a pooled connection at all (its timeout log line) —
   if the pool is empty when the real adb server dials in, either the pool isn't being
   replenished fast enough or `ReversePoolRelay` never started it (check for `Pooling
   reverse-tunnel connections...` in `companion.log` in the first place).
