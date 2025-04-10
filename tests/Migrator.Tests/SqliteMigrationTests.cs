using DotNet.Testcontainers.Containers;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Migrator.Core;
using Migrator.Tests.Base;
using Xunit.Abstractions;

namespace Migrator.Tests;

public class SqliteMigrationTests : MigrationTestBase
{
    public SqliteMigrationTests(ITestOutputHelper outputHelper)
        : base(outputHelper)
    { }

    protected override DatabaseType GetTestDatabaseType() => DatabaseType.SQLite;

    protected override IContainer BuildTestContainer()
    {
        return new SQLiteContainer();
    }

    [Fact]
    public async Task Test_SQLite_AppliesMigrationsSuccessfully()
    {
        var dbType = GetTestDatabaseType();
        var connectionString = GetConnectionString();
        var migrationsPath = GetTestSpecificMigrationsPath("BasicRun_SQLite", dbType);

        await PrepareCSharpMigrationDll(migrationsPath);

        await RunMigrationsAsync(dbType, connectionString, migrationsPath);

        await AssertMigrationAppliedAsync(dbType, connectionString, migrationsPath, 202504091000L);
        await AssertMigrationAppliedAsync(dbType, connectionString, migrationsPath, 202504091002L);
        await AssertMigrationAppliedAsync(dbType, connectionString, migrationsPath, 202504091004L);

        await AssertTableExistsAsync(dbType, connectionString, "Users");
        await AssertTableExistsAsync(dbType, connectionString, "Settings");
        await AssertTableExistsAsync(dbType, connectionString, "Products");

        var finalCount = await GetVersionInfoRowCountAsync(dbType, connectionString, migrationsPath);
        Assert.Equal(3, finalCount);
    }
}
