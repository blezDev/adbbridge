# AdbBridge — project context

## Native Android Studio mirroring codec override (2026-11-09)

The VP8 failure described below has a Studio-side workaround worth testing before
concluding that native mirroring cannot work. Official Studio source defines
`device.mirroring.video.codec` as a string feature flag. `PropertyOverrides` reads
the flag ID directly from Java system properties, and `DeviceClient` passes its
value to the agent as `--codec=<value>`. The agent maps `avc` to `video/avc` (H.264).

Configured this Companion machine's AndroidStudio2026.2.2 custom `idea.properties`
with `device.mirroring.video.codec=avc`. The IDE was left running because it had active
terminal work. A restart and real mirroring test remain required. Verify the next
agent launch in `idea.log` includes `--codec=avc` and that Running Devices renders
the phone before calling the workaround confirmed. The separate scrcpy button does
not configure or replace native Studio mirroring.

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

## Host-side test result: pool never pairs (2026-08-23, Host PC)

Rebuilt and ran as Host against the same live Companion (already running the reverse-pool
build) and a real device attached here. Two Device Mirroring attempts from Studio,
`host.log` shows the same pattern both times:

```
17:31:12  Reverse-tunnel stand-in listener bound on 127.0.0.1:61494 for adb reverse.
17:31:17  Reverse connection on port 61494 timed out waiting for a pooled tunnel connection.

17:31:34  Reverse-tunnel stand-in listener bound on 127.0.0.1:61555 for adb reverse.
17:31:39  Reverse connection on port 61555 timed out waiting for a pooled tunnel connection.
```

So: `reverse:forward:` detection and the stand-in listener bind both work — confirmed on
the Host side. But `PairReverseConnectionAsync` never finds a pooled connection in either
attempt, timing out at exactly `ReversePairTimeout` (5.0s) both times. This is the "pool
is empty" branch CLAUDE.md's step 5 above called out.

