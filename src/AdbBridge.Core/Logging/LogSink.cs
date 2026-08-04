using System.Collections.ObjectModel;
using System.Threading;

namespace AdbBridge.Core.Logging;

/// <summary>
/// An ObservableCollection-backed log the UI can bind directly to. Appends are
/// marshalled onto the SynchronizationContext captured at construction time (call this
/// from the UI thread), so producers on background threads don't need to know about UI
/// thread affinity.
/// </summary>
public sealed class LogSink
{
    private const int MaxLines = 2000;
    private readonly SynchronizationContext _uiContext;

    public ObservableCollection<string> Lines { get; } = new();

    public LogSink()
    {
        _uiContext = SynchronizationContext.Current
            ?? throw new InvalidOperationException("LogSink must be constructed on the UI thread.");
    }

    public void Append(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss} {message}";
        _uiContext.Post(_ => AppendCore(line), null);
    }

    private void AppendCore(string line)
    {
        Lines.Add(line);
        while (Lines.Count > MaxLines)
            Lines.RemoveAt(0);
    }
}
