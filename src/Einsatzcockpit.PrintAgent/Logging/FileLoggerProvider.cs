using Microsoft.Extensions.Logging;

namespace Einsatzcockpit.PrintAgent.Logging;

public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly object _gate = new();

    public FileLoggerProvider(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
        DeleteOldFiles();
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(_directory, categoryName, _gate);

    public void Dispose()
    {
    }

    private void DeleteOldFiles()
    {
        foreach (var file in Directory.EnumerateFiles(_directory, "*.log"))
        {
            if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-14))
            {
                File.Delete(file);
            }
        }
    }

    private sealed class FileLogger(string directory, string category, object gate) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var path = Path.Combine(directory, $"agent-{DateTime.Now:yyyyMMdd}.log");
            var line = $"{DateTimeOffset.Now:O} [{logLevel}] {category}: {formatter(state, exception)}";
            if (exception is not null)
            {
                line += Environment.NewLine + exception;
            }

            lock (gate)
            {
                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
    }
}
