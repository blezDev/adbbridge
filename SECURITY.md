# Security

## v1.0.5 audit (2026-11-09)

The review covered the tracked C#/XAML application and core library, ADB/broker
protocol parsing, tunnel providers, credential persistence and diagnostics, updater,
scrcpy installation, and package dependencies. It combined source review, targeted
secret-pattern checks, NuGet advisory checks, and adversarial loopback/process tests.
This is a focused engineering review, not an independent penetration test or a claim
that every possible vulnerability has been eliminated.

| Finding | Impact before v1.0.5 | Fix |
| --- | --- | --- |
| Critical: public broker accepted unauthenticated streams | Anyone knowing a public endpoint could issue ADB commands to the shared phone. | Mutual TLS, pinned Host and client certificates, private connection code; no plaintext fallback. |
| High: broker markers selected arbitrary loopback ports | A public caller could request forwarding to local services or allocate reverse listeners. | Authenticate first; allow only successful ADB-registered mappings; bound handshake time, ingress connections, reverse listeners, and idle pools. |
| High: Host ADB listened on all interfaces | Other network peers could reach the Host's ADB server directly. | Explicit loopback binding on port 5037. |
| Credential exposure in provider launch/diagnostics | Tokens could appear in process arguments or provider output; ADB service strings could include sensitive command payloads. | Environment/private SSH configuration instead of token arguments; validated configuration values; restricted logs; safe service-category diagnostics. |
| Update/extraction hardening gaps | Broad URL acceptance, oversized checksum metadata/extraction, linked paths, and changed staged files increased installation risk. | Exact repository asset URLs, streaming limits, fresh extraction files, link rejection, and post-parent-exit hash verification of copied bytes before atomic replacement. |

### Updating and revoking access

Update **both** Host and Companion to v1.0.5. Stop old Host sharing while upgrading.
Start sharing, copy the full private `adbbridge://...` code, and transfer it privately
to Companion. Old versions and ordinary host:port addresses are incompatible. A code
contains the client private key and Host fingerprint; possessing it grants ADB access
while the corresponding Host is sharing. Do not publish it in issues, screenshots,
logs, or clipboard sync/history. Host's **Reset pairing** stops sharing and invalidates
all old codes. Start sharing again and copy the new code to trusted PCs. Pairing
certificates expire after five years; reset pairing to renew them.

Saved provider tokens, Host identity, and Companion connection codes use Windows
DPAPI under the current user. Settings are written atomically in a restricted directory.
ngrok receives its token through the child environment. Pinggy needs a plaintext User
value in a current-user/SYSTEM-only temporary SSH configuration; normal disposal deletes
it, but a crash may leave it in `%LOCALAPPDATA%\AdbBridge\private`.

Older releases may have written ngrok credentials into ngrok's own configuration or
exposed provider output in logs. This release does not delete existing third-party
configuration or historic logs. Review those files and rotate provider tokens if they
were exposed. Updating cannot invalidate previously copied provider tokens.

### Trust assumptions and remaining limits

- A paired Companion is fully trusted to use ADB: shell, install, file transfer, and
  inspection access are intentional. The shared client credential applies to every
  Companion paired with this Host; revocation resets them all.
- The private code must arrive through a trusted channel. TLS validates the exact
  certificates supplied by that pairing, rather than a public CA. The tunnel provider
  can observe connection metadata and disrupt traffic but does not receive plaintext
  ADB traffic through the encrypted bridge transport.
- Pinggy SSH uses `accept-new` and a persisted known_hosts file: this is trust on first
  use, not a preverified provider key. Subsequent changed keys are rejected. Bridge
  mutual TLS still validates the paired Host independently.
- The current Windows account, administrators/SYSTEM, and locally selected adb,
  ngrok, ssh, and scrcpy executables remain trusted. Loopback is not an authorization
  boundary against malware running on either PC. DPAPI/ACLs do not protect secrets
  from an attacker already controlling the same account.
- Updates trust this GitHub repository, HTTPS, release assets and their SHA-256 file.
  Checksums detect corruption; there is no independent publisher signature. A
  compromised release account could replace both the archive and checksum.
- Limits reduce unauthenticated connection exhaustion but do not prevent all denial
  of service. ADB forwarding observation is best-effort parsing; protocol changes can
  require an update. This release does not implement individual Companion roles or
  per-command ADB permissions.

### Validation

The release validation includes:

```powershell
dotnet build AdbBridge.sln -c Release
dotnet list AdbBridge.sln package --vulnerable --include-transitive
dotnet run --project tests/AdbBridge.ProtocolTests -c Release
dotnet run --project tests/AdbBridge.SecurityTests -c Release
dotnet run --project tests/AdbBridge.RelayTests -c Release
dotnet run --project tests/AdbBridge.UpdateTests -c Release
dotnet run --project tests/AdbBridge.ScrcpyTests -c Release
```

The NuGet advisory scan reported no known vulnerable direct/transitive packages at
review time. Security tests exercise the actual Host broker with paired/unpaired
certificates, wrong Host pins, plaintext rejection, arbitrary-port rejection, encrypted
forward/reverse traffic and mapping removal. Relay tests cover encrypted stream
lifetimes and reverse arrival order. Updater tests cover URLs, checksums, metadata
bounds, extraction, cancellation, plus isolated single-file replacement, rollback,
and tampering while the parent waits to exit. No production executable is replaced
by those tests. A release-build helper is tested against a disposable fixture too.

scrcpy and Android Studio mirroring were confirmed working on v1.0.4 (Studio was
slower). v1.0.5 preserves those stream-lifetime behaviors in encrypted loopback tests;
a fresh remote device session requires updating both PCs and is not claimed here.

### Reporting

Do not include private connection codes, provider tokens, phone data, or exploitable
live endpoints in public issues. Use this repository's GitHub private vulnerability
reporting option when available, or contact the maintainer privately before disclosure.
