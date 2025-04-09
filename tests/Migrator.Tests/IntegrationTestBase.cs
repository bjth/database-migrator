using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Logging;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;
using Xunit.Abstractions; // Required for ITestOutputHelper

namespace Migrator.Tests;

// Base class for integration tests requiring a database container
public abstract class IntegrationTestBase : IAsyncLifetime
{
    protected readonly ITestOutputHelper OutputHelper;
    protected readonly ILoggerFactory LoggerFactory;
    protected IContainer? Container { get; private set; }
    protected string ConnectionString => Container?.State == TestcontainersStates.Running ? GetConnectionString(Container) : string.Empty;

    protected abstract TestcontainerDatabase ContainerDatabase { get; }

    protected IntegrationTestBase(ITestOutputHelper outputHelper)
    {
        OutputHelper = outputHelper;
        // Create a logger factory that directs output to xUnit's output helper
        LoggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder =>
        {
            builder
                .AddXUnit(outputHelper) // Add xUnit logger provider
                .SetMinimumLevel(LogLevel.Trace); // Adjust log level as needed
        });
    }

    public async Task InitializeAsync()
    {
        Container = BuildContainer();
        await Container.StartAsync();
        // Optional: Add a small delay or readiness check if needed after start
    }

    public async Task DisposeAsync()
    {
        if (Container != null)
        {
            await Container.StopAsync();
            await Container.DisposeAsync();
        }
         LoggerFactory.Dispose();
    }

    private IContainer BuildContainer()
    {
         var logger = LoggerFactory.CreateLogger("Testcontainers");

        switch (ContainerDatabase)
        {
            case TestcontainerDatabase.MsSql:
                 logger.LogInformation("Building MsSql Testcontainer...");
                return new MsSqlBuilder()
                    .WithImage("mcr.microsoft.com/mssql/server:latest") // Specify image if needed
                    .WithPassword("yourStrong(!)Password") // Use a strong password
                    .WithPortBinding(1433, true) // Assign random host port
                    .Build();
            case TestcontainerDatabase.PostgreSql:
                 logger.LogInformation("Building PostgreSql Testcontainer...");
                return new PostgreSqlBuilder()
                    .WithImage("postgres:latest") // Specify image if needed
                    .WithDatabase("test_db")
                    .WithUsername("test_user")
                    .WithPassword("test_password")
                    .WithPortBinding(5432, true) // Assign random host port
                    .Build();
            default:
                throw new ArgumentOutOfRangeException(nameof(ContainerDatabase), "Unsupported database type for testing.");
        }
    }

    private string GetConnectionString(IContainer container)
    {
        switch (container)
        {
            case MsSqlContainer msSql: return msSql.GetConnectionString();
            case PostgreSqlContainer postgreSql: return postgreSql.GetConnectionString();
            default: throw new InvalidOperationException("Cannot get connection string for unknown container type.");
        }
    }

    protected enum TestcontainerDatabase
    {
        MsSql,
        PostgreSql
    }
}

// Helper extension for ILoggingBuilder
public static class XUnitLoggerExtensions
{
    public static ILoggingBuilder AddXUnit(this ILoggingBuilder builder, ITestOutputHelper outputHelper)
    {
        builder.AddProvider(new XUnitLoggerProvider(outputHelper));
        return builder;
    }
}

// Custom Logger Provider for xUnit
public class XUnitLoggerProvider(ITestOutputHelper outputHelper) : ILoggerProvider
{
    private readonly ITestOutputHelper _outputHelper = outputHelper;

    public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName)
    {
        return new XUnitLogger(_outputHelper, categoryName);
    }

    public void Dispose()
    {
         GC.SuppressFinalize(this);
    }
}

// Custom Logger for xUnit
public class XUnitLogger(ITestOutputHelper outputHelper, string categoryName) : Microsoft.Extensions.Logging.ILogger
{
    private readonly ITestOutputHelper _outputHelper = outputHelper;
    private readonly string _categoryName = categoryName;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null; // Scopes not easily represented

    public bool IsEnabled(LogLevel logLevel) => true; // Log everything

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        try
        {
            _outputHelper.WriteLine($"[{DateTime.Now:HH:mm:ss} {logLevel.ToString().ToUpperInvariant().Substring(0, 3)}] {_categoryName}: {formatter(state, exception)}");
            if (exception != null)
            {
                _outputHelper.WriteLine(exception.ToString());
            }
        }
        catch (Exception ex) // Avoid exceptions from logging stopping tests
        {
             Console.WriteLine($"Error writing log message: {ex}");
        }
    }
} 