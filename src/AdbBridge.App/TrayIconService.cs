using System.Windows;
using System.Windows.Forms;
using MessageBox = System.Windows.MessageBox;

namespace AdbBridge.App;

/// <summary>
/// Minimize-to-tray support. WPF has no native tray icon, so this leans on
/// System.Windows.Forms.NotifyIcon (enabled via UseWindowsForms in the csproj).
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly Window _window;

    // Set right before we actually intend to exit (tray "Exit", or "No" in the close
    // dialog). Application.Shutdown() closes the window again internally, re-firing
    // this same Closing handler a second time — without this guard that showed the
    // dialog twice, and since Shutdown() doesn't honor a cancelled Closing the way a
    // normal user-initiated Close() does, every button in that second dialog just let
    // it exit anyway. Once this is true, Closing is let through untouched.
    private bool _isExiting;

    /// <summary>Raised when the user chooses to exit (tray menu or the close dialog).
    /// The owner is responsible for tearing down any live view (relay/tunnel) before
    /// actually shutting down — this event intentionally does not call
    /// Application.Shutdown() itself.</summary>
    public event Action? ExitRequested;

    public TrayIconService(Window window)
    {
        _window = window;

        var menu = new ContextMenuStrip();
        menu.Items.Add("Show", null, (_, _) => ShowWindow());
        menu.Items.Add("Exit", null, (_, _) => RequestExit());

        _notifyIcon = new NotifyIcon
        {
            // Pulls the icon back out of this running exe (embedded at build time via
            // ApplicationIcon) rather than referencing the loose .ico file, since a
            // single-file publish has no assets folder alongside it at runtime.
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? Environment.GetCommandLineArgs()[0])
                   ?? System.Drawing.SystemIcons.Application,
            Text = "AdbBridge",
            Visible = false,
            ContextMenuStrip = menu,
        };
        _notifyIcon.DoubleClick += (_, _) => ShowWindow();

        _window.StateChanged += (_, _) =>
        {
            if (_window.WindowState == WindowState.Minimized)
            {
                _window.Hide();
                _notifyIcon.Visible = true;
            }
        };

        _window.Closing += (_, e) =>
        {
            if (_isExiting) return; // already decided to exit — let this Close proceed

            // Always intercept the close button — ask what the user actually wants
            // instead of silently picking one. A tunnel/relay can be actively running,
            // so "just close" losing that silently would be surprising.
            e.Cancel = true;

            var result = MessageBox.Show(
                _window,
                "Minimize AdbBridge to the system tray and keep it running (Yes), " +
                "or close it completely (No)?",
                "AdbBridge",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            switch (result)
            {
                case MessageBoxResult.Yes:
                    _window.Hide();
                    _notifyIcon.Visible = true;
                    break;
                case MessageBoxResult.No:
                    RequestExit();
                    break;
                // Cancel (or closing the dialog itself): do nothing, window stays open.
            }
        };
    }

    private void RequestExit()
    {
        _isExiting = true;
        ExitRequested?.Invoke();
    }

    private void ShowWindow()
    {
        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
        _notifyIcon.Visible = false;
    }

    public void Dispose() => _notifyIcon.Dispose();
}
