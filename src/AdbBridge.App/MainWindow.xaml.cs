using System.Linq;
using System.Windows;
using AdbBridge.App.Views;
using AdbBridge.Core.Update;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace AdbBridge.App;

public partial class MainWindow : Window
{
    private TrayIconService? _tray;
    private IBridgeView? _currentView;
    private UpdateInfo? _availableUpdate;
    private CancellationTokenSource? _updateCancellation;
    private bool _installingUpdate;
    private string? _currentRole;

    public MainWindow()
    {
        InitializeComponent();

        Loaded += (_, _) =>
        {
            _tray = new TrayIconService(this);
            _tray.ExitRequested += OnExitRequested;
        };

        var startupRole = ParseStartupRole(Environment.GetCommandLineArgs());
        switch (startupRole)
        {
            case "host":
                ShowHost();
                break;
            case "companion":
                ShowCompanion(autoConnect: true);
                break;
            default:
                ShowSelection();
                break;
        }

        _ = CheckForUpdatesAsync(interactive: false);
    }

    /// <summary>
    /// interactive=false is the silent startup check: shows the banner if there's an
    /// update, says nothing otherwise. interactive=true is a user-initiated "Check for
    /// updates" click, which should always give some feedback either way — including a
    /// small "you're up to date" confirmation when there's nothing new, since silence
    /// would look like the button didn't do anything.
    /// </summary>
    private async Task CheckForUpdatesAsync(bool interactive)
    {
        if (_updateCancellation is not null) return;
        UpdateInfo? update;
        try { update = await UpdateChecker.CheckAsync(throwOnError: interactive); }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not check for updates: {ex.Message}", "AdbBridge", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (_updateCancellation is not null) return;

        if (update is not null)
        {
            _availableUpdate = update;
            Dispatcher.Invoke(() =>
            {
                UpdateBannerText.Text = $"AdbBridge {update.Version} is available (you're on an older version).";
                UpdateBanner.Visibility = Visibility.Visible;
            });
        }
        else if (interactive)
        {
            Dispatcher.Invoke(() =>
                MessageBox.Show(this, "You're up to date.", "AdbBridge", MessageBoxButton.OK, MessageBoxImage.Information));
        }
    }

    private async void UpdateDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate is null || _updateCancellation is not null) return;
        using var cancellation = new CancellationTokenSource();
        _updateCancellation = cancellation;
        UpdateDownloadButton.IsEnabled = false;
        UpdateDismissButton.Content = "Cancel";
        ViewHost.IsEnabled = false;
        UpdateBannerText.Text = "Downloading update...";
        try
        {
            var progress = new Progress<int>(percent => UpdateBannerText.Text = percent >= 0
                ? $"Downloading update... {percent}%" : "Downloading update...");
            var package = await SelfUpdate.PrepareAsync(_availableUpdate, progress, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            _installingUpdate = true;
            UpdateDismissButton.IsEnabled = false;
            UpdateBannerText.Text = "Installing update and restarting...";
            if (_currentView is not null) await _currentView.CleanupAsync();
            await SelfUpdate.StartHelperAsync(package, _currentRole);
            _tray?.PrepareForShutdown();
            Application.Current.Shutdown();
        }
        catch (OperationCanceledException)
        {
            UpdateBannerText.Text = "Update canceled. You can try again.";
        }
        catch (Exception ex)
        {
            UpdateBannerText.Text = "Update failed. You can try again.";
            MessageBox.Show(this, ex.Message, "AdbBridge update", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _updateCancellation = null;
            _installingUpdate = false;
            UpdateDownloadButton.IsEnabled = true;
            UpdateDismissButton.IsEnabled = true;
            UpdateDismissButton.Content = "Dismiss";
            ViewHost.IsEnabled = true;
        }
    }

    private void UpdateDismissButton_Click(object sender, RoutedEventArgs e)
    {
        if (_updateCancellation is not null) { _updateCancellation.Cancel(); return; }
        UpdateBanner.Visibility = Visibility.Collapsed;
    }

    private static string? ParseStartupRole(string[] args) =>
        args.Skip(1).FirstOrDefault(a => a is "host" or "companion");

    private void OnExitRequested()
    {
        if (_installingUpdate) return;
        _updateCancellation?.Cancel();
        _currentView?.Cleanup();
        Application.Current.Shutdown();
    }

    private void ShowSelection()
    {
        _currentRole = null;
        var view = new SelectionView();
        view.HostChosen += ShowHost;
        view.CompanionChosen += () => ShowCompanion(autoConnect: false);
        view.CheckUpdatesRequested += () => _ = CheckForUpdatesAsync(interactive: true);
        SetView(null, view);
        Title = "AdbBridge";
    }

    private void ShowHost()
    {
        _currentRole = "host";
        var view = new HostView();
        view.BackRequested += ShowSelection;
        SetView(view, view);
        Title = "AdbBridge — Host (Local PC)";
    }

    private async void ShowCompanion(bool autoConnect)
    {
        _currentRole = "companion";
        var view = new CompanionView();
        view.BackRequested += ShowSelection;
        SetView(view, view);
        Title = "AdbBridge — Companion (Cloud PC)";

        if (autoConnect)
            await view.AutoConnectIfConfiguredAsync();
    }

    private void SetView(IBridgeView? bridgeView, object visual)
    {
        _currentView?.Cleanup();
        _currentView = bridgeView;
        ViewHost.Content = visual;
        ViewHost.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
    }
}
