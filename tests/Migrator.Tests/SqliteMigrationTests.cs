using DotNet.Testcontainers.Containers;
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

    [Fact]
    public async Task SQLite_Applies_SQL_Only_Migrations()
    {
        var dbType = GetTestDatabaseType();
        var connectionString = GetConnectionString();
        var migrationsPath = GetTestSpecificMigrationsPath("SqlOnly_Lite", dbType);
        long version1 = 202505011000;
        long version2 = 202505011001;

        await PrepareSqlFile(migrationsPath, version1, "-- SQL Migration 1 (Lite)\nCREATE TABLE \"SqlOnlyTest1Lite\" (id INTEGER PRIMARY KEY);");
        await PrepareSqlFile(migrationsPath, version2, "-- SQL Migration 2 (Lite)\nCREATE TABLE \"SqlOnlyTest2Lite\" (name TEXT);");

        await RunMigrationsAsync(dbType, connectionString, migrationsPath);

        await AssertMigrationAppliedAsync(dbType, connectionString, migrationsPath, version1);
        await AssertMigrationAppliedAsync(dbType, connectionString, migrationsPath, version2);
        await AssertTableExistsAsync(dbType, connectionString, "SqlOnlyTest1Lite");
        await AssertTableExistsAsync(dbType, connectionString, "SqlOnlyTest2Lite");
        var count = await GetVersionInfoRowCountAsync(dbType, connectionString, migrationsPath);
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task SQLite_Applies_Interleaved_Migrations_Correctly()
    {
        var dbType = GetTestDatabaseType();
        var connectionString = GetConnectionString();
        var migrationsPath = GetTestSpecificMigrationsPath("Interleaved_Lite", dbType);
        long sqlVersion1 = 202504091001L;
        long csharpVersion1 = 202504091000L;
        long sqlVersion2 = 202504091003L;
        long csharpVersion2 = 202504091002L;
        long csharpVersion3 = 202504091004L;
        var expectedVersions = new[] { csharpVersion1, sqlVersion1, csharpVersion2, sqlVersion2, csharpVersion3 };

        await PrepareSqlFile(migrationsPath, sqlVersion1, "-- SQL 1 (Lite) Interleaved\nCREATE TABLE \"SqlInterleaved1Lite\" (Id INTEGER);");
        await PrepareSqlFile(migrationsPath, sqlVersion2, "-- SQL 2 (Lite) Interleaved\nCREATE TABLE \"SqlInterleaved2Lite\" (Data TEXT);");
        await PrepareCSharpMigrationDll(migrationsPath);

        await RunMigrationsAsync(dbType, connectionString, migrationsPath);

        foreach (var version in expectedVersions.OrderBy(v => v))
        {
            await AssertMigrationAppliedAsync(dbType, connectionString, migrationsPath, version);
        }

        await AssertTableExistsAsync(dbType, connectionString, "Users");
        await AssertTableExistsAsync(dbType, connectionString, "Settings");
        await AssertTableExistsAsync(dbType, connectionString, "Products");
        await AssertTableExistsAsync(dbType, connectionString, "SqlInterleaved1Lite");
        await AssertTableExistsAsync(dbType, connectionString, "SqlInterleaved2Lite");

        var count = await GetVersionInfoRowCountAsync(dbType, connectionString, migrationsPath);
        Assert.Equal(expectedVersions.Length, count);
    }

    [Fact]
    public async Task SQLite_Does_Not_ReApply_Already_Applied_Migrations()
    {
        var dbType = GetTestDatabaseType();
        var connectionString = GetConnectionString();
        var migrationsPath = GetTestSpecificMigrationsPath("AlreadyApplied_Lite", dbType);
        long sqlVersion1 = 202505041000L;
        var initialExpectedVersions = new[] { sqlVersion1, 202504091000L, 202504091002L, 202504091004L };

        await PrepareSqlFile(migrationsPath, sqlVersion1, "-- SQL V1 (Lite) AlreadyApplied\nCREATE TABLE \"SqlAlreadyApplied1Lite\" (Id INTEGER);");
        await PrepareCSharpMigrationDll(migrationsPath);

        OutputHelper.WriteLine("First migration run...");
        await RunMigrationsAsync(dbType, connectionString, migrationsPath);

        foreach (var v in initialExpectedVersions.OrderBy(v => v))
        {
            await AssertMigrationAppliedAsync(dbType, connectionString, migrationsPath, v);
        }
        await AssertTableExistsAsync(dbType, connectionString, "SqlAlreadyApplied1Lite");
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
    public async Task SQLite_Applies_OutOfOrder_Migration_If_Not_Applied()
    {
        var dbType = GetTestDatabaseType();
        var connectionString = GetConnectionString();
        var migrationsPath = GetTestSpecificMigrationsPath("OutOfOrder_Lite", dbType);
        long sqlVersion2 = 202504091003L;
        long csharpVersion3 = 202504091004L;
        long sqlVersion1 = 202504091001L;

        await PrepareSqlFile(migrationsPath, sqlVersion2, "-- SQL V2 (Lite) OutOfOrder\nCREATE TABLE \"SqlOutOfOrder2Lite\" (Id INTEGER);");
        await PrepareCSharpMigrationDll(migrationsPath);

        OutputHelper.WriteLine("First run (SQL V2, C# V0, V2, V4)...");
        await RunMigrationsAsync(dbType, connectionString, migrationsPath);

        await AssertMigrationAppliedAsync(dbType, connectionString, migrationsPath, 202504091000L);
        await AssertMigrationAppliedAsync(dbType, connectionString, migrationsPath, 202504091002L);
        await AssertMigrationAppliedAsync(dbType, connectionString, migrationsPath, csharpVersion3);
        await AssertMigrationAppliedAsync(dbType, connectionString, migrationsPath, sqlVersion2);
        await AssertMigrationNotAppliedAsync(dbType, connectionString, migrationsPath, sqlVersion1);
        await AssertTableExistsAsync(dbType, connectionString, "Users");
        await AssertTableExistsAsync(dbType, connectionString, "Settings");
        await AssertTableExistsAsync(dbType, connectionString, "Products");
        await AssertTableExistsAsync(dbType, connectionString, "SqlOutOfOrder2Lite");
        var initialCount = await GetVersionInfoRowCountAsync(dbType, connectionString, migrationsPath);
        Assert.Equal(4, initialCount);

        await PrepareSqlFile(migrationsPath, sqlVersion1, "-- SQL V1 (Lite - Out Of Order)\nCREATE TABLE \"SqlOutOfOrder1Lite\" (Name TEXT);");

        OutputHelper.WriteLine("Second run (adding SQL V1)...");
        ClearCapturedLogs();
        await RunMigrationsAsync(dbType, connectionString, migrationsPath);

        AssertLogContainsSubstring(LogLevel.Warning, $"Applying out-of-order migration: Version {sqlVersion1} is being applied after a higher version {csharpVersion3} has already been applied.");

        await AssertMigrationAppliedAsync(dbType, connectionString, migrationsPath, sqlVersion1);
        await AssertMigrationAppliedAsync(dbType, connectionString, migrationsPath, 202504091000L);
        await AssertMigrationAppliedAsync(dbType, connectionString, migrationsPath, 202504091002L);
        await AssertMigrationAppliedAsync(dbType, connectionString, migrationsPath, csharpVersion3);
        await AssertMigrationAppliedAsync(dbType, connectionString, migrationsPath, sqlVersion2);
        await AssertTableExistsAsync(dbType, connectionString, "SqlOutOfOrder1Lite");
        var finalCount = await GetVersionInfoRowCountAsync(dbType, connectionString, migrationsPath);
        Assert.Equal(5, finalCount);
        AssertLogContainsSubstring(LogLevel.Information, $"Applying SQL migration: {sqlVersion1}");
    }

    [Fact]
    public async Task SQLite_Halts_And_RollsBack_On_Failure()
    {
        var dbType = GetTestDatabaseType();
        var connectionString = GetConnectionString();
        var migrationsPath = GetTestSpecificMigrationsPath("Failure_Lite", dbType);
        long versionOk = 202505061000L;      // SQL OK
        long versionFail = 202505061001L;    // SQL Fail
        long versionNext = 202505061002L;    // SQL Next (Should not run)
        long csharpV0 = 202504091000L;
        long csharpV2 = 202504091002L;
        long csharpV4 = 202504091004L;

        // Prepare migrations in execution order: C# V0, SQL OK, SQL Fail, C# V2, SQL Next, C# V4
        await PrepareCSharpMigrationDll(migrationsPath); // Contains V0, V2, V4
        await PrepareSqlFile(migrationsPath, versionOk, "-- SQL VOk (Lite)\nCREATE TABLE \"SqlFailTestOkLite\" (id INTEGER);");
        await PrepareSqlFile(migrationsPath, versionFail, "CREATE TABLE Fail;;"); // Invalid syntax for SQLite
        await PrepareSqlFile(migrationsPath, versionNext, "-- SQL VNext (Lite)\nCREATE TABLE \"SqlFailTestNextLite\" (id INTEGER);");

        OutputHelper.WriteLine("Running migration expected to fail...");
        ClearCapturedLogs();

        var exception = await Assert.ThrowsAsync<Exception>(async () => await RunMigrationsAsync(dbType, connectionString, migrationsPath));
        OutputHelper.WriteLine($"Caught expected exception: {exception.Message}");

        // Assert migrations *before* failure WERE applied
        await AssertMigrationAppliedAsync(dbType, connectionString, migrationsPath, csharpV0);
        await AssertMigrationAppliedAsync(dbType, connectionString, migrationsPath, csharpV2);
        await AssertMigrationAppliedAsync(dbType, connectionString, migrationsPath, csharpV4);
        await AssertMigrationAppliedAsync(dbType, connectionString, migrationsPath, versionOk);
        await AssertTableExistsAsync(dbType, connectionString, "Users");
        await AssertTableExistsAsync(dbType, connectionString, "Settings");
        await AssertTableExistsAsync(dbType, connectionString, "Products");
        await AssertTableExistsAsync(dbType, connectionString, "SqlFailTestOkLite");

        // Assert FAILING migration was NOT applied (rolled back)
        await AssertMigrationNotAppliedAsync(dbType, connectionString, migrationsPath, versionFail);
        await AssertTableDoesNotExistAsync(dbType, connectionString, "Fail"); // Table from failing script

        // Assert migrations *after* failure were NOT applied (halted)
        await AssertMigrationNotAppliedAsync(dbType, connectionString, migrationsPath, versionNext);
        await AssertTableDoesNotExistAsync(dbType, connectionString, "SqlFailTestNextLite"); // From SQL VNext

        // Verify VersionInfo count reflects only successful migrations before halt
        var finalCount = await GetVersionInfoRowCountAsync(dbType, connectionString, migrationsPath);
        Assert.Equal(4, finalCount);

        // Verify logs
        AssertLogContainsSubstring(LogLevel.Error, $"migration {versionFail}");
        AssertLogContainsSubstring(LogLevel.Error, "Rolling back transaction");
        AssertLogContainsSubstring(LogLevel.Information, $"Applying C# migration: {csharpV2}");
        AssertLogContainsSubstring(LogLevel.Information, $"Applying C# migration: {csharpV4}");
        AssertLogDoesNotContainSubstring(LogLevel.Information, $"Applying SQL migration: {versionNext}");
    }

    [Fact]
    public async Task Sqlite_Runs_Successfully_With_Empty_Folder()
    {
        var dbType = GetTestDatabaseType();
        var connectionString = GetConnectionString();
        var migrationsPath = GetTestSpecificMigrationsPath("EmptyFolder_Lite", dbType);
        // Path is created empty by GetTestSpecificMigrationsPath

        ClearCapturedLogs();
        await RunMigrationsAsync(dbType, connectionString, migrationsPath);

        // Assert no migrations were applied
        var count = await GetVersionInfoRowCountAsync(dbType, connectionString, migrationsPath);
        Assert.Equal(0, count);

        // Assert warnings were logged
        AssertLogContainsSubstring(LogLevel.Warning, "No assemblies containing migrations found");
        AssertLogContainsSubstring(LogLevel.Information, "SQL file scan complete. Discovered 0 SQL migration tasks");
        AssertLogContainsSubstring(LogLevel.Warning, "No migration jobs (C# or SQL) found to apply");
        AssertLogContainsSubstring(LogLevel.Information, "Migration process completed successfully");
    }

    [Fact]
    public async Task Sqlite_Throws_Exception_For_NonExistent_Folder()
    {
        var dbType = GetTestDatabaseType();
        var connectionString = GetConnectionString(); // Connection might not even be needed, but setup requires it
        var nonExistentPath = Path.Combine(BaseMigrationsPath, $"NonExistentFolder_{Guid.NewGuid()}");

        // Ensure the path does NOT exist
        if (Directory.Exists(nonExistentPath))
        {
            Directory.Delete(nonExistentPath, true);
        }

        ClearCapturedLogs();
        var exception = await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            RunMigrationsAsync(dbType, connectionString, nonExistentPath)
        );

        Assert.Contains(nonExistentPath, exception.Message);
        AssertLogContainsSubstring(LogLevel.Error, "Migrations directory not found");
        AssertLogContainsSubstring(LogLevel.Error, nonExistentPath);
        // Ensure migration process did not complete successfully
        AssertLogDoesNotContainSubstring(LogLevel.Information, "Migration process completed successfully");
    }
}
