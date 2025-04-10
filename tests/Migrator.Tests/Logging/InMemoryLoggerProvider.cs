using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace Migrator.Tests.Logging;

/// <summary>
/// An ILoggerProvider that creates instances of InMemoryLogger. 
/// It holds a shared collection of log entries and allows clearing them.
/// </summary>
public class InMemoryLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentBag<InMemoryLogEntry> _logEntries = new ConcurrentBag<InMemoryLogEntry>();
    private readonly ConcurrentDictionary<string, InMemoryLogger> _loggers = new ConcurrentDictionary<string, InMemoryLogger>();
    private readonly Action<string>? _outputAction;

    /// <summary>
    /// Initializes a new instance of the <see cref="InMemoryLoggerProvider"/> class.
    /// </summary>
    /// <param name="outputAction">Optional action to perform when a log entry is recorded (e.g., write to ITestOutputHelper).</param>
    public InMemoryLoggerProvider(Action<string>? outputAction = null)
    {
        _outputAction = outputAction;
    }

    /// <summary>
    /// Creates a new <see cref="InMemoryLogger"/> instance for the specified category name.
    /// </summary>
    public ILogger CreateLogger(string categoryName)
    {
        return _loggers.GetOrAdd(categoryName, name => new InMemoryLogger(name, _logEntries, _outputAction));
    }

    /// <summary>
    /// Gets a snapshot of all log entries captured so far.
    /// </summary>
    public IEnumerable<InMemoryLogEntry> GetLogEntries() => _logEntries.ToList();

    /// <summary>
    /// Clears all captured log entries.
    /// </summary>
    public void ClearLogEntries() => _logEntries.Clear();

    /// <summary>
    /// Disposes the provider, clearing internal collections.
    /// </summary>
    public void Dispose()
    {
        _loggers.Clear();
        _logEntries.Clear();
        GC.SuppressFinalize(this);
    }
}
