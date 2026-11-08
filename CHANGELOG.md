# Changelog

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
active-session shutdown, and video/control connection ordering. Native Android Studio
mirroring still needs a separate retest; the fix is verified for scrcpy.

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
