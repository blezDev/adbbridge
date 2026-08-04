using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AdbBridge.Core.Adb;
using AdbBridge.Core.Config;
using AdbBridge.Core.Logging;
using AdbBridge.Core.Tunnels;
using Microsoft.Win32;
using Clipboard = System.Windows.Clipboard;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using UserControl = System.Windows.Controls.UserControl;

namespace AdbBridge.App.Views;

public partial class HostView : UserControl, IBridgeView
{
    private readonly HostSettings _settings;
    private readonly AdbProcessManager _adb = new();
    private readonly LogSink _log;
    private readonly ObservableCollection<AdbDevice> _devices = new();
    private readonly DispatcherTimer _deviceRefreshTimer;
    private readonly DispatcherTimer _statusTimer;

    private ITunnelProvider? _tunnel;
    private bool _isSharing;
    private const string RunKeyName = "AdbBridgeHost";

    public event Action? BackRequested;

    public HostView()
    {
        InitializeComponent();

        _settings = HostSettings.Load();
        _log = new LogSink();
        LogListBox.ItemsSource = _log.Lines;
        DeviceListView.ItemsSource = _devices;

        AdbPathBox.Text = _settings.PlatformToolsPath;
        NgrokPathBox.Text = _settings.NgrokExePath;
        AuthTokenBox.Password = _settings.NgrokAuthToken;
        SshPathBox.Text = _settings.SshExePath;
        PinggyTokenBox.Password = _settings.PinggyToken;
        RunAtStartupCheck.IsChecked = _settings.RunAtStartup;

        ProviderCombo.SelectedIndex = _settings.Provider == TunnelProviderKind.Pinggy ? 1 : 0;
        UpdateProviderPanels();

        _adb.LogMessage += (_, msg) => _log.Append(msg);

        _deviceRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _deviceRefreshTimer.Tick += async (_, _) => await RefreshDevicesAsync();

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _statusTimer.Tick += (_, _) => RefreshStatusText();

        Loaded += (_, _) => CheckNgrokAvailability();
    }

