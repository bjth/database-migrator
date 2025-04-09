using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Migrator.Core;
using Shouldly;
using SQLitePCL;
using Xunit.Abstractions;
using ILoggerFactory = Microsoft.Extensions.Logging.ILoggerFactory; // Add this using statement

namespace Migrator.Tests;

// Tests for SQLite - uses a temporary file DB, no TestContainers needed
public class SqliteMigrationTests : IDisposable
{
    private readonly string _dbFilePath;
    private readonly string _connectionString;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ServiceProvider _serviceProvider; // For logging

    public SqliteMigrationTests(ITestOutputHelper outputHelper)
    {
        // Initialize SQLitePCL provider - Add this line
        raw.SetProvider(new SQLite3Provider_e_sqlite3());

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
        try
        {
            File.Delete(_dbFilePath);
        }
        catch
        {
            /* Ignore cleanup errors */
        }

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
        const string testId = "sqlite_rerun_test";
        var migrationsDir = TestHelpers.PrepareTestMigrations(testId, DatabaseType.SQLite);
        var logger = _loggerFactory.CreateLogger<MigrationService>();
        var migrationService = new MigrationService(logger);

        // Act 1: Run migrations first time
        await migrationService.ExecuteMigrationsAsync(DatabaseType.SQLite, _connectionString, migrationsDir);

        // Assert 1: Check initial state
        await TestHelpers.AssertDatabaseStateAfterMigrations(DatabaseType.SQLite, _connectionString);

        // Act 2: Run migrations second time
        await Should.NotThrowAsync(async () =>
        {
            await migrationService.ExecuteMigrationsAsync(DatabaseType.SQLite, _connectionString, migrationsDir);
        });

        // Assert 2: State should remain unchanged
        await TestHelpers.AssertDatabaseStateAfterMigrations(DatabaseType.SQLite, _connectionString);
        
        // Cleanup
        TestHelpers.CleanupTestMigrations(migrationsDir);
    }

    [Fact]
    public async Task ExecuteMigrationsAsync_SQLite_RunsInterleavedMigrationsInOrder()
    {
        // Arrange
        const string testId = "sqlite_interleaved";
        // Pass the database type to the helper
        var migrationsDir = TestHelpers.PrepareInterleavedMigrations(testId);
        var logger = _loggerFactory.CreateLogger<MigrationService>();
        var migrationService = new MigrationService(logger);

        // Act
        await Should.NotThrowAsync(async () =>
        {
            await migrationService.ExecuteMigrationsAsync(DatabaseType.SQLite, _connectionString, migrationsDir);
        });

        // Assert
        // This helper will need to check the specific state resulting from the interleaved migrations
        await TestHelpers.AssertDatabaseStateAfterInterleavedMigrations(DatabaseType.SQLite, _connectionString);
        // This helper will need to verify the VersionInfo table entries and order
        await TestHelpers.AssertVersionInfoOrder(DatabaseType.SQLite, _connectionString,
            TestHelpers.GetExpectedInterleavedVersions());

        // Cleanup
        TestHelpers.CleanupTestMigrations(migrationsDir); // Can reuse cleanup
    }

    [Fact]
    public async Task ExecuteMigrationsAsync_SQLite_HaltsAndRollsBackOnFailure()
    {
        // Arrange
        var testId = "sqlite_failure";
        // Expected successful: C# 1000, SQL 1001, C# 1002
        var expectedSuccessfulVersions = new List<long> { 202504091000, 202504091001, 202504091002 };
        var faultyMigrationVersion = 202504091003; // This one will fail
        var skippedCSharpMigrationVersion = 202504091004; // Should be skipped
        var skippedSqlMigrationVersion = 202504091005; // Should be skipped

        // Prepare migrations including one designed to fail at timestamp 1003
        var migrationsDir =
            TestHelpers.PrepareMigrationsWithFailure(testId, faultyMigrationVersion, skippedSqlMigrationVersion);
        var logger = _loggerFactory.CreateLogger<MigrationService>();
        var migrationService = new MigrationService(logger);
        var logFilePath = Path.Combine(Directory.GetCurrentDirectory(), "logs", "migration-error.log");
        if (File.Exists(logFilePath)) File.Delete(logFilePath); // Clear log before test

        // Act
        var exception = await Should.ThrowAsync<Exception>(async () =>
        {
            await migrationService.ExecuteMigrationsAsync(DatabaseType.SQLite, _connectionString, migrationsDir);
        });

        // Assert Exception
        exception.ShouldNotBeNull();
        exception.Message.ShouldContain($"CRITICAL ERROR applying SQL migration {faultyMigrationVersion}");
        exception.Message.ShouldContain("Halting execution");

        // Assert Database State (only migrations *before* failure should be applied)
        await TestHelpers.AssertVersionInfoOrder(DatabaseType.SQLite, _connectionString, expectedSuccessfulVersions);
        // Verify the faulty and subsequent migrations are NOT in the version table
        var appliedVersions = await TestHelpers.GetAppliedVersionsAsync(DatabaseType.SQLite, _connectionString);
        appliedVersions.ShouldNotContain(faultyMigrationVersion);
        appliedVersions.ShouldNotContain(skippedCSharpMigrationVersion); // Check skipped C#
        appliedVersions.ShouldNotContain(skippedSqlMigrationVersion); // Check skipped SQL
        // Verify that schema/data changes from the *skipped* migration did not occur
        await TestHelpers.AssertDataFromSkippedMigrationNotPresent(DatabaseType.SQLite, _connectionString,
            skippedSqlMigrationVersion); // Pass skipped version

        // Assert Log File (Optional but good)
        File.Exists(logFilePath).ShouldBeTrue("Error log file should exist.");
        var logContent = await File.ReadAllTextAsync(logFilePath);
        logContent.ShouldContain($"CRITICAL ERROR applying SQL migration {faultyMigrationVersion}");
        logContent.ShouldContain("Migration process stopped.");
        logContent.ShouldContain("Underlying Exception:"); // Check for the original exception details

        // Cleanup
        TestHelpers.CleanupTestMigrations(migrationsDir);
        if (File.Exists(logFilePath)) File.Delete(logFilePath); // Clean up log file
    }
}