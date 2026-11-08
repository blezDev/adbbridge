using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AdbBridge.Core.Adb;
using AdbBridge.Core.Config;
using AdbBridge.Core.Logging;
using AdbBridge.Core.Relay;
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
    private ForwardBroker? _broker;
    private AdbBridge.Core.Security.BridgeIdentity? _identity;
    private bool _isSharing;
    private const string RunKeyName = "AdbBridgeHost";

    public event Action? BackRequested;

    public HostView()
    {
        InitializeComponent();

        _settings = HostSettings.Load();
        _log = new LogSink("host");
        LogListBox.ItemsSource = _log.Lines;
        DeviceListView.ItemsSource = _devices;

        AdbPathBox.Text = _settings.PlatformToolsPath;
        NgrokPathBox.Text = _settings.NgrokExePath;
        AuthTokenBox.Password = _settings.NgrokAuthToken;
        SshPathBox.Text = _settings.SshExePath;
        PinggyTokenBox.Password = _settings.PinggyToken;
        RunAtStartupCheck.IsChecked = _settings.RunAtStartup;
        AdbSettingsExpander.IsExpanded = !File.Exists(_settings.PlatformToolsPath);

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
    public Task CleanupAsync() => StopSharingAsync();

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        Cleanup();
        BackRequested?.Invoke();
    }

    private async void StartStopButton_Click(object sender, RoutedEventArgs e)
    {
        try
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
        catch (Exception ex)
        {
            _log.Append($"Sharing failed: {ex.Message}");
            await StopSharingAsync();
            MessageBox.Show(Window.GetWindow(this), ex.Message, "Could not start sharing", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            tunnel = new PinggyTunnelProvider(_settings.SshExePath, _settings.PinggyToken, ForwardBroker.DefaultPort);
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
            tunnel = new NgrokTunnelProvider(_settings.NgrokExePath, _settings.NgrokAuthToken, ForwardBroker.DefaultPort);
        }

        StartStopButton.IsEnabled = false;
        ResetPairingButton.IsEnabled = false;
        StartStopButton.Content = "Starting sharing...";
        SharingProgress.Visibility = Visibility.Visible;
        BackButton.IsEnabled = false;
        try
        {
            _adb.StartNodaemonServer(_settings.PlatformToolsPath);

            _identity = _settings.OpenIdentity();
            _broker = new ForwardBroker(identity: _identity);
            _broker.LogMessage += (_, msg) => _log.Append(msg);
            await _broker.StartAsync();

            _tunnel = tunnel;
            _tunnel.LogMessage += (_, msg) => _log.Append(msg);
            _tunnel.AddressChanged += (_, info) =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (_broker is null) return;
                    PublicAddressBox.Text = _broker.SecureAddress(info).ConnectionCode;
                    CopyAddressButton.IsEnabled = true;
                });
            };
            await _tunnel.StartAsync();

            _isSharing = true;
            StartStopButton.Content = "Stop sharing";
            _deviceRefreshTimer.Start();
            _statusTimer.Start();
            _log.Append("Sharing started.");
        }
        finally
        {
            StartStopButton.IsEnabled = true;
            ResetPairingButton.IsEnabled = true;
            StartStopButton.Content = _isSharing ? "Stop sharing" : "Start sharing";
            SharingProgress.Visibility = Visibility.Collapsed;
            BackButton.IsEnabled = true;
            ProviderCombo.IsEnabled = !_isSharing;
            RefreshStatusText();
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
        if (_broker is not null)
        {
            await _broker.DisposeAsync();
            _broker = null;
        }
        _identity?.Dispose();
        _identity = null;
        _adb.StopServer();

        _isSharing = false;
        Dispatcher.Invoke(() =>
        {
            StartStopButton.Content = "Start sharing";
            PublicAddressBox.Text = "Waiting for sharing to start";
            CopyAddressButton.IsEnabled = false;
            ProviderCombo.IsEnabled = true;
            AdbStatusText.Text = "ADB server: stopped";
            TunnelStatusText.Text = "Tunnel: stopped";
        });
        _log.Append("Sharing stopped.");
        RefreshStatusText();
    }

    private async void ResetPairingButton_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(Window.GetWindow(this), "This disconnects all Companions and invalidates every previously shared connection code. Start sharing again and copy the new code to each trusted PC.", "Reset pairing?", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        ResetPairingButton.IsEnabled = false;
        StartStopButton.IsEnabled = false;
        try
        {
            await StopSharingAsync();
            _settings.BridgeIdentityProtected = string.Empty;
            _settings.Save();
            _log.Append("Pairing reset. Start sharing to generate a new private connection code.");
        }
        catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), ex.Message, "Could not reset pairing", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { ResetPairingButton.IsEnabled = true; StartStopButton.IsEnabled = true; }
    }

    private void RefreshStatusText()
    {
        AdbStatusText.Text = $"ADB server: {(_adb.IsServerRunning ? "running" : "stopped")}";
        TunnelStatusText.Text = $"Tunnel: {(_tunnel?.IsRunning == true ? "connected" : "reconnecting…")}";
        if (!_isSharing) TunnelStatusText.Text = "Tunnel: stopped";
        SharingIndicator.Fill = (System.Windows.Media.Brush)FindResource(_tunnel?.IsRunning == true ? "SuccessBrush" : "MutedBrush");
        AdbStatusText.Foreground = (System.Windows.Media.Brush)FindResource(_adb.IsServerRunning ? "SuccessBrush" : "InkBrush");
        SharingStatusCard.Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(_tunnel?.IsRunning == true ? "#EDF8F2" : "#F3F6FB")!;
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

    private async void CopyAddressButton_Click(object sender, RoutedEventArgs e)
    {
        if (PublicAddressBox.Text is { Length: > 0 } text && text != "(not connected)")
        {
            Clipboard.SetText(text);
            _log.Append("Private connection code copied to clipboard.");
            CopyAddressButton.Content = "✓  Connection code copied";
            await Task.Delay(1800);
            CopyAddressButton.Content = "Copy connection code";
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
        CheckNgrokAvailability();
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

    private void CheckNgrokAvailability()
    {
        if (NgrokAvailabilityText is null || DownloadNgrokButton is null) return;
        var isNgrok = (ProviderCombo.SelectedItem as ComboBoxItem)?.Tag as string != "Pinggy";
        if (!isNgrok) return;
        var path = NgrokPathBox.Text.Trim();
        var available = path.Length > 0 && File.Exists(path);
        NgrokAvailabilityText.Text = available ? "ngrok is ready." : "ngrok is not installed or its path is missing.";
        DownloadNgrokButton.Visibility = available ? Visibility.Collapsed : Visibility.Visible;
    }

    private void DownloadNgrokButton_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://ngrok.com/download") { UseShellExecute = true }); }
        catch (Exception ex) { _log.Append($"Could not open ngrok download: {ex.Message}"); }
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
