using System.Windows;
using UserControl = System.Windows.Controls.UserControl;

namespace AdbBridge.App.Views;

public partial class SelectionView : UserControl
{
    public event Action? HostChosen;
    public event Action? CompanionChosen;

    public SelectionView()
    {
        InitializeComponent();
    }

    private void HostButton_Click(object sender, RoutedEventArgs e) => HostChosen?.Invoke();
    private void CompanionButton_Click(object sender, RoutedEventArgs e) => CompanionChosen?.Invoke();
}
