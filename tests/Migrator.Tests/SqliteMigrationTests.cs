using Xunit;
using Xunit.Abstractions;
using Migrator.Core;
using Shouldly;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using System; // Added for IDisposable
using SQLitePCL; // Add this using statement

namespace Migrator.Tests;

// Tests for SQLite - uses a temporary file DB, no Testcontainers needed
public class SqliteMigrationTests : IDisposable
{
    private readonly string _dbFilePath;
    private readonly string _connectionString;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ServiceProvider _serviceProvider; // For logging

    public SqliteMigrationTests(ITestOutputHelper outputHelper)
    {
        // Initialize SQLitePCL provider - Add this line
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlite3());

        _dbFilePath = Path.Combine(Path.GetTempPath(), $"migrator_sqlite_test_{Guid.NewGuid()}.db");
        _connectionString = $"Data Source={_dbFilePath}";

        // Setup logging directed to xUnit output
         _loggerFactory = LoggerFactory.Create(builder =>
        {
            builder
                .AddXUnit(outputHelper) // Reuse the extension method from IntegrationTestBase file
                .SetMinimumLevel(LogLevel.Trace);
        });

         _serviceProvider = new ServiceCollection()
            .AddSingleton(_loggerFactory)
            .AddLogging()
            .BuildServiceProvider();
    }

    public void Dispose()
    {
         _serviceProvider.Dispose();
         _loggerFactory.Dispose();
        // Attempt to delete the temp DB file
        try { File.Delete(_dbFilePath); } catch { /* Ignore cleanup errors */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task ExecuteMigrationsAsync_SQLite_RunsMixedMigrationsSuccessfully()
    {
        // Arrange
        var migrationsDir = TestHelpers.PrepareTestMigrations("sqlite_test", DatabaseType.SQLite);
        var logger = _loggerFactory.CreateLogger<MigrationService>();
        var migrationService = new MigrationService(logger);

        // Act & Assert
        await Should.NotThrowAsync(async () =>
        {
            await migrationService.ExecuteMigrationsAsync(DatabaseType.SQLite, _connectionString, migrationsDir);
        });

        // Assert: Check database state
        await TestHelpers.AssertDatabaseStateAfterMigrations(DatabaseType.SQLite, _connectionString);

        // Cleanup
        TestHelpers.CleanupTestMigrations(migrationsDir);
    }

    [Fact]
    public async Task ExecuteMigrationsAsync_SQLite_HandlesAlreadyAppliedMigrations()
    {
        // Arrange
        var testId = "sqlite_rerun_test";
        var migrationsDir = TestHelpers.PrepareTestMigrations(testId, DatabaseType.SQLite);
        var logger = _loggerFactory.CreateLogger<MigrationService>();
        var migrationService = new MigrationService(logger);

        // Act 1: Run migrations first time
        await migrationService.ExecuteMigrationsAsync(DatabaseType.SQLite, _connectionString, migrationsDir);

        // Assert 1: Check initial state
        await TestHelpers.AssertDatabaseStateAfterMigrations(DatabaseType.SQLite, _connectionString, 6);

        // Act 2: Run migrations second time
        await Should.NotThrowAsync(async () =>
        {
            await migrationService.ExecuteMigrationsAsync(DatabaseType.SQLite, _connectionString, migrationsDir);
        });

        // Assert 2: State should remain unchanged
        await TestHelpers.AssertDatabaseStateAfterMigrations(DatabaseType.SQLite, _connectionString, 6);

        // Cleanup
        TestHelpers.CleanupTestMigrations(migrationsDir);
    }
} 