using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace Migrator.Tests.Logging;

// --------------- ILoggerProvider and ILogger for xUnit ---------------

// Helper extension for ILoggingBuilder
public static class XUnitLoggerExtensions
{
    /// <summary>
    /// Adds an xUnit logger provider to the logging builder.
    /// </summary>
    /// <param name="builder">The logging builder.</param>
    /// <param name="outputHelper">The ITestOutputHelper instance to write logs to.</param>
    /// <returns>The logging builder.</returns>
    public static ILoggingBuilder AddXUnit(this ILoggingBuilder builder, ITestOutputHelper outputHelper)
    {
        // Add the output helper itself to DI if needed by other services
        builder.Services.AddSingleton(outputHelper);
        // Add the custom provider
        builder.AddProvider(new XUnitLoggerProvider(outputHelper));
        return builder;
    }
}

// Custom Logger Provider for xUnit
public class XUnitLoggerProvider(ITestOutputHelper outputHelper) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName)
    {
        return new XUnitLogger(outputHelper, categoryName);
    }

    public void Dispose()
    {
        // No resources to dispose in this simple provider
        GC.SuppressFinalize(this);
    }
}

// Custom Logger for xUnit
public class XUnitLogger(ITestOutputHelper outputHelper, string categoryName) : ILogger
{
    // Simple implementation: no scope support
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    // Log everything passed to it for simplicity in tests
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        // Using try-catch to prevent logging errors from stopping tests
        try
        {
            outputHelper.WriteLine(
                $"[{DateTime.Now:HH:mm:ss.fff} {logLevel.ToString().ToUpperInvariant()[..3]}] {categoryName}: {formatter(state, exception)}");

            if (exception == null)
            {
                return;
            }

            outputHelper.WriteLine("---------- Exception ----------");
            outputHelper.WriteLine(exception.ToString());
            outputHelper.WriteLine("-----------------------------");
        }
        catch (Exception logEx)
        {
            Console.WriteLine($"!!! Error writing log to xUnit output: {logEx.Message}");
        }
    }
}
