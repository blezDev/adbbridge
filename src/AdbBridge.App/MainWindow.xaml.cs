using System.Diagnostics;
using System.Linq;
using System.Windows;
using AdbBridge.App.Views;
using AdbBridge.Core.Update;
using Application = System.Windows.Application;

namespace AdbBridge.App;

public partial class MainWindow : Window
{
    private TrayIconService? _tray;
    private IBridgeView? _currentView;
    private UpdateInfo? _availableUpdate;

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

        _ = CheckForUpdatesAsync();
    }

    private async Task CheckForUpdatesAsync()
    {
        var update = await UpdateChecker.CheckAsync();
        if (update is null) return;

        _availableUpdate = update;
        Dispatcher.Invoke(() =>
        {
            UpdateBannerText.Text = $"AdbBridge {update.Version} is available (you're on an older version).";
            UpdateBanner.Visibility = Visibility.Visible;
        });
    }

    private void UpdateDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate is null) return;
        try
        {
            Process.Start(new ProcessStartInfo(_availableUpdate.ReleaseUrl) { UseShellExecute = true });
        }
        catch
        {
            // Best-effort — if the default browser can't be launched for some reason,
            // there's nothing more useful to do than leave the banner up.
        }
    }

    private void UpdateDismissButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateBanner.Visibility = Visibility.Collapsed;
    }

    private static string? ParseStartupRole(string[] args) =>
        args.Skip(1).FirstOrDefault(a => a is "host" or "companion");

    private void OnExitRequested()
    {
        _currentView?.Cleanup();
        Application.Current.Shutdown();
    }

    private void ShowSelection()
    {
        var view = new SelectionView();
        view.HostChosen += ShowHost;
        view.CompanionChosen += () => ShowCompanion(autoConnect: false);
        SetView(null, view);
        Title = "AdbBridge";
    }

    private void ShowHost()
    {
        var view = new HostView();
        view.BackRequested += ShowSelection;
        SetView(view, view);
        Title = "AdbBridge — Host (Local PC)";
    }

    private async void ShowCompanion(bool autoConnect)
    {
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
    }
}
