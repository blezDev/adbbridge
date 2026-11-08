using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AdbBridge.Core.Adb;
using AdbBridge.Core.Config;
using AdbBridge.Core.Logging;
using AdbBridge.Core.Relay;
using AdbBridge.Core.Tunnels;
using Microsoft.Win32;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using UserControl = System.Windows.Controls.UserControl;

namespace AdbBridge.App.Views;

public partial class CompanionView : UserControl, IBridgeView
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(12);

    private readonly CompanionSettings _settings;
    private readonly AdbProcessManager _adb = new();
    private readonly LogSink _log;
    private readonly ObservableCollection<AdbDevice> _devices = new();
    private readonly DispatcherTimer _deviceRefreshTimer;
    private readonly DispatcherTimer _statusTimer;

    private TcpRelay? _relay;
    private PortGuardService? _guard;
    private ForwardPortRelay? _forwardRelay;
    private ReversePoolRelay? _reversePoolRelay;
    private bool _isConnected;
    private const string RunKeyName = "AdbBridgeCompanion";

    public event Action? BackRequested;

    public CompanionView()
    {
        InitializeComponent();

        _settings = CompanionSettings.Load();
        _log = new LogSink("companion");
        LogListBox.ItemsSource = _log.Lines;
        DeviceListView.ItemsSource = _devices;

        AdbPathBox.Text = _settings.PlatformToolsPath;
        AddressBox.Text = _settings.LastTunnelAddress;
        RunAtStartupCheck.IsChecked = _settings.RunAtStartup;
        AutoConnectCheck.IsChecked = _settings.AutoConnectOnStartup;

        _adb.LogMessage += (_, msg) => _log.Append(msg);

        _deviceRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _deviceRefreshTimer.Tick += async (_, _) => await RefreshDevicesAsync();

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _statusTimer.Tick += (_, _) => RefreshStatusText();
    }

    /// <summary>Called by the shell right after showing this view when launched for a
    /// startup-triggered relaunch, so "auto-connect on startup" actually connects
    /// without the user having to click anything.</summary>
    public async Task AutoConnectIfConfiguredAsync()
    {
        if (_settings.AutoConnectOnStartup && !string.IsNullOrWhiteSpace(_settings.LastTunnelAddress))
            await ConnectAsync();
    }

    public void Cleanup() => _ = DisconnectAsync();
    public Task CleanupAsync() => DisconnectAsync();

    private void MirrorButton_Click(object sender, RoutedEventArgs e)
    {
        var owner = Window.GetWindow(this);
        var ready = _devices.Where(d => d.State == "device").ToList();
        var device = DeviceListView.SelectedItem as AdbDevice;
        if (device is null && ready.Count == 1) device = ready[0];
        if (!_isConnected || device?.State != "device")
        {
            MessageBox.Show(owner, "Connect to the Host and select a ready device first.", "Device mirroring");
            return;
        }

        if (!File.Exists(_settings.ScrcpyExePath))
        {
            var dialog = new OpenFileDialog { Title = "Select scrcpy.exe from an extracted scrcpy release", Filter = "scrcpy executable|scrcpy.exe" };
            if (dialog.ShowDialog(owner) != true) return;
            _settings.ScrcpyExePath = dialog.FileName;
            _settings.Save();
        }

        try
        {
            var start = new ProcessStartInfo(_settings.ScrcpyExePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(_settings.ScrcpyExePath)!,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            // Use Studio's adb and our local relay, even when scrcpy bundles another adb.
            start.Environment["ADB"] = _settings.PlatformToolsPath;
            start.Environment["ADB_SERVER_SOCKET"] = "tcp:127.0.0.1:5037";
            foreach (var argument in new[] { "--serial", device.Serial, "--force-adb-forward",
                         "--video-codec=h264", "--max-size=1280", "--max-fps=30", "--no-audio" })
                start.ArgumentList.Add(argument);
            var process = new Process { StartInfo = start, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, args) => { if (args.Data is { } line) _log.Append($"scrcpy: {line}"); };
            process.ErrorDataReceived += (_, args) => { if (args.Data is { } line) _log.Append($"scrcpy: {line}"); };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _ = ObserveScrcpyAsync(process);
        }
        catch (Exception ex)
        {
            _log.Append($"Could not start scrcpy: {ex.Message}");
            MessageBox.Show(owner, ex.Message, "Device mirroring", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task ObserveScrcpyAsync(Process process)
    {
        using (process)
        {
            try
            {
                await process.WaitForExitAsync();
                _log.Append($"scrcpy exited with code {process.ExitCode}.");
            }
            catch (Exception ex) { _log.Append($"scrcpy monitoring failed: {ex.Message}"); }
        }
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        Cleanup();
        BackRequested?.Invoke();
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isConnected)
        {
            await DisconnectAsync();
        }
        else
        {
            await ConnectAsync();
        }
    }

    private async Task ConnectAsync()
    {
        SaveSettingsFromUi();
        var owner = Window.GetWindow(this);

        if (!File.Exists(_settings.PlatformToolsPath))
        {
            MessageBox.Show(owner, $"adb.exe not found at:\n{_settings.PlatformToolsPath}", "AdbBridge Companion",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!TunnelInfo.TryParse(_settings.LastTunnelAddress, out var target) || target is null)
        {
            MessageBox.Show(owner, "Enter a valid address, e.g. 0.tcp.ngrok.io:12345", "AdbBridge Companion",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ConnectButton.IsEnabled = false;
        try
        {
            _log.Append("Clearing any existing adb.exe so the relay can claim port 5037...");
            _adb.KillAllAdb();

            _relay = new TcpRelay(_adb);
            _relay.LogMessage += (_, msg) => _log.Append(msg);
            _relay.UpdateTarget(target);
            await _relay.StartAsync();

            _guard = new PortGuardService(_relay, _adb);
            _guard.LogMessage += (_, msg) => _log.Append(msg);
            _guard.Start();

            _forwardRelay = new ForwardPortRelay(_relay);
            _forwardRelay.LogMessage += (_, msg) => _log.Append(msg);

            _reversePoolRelay = new ReversePoolRelay(_relay);
            _reversePoolRelay.LogMessage += (_, msg) => _log.Append(msg);

            _isConnected = true;
            ConnectButton.Content = "Disconnect";
            _deviceRefreshTimer.Start();
            _statusTimer.Start();
            _log.Append($"Connected. Relay forwarding 127.0.0.1:5037 -> {target}.");
        }
        catch (Exception ex)
        {
            _log.Append($"Connect failed: {ex.Message}");
            MessageBox.Show(owner, $"Could not start the relay:\n{ex.Message}", "AdbBridge Companion",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ConnectButton.IsEnabled = true;
        }
    }

    private async Task DisconnectAsync()
    {
        _deviceRefreshTimer.Stop();
        _statusTimer.Stop();

        if (_reversePoolRelay is not null)
        {
            await _reversePoolRelay.DisposeAsync();
            _reversePoolRelay = null;
        }
        if (_forwardRelay is not null)
        {
            await _forwardRelay.DisposeAsync();
            _forwardRelay = null;
        }
        if (_guard is not null)
        {
            await _guard.DisposeAsync();
            _guard = null;
        }
        if (_relay is not null)
        {
            await _relay.DisposeAsync();
            _relay = null;
        }

        _isConnected = false;
        _devices.Clear();
        Dispatcher.Invoke(() =>
        {
            ConnectButton.Content = "Connect";
            RelayStatusText.Text = "Relay: stopped";
            WatchdogStatusText.Text = "Watchdog: stopped";
            HostConnectionText.Text = "Host: not connected";
            PhoneStatusText.Text = "Phone: unknown";
        });
        _log.Append("Disconnected.");
    }

    private void RefreshStatusText()
    {
        RelayStatusText.Text = $"Relay: {(_relay?.IsListening == true ? "listening on 127.0.0.1:5037" : "not listening")}";
        WatchdogStatusText.Text = $"Watchdog: {(_guard is not null ? "active" : "stopped")}";
        LastConnectionText.Text = _relay?.LastSuccessfulConnection is { } t
            ? $"Last proxied connection: {t.ToLocalTime():HH:mm:ss}"
            : "Last proxied connection: never";

        UpdateConnectivitySummary();
    }

    /// <summary>
    /// Derives two independent, auto-updating facts from the relay's own connection
    /// history and the last parsed device list: whether the Companion is actually
    /// reaching the Host (not just "relay is listening" — a listening relay with a dead
    /// tunnel still fails every real connection attempt), and separately, whether the
    /// phone itself is present and forwarded through that connection.
    /// </summary>
    private void UpdateConnectivitySummary()
    {
        if (_relay is null || !_relay.IsListening)
        {
            HostConnectionText.Text = "Host: not connected";
            PhoneStatusText.Text = "Phone: unknown";
            return;
        }

        var success = _relay.LastSuccessfulConnection;
        var failure = _relay.LastFailedConnection;
        var hostConnected = success is not null &&
                             (failure is null || success > failure) &&
                             DateTime.UtcNow - success < StaleAfter;

        if (hostConnected)
        {
            HostConnectionText.Text = "Host: connected";
        }
        else if (failure is not null && (success is null || failure > success))
        {
            HostConnectionText.Text = $"Host: unreachable ({_relay.LastError ?? "connection failed"})";
        }
        else
        {
            HostConnectionText.Text = "Host: checking…";
        }

        if (!hostConnected)
        {
            PhoneStatusText.Text = "Phone: unknown (host unreachable)";
            return;
        }

        if (_devices.Count == 0)
        {
            PhoneStatusText.Text = "Phone: not detected on Host";
            return;
        }

        var ready = _devices.FirstOrDefault(d => d.State == "device");
        if (ready is not null)
        {
            var label = string.IsNullOrWhiteSpace(ready.Model) ? ready.Serial : ready.Model;
            PhoneStatusText.Text = $"Phone: forwarded — {label}";
        }
        else
        {
            var first = _devices[0];
            PhoneStatusText.Text = $"Phone: present but not ready ({first.State})";
        }
    }

    private async Task RefreshDevicesAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            var devices = await _adb.GetDevicesAsync(_settings.PlatformToolsPath, cts.Token);
            _devices.Clear();
            foreach (var d in devices)
                _devices.Add(d);
        }
        catch (Exception ex)
        {
            _log.Append($"Device refresh failed: {ex.Message}");
        }
    }

    private void BrowseAdbButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "adb.exe|adb.exe", Title = "Locate adb.exe" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            AdbPathBox.Text = dialog.FileName;
    }

    private void RunAtStartupCheck_Changed(object sender, RoutedEventArgs e)
    {
        var enabled = RunAtStartupCheck.IsChecked == true;
        SetRunAtStartup(enabled);
        _settings.RunAtStartup = enabled;
        _settings.Save();
    }

    private void AutoConnectCheck_Changed(object sender, RoutedEventArgs e)
    {
        _settings.AutoConnectOnStartup = AutoConnectCheck.IsChecked == true;
        _settings.Save();
    }

    private static void SetRunAtStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
        if (key is null) return;

        if (enabled)
        {
            var exePath = Environment.ProcessPath ?? Environment.GetCommandLineArgs()[0];
            key.SetValue(RunKeyName, $"\"{exePath}\" companion");
        }
        else
        {
            key.DeleteValue(RunKeyName, throwOnMissingValue: false);
        }
    }

    private void SaveSettingsFromUi()
    {
        _settings.PlatformToolsPath = AdbPathBox.Text.Trim();
        _settings.LastTunnelAddress = AddressBox.Text.Trim();
        _settings.RunAtStartup = RunAtStartupCheck.IsChecked == true;
        _settings.AutoConnectOnStartup = AutoConnectCheck.IsChecked == true;
        _settings.Save();
    }
}
