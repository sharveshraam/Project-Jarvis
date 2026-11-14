using System.Globalization;
using System.Text;
using Jarvis.Core.Security;
using Microsoft.Extensions.Logging;

namespace Jarvis.Infrastructure.Logging;

/// <summary>
/// Structured, daily-rolling file log. Every line passes through <see cref="SecretDetector.Redact"/>,
/// so API keys and tokens cannot end up on disk even if a caller logs them by mistake.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly object _gate = new();
    private StreamWriter? _writer;
    private string _currentDay = "";
    private bool _disposed;

    public FileLoggerProvider(string directory, LogLevel minimumLevel = LogLevel.Information, int retainDays = 7)
    {
        _directory = directory;
        MinimumLevel = minimumLevel;
        Directory.CreateDirectory(directory);
        Prune(retainDays);
    }

    public LogLevel MinimumLevel { get; }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal void Write(LogLevel level, string category, EventId eventId, string message, Exception? exception)
    {
        var now = DateTime.Now;
        var line = new StringBuilder(256);
        line.Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append(' ')
            .Append(LevelName(level)).Append(' ')
            .Append(ShortCategory(category));
        if (eventId.Id != 0) line.Append('#').Append(eventId.Id.ToString(CultureInfo.InvariantCulture));
        line.Append(" | ").Append(SecretDetector.Redact(message));
        if (exception is not null)
            line.Append(" | ").Append(exception.GetType().Name).Append(": ").Append(SecretDetector.Redact(exception.Message));

        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                var day = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                if (_writer is null || day != _currentDay)
                {
                    _writer?.Dispose();
                    var path = Path.Combine(_directory, $"jarvis-{day}.log");
                    _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false))
                    {
                        AutoFlush = true
                    };
                    _currentDay = day;
                }
                _writer.WriteLine(line.ToString());
            }
            catch (IOException)
            {
                // Logging must never take the application down.
            }
        }
    }

    private void Prune(int retainDays)
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-retainDays);
            foreach (var file in new DirectoryInfo(_directory).GetFiles("jarvis-*.log"))
            {
                if (file.LastWriteTime < cutoff) file.Delete();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC", LogLevel.Debug => "DBG", LogLevel.Information => "INF",
        LogLevel.Warning => "WRN", LogLevel.Error => "ERR", LogLevel.Critical => "CRT", _ => "---"
    };

    private static string ShortCategory(string category)
    {
        var index = category.LastIndexOf('.');
        return index >= 0 ? category.Substring(index + 1) : category;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _writer?.Dispose();
            _writer = null;
        }
    }

    private sealed class FileLogger : ILogger
    {
        private readonly FileLoggerProvider _provider;
        private readonly string _category;

        public FileLogger(FileLoggerProvider provider, string category)
        {
            _provider = provider;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= _provider.MinimumLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            _provider.Write(logLevel, _category, eventId, formatter(state, exception), exception);
        }
    }
}
