using System;
using System.Collections.ObjectModel;
using Microsoft.Extensions.Logging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DoorTelnet.Wpf.ViewModels;

public class LogEntry : ObservableObject
{
    public DateTime Timestamp { get; set; }
    public LogLevel Level { get; set; }
    public string Message { get; set; } = string.Empty;
    public string Display => $"{Timestamp:HH:mm:ss} [{Level}] {Message}";
}

public class LogBuffer
{
    private readonly int _max; public LogBuffer(int max) { _max = max; }
    public ObservableCollection<LogEntry> Entries { get; } = new();
    public void Add(LogEntry e)
    {
        App.Current?.Dispatcher.Invoke(() =>
        {
            Entries.Add(e);
            while (Entries.Count > _max) Entries.RemoveAt(0);
        });
    }
}

/// <summary>
/// ILoggerProvider capturing logs and surfacing them to WPF via observable collection
/// </summary>
public class WpfLogProvider : ILoggerProvider
{
    private readonly LogBuffer _buffer;
    public ObservableCollection<LogEntry> Entries => _buffer.Entries;
    public WpfLogProvider(LogBuffer buffer) { _buffer = buffer; }
    public ILogger CreateLogger(string categoryName) => new WpfLogger(_buffer, categoryName);
    public void Dispose() { }

    private class WpfLogger : ILogger
    {
        private readonly LogBuffer _buffer; private readonly string _cat;
        public WpfLogger(LogBuffer buf, string cat) { _buffer = buf; _cat = cat; }
        public IDisposable BeginScope<TState>(TState state) => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var msg = formatter(state, exception);
            if (exception != null) msg += $" :: {exception.GetType().Name}: {exception.Message}";
            _buffer.Add(new LogEntry { Timestamp = DateTime.UtcNow, Level = logLevel, Message = msg });
        }
    }
    private class NullScope : IDisposable { public static readonly NullScope Instance = new(); public void Dispose() { } }
}

public class LogViewModel
{
    public ObservableCollection<LogEntry> Entries { get; }
    public LogViewModel(WpfLogProvider provider)
    {
        Entries = provider.Entries;
    }
}
