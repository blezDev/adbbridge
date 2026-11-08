# Changelog

## v1.0.5 - security update

- Replace unauthenticated public ADB transport with mutually authenticated TLS 1.2/1.3. Each private connection code includes a pinned Host certificate and paired client credential; plaintext connections have no fallback.
- Keep Host ADB on `127.0.0.1:5037`. Permit broker forward/reverse ports only after successful ADB registration, with handshake deadlines and connection/pool limits.
- Protect saved pairing identities and Companion connection codes with Windows DPAPI and restricted settings-directory permissions. Add **Reset pairing** to revoke all previously shared codes.
- Remove ngrok and Pinggy tokens from process arguments. Use ngrok's child environment and a restricted temporary Pinggy SSH configuration; validate SSH configuration input and avoid logging provider terminal output or ADB command payloads.
- Require exact repository HTTPS release asset URLs, cap checksum metadata and actual extracted bytes, reject linked installation paths, and reverify the copied update immediately before executable replacement.
- Add security regression coverage for unpaired clients, incorrect certificate pins, plaintext downgrade, broker port restrictions, TLS forward/reverse stream survival, credential handling, and updater tampering.
- Update README setup and migration guidance; add a security audit report and trust limitations in `SECURITY.md`.

### Updating

**Update both PCs to v1.0.5, start Host sharing, and copy its new private connection code to Companion.** Old versions and bare host:port addresses cannot connect to the new transport. Settings are preserved; the old plaintext Companion endpoint is intentionally not reused.

Choose **Update and restart**, or download the self-contained Windows x64 ZIP. The separate `SHA256SUMS.txt` asset supports verification and in-app updates. scrcpy remains installed on demand.

### Validation

Release build, dependency advisory scan, security tests, encrypted relay lifecycle/order tests, ADB protocol tests, updater tests, and scrcpy installer tests. Isolated updater processes verify replacement/restart, rollback, and rejection of a package changed after the initial verification. v1.0.4 scrcpy and Android Studio mirroring behavior is retained by the loopback regression checks; a fresh two-PC device session on the new TLS transport still requires both machines to update.

## v1.0.4

- Keep established forward and reverse streams alive when ADB mappings are removed. scrcpy and Studio can remove their setup mappings immediately after connecting; this must not disconnect video or control sockets.
- Preserve reverse connection arrival order across the tunnel by replenishing one idle slot only after its local connection succeeds. Active sessions remain concurrent.
- Cancel and await both relay directions when an actual session closes, and close all retained streams when Companion disconnects.
- Verified live scrcpy forward and reverse mirroring for 20 seconds each, mostly around 30 FPS, with normal exit.

### Test this release

Choose **Update and restart** in AdbBridge, or download and extract the Windows ZIP.
On Companion, connect to the Host, select a ready device, and click **Mirror device**.
Video should stay connected after scrcpy finishes its initial tunnel setup.

Regression checks cover forward/reverse mapping removal, remove-all, port reuse,
active-session shutdown, and video/control connection ordering. The user subsequently confirmed native Android Studio mirroring also works, though
it is somewhat slower than standalone scrcpy. App Inspection also works.

## v1.0.3

- Unify the role chooser, Host, and Companion screens with shared colors, typography, card layouts, control sizes, and status badges.
- Show live connection states, startup progress, device readiness, useful empty states, and expandable diagnostics.
- Preserve device selection during refresh, connect with Enter in the address field, and show feedback when a sharing address is copied.
- Find scrcpy automatically in saved, adjacent, managed, and PATH locations. Download and install the official portable scrcpy 5.0.1 Windows x64 release when missing, after checking its published SHA-256 checksum.
- Show setup progress and cancellation; reuse the installed scrcpy executable on subsequent launches.
- Show missing ngrok as an inline setup hint instead of an unsolicited dialog.
- Add README screenshots of the updated screens, rendered with sample connection and device data.

### Updating

Rebuilt v1.0.2 users can choose **Update and restart** in the app. Original v1.0.2 or earlier builds that only offer **View release** need to download this release manually once. Update both Host and Companion; settings are preserved.

The release ZIP is a self-contained Windows x64 build and includes `SHA256SUMS.txt` as a separate release asset for verification and in-app updates. scrcpy is installed on demand, not bundled in the ZIP.

### Validation

- Release build and self-contained publish.
- ADB forwarding protocol regression checks.
- Updater metadata, checksum, archive, cancellation, replacement/restart, and rollback checks.
- scrcpy discovery, checksum rejection, cancellation, complete archive extraction, and path traversal checks.
- Real official scrcpy download, SHA-256 verification, portable installation, and `--version` startup check.
- UI previews at 1080px and 840px widths.

Native Android Studio mirroring on the Infinix device remains unresolved. This release provides standalone scrcpy mirroring and does not claim to fix Studio's screen-sharing agent.
