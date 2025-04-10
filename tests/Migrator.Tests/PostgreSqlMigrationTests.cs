using DotNet.Testcontainers.Builders; // Add for Builders
using DotNet.Testcontainers.Containers; // Add for IContainer
using Microsoft.Extensions.Logging;
using Migrator.Core;
using Migrator.Tests.Base;
using Testcontainers.PostgreSql; // Add for PostgreSqlBuilder
using Xunit.Abstractions;

namespace Migrator.Tests;

// Remove IClassFixture, Inherit from non-generic MigrationTestBase
public class PostgreSqlMigrationTests : MigrationTestBase
{
    // Constructor now only takes ITestOutputHelper
    public PostgreSqlMigrationTests(ITestOutputHelper outputHelper)
        : base(outputHelper) // Pass output helper to base
    { }

    // Implement abstract methods from base class
    protected override DatabaseType GetTestDatabaseType() => DatabaseType.PostgreSql;

    protected override IContainer BuildTestContainer()
    {
        // Configure and build the PostgreSQL container for each test
        // Use unique names/ports if running tests in parallel (though collection should prevent this)
        return new PostgreSqlBuilder()
            .WithImage("postgres:latest") // Use a specific version in real scenarios
            .WithDatabase("test_db")
            .WithUsername("test_user")
            .WithPassword("test_password")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(5432))
            .Build();
    }

    // Example Test Method (Adapt your existing tests)
    [Fact]
    public async Task Test_PostgreSql_AppliesMigrationsSuccessfully()
    {
        // Arrange
        var dbType = GetTestDatabaseType(); // Use helper
        var connectionString = GetConnectionString(); // Use helper
        var migrationsPath = GetTestSpecificMigrationsPath("BasicRun", dbType);
        long migrationVersionToTest = 202504091000; // Corrected version

        // Prepare DLL (using INHERITED helper)
        await PrepareCSharpMigrationDll(migrationsPath);

        // Act
        await RunMigrationsAsync(dbType, connectionString, migrationsPath);
        // Assert
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
        long version1 = 202504091000; // Corrected version
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
        long csharpVersion1 = 202504091000; // Corrected version
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
        var initialExpectedVersions = new[] { sqlVersion1, 202504091000L, 202504091002L, 202504091004L }; // Corrected version (202504091000L)

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

        // --> Assert for the new Warning log <--
        AssertLogContainsSubstring(LogLevel.Warning, $"Applying out-of-order migration: Version {sqlVersion1} is being applied after a higher version {csharpVersion3} has already been applied.");

        // Assert all migrations are now applied
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
        await PrepareSqlFile(migrationsPath, versionFail, "CREATE TABLE Fail;;"); // Intentionally invalid SQL syntax for PG
        await PrepareSqlFile(migrationsPath, versionNext, "-- SQL VNext (PG)\nCREATE TABLE \"SqlFailTestNextPg\" (id INT);");
        await PrepareCSharpMigrationDll(migrationsPath); // Add C# migrations to ensure interleaving check

        OutputHelper.WriteLine("Running migration expected to fail...");
        ClearCapturedLogs();

        // Act & Assert Exception
        var exception = await Assert.ThrowsAsync<Exception>(async () => await RunMigrationsAsync(dbType, connectionString, migrationsPath));

        // Assert Rollback Behavior (only failed script rolls back, process halts)
        OutputHelper.WriteLine($"Caught expected exception: {exception.Message}");

        // Check that migrations *before* the failure WERE applied and committed
        await AssertMigrationAppliedAsync(dbType, connectionString, migrationsPath, 202504091000L); // C# V0
        await AssertMigrationAppliedAsync(dbType, connectionString, migrationsPath, 202504091002L); // C# V2
        await AssertMigrationAppliedAsync(dbType, connectionString, migrationsPath, 202504091004L); // C# V4
        await AssertMigrationAppliedAsync(dbType, connectionString, migrationsPath, versionOk);      // SQL VOk
        await AssertTableExistsAsync(dbType, connectionString, "Users");
        await AssertTableExistsAsync(dbType, connectionString, "Settings");
        await AssertTableExistsAsync(dbType, connectionString, "Products");
        await AssertTableExistsAsync(dbType, connectionString, "SqlFailTestOkPg");

        // Check that the FAILING migration was NOT applied (rolled back)
        await AssertMigrationNotAppliedAsync(dbType, connectionString, migrationsPath, versionFail);
        await AssertTableDoesNotExistAsync(dbType, connectionString, "Fail"); // Assuming table name would be Fail

        // Check that migrations *after* the failure were NOT applied (process halted)
        await AssertMigrationNotAppliedAsync(dbType, connectionString, migrationsPath, versionNext); // SQL VNext
        await AssertTableDoesNotExistAsync(dbType, connectionString, "SqlFailTestNextPg");
        // C# migrations were applied *before* the halt
        // await AssertMigrationNotAppliedAsync(dbType, connectionString, migrationsPath, 202504091002L); // C# V2 - Should be applied
        // await AssertTableDoesNotExistAsync(dbType, connectionString, "Settings");
        // await AssertMigrationNotAppliedAsync(dbType, connectionString, migrationsPath, 202504091004L); // C# V4 - Should be applied
        // await AssertTableDoesNotExistAsync(dbType, connectionString, "Products");

        // Verify VersionInfo count reflects only successful migrations before halt
        var finalCount = await GetVersionInfoRowCountAsync(dbType, connectionString, migrationsPath);
        Assert.Equal(4, finalCount); // C# V0, V2, V4, SQL VOk applied before halt

        // Verify logs
        AssertLogContainsSubstring(LogLevel.Error, $"migration {versionFail}"); // Check error log contains failing version
        AssertLogContainsSubstring(LogLevel.Error, "Rolling back transaction"); // Check handler logs rollback attempt
        // Cannot check V2/V4 logs as they ran successfully before the error log check
        AssertLogDoesNotContainSubstring(LogLevel.Information, $"Applying SQL migration: {versionNext}"); // Check next SQL migration didn't start
    }
}
