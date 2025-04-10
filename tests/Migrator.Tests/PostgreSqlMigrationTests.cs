using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Logging;
using Migrator.Core;
using Migrator.Tests.Base;
using Testcontainers.PostgreSql;
using Xunit.Abstractions;

namespace Migrator.Tests;

public class PostgreSqlMigrationTests : MigrationTestBase
{
    public PostgreSqlMigrationTests(ITestOutputHelper outputHelper)
        : base(outputHelper)
    { }

    protected override DatabaseType GetTestDatabaseType() => DatabaseType.PostgreSql;

    protected override IContainer BuildTestContainer()
    {
        return new PostgreSqlBuilder()
            .WithImage("postgres:latest")
            .WithDatabase("test_db")
            .WithUsername("test_user")
            .WithPassword("test_password")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(5432))
            .Build();
    }

    [Fact]
    public async Task Test_PostgreSql_AppliesMigrationsSuccessfully()
    {
        var dbType = GetTestDatabaseType();
        var connectionString = GetConnectionString();
        var migrationsPath = GetTestSpecificMigrationsPath("BasicRun", dbType);
        long migrationVersionToTest = 202504091000;

        await PrepareCSharpMigrationDll(migrationsPath);

        await RunMigrationsAsync(dbType, connectionString, migrationsPath);
        await AssertMigrationAppliedAsync(dbType, connectionString, migrationsPath, migrationVersionToTest);
        await AssertTableExistsAsync(dbType, connectionString, "Users");
    }

    [Fact]
    public async Task PostgreSQL_Applies_SQL_Only_Migrations()
    {
        var dbType = GetTestDatabaseType();
        var connectionString = GetConnectionString();
        var migrationsPath = GetTestSpecificMigrationsPath("SqlOnly", dbType);
        long version1 = 202505011000;
        long version2 = 202505011001;

        await PrepareSqlFile(migrationsPath, version1, "-- SQL Migration 1 (PG)\nCREATE TABLE \"SqlOnlyTest1Pg\" (id INT PRIMARY KEY);");
        await PrepareSqlFile(migrationsPath, version2, "-- SQL Migration 2 (PG)\nCREATE TABLE \"SqlOnlyTest2Pg\" (name TEXT);");

        await RunMigrationsAsync(dbType, connectionString, migrationsPath);

        await AssertMigrationAppliedAsync(dbType, connectionString, migrationsPath, version1);
        await AssertMigrationAppliedAsync(dbType, connectionString, migrationsPath, version2);
        await AssertTableExistsAsync(dbType, connectionString, "SqlOnlyTest1Pg");
        await AssertTableExistsAsync(dbType, connectionString, "SqlOnlyTest2Pg");
        var count = await GetVersionInfoRowCountAsync(dbType, connectionString, migrationsPath);
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task PostgreSQL_Applies_CSharp_Only_Migrations()
    {
        var dbType = GetTestDatabaseType();
        var connectionString = GetConnectionString();
        var migrationsPath = GetTestSpecificMigrationsPath("CSharpOnly", dbType);
        long version1 = 202504091000;
        long version2 = 202504091002;
        long version3 = 202504091004;

        await PrepareCSharpMigrationDll(migrationsPath);

        await RunMigrationsAsync(dbType, connectionString, migrationsPath);

        await AssertMigrationAppliedAsync(dbType, connectionString, migrationsPath, version1);
        await AssertMigrationAppliedAsync(dbType, connectionString, migrationsPath, version2);
        await AssertMigrationAppliedAsync(dbType, connectionString, migrationsPath, version3);

        await AssertTableExistsAsync(dbType, connectionString, "Users");
        await AssertTableExistsAsync(dbType, connectionString, "Settings");
        await AssertTableExistsAsync(dbType, connectionString, "Products");

        var count = await GetVersionInfoRowCountAsync(dbType, connectionString, migrationsPath);
        Assert.Equal(3, count);
    }

    [Fact]
    public async Task PostgreSQL_Applies_Interleaved_Migrations_Correctly()
    {
        var dbType = GetTestDatabaseType();
        var connectionString = GetConnectionString();
        var migrationsPath = GetTestSpecificMigrationsPath("Interleaved", dbType);
        long sqlVersion1 = 202504091001;
        long csharpVersion1 = 202504091000;
        long sqlVersion2 = 202504091003;
        long csharpVersion2 = 202504091002;
        long csharpVersion3 = 202504091004;
        var expectedVersions = new[] { csharpVersion1, sqlVersion1, csharpVersion2, sqlVersion2, csharpVersion3 };

        await PrepareSqlFile(migrationsPath, sqlVersion1, "-- SQL 1 (PG) Interleaved\nCREATE TABLE \"SqlInterleaved1Pg\" (id INT);");
        await PrepareSqlFile(migrationsPath, sqlVersion2, "-- SQL 2 (PG) Interleaved\nCREATE TABLE \"SqlInterleaved2Pg\" (data TEXT);");
        await PrepareCSharpMigrationDll(migrationsPath);

        await RunMigrationsAsync(dbType, connectionString, migrationsPath);

        foreach (var version in expectedVersions.OrderBy(v => v))
        {
            await AssertMigrationAppliedAsync(dbType, connectionString, migrationsPath, version);
        }

        await AssertTableExistsAsync(dbType, connectionString, "Users");
        await AssertTableExistsAsync(dbType, connectionString, "Settings");
        await AssertTableExistsAsync(dbType, connectionString, "Products");
        await AssertTableExistsAsync(dbType, connectionString, "SqlInterleaved1Pg");
        await AssertTableExistsAsync(dbType, connectionString, "SqlInterleaved2Pg");

        var count = await GetVersionInfoRowCountAsync(dbType, connectionString, migrationsPath);
        Assert.Equal(expectedVersions.Length, count);
    }

    [Fact]
    public async Task PostgreSQL_Does_Not_ReApply_Already_Applied_Migrations()
    {
        var dbType = GetTestDatabaseType();
        var connectionString = GetConnectionString();
        var migrationsPath = GetTestSpecificMigrationsPath("AlreadyApplied", dbType);
        long sqlVersion1 = 202505041000;
        var initialExpectedVersions = new[] { sqlVersion1, 202504091000L, 202504091002L, 202504091004L };

        await PrepareSqlFile(migrationsPath, sqlVersion1, "-- SQL V1 (PG) AlreadyApplied\nCREATE TABLE \"SqlAlreadyApplied1Pg\" (id INT);");
        await PrepareCSharpMigrationDll(migrationsPath);

        OutputHelper.WriteLine("First migration run...");
        await RunMigrationsAsync(dbType, connectionString, migrationsPath);

        foreach (var v in initialExpectedVersions.OrderBy(v => v))
        {
            await AssertMigrationAppliedAsync(dbType, connectionString, migrationsPath, v);
        }
        await AssertTableExistsAsync(dbType, connectionString, "SqlAlreadyApplied1Pg");
        await AssertTableExistsAsync(dbType, connectionString, "Users");
        await AssertTableExistsAsync(dbType, connectionString, "Settings");
        await AssertTableExistsAsync(dbType, connectionString, "Products");
        var initialCount = await GetVersionInfoRowCountAsync(dbType, connectionString, migrationsPath);
        Assert.Equal(initialExpectedVersions.Length, initialCount);

        OutputHelper.WriteLine("Second migration run...");
        ClearCapturedLogs();
        await RunMigrationsAsync(dbType, connectionString, migrationsPath);

        foreach (var v in initialExpectedVersions.OrderBy(v => v))
        {
            await AssertMigrationAppliedAsync(dbType, connectionString, migrationsPath, v);
        }
        var finalCount = await GetVersionInfoRowCountAsync(dbType, connectionString, migrationsPath);
        Assert.Equal(initialExpectedVersions.Length, finalCount);
        foreach (var v in initialExpectedVersions)
        {
            AssertLogContainsSubstring(LogLevel.Information, $"Skipping already applied migration (from previous run): {v}");
        }
    }

    [Fact]
    public async Task PostgreSQL_Applies_OutOfOrder_Migration_If_Not_Applied()
    {
        var dbType = GetTestDatabaseType();
        var connectionString = GetConnectionString();
        var migrationsPath = GetTestSpecificMigrationsPath("OutOfOrder", dbType);
        long sqlVersion2 = 202504091003;
        long csharpVersion3 = 202504091004;
        long sqlVersion1 = 202504091001;

        await PrepareSqlFile(migrationsPath, sqlVersion2, "-- SQL V2 (PG) OutOfOrder\nCREATE TABLE \"SqlOutOfOrder2Pg\" (id INT);");
        await PrepareCSharpMigrationDll(migrationsPath);

        OutputHelper.WriteLine("First run (SQL V2, C# V0, V2, V4)...");
        await RunMigrationsAsync(dbType, connectionString, migrationsPath);

        await AssertMigrationAppliedAsync(dbType, connectionString, migrationsPath, 202504091000);
        await AssertMigrationAppliedAsync(dbType, connectionString, migrationsPath, 202504091002);
        await AssertMigrationAppliedAsync(dbType, connectionString, migrationsPath, csharpVersion3);
        await AssertMigrationAppliedAsync(dbType, connectionString, migrationsPath, sqlVersion2);
        await AssertMigrationNotAppliedAsync(dbType, connectionString, migrationsPath, sqlVersion1);
        await AssertTableExistsAsync(dbType, connectionString, "Users");
        await AssertTableExistsAsync(dbType, connectionString, "Settings");
        await AssertTableExistsAsync(dbType, connectionString, "Products");
        await AssertTableExistsAsync(dbType, connectionString, "SqlOutOfOrder2Pg");
        var initialCount = await GetVersionInfoRowCountAsync(dbType, connectionString, migrationsPath);
        Assert.Equal(4, initialCount);

        await PrepareSqlFile(migrationsPath, sqlVersion1, "-- SQL V1 (PG - Out Of Order)\nCREATE TABLE \"SqlOutOfOrder1Pg\" (name TEXT);");

        OutputHelper.WriteLine("Second run (adding SQL V1)...");
        ClearCapturedLogs();
        await RunMigrationsAsync(dbType, connectionString, migrationsPath);

        AssertLogContainsSubstring(LogLevel.Warning, $"Applying out-of-order migration: Version {sqlVersion1} is being applied after a higher version {csharpVersion3} has already been applied.");

        await AssertMigrationAppliedAsync(dbType, connectionString, migrationsPath, sqlVersion1);
        await AssertMigrationAppliedAsync(dbType, connectionString, migrationsPath, 202504091000);
        await AssertMigrationAppliedAsync(dbType, connectionString, migrationsPath, 202504091002);
        await AssertMigrationAppliedAsync(dbType, connectionString, migrationsPath, csharpVersion3);
        await AssertMigrationAppliedAsync(dbType, connectionString, migrationsPath, sqlVersion2);

        await AssertTableExistsAsync(dbType, connectionString, "SqlOutOfOrder1Pg");
        var finalCount = await GetVersionInfoRowCountAsync(dbType, connectionString, migrationsPath);
        Assert.Equal(5, finalCount);
        AssertLogContainsSubstring(LogLevel.Information, $"Applying SQL migration: {sqlVersion1}");
    }

    [Fact]
    public async Task PostgreSQL_Halts_And_RollsBack_On_Failure()
    {
        var dbType = GetTestDatabaseType();
        var connectionString = GetConnectionString();
        var migrationsPath = GetTestSpecificMigrationsPath("Failure", dbType);
        long versionOk = 202505061000;
        long versionFail = 202505061001;
        long versionNext = 202505061002;

        await PrepareSqlFile(migrationsPath, versionOk, "-- SQL VOk (PG)\nCREATE TABLE \"SqlFailTestOkPg\" (id INT);");
        await PrepareSqlFile(migrationsPath, versionFail, "CREATE TABLE Fail;;");
        await PrepareSqlFile(migrationsPath, versionNext, "-- SQL VNext (PG)\nCREATE TABLE \"SqlFailTestNextPg\" (id INT);");
        await PrepareCSharpMigrationDll(migrationsPath);

        OutputHelper.WriteLine("Running migration expected to fail...");
        ClearCapturedLogs();

        var exception = await Assert.ThrowsAsync<Exception>(async () => await RunMigrationsAsync(dbType, connectionString, migrationsPath));

        OutputHelper.WriteLine($"Caught expected exception: {exception.Message}");

        await AssertMigrationAppliedAsync(dbType, connectionString, migrationsPath, 202504091000L);
        await AssertMigrationAppliedAsync(dbType, connectionString, migrationsPath, 202504091002L);
        await AssertMigrationAppliedAsync(dbType, connectionString, migrationsPath, 202504091004L);
        await AssertMigrationAppliedAsync(dbType, connectionString, migrationsPath, versionOk);
        await AssertTableExistsAsync(dbType, connectionString, "Users");
        await AssertTableExistsAsync(dbType, connectionString, "Settings");
        await AssertTableExistsAsync(dbType, connectionString, "Products");
        await AssertTableExistsAsync(dbType, connectionString, "SqlFailTestOkPg");

        await AssertMigrationNotAppliedAsync(dbType, connectionString, migrationsPath, versionFail);
        await AssertTableDoesNotExistAsync(dbType, connectionString, "Fail");

        await AssertMigrationNotAppliedAsync(dbType, connectionString, migrationsPath, versionNext);
        await AssertTableDoesNotExistAsync(dbType, connectionString, "SqlFailTestNextPg");

        var finalCount = await GetVersionInfoRowCountAsync(dbType, connectionString, migrationsPath);
        Assert.Equal(4, finalCount);

        AssertLogContainsSubstring(LogLevel.Error, $"migration {versionFail}");
        AssertLogContainsSubstring(LogLevel.Error, "Rolling back transaction");
        AssertLogDoesNotContainSubstring(LogLevel.Information, $"Applying SQL migration: {versionNext}");
    }
}