**Not yet known — needs `companion.log` from the Companion PC to settle:**
- Does `ReversePoolRelay` even log `Pooling reverse-tunnel connections for
  127.0.0.1:61494...` / `...61555...` for these two ports? If not, the
  `ReverseForwardEstablished` event never reached it (check `TcpRelay`'s parsing of the
  device's `reverse:forward:localabstract:screen-sharing-agent-<N>;tcp:<N>` request on the
  Companion side — same request `AdbForwardWatcher.ClassifyService` needs to recognize,
  but parsed from a different position in the byte stream than the Host sees since the
  Host only sees it after it's already relayed through).
- If it does log pooling, are the pooled `REV:<port>\n` connections actually reaching
  Host inside the 5s window? Measured Pinggy round-trip was ~760ms for a single
  `host:version` earlier — opening a fresh tunnel connection, sending the marker, and
  Host accepting it should be well inside 5s if the pool is actually being replenished,
  so a timeout suggests either the pool isn't started at all for these ports, or something
  about the Companion-side dial to the tunnel address is failing/retrying silently.
- Worth also checking whether Host's `_brokerPort`/adb-server-port pairing has any
  contention with the `ForwardBroker` accept loop under concurrent normal adb traffic —
  the Pinggy log lines interleaved in `host.log` show steady byte counts (`RB`/`SB`)
  throughout both attempts, so the tunnel itself was alive and moving data the whole time;
  this isn't a "tunnel dropped" failure.

**Suggested next step for whoever has `companion.log`:** grep it for `Pooling
reverse-tunnel connections for 127.0.0.1:61494` and `...61555` (the exact two ports from
this run) and for any `REV:` or connection-refused/failure lines in the same 17:31:07–17:31:40
window (IST, matches `host.log` timestamps — confirm Companion's clock offset if any).
If there's no `Pooling` line at all for those ports, the bug is on the detection/event
side in `ReversePoolRelay`/`TcpRelay`, not the pairing timeout math.

## Companion-side follow-up (2026-08-23, same day, later)

Checked `companion.log` for exactly those two ports (61494, 61555) — `Pooling
reverse-tunnel connections for 127.0.0.1:<port>...` DID fire for both, immediately after
each `reverse:forward:` request, and there are **no** failure/error lines anywhere near
them (grepped `failed|error|Reverse-tunnel|nothing.*listening` across the whole file —
nothing). So detection isn't the bug: `ReversePoolRelay` definitely started a pool for
both ports. But that leaves a real mystery — if `RunSlotAsync` successfully connects,
sends `"REV:<port>\n"`, and *doesn't* log any failure, it should be sitting in
`WaitForGoAsync`. And on the Host side, if `EnsureReverseListener` logged "bound" (which
only happens inside `HandleReverseMarkerAsync`, i.e. only after a REV-tagged connection
was already parsed), the very next lines in that same method are just a dictionary
lookup/create and `channel.Writer.WriteAsync(inbound, ct)` — nothing between them that
should plausibly fail or delay. A `Channel<T>` reader is supposed to find a write
regardless of which happens first, so a 5s timeout doesn't have an obvious explanation
from re-reading the code alone.

Rather than keep guessing, added explicit instrumentation to pin down exactly which step
is failing next time (this build is what's running on Companion now):
- `ForwardBroker.cs`: logs when a Companion connection's item is actually written to the
  channel (`"Reverse-tunnel connection from Companion queued for port {port}."`), when
  the real adb server connects to the stand-in listener
  (`"Real adb server connected to the reverse stand-in listener on port {port}..."`), and
  when a pairing actually succeeds (`"...paired with a pooled tunnel connection."`).
- `ReversePoolRelay.cs`: logs once a pooled connection's marker write completes and it
  starts waiting (`"...pooled connection reached the Host, waiting for GO."`) and once it
  receives GO (`"...received GO, dialing the local app."`).

**To pick this up on the Host PC:** `git pull`, rebuild, relaunch Host (Companion is
already running this build). Retry Device Mirroring, then read `host.log` in order and
check exactly which of these four new lines appear and which don't, for the same port
number both sides will report — that sequencing will localize the bug precisely instead
of guessing further:
1. Companion: `pooled connection reached the Host, waiting for GO` (proves the marker
   write reached Host and WaitForGoAsync started)
2. Host: `Reverse-tunnel connection from Companion queued for port <port>` (proves the
   channel write on Host's side completed)
3. Host: `Real adb server connected to the reverse stand-in listener on port <port>`
   (proves the actual device-originated connection arrived)
4. Host: `paired with a pooled tunnel connection` / Companion: `received GO, dialing the
   local app` (proves the handoff worked)

If (1) happens but (2) never does, the bug is somewhere in `HandleReverseMarkerAsync`
between the marker parse and the channel write (a silent exception the outer catch in
`HandleClientAsync` should also have logged as `Broker connection error: ...` — check for
that too). If (2) happens before (3), the connection was queued in time and the timeout
is genuinely unexplained by ordering — worth checking whether `port` in step 2's log
matches exactly what step 3 reports, in case of a subtle port-number mismatch between the
two sides.

## Root cause found: pool exhaustion, not a race (2026-08-23, Host PC, rebuilt with instrumentation)

The four new log lines answered it immediately — no ambiguity. `host.log`, port 54540,
one Device Mirroring attempt:

```
17:40:17  Reverse-tunnel connection from Companion queued for port 54540.   (slot 1)
17:40:17  Reverse-tunnel connection from Companion queued for port 54540.   (slot 2)
17:40:17  Real adb server connected... waiting up to 5s...
17:40:17  Reverse connection on port 54540 paired with a pooled tunnel connection.   #1 OK
17:40:17  Real adb server connected... waiting up to 5s...
17:40:17  Reverse connection on port 54540 paired with a pooled tunnel connection.   #2 OK
17:40:17  Real adb server connected... waiting up to 5s...
          [5s of normal Pinggy traffic, no replenishment log]
17:40:22  Reverse connection on port 54540 timed out waiting for a pooled tunnel connection.   #3 FAILED
17:40:22  Reverse-tunnel connection from Companion queued for port 54540.   (replenishment — 5s too late)
17:40:22  Reverse-tunnel connection from Companion queued for port 54540.
```

The screen-sharing-agent opens **three** near-simultaneous reverse connections at startup
(all three `Real adb server connected` lines land in the same second) — not one, as
assumed when `PoolSizePerPort = 2` was picked. The first two pair instantly and correctly.
The third times out.

**This is not a latency/race bug, it's pool exhaustion by design.** In
[ReversePoolRelay.cs](src/AdbBridge.Core/Relay/ReversePoolRelay.cs), `RunSlotAsync`'s loop
(lines 137–193) only opens a fresh pooled connection *after* `await
RelayPump.CopyBothWaysAsync(...)` on line 178 returns — i.e. after that slot's bridged
data connection fully closes and the loop wraps back to the top. But the two successful
pairings became long-lived data streams (the mirroring session itself), so both slots stay
consumed for the session's whole duration — they don't free up in 5s, they free up when
mirroring stops. `PoolSizePerPort` is effectively "how many concurrent live connections
this pool can ever support," not "how many idle spares are kept on standby," because
replenishment is serialized behind each slot's own bridge lifetime instead of being
decoupled from it.

**Fix direction (not yet implemented):** spawn a new idle pooled-connection task the
moment a slot's connection is *consumed* (paired via GO), independent of how long that
consumed connection's bridge then runs — e.g. replace the one-task-per-slot loop with a
target idle-count (still `PoolSizePerPort`, or bump it) maintained by a semaphore/counter,
where each successful pairing immediately triggers spinning up a replacement idle
connection in a new fire-and-forget task rather than waiting inline. Also worth bumping
`PoolSizePerPort` past whatever the real concurrent-connection ceiling turns out to be
(3 was enough to reproduce here; unclear if Studio ever wants more, e.g. if audio mirroring
or multiple inspector features are active simultaneously) as a belt-and-suspenders margin
even after replenishment is decoupled.

**Whoever picks this up next:** the fix is isolated to `ReversePoolRelay.PortPool` — no
changes needed on the Host/`ForwardBroker` side, which is already behaving correctly
(binds, queues, pairs, and times out exactly as designed). After implementing, retest by
watching for a 4th+ `Real adb server connected... paired` sequence landing within the same
burst as the first two, not just 2-then-timeout.

## Fix implemented: decoupled replenishment (2026-08-23, Companion PC)

`ReversePoolRelay.PortPool` rewritten as suggested above. `PoolSizePerPort` renamed in
spirit to an idle-standby target and bumped `2 → 4`. Replaced the one-task-per-slot loop
(`RunSlotAsync`, which only opened a new connection after its *own* bridge fully closed)
with `MaintainIdleConnectionAsync`: each task owns exactly one pooled connection: open,
send marker, wait for GO — and the instant GO arrives, it calls `SpawnIdleConnection()`
to fire off a brand new independent task *before* dialing the local app and running
`BridgeConsumedConnectionAsync`. So a consumed slot's own lifetime (which can be the
entire mirroring session) no longer blocks the idle count from being restored — it's
restored immediately instead.

`PortPool.DisposeAsync` now snapshots whatever's in the dynamically-growing `_tasks` list
under a lock and awaits that (best-effort — a task spawned in the exact instant disposal
starts might not be captured, but it still observes `_cts` and exits on its own quickly).

Companion is running this build now (this session). **Needs the Host PC to `git pull` +
rebuild + relaunch, then retry Device Mirroring** — same as every prior round. Watch for
whether all 3 (or however many) near-simultaneous `Real adb server connected...` events
now all get `paired with a pooled tunnel connection` instead of 2-then-timeout, and
whether Device Mirroring actually renders/attaches in Studio this time (the real

## Retested: pool fix confirmed working, but a different root cause found (2026-08-23, Companion PC)

Retested with the pool fix live on both sides (Host had already rebuilt by this point).
`companion.log`: all 3 reverse connections this time got `received GO, dialing the local
app` — **zero timeouts, zero `nothing local is listening` failures.** The pool-exhaustion
fix works; the full bridge (device → Host → tunnel → Companion → Studio) connects
end-to-end correctly now.

Device Mirroring still failed, but faster (~2.5s vs ~7-9s before) and with **no exception
logged in `idea.log` at all** this time — a different failure shape than the earlier
`TimeoutException: Device agent is not responding`, which was the signature of nothing
ever connecting. Something connects now; it just doesn't work.

**Captured the device's own logcat through the bridge** (`adb logcat` run directly from
Companion — it routes through the relay to the Host's real adb server exactly like any
other adb client, no special access needed) during a fresh attempt. Full trace for PID
19406 (the agent process) shows it starting correctly, initializing the hardware VP8
encoder (`c2.android.vp8.encoder`), configuring the video format (320x730@60fps,
10Mbps) — all normal — and then:

```
17:51:43.011 E/ion              (19406): ioctl c0044901 failed with code -1: Invalid argument
17:51:43.014 W/studio.screen.sharing(19406): Display 0: AMediaCodec_dequeueOutputBuffer(..., 200000) returned -2
17:51:43.791 E/NdkMediaCodec    (19406): sf error code: -38
17:51:43.791 W/studio.screen.sharing(19406): Display 0: AMediaCodec_dequeueOutputBuffer(..., 30000000) returned -10000
17:51:43.802 I/BufferQueueConsumer(19406): [GraphicBufferSource]... disconnect
17:51:43.818 I/app_process      (19406): System.exit called, status: 0
```

**This is a device-side hardware video encoder failure, unrelated to the bridge.** `ion`
(Android's graphics memory allocator) fails an `ioctl`, the codec starts returning error
codes instead of encoded frames, and the agent's own code calls `System.exit(0)` — a
deliberate clean exit, not a crash and not our earlier "nobody ever connected" timeout.
The phone is an Infinix X6815B (Transsion, MediaTek-based — note `mediatek-cta.jar` and
`vendor.tran.*` properties in the log). This looks like a device/vendor hardware codec
compatibility issue with the VP8 encoder Device Mirroring's agent selects, and would very
plausibly fail identically over a direct USB connection with no AdbBridge involved at
all — nothing left to fix in the relay for this specific symptom.

**Where this leaves the project:**
- Forward tunneling (`ForwardBroker`/`ForwardPortRelay`), the `host:kill` guard, and the
  reverse-tunnel pool (`ReversePoolRelay`) are all confirmed working end-to-end over a
  real two-machine Pinggy tunnel. These fix real, previously-broken functionality:
  Layout Inspector, Network/Database Inspector, and profilers (which use classic `adb
  forward`) and the Device Mirroring *connection* itself (which needs `adb reverse`).
- Device Mirroring specifically still doesn't render on **this** phone, but the cause
  has moved from "AdbBridge doesn't tunnel this" (fixed) to "this phone's hardware video
  encoder fails" (out of scope for a relay). Worth confirming by testing Device Mirroring
  with the phone plugged in directly via USB (bypassing AdbBridge) if there's any doubt —
  if it fails the same way there, that confirms it's fully unrelated to this project.
- Not yet re-verified: Layout Inspector / Network Inspector / Database Inspector actually
  opening and working in Studio's UI (the Transport-layer forward they depend on is
  confirmed working at the relay level, but nobody has clicked through the actual Studio
  panels yet).

## Layout Inspector confirmed working; reverse pool re-confirmed under real retry load (2026-08-23, Host PC)

Retested with the pool-fix build freshly rebuilt on Host. Two things confirmed:

- **Layout Inspector opens and works in Studio's UI.** Closes the last open item above —
  forward tunneling (`ForwardBroker`/`ForwardPortRelay`) is verified end-to-end at the
  actual feature level now, not just at the relay/wire level.
- **Reverse pool held up under real retry churn.** Studio auto-retried Device Mirroring 4
  times in this one session (4 separate `reverse:forward:` registrations, ports 53035,
  53088, 53123, 53159), each producing 3 pooled connections and 3 successful pairings —
  **12/12 paired, zero timeouts.** The fix isn't just a one-off pass, it holds under
  repeated real-world reconnect load.
- **Device Mirroring still fails**, consistent with the hardware-encoder diagnosis above —
  not re-investigated further since that's already understood to be device-side, not a
  relay bug. Not yet confirmed via a direct-USB control test (would fully rule out any
  residual relay involvement, but isn't expected to change the conclusion).

**Still open:** Network Inspector and Database Inspector haven't been clicked through in
Studio's UI yet — same forward-tunnel mechanism as Layout Inspector, so likely fine, but
unverified.
