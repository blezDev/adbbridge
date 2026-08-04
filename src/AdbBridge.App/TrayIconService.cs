using System.Windows;
using System.Windows.Forms;

namespace AdbBridge.App;

/// <summary>
/// Minimize-to-tray support. WPF has no native tray icon, so this leans on
/// System.Windows.Forms.NotifyIcon (enabled via UseWindowsForms in the csproj).
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly Window _window;

    /// <summary>Raised when the user picks Exit from the tray menu. The owner is
    /// responsible for tearing down any live view (relay/tunnel) before actually
    /// shutting down — this event intentionally does not call Application.Shutdown()
    /// itself.</summary>
    public event Action? ExitRequested;

    public TrayIconService(Window window)
    {
        _window = window;

        var menu = new ContextMenuStrip();
        menu.Items.Add("Show", null, (_, _) => ShowWindow());
        menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke());

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
            e.Cancel = true;
            _window.Hide();
            _notifyIcon.Visible = true;
        };
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
