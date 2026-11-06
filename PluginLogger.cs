namespace BetterLyrics.Plugins.AILyricsTranslation;

/// <summary>
/// Port of the <c>log_debug</c> helper from BetterLyrics.py.
/// Writes to &lt;pluginDir&gt;/logs/plugin.log and rotates (keeps the last 1000 lines)
/// once the file grows beyond 5 MB.
/// </summary>
internal sealed class PluginLogger
{
    private const long MaxBytes = 5 * 1024 * 1024;
    private const int KeepLines = 1000;

    private readonly object _gate = new();
    private readonly string _filePath;

    public PluginLogger(string logDirectory)
    {
        try
        {
            Directory.CreateDirectory(logDirectory);
            _filePath = Path.Combine(logDirectory, "plugin.log");
        }
        catch
        {
            _filePath = string.Empty;
        }
    }

    public string FilePath => _filePath;

    public void Info(string message) => Write("INFO", message);

    public void Warn(string message) => Write("WARN", message);

    public void Debug(string message) => Write("DEBUG", message);

    public void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex == null ? message : $"{message}{Environment.NewLine}{ex}");

    public void Write(string level, string message)
    {
        if (string.IsNullOrEmpty(_filePath)) return;

        try
        {
            lock (_gate)
            {
                RotateIfNeeded();
                File.AppendAllText(
                    _filePath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // logging must never break translation
        }
    }

    private void RotateIfNeeded()
    {
        var info = new FileInfo(_filePath);
        if (!info.Exists || info.Length <= MaxBytes) return;

        var tail = File.ReadLines(_filePath).TakeLast(KeepLines).ToList();
        File.WriteAllLines(_filePath, tail);
    }
}
