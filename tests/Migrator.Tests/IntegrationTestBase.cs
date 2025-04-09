using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;
using Xunit.Abstractions;

// Required for ITestOutputHelper

namespace Migrator.Tests;

// Base class for integration tests requiring a database container
public abstract class IntegrationTestBase : IAsyncLifetime, IDisposable
{
    private readonly ServiceProvider _serviceProvider; // Own service provider for logging
    protected readonly ILoggerFactory LoggerFactory;
    protected readonly ITestOutputHelper OutputHelper;

    // Database specific containers - inheriting classes must initialize ONE of these
    private DockerContainer? _dbContainer;
    protected string? ConnectionString;

    protected IntegrationTestBase(ITestOutputHelper outputHelper)
    {
        ArgumentNullException.ThrowIfNull(outputHelper);
        OutputHelper = outputHelper;

        // Setup logging
        LoggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder =>
        {
            builder
                .AddXUnit(outputHelper)
                .SetMinimumLevel(LogLevel.Trace);
        });

        _serviceProvider = new ServiceCollection()
            .AddSingleton(LoggerFactory)
            .BuildServiceProvider();
    }

    protected abstract TestcontainerDatabase ContainerDatabase { get; }

    public async Task InitializeAsync()
    {
        _dbContainer = BuildContainer();
        await _dbContainer.StartAsync();
        // Optional: Add a small delay or readiness check if needed after start
        ConnectionString = _dbContainer.State == TestcontainersStates.Running
            ? GetConnectionString(_dbContainer)
            : null;
        Assert.NotNull(ConnectionString);
    }

    public async Task DisposeAsync()
    {
        if (_dbContainer != null)
        {
            await _dbContainer.StopAsync();
            await _dbContainer.DisposeAsync();
        }

        LoggerFactory.Dispose();
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private DockerContainer BuildContainer()
    {
        var logger = LoggerFactory.CreateLogger("Testcontainers");

        switch (ContainerDatabase)
        {
            case TestcontainerDatabase.MsSql:
                logger.LogInformation("Building MsSql Testcontainer...");
                return new MsSqlBuilder()
                    .WithImage("mcr.microsoft.com/mssql/server:latest")
                    .WithPassword("yourStrong(!)Password")
                    .WithPortBinding(1433, true)
                    .Build();
            case TestcontainerDatabase.PostgreSql:
                logger.LogInformation("Building PostgreSql Testcontainer...");
                return new PostgreSqlBuilder()
                    .WithImage("postgres:latest")
                    .WithDatabase("test_db")
                    .WithUsername("test_user")
                    .WithPassword("test_password")
                    .WithPortBinding(5432, true)
                    .Build();
            default:
                throw new ArgumentOutOfRangeException(nameof(ContainerDatabase),
                    "Unsupported database type for testing.");
        }
    }

    private string GetConnectionString(DockerContainer container)
    {
        return container switch
        {
            MsSqlContainer msSql => msSql.GetConnectionString(),
            PostgreSqlContainer postgreSql => postgreSql.GetConnectionString(),
            _ => throw new InvalidOperationException("Cannot get connection string for unknown container type.")
        };
    }

    private void Dispose(bool disposing)
    {
        if (disposing)
        {
            _serviceProvider.Dispose();
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
    public ILogger CreateLogger(string categoryName)
    {
        return new XUnitLogger(outputHelper, categoryName);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}

// Custom Logger for xUnit
public class XUnitLogger(ITestOutputHelper outputHelper, string categoryName) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        return null;
        // Scopes not easily represented
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return true;
        // Log everything
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        try
        {
            outputHelper.WriteLine(
                $"[{DateTime.Now:HH:mm:ss} {logLevel.ToString().ToUpperInvariant()[..3]}] {categoryName}: {formatter(state, exception)}");

            if (exception != null)
            {
                outputHelper.WriteLine(exception.ToString());
            }
        }
        catch (Exception ex) // Avoid exceptions from logging stopping tests
        {
            Console.WriteLine($"Error writing log message: {ex}");
        }
    }
}
