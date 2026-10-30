using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace HCG.Mail.Infrastructure.Logging;

/// <summary>
/// Minimal daily-rolling file logger. HCG Mail logs operations, hosts, account ids and error
/// types only — never passwords, message bodies, subjects or recipient lists.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private const int RetainDays = 7;
    private readonly string _directory;
    private readonly LogLevel _minimumLevel;
    private readonly object _lock = new();

    public FileLoggerProvider(string directory, LogLevel minimumLevel = LogLevel.Information)
    {
        _directory = directory;
        _minimumLevel = minimumLevel;
        CleanupOldLogs();
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
    }

    private void Write(string category, LogLevel level, string message, Exception? exception)
    {
        var sb = new StringBuilder();
        sb.Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
          .Append(" [").Append(level.ToString().ToUpperInvariant()[..4]).Append("] ")
          .Append(category[(category.LastIndexOf('.') + 1)..]).Append(": ").Append(message);
        if (exception is not null)
            sb.Append(" | ").Append(exception.GetType().FullName).Append(": ").Append(exception.Message);
        sb.Append(Environment.NewLine);

        var path = Path.Combine(_directory, $"hcg-mail-{DateTime.Now:yyyyMMdd}.log");
        lock (_lock)
        {
            try
            {
                File.AppendAllText(path, sb.ToString());
            }
            catch (IOException)
            {
                // Logging must never take the application down.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private void CleanupOldLogs()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_directory, "hcg-mail-*.log"))
            {
                if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-RetainDays))
                    File.Delete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= provider._minimumLevel && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
                provider.Write(category, logLevel, formatter(state, exception), exception);
        }
    }
}
