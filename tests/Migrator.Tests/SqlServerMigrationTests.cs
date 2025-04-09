using Microsoft.Extensions.Logging;
using Migrator.Core;
using Shouldly;
using Xunit.Abstractions;

namespace Migrator.Tests;

public class SqlServerMigrationTests : IntegrationTestBase
{
    public SqlServerMigrationTests(ITestOutputHelper outputHelper) : base(outputHelper)
    {
    }

    protected override TestcontainerDatabase ContainerDatabase => TestcontainerDatabase.MsSql;

    [Fact]
    public async Task ExecuteMigrationsAsync_SqlServer_RunsMixedMigrationsSuccessfully()
    {
        // Arrange
        var migrationsDir = TestHelpers.PrepareTestMigrations("sqlserver_test", DatabaseType.SqlServer);
        var logger = LoggerFactory.CreateLogger<MigrationService>();
        var migrationService = new MigrationService(logger);

        await Should.NotThrowAsync(async () =>
        {
            await migrationService.ExecuteMigrationsAsync(DatabaseType.SqlServer, ConnectionString!, migrationsDir);
        });

        await TestHelpers.AssertDatabaseStateAfterMigrations(DatabaseType.SqlServer, ConnectionString!);

        TestHelpers.CleanupTestMigrations(migrationsDir);
    }

    [Fact]
    public async Task ExecuteMigrationsAsync_SqlServer_HandlesAlreadyAppliedMigrations()
    {
        // Arrange
        const string testId = "sqlserver_rerun_test";
        var migrationsDir = TestHelpers.PrepareTestMigrations(testId, DatabaseType.SqlServer);
        var logger = LoggerFactory.CreateLogger<MigrationService>();
        var migrationService = new MigrationService(logger);

        // Run migrations first time
        await migrationService.ExecuteMigrationsAsync(DatabaseType.SqlServer, ConnectionString!, migrationsDir);

        // Check initial state
        await TestHelpers.AssertDatabaseStateAfterMigrations(DatabaseType.SqlServer,
            ConnectionString!); // Expect 6 migrations applied

        // Run migrations second time
        await Should.NotThrowAsync(async () =>
        {
            await migrationService.ExecuteMigrationsAsync(DatabaseType.SqlServer, ConnectionString!, migrationsDir);
        });

        // State should remain unchanged
        await TestHelpers.AssertDatabaseStateAfterMigrations(DatabaseType.SqlServer,
            ConnectionString!); // Expect 6 migrations applied

        TestHelpers.CleanupTestMigrations(migrationsDir);
    }

    [Fact]
    public async Task ExecuteMigrationsAsync_SqlServer_RunsCSharpOnlyMigrationsSuccessfully()
    {
        // Arrange
        var migrationsDir = TestHelpers.PrepareCSharpOnlyMigrations("sqlserver_csharp_only");
        var logger = LoggerFactory.CreateLogger<MigrationService>();
        var migrationService = new MigrationService(logger);

        // Act
        await Should.NotThrowAsync(async () =>
        {
            await migrationService.ExecuteMigrationsAsync(DatabaseType.SqlServer, ConnectionString!, migrationsDir);
        });

        // Assert
        await TestHelpers.AssertDatabaseStateAfterCSharpOnlyMigrations(DatabaseType.SqlServer, ConnectionString!);

        // Cleanup
        TestHelpers.CleanupTestMigrations(migrationsDir);
    }

    [Fact]
    public async Task ExecuteMigrationsAsync_SqlServer_RunsSqlOnlyMigrationsSuccessfully()
    {
        // Arrange
        var migrationsDir = TestHelpers.PrepareSqlOnlyMigrations("sqlserver_sql_only", DatabaseType.SqlServer);
        var logger = LoggerFactory.CreateLogger<MigrationService>();
        var migrationService = new MigrationService(logger);

        // Act
        await migrationService.ExecuteMigrationsAsync(DatabaseType.SqlServer, ConnectionString!, migrationsDir);

        // Assert
        await TestHelpers.AssertDatabaseStateAfterSqlOnlyMigrations(DatabaseType.SqlServer, ConnectionString!);

        // Cleanup
        TestHelpers.CleanupTestMigrations(migrationsDir);
    }

    [Fact]
    public async Task ExecuteMigrationsAsync_SqlServer_RunsInterleavedMigrationsInOrder()
    {
        // Arrange
        const string testId = "sqlserver_interleaved";
        var migrationsDir = TestHelpers.PrepareInterleavedMigrations(testId, DatabaseType.SqlServer);
        var logger = LoggerFactory.CreateLogger<MigrationService>();
        var migrationService = new MigrationService(logger);

        // Act
        await Should.NotThrowAsync(async () =>
        {
            await migrationService.ExecuteMigrationsAsync(DatabaseType.SqlServer, ConnectionString!, migrationsDir);
        });

        // Assert
        await TestHelpers.AssertDatabaseStateAfterInterleavedMigrations(DatabaseType.SqlServer, ConnectionString!);
        await TestHelpers.AssertVersionInfoOrder(DatabaseType.SqlServer, ConnectionString!,
            TestHelpers.GetExpectedInterleavedVersions());

        // Cleanup
        TestHelpers.CleanupTestMigrations(migrationsDir);
    }
}