    public void Cleanup() => _ = StopSharingAsync();

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        Cleanup();
        BackRequested?.Invoke();
    }

    private async void StartStopButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isSharing)
        {
            await StopSharingAsync();
        }
        else
        {
            await StartSharingAsync();
        }
    }

    private async Task StartSharingAsync()
    {
        SaveSettingsFromUi();
        var owner = Window.GetWindow(this);

        if (!File.Exists(_settings.PlatformToolsPath))
        {
            MessageBox.Show(owner, $"adb.exe not found at:\n{_settings.PlatformToolsPath}", "AdbBridge Host",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ITunnelProvider tunnel;
        if (_settings.Provider == TunnelProviderKind.Pinggy)
        {
            if (!File.Exists(_settings.SshExePath))
            {
                MessageBox.Show(owner, $"ssh.exe not found at:\n{_settings.SshExePath}", "AdbBridge Host",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (string.IsNullOrWhiteSpace(_settings.PinggyToken))
            {
                MessageBox.Show(owner, "Enter your Pinggy token first.", "AdbBridge Host",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            tunnel = new PinggyTunnelProvider(_settings.SshExePath, _settings.PinggyToken);
        }
        else
        {
            if (!File.Exists(_settings.NgrokExePath))
            {
                MessageBox.Show(owner, $"ngrok.exe not found at:\n{_settings.NgrokExePath}", "AdbBridge Host",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (string.IsNullOrWhiteSpace(_settings.NgrokAuthToken))
            {
                MessageBox.Show(owner, "Enter your ngrok authtoken first.", "AdbBridge Host",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            tunnel = new NgrokTunnelProvider(_settings.NgrokExePath, _settings.NgrokAuthToken);
        }

        StartStopButton.IsEnabled = false;
        try
        {
            _adb.StartNodaemonServer(_settings.PlatformToolsPath);

            _tunnel = tunnel;
            _tunnel.LogMessage += (_, msg) => _log.Append(msg);
            _tunnel.AddressChanged += (_, info) =>
            {
                Dispatcher.Invoke(() => PublicAddressBox.Text = info.ToString());
            };
            await _tunnel.StartAsync();

            _isSharing = true;
            StartStopButton.Content = "Stop Sharing";
            _deviceRefreshTimer.Start();
            _statusTimer.Start();
            _log.Append("Sharing started.");
        }
        finally
        {
            StartStopButton.IsEnabled = true;
        }
    }

    private async Task StopSharingAsync()
    {
        _deviceRefreshTimer.Stop();
        _statusTimer.Stop();

        if (_tunnel is not null)
        {
            await _tunnel.DisposeAsync();
            _tunnel = null;
        }
        _adb.StopServer();

        _isSharing = false;
        Dispatcher.Invoke(() =>
        {
            StartStopButton.Content = "Start Sharing";
            PublicAddressBox.Text = "(not connected)";
            AdbStatusText.Text = "ADB server: stopped";
            TunnelStatusText.Text = "Tunnel: stopped";
        });
        _log.Append("Sharing stopped.");
    }

    private void RefreshStatusText()
    {
        AdbStatusText.Text = $"ADB server: {(_adb.IsServerRunning ? "running" : "stopped")}";
        TunnelStatusText.Text = $"Tunnel: {(_tunnel?.IsRunning == true ? "connected" : "reconnecting…")}";
    }

    private async Task RefreshDevicesAsync()
    {
        try
        {
            var devices = await _adb.GetDevicesAsync(_settings.PlatformToolsPath);
            _devices.Clear();
            foreach (var d in devices)
                _devices.Add(d);
        }
        catch (Exception ex)
        {
            _log.Append($"Device refresh failed: {ex.Message}");
        }
    }

    private void CopyAddressButton_Click(object sender, RoutedEventArgs e)
    {
        if (PublicAddressBox.Text is { Length: > 0 } text && text != "(not connected)")
        {
            Clipboard.SetText(text);
            _log.Append("Address copied to clipboard.");
        }
    }

    private void BrowseAdbButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "adb.exe|adb.exe", Title = "Locate adb.exe" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            AdbPathBox.Text = dialog.FileName;
    }

    private void BrowseNgrokButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "ngrok.exe|ngrok.exe", Title = "Locate ngrok.exe" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            NgrokPathBox.Text = dialog.FileName;
    }

    private void BrowseSshButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "ssh.exe|ssh.exe", Title = "Locate ssh.exe" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            SshPathBox.Text = dialog.FileName;
    }

    private void ProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateProviderPanels();
        CheckNgrokAvailability();
    }

    private void UpdateProviderPanels()
    {
        if (NgrokPanel is null || PinggyPanel is null) return;
        var isPinggy = (ProviderCombo.SelectedItem as ComboBoxItem)?.Tag as string == "Pinggy";
        NgrokPanel.Visibility = isPinggy ? Visibility.Collapsed : Visibility.Visible;
        PinggyPanel.Visibility = isPinggy ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// ngrok isn't bundled or auto-installable — it's a separate closed-source binary
    /// from ngrok.com (unlike Pinggy, which only needs Windows' own OpenSSH client).
    /// Whenever ngrok is the selected provider and no valid ngrok.exe is configured,
    /// proactively offers to open the download page instead of waiting for Start
    /// Sharing to fail with a plain "not found" error.
    /// </summary>
    private void CheckNgrokAvailability()
    {
        var isNgrok = (ProviderCombo.SelectedItem as ComboBoxItem)?.Tag as string != "Pinggy";
        if (!isNgrok) return;

        var path = NgrokPathBox.Text.Trim();
        if (path.Length > 0 && File.Exists(path)) return;

        var owner = Window.GetWindow(this);
        var result = MessageBox.Show(owner,
            "ngrok.exe wasn't found. AdbBridge doesn't bundle ngrok — it's a separate free " +
            "download from ngrok.com.\n\nOpen the ngrok download page now?",
            "ngrok required", MessageBoxButton.YesNo, MessageBoxImage.Information);

        if (result == MessageBoxResult.Yes)
        {
            try { Process.Start(new ProcessStartInfo("https://ngrok.com/download") { UseShellExecute = true }); }
            catch { /* best effort — nothing more useful to do if the browser won't launch */ }
        }
    }

    private void RunAtStartupCheck_Changed(object sender, RoutedEventArgs e)
    {
        var enabled = RunAtStartupCheck.IsChecked == true;
        SetRunAtStartup(enabled);
        _settings.RunAtStartup = enabled;
        _settings.Save();
    }

    private static void SetRunAtStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
        if (key is null) return;

        if (enabled)
        {
            var exePath = Environment.ProcessPath ?? Environment.GetCommandLineArgs()[0];
            key.SetValue(RunKeyName, $"\"{exePath}\" host");
        }
        else
        {
            key.DeleteValue(RunKeyName, throwOnMissingValue: false);
        }
    }

    private void SaveSettingsFromUi()
    {
        _settings.PlatformToolsPath = AdbPathBox.Text.Trim();
        _settings.Provider = (ProviderCombo.SelectedItem as ComboBoxItem)?.Tag as string == "Pinggy"
            ? TunnelProviderKind.Pinggy
            : TunnelProviderKind.Ngrok;
        _settings.NgrokExePath = NgrokPathBox.Text.Trim();
        _settings.NgrokAuthToken = AuthTokenBox.Password;
        _settings.SshExePath = SshPathBox.Text.Trim();
        _settings.PinggyToken = PinggyTokenBox.Password;
        _settings.RunAtStartup = RunAtStartupCheck.IsChecked == true;
        _settings.Save();
    }
}
