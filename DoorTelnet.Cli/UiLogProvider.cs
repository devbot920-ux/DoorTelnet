using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace DoorTelnet.Cli;

public record UiLogEntry(DateTime Timestamp, LogLevel Level, string Message, Exception? Exception);

/// <summary>
/// Logger provider that buffers recent log messages and raises an event so WinForms UI can display them.
/// </summary>
public class UiLogProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<UiLogEntry> _buffer = new();
    private const int MaxEntries = 1000; // Match the StatsForm buffer size
    public event Action<UiLogEntry>? Message;

    public ILogger CreateLogger(string categoryName) => new UiLogger(this, categoryName);
    public void Dispose() { }

    internal void Publish(LogLevel level, string category, string message, Exception? ex)
    {
        var entry = new UiLogEntry(DateTime.UtcNow, level, message, ex);
        _buffer.Enqueue(entry);
        while (_buffer.Count > MaxEntries && _buffer.TryDequeue(out _)) { }
        Message?.Invoke(entry);
    }

    private class UiLogger : ILogger
    {
        private readonly UiLogProvider _owner;
        private readonly string _category;
        public UiLogger(UiLogProvider owner, string category) { _owner = owner; _category = category; }
        public IDisposable BeginScope<TState>(TState state) => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var msg = formatter(state, exception);
            _owner.Publish(logLevel, _category, msg, exception);
        }
    }

    private class NullScope : IDisposable { public static readonly NullScope Instance = new(); public void Dispose() { } }
}
