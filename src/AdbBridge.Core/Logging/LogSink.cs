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
    private readonly string? _logFilePath;

    public ObservableCollection<string> Lines { get; } = new();

    public LogSink() : this(null) { }

    /// <summary>
    /// <paramref name="role"/>, when given (e.g. "host" or "companion"), also appends
    /// every line to <c>%LOCALAPPDATA%\AdbBridge\logs\{role}.log</c> (overwritten fresh
    /// each run) — the in-memory <see cref="Lines"/> only lives as long as the process
    /// and isn't visible to anything outside the running UI, which makes diagnosing a
    /// connection problem after the fact impossible without this.
    /// </summary>
    public LogSink(string? role)
    {
        _uiContext = SynchronizationContext.Current
            ?? throw new InvalidOperationException("LogSink must be constructed on the UI thread.");

        if (!string.IsNullOrEmpty(role))
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AdbBridge", "logs");
            try
            {
                Directory.CreateDirectory(dir);
                _logFilePath = Path.Combine(dir, $"{role}.log");
                File.WriteAllText(_logFilePath, string.Empty);
            }
            catch
            {
                // Best-effort — file logging is a diagnostic aid, not load-bearing.
                _logFilePath = null;
            }
        }
    }

    public void Append(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss} {message}";
        _uiContext.Post(_ => AppendCore(line), null);

        if (_logFilePath is not null)
        {
            try { File.AppendAllText(_logFilePath, line + Environment.NewLine); }
            catch { /* best-effort */ }
        }
    }

    private void AppendCore(string line)
    {
        Lines.Add(line);
        while (Lines.Count > MaxLines)
            Lines.RemoveAt(0);
    }
}
