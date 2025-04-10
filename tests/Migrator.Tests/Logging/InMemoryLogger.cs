using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Migrator.Tests.Logging;

/// <summary>
/// Simple ILogger implementation that stores log entries in a shared ConcurrentBag 
/// and optionally writes them to a provided output action (like ITestOutputHelper.WriteLine).
/// </summary>
public class InMemoryLogger : ILogger
{
    private readonly string _categoryName;
    private readonly ConcurrentBag<InMemoryLogEntry> _logEntries;
    private readonly Action<string> _outputAction;

    public InMemoryLogger(string categoryName, ConcurrentBag<InMemoryLogEntry> logEntries, Action<string>? outputAction = null)
    {
        _categoryName = categoryName;
        _logEntries = logEntries ?? throw new ArgumentNullException(nameof(logEntries));
        _outputAction = outputAction ?? (_ => { });
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => default!;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var message = formatter(state, exception);
        if (string.IsNullOrEmpty(message) && exception == null)
        {
            return;
        }

        var entry = new InMemoryLogEntry(logLevel, eventId, message, exception);
        _logEntries.Add(entry);

        var outputMessage = $"[{logLevel.ToString().Substring(0, 3).ToUpper()}] [{_categoryName}] {message}";
        if (exception != null)
        {
            outputMessage += $"\n{exception}";
        }
        _outputAction(outputMessage);
    }
}
