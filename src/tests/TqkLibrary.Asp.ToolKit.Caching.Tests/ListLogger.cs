using Microsoft.Extensions.Logging;

namespace TqkLibrary.Asp.ToolKit.Caching.Tests;

/// <summary>Records every log entry (level + message); also usable as an <see cref="ILoggerProvider"/>.</summary>
internal sealed class ListLogger : ILogger, ILoggerProvider
{
    private readonly object _gate = new();
    private readonly List<(LogLevel Level, string Message)> _entries = [];

    public IReadOnlyList<(LogLevel Level, string Message)> Entries
    {
        get { lock (_gate) return _entries.ToArray(); }
    }

    public IReadOnlyList<LogLevel> Levels => Entries.Select(e => e.Level).ToArray();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;
        lock (_gate)
            _entries.Add((logLevel, formatter(state, exception)));
    }

    public ILogger CreateLogger(string categoryName) => this;

    public void Dispose() { }
}
