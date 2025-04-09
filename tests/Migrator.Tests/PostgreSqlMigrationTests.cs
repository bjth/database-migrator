using Microsoft.Extensions.Logging;
using Migrator.Core;
using Shouldly;
using Xunit.Abstractions;

namespace Migrator.Tests;

public class PostgreSqlMigrationTests : IntegrationTestBase
{
    public PostgreSqlMigrationTests(ITestOutputHelper outputHelper) : base(outputHelper)
    {
    }

    protected override TestcontainerDatabase ContainerDatabase => TestcontainerDatabase.PostgreSql;

    [Fact]
    public async Task ExecuteMigrationsAsync_PostgreSql_RunsMixedMigrationsSuccessfully()
    {
        // Arrange
        var migrationsDir = TestHelpers.PrepareTestMigrations("postgres_test", DatabaseType.PostgreSql);
        var logger = LoggerFactory.CreateLogger<MigrationService>();
        var migrationService = new MigrationService(logger);

        // Act & Assert
        await Should.NotThrowAsync(async () =>
        {
            await migrationService.ExecuteMigrationsAsync(DatabaseType.PostgreSql, ConnectionString!,
                migrationsDir);
        });

        // Assert: Check database state
        await TestHelpers.AssertDatabaseStateAfterMigrations(DatabaseType.PostgreSql, ConnectionString!);

        // Cleanup
        TestHelpers.CleanupTestMigrations(migrationsDir);
    }

    [Fact]
    public async Task ExecuteMigrationsAsync_PostgreSql_HandlesAlreadyAppliedMigrations()
    {
        // Arrange
        const string testId = "postgres_rerun_test";
        var migrationsDir = TestHelpers.PrepareTestMigrations(testId, DatabaseType.PostgreSql);
        var logger = LoggerFactory.CreateLogger<MigrationService>();
        var migrationService = new MigrationService(logger);

        // Act 1: Run migrations first time
        await migrationService.ExecuteMigrationsAsync(DatabaseType.PostgreSql, ConnectionString!, migrationsDir);

        // Assert 1: Check initial state
        await TestHelpers.AssertDatabaseStateAfterMigrations(DatabaseType.PostgreSql,
            ConnectionString!); // Expect 6 migrations applied

        // Act 2: Run migrations second time
        await Should.NotThrowAsync(async () =>
        {
            await migrationService.ExecuteMigrationsAsync(DatabaseType.PostgreSql, ConnectionString!,
                migrationsDir);
        });

        // Assert 2: State should remain unchanged
        await TestHelpers.AssertDatabaseStateAfterMigrations(DatabaseType.PostgreSql,
            ConnectionString!); // Expect 6 migrations applied

        // Cleanup
        TestHelpers.CleanupTestMigrations(migrationsDir);
    }
}