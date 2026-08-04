using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace AdbBridge.App;

/// <summary>
/// Enforces a single running copy of AdbBridge. A second launch (e.g. double-clicking
/// the exe again while the first is minimized to tray) exits immediately after trying
/// to bring the existing instance's window to the front, instead of running two copies
/// that then fight over the same adb server port and the same tunnel provider token —
/// which is exactly what happened when two Host instances both tried to open a Pinggy
/// tunnel with the same token (Pinggy rejects the second as "already active").
/// </summary>
public static class SingleInstanceGuard
{
    // Fixed, app-specific name so this never collides with an unrelated app's mutex.
    private const string MutexName = "Global\\AdbBridge-9f3e2b1a-6d4c-4e7a-8b2f-1c5a7e9d3f60";

    private static Mutex? _mutex;

    /// <summary>Returns true if this process now owns the single-instance lock and
    /// should proceed to start normally. Returns false if another instance already
    /// owns it — the caller should exit without creating a window.</summary>
    public static bool TryAcquire()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (createdNew) return true;

        BringExistingInstanceToFront();
        _mutex.Dispose();
        _mutex = null;
        return false;
    }

    public static void Release()
    {
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        _mutex = null;
    }

    private static void BringExistingInstanceToFront()
    {
        try
        {
            var currentProcess = Process.GetCurrentProcess();
            var other = Process.GetProcessesByName(currentProcess.ProcessName)
                .FirstOrDefault(p => p.Id != currentProcess.Id);

            if (other?.MainWindowHandle is { } handle && handle != IntPtr.Zero)
            {
                ShowWindow(handle, SW_RESTORE);
                SetForegroundWindow(handle);
            }
        }
        catch
        {
            // Best-effort — if we can't locate or focus the other window, the user
            // still finds it in the tray. Not worth failing startup over.
        }
    }

    private const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
