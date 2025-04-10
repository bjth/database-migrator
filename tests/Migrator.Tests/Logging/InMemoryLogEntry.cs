using System;
using Microsoft.Extensions.Logging;

namespace Migrator.Tests.Logging;

/// <summary>
/// Represents a captured log entry for testing purposes.
/// </summary>
/// <param name="Level">The log level.</param>
/// <param name="EventId">The event ID.</param>
/// <param name="Message">The formatted log message.</param>
/// <param name="Exception">The exception associated with the log entry, if any.</param>
public record InMemoryLogEntry(LogLevel Level, EventId EventId, string Message, Exception? Exception);
