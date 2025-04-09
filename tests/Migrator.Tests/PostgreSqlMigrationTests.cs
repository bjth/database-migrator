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

        await Should.NotThrowAsync(async () =>
        {
            await migrationService.ExecuteMigrationsAsync(DatabaseType.PostgreSql, ConnectionString!,
                migrationsDir);
        });

        await TestHelpers.AssertDatabaseStateAfterMigrations(DatabaseType.PostgreSql, ConnectionString!);

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

        // Run migrations first time
        await migrationService.ExecuteMigrationsAsync(DatabaseType.PostgreSql, ConnectionString!, migrationsDir);

        // Check initial state
        await TestHelpers.AssertDatabaseStateAfterMigrations(DatabaseType.PostgreSql,
            ConnectionString!); // Expect 6 migrations applied

        // Run migrations second time
        await Should.NotThrowAsync(async () =>
        {
            await migrationService.ExecuteMigrationsAsync(DatabaseType.PostgreSql, ConnectionString!,
                migrationsDir);
        });

        // State should remain unchanged
        await TestHelpers.AssertDatabaseStateAfterMigrations(DatabaseType.PostgreSql,
            ConnectionString!); // Expect 6 migrations applied

        TestHelpers.CleanupTestMigrations(migrationsDir);
    }
}