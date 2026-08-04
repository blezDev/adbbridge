namespace AdbBridge.App.Views;

/// <summary>
/// Implemented by views that hold live resources (a relay, a tunnel process) which must
/// be torn down when the user navigates back to the role-selection screen or closes the
/// app — otherwise a stale adb server / ssh / ngrok process would keep running.
/// </summary>
public interface IBridgeView
{
    void Cleanup();
}
