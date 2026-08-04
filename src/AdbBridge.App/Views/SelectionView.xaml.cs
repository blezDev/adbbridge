using System.Reflection;
using System.Windows;
using UserControl = System.Windows.Controls.UserControl;

namespace AdbBridge.App.Views;

public partial class SelectionView : UserControl
{
    public event Action? HostChosen;
    public event Action? CompanionChosen;
    public event Action? CheckUpdatesRequested;

    public SelectionView()
    {
        InitializeComponent();

        var version = Assembly.GetEntryAssembly()?.GetName().Version;
        VersionText.Text = version is null ? "AdbBridge" : $"AdbBridge v{version.Major}.{version.Minor}.{version.Build}";
    }

    private void HostButton_Click(object sender, RoutedEventArgs e) => HostChosen?.Invoke();
    private void CompanionButton_Click(object sender, RoutedEventArgs e) => CompanionChosen?.Invoke();
    private void CheckUpdatesButton_Click(object sender, RoutedEventArgs e) => CheckUpdatesRequested?.Invoke();
}
