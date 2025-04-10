using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Logging; // Added for LogLevel
using Migrator.Core;
using Migrator.Tests.Base; // Add using for base class namespace
using Testcontainers.MsSql; // Add for MsSqlBuilder
// using Migrator.Tests.Fixtures; // Removed
using Xunit.Abstractions;

namespace Migrator.Tests;

[Collection("MigrationTests")]
public class SqlServerMigrationTests : MigrationTestBase // Remove generic and fixture
{
    // Constructor only takes output helper
    public SqlServerMigrationTests(ITestOutputHelper outputHelper)
        : base(outputHelper)
    { }

    // Implement abstract methods
    protected override DatabaseType GetTestDatabaseType() => DatabaseType.SqlServer;

    protected override IContainer BuildTestContainer()
    {
        // Configure and build SQL Server container
        return new MsSqlBuilder()
            .WithImage("mcr.microsoft.com/mssql/server:2022-latest") // Use appropriate image
            .WithPassword("yourStrong(!)Password") // Testcontainers requires a strong password for SQL Server
            .Build();
    }

    // --- Test Methods --- (Adjust to use base class helpers correctly)
    [Fact]
    public async Task Test_SqlServer_AppliesMigrationsSuccessfully()
    {
        // Arrange
        var dbType = GetTestDatabaseType();
        var connectionString = GetConnectionString();
        var migrationsPath = GetTestSpecificMigrationsPath("BasicRun_SS", dbType);
        long migrationVersionToTest = 202504091000L;

        await PrepareCSharpMigrationDll(migrationsPath);

        // Act
        await RunMigrationsAsync(dbType, connectionString, migrationsPath);

        // Assert
        await AssertMigrationAppliedAsync(dbType, connectionString, migrationsPath, migrationVersionToTest);
        await AssertTableExistsAsync(dbType, connectionString, "Users");
    }

    // ... Adapt other SQL Server tests similarly ...
    [Fact]
    public async Task SqlServer_Applies_SQL_Only_Migrations()
    {
        var dbType = GetTestDatabaseType();
        var connectionString = GetConnectionString();
        var migrationsPath = GetTestSpecificMigrationsPath("SqlOnly_SS", dbType);
        long version1 = 202505011000;
        long version2 = 202505011001;

        await PrepareSqlFile(migrationsPath, version1, "-- SQL Migration 1 (SS)\nCREATE TABLE SqlOnlyTest1Ss (id INT PRIMARY KEY);");
        await PrepareSqlFile(migrationsPath, version2, "-- SQL Migration 2 (SS)\nCREATE TABLE SqlOnlyTest2Ss (name NVARCHAR(100));");

        await RunMigrationsAsync(dbType, connectionString, migrationsPath);

        await AssertMigrationAppliedAsync(dbType, connectionString, migrationsPath, version1);
        await AssertMigrationAppliedAsync(dbType, connectionString, migrationsPath, version2);
        await AssertTableExistsAsync(dbType, connectionString, "SqlOnlyTest1Ss");
        await AssertTableExistsAsync(dbType, connectionString, "SqlOnlyTest2Ss");
        var count = await GetVersionInfoRowCountAsync(dbType, connectionString, migrationsPath);
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task SQLServer_Applies_CSharp_Only_Migrations()
    {
        var dbType = DatabaseType.SqlServer;
        var connectionString = GetConnectionString();
        var migrationsPath = GetTestSpecificMigrationsPath("CSharpOnly", dbType);
        // Use actual versions from ExampleMigrations.dll
        long version1 = 202504091000L; // CreateInitialSchema
        long version2 = 202504091002L;   // CreateSettingsTable
        long version3 = 202504091004L;   // CreateProductsTable

        // Prepare DLL (using INHERITED helper)
        await PrepareCSharpMigrationDll(migrationsPath);

        await RunMigrationsAsync(dbType, connectionString, migrationsPath);

        // Assert based on the actual versions in the DLL
        await AssertMigrationAppliedAsync(dbType, connectionString, migrationsPath, version1);
        await AssertMigrationAppliedAsync(dbType, connectionString, migrationsPath, version2);
        await AssertMigrationAppliedAsync(dbType, connectionString, migrationsPath, version3);

        // Assert tables created by these C# migrations exist
        await AssertTableExistsAsync(dbType, connectionString, "Users");
        await AssertTableExistsAsync(dbType, connectionString, "Settings");
        await AssertTableExistsAsync(dbType, connectionString, "Products");

        // Assert VersionInfo count
        var count = await GetVersionInfoRowCountAsync(dbType, connectionString, migrationsPath);
        Assert.Equal(3, count);
    }

    [Fact]
    public async Task SQLServer_Applies_Interleaved_Migrations_Correctly()
    {
        var dbType = DatabaseType.SqlServer;
        var connectionString = GetConnectionString();
        var migrationsPath = GetTestSpecificMigrationsPath("Interleaved", dbType);
        long sqlVersion1 = 202504091001L;
        long csharpVersion1 = 202504091000L;
        long sqlVersion2 = 202504091003L;
        long csharpVersion2 = 202504091002L;
        long csharpVersion3 = 202504091004L;
        var expectedVersions = new[] { csharpVersion1, sqlVersion1, csharpVersion2, sqlVersion2, csharpVersion3 };

        await PrepareSqlFile(migrationsPath, sqlVersion1, "-- SQL 1 Interleaved\nCREATE TABLE SqlInterleaved1 (Id INT);");
        await PrepareSqlFile(migrationsPath, sqlVersion2, "-- SQL 2 Interleaved\nCREATE TABLE SqlInterleaved2 (Data INT);");
        await PrepareCSharpMigrationDll(migrationsPath);

        await RunMigrationsAsync(dbType, connectionString, migrationsPath);

        foreach (var version in expectedVersions.OrderBy(v => v))
        {
            await AssertMigrationAppliedAsync(dbType, connectionString, migrationsPath, version);
        }

        await AssertTableExistsAsync(dbType, connectionString, "Users");
        await AssertTableExistsAsync(dbType, connectionString, "Settings");
        await AssertTableExistsAsync(dbType, connectionString, "Products");
        await AssertTableExistsAsync(dbType, connectionString, "SqlInterleaved1");
        await AssertTableExistsAsync(dbType, connectionString, "SqlInterleaved2");

        var count = await GetVersionInfoRowCountAsync(dbType, connectionString, migrationsPath);
        Assert.Equal(expectedVersions.Length, count);
    }

    [Fact]
    public async Task SQLServer_Does_Not_ReApply_Already_Applied_Migrations()
    {
        var dbType = DatabaseType.SqlServer;
        var connectionString = GetConnectionString();
        var migrationsPath = GetTestSpecificMigrationsPath("AlreadyApplied", dbType);
        long sqlVersion1 = 202505041000L;
        var initialExpectedVersions = new[] { sqlVersion1, 202504091000L, 202504091002L, 202504091004L };

        await PrepareSqlFile(migrationsPath, sqlVersion1, "-- SQL V1 AlreadyApplied\nCREATE TABLE SqlAlreadyApplied1 (Id INT);");
        await PrepareCSharpMigrationDll(migrationsPath);

        OutputHelper.WriteLine("First migration run...");
        await RunMigrationsAsync(dbType, connectionString, migrationsPath);

        foreach (var v in initialExpectedVersions.OrderBy(v => v))
        {
            await AssertMigrationAppliedAsync(dbType, connectionString, migrationsPath, v);
        }
        await AssertTableExistsAsync(dbType, connectionString, "SqlAlreadyApplied1");
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
    public async Task SQLServer_Applies_OutOfOrder_Migration_If_Not_Applied()
    {
        var dbType = DatabaseType.SqlServer;
        var connectionString = GetConnectionString();
        var migrationsPath = GetTestSpecificMigrationsPath("OutOfOrder", dbType);
        long sqlVersion2 = 202504091003L;
        long csharpVersion3 = 202504091004L;
        long sqlVersion1 = 202504091001L;

        await PrepareSqlFile(migrationsPath, sqlVersion2, "-- SQL V2 OutOfOrder\nCREATE TABLE SqlOutOfOrder2 (Id INT);");
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
        await AssertTableExistsAsync(dbType, connectionString, "SqlOutOfOrder2");
        var initialCount = await GetVersionInfoRowCountAsync(dbType, connectionString, migrationsPath);
        Assert.Equal(4, initialCount);

        await PrepareSqlFile(migrationsPath, sqlVersion1, "-- SQL V1 (Out Of Order)\nCREATE TABLE SqlOutOfOrder1 (Name VARCHAR(10));");

        OutputHelper.WriteLine("Second run (adding SQL V1)...");
        ClearCapturedLogs();
        await RunMigrationsAsync(dbType, connectionString, migrationsPath);

        await AssertMigrationAppliedAsync(dbType, connectionString, migrationsPath, sqlVersion1);
        await AssertMigrationAppliedAsync(dbType, connectionString, migrationsPath, 202504091000L);
        await AssertMigrationAppliedAsync(dbType, connectionString, migrationsPath, 202504091002L);
        await AssertMigrationAppliedAsync(dbType, connectionString, migrationsPath, csharpVersion3);
        await AssertMigrationAppliedAsync(dbType, connectionString, migrationsPath, sqlVersion2);
        await AssertTableExistsAsync(dbType, connectionString, "SqlOutOfOrder1");
        var finalCount = await GetVersionInfoRowCountAsync(dbType, connectionString, migrationsPath);
        Assert.Equal(5, finalCount);
        AssertLogContainsSubstring(LogLevel.Warning, $"Applying out-of-order migration: Version {sqlVersion1} is being applied after a higher version {csharpVersion3} has already been applied.");
    }

    [Fact]
    public async Task SQLServer_Halts_And_RollsBack_On_Failure()
    {
        var dbType = DatabaseType.SqlServer;
        var connectionString = GetConnectionString();
        var migrationsPath = GetTestSpecificMigrationsPath("Failure", dbType);
        long version1 = 202504091000L;
        long versionFail = 202504091001;
        long version3 = 202504091002L;
        long version4 = 202504091004L;
        // var expectedErrorLogFile = Path.Combine(AppContext.BaseDirectory, "migration-error.log"); // Keep if needed

        // if (File.Exists(expectedErrorLogFile))
        // {
        //     File.Delete(expectedErrorLogFile);
        // }

        // Prepare the failing SQL script
        await PrepareSqlFile(migrationsPath, versionFail, "CREATE TABLE Fail;;"); // Invalid syntax for SQL Server
        // Prepare the C# migrations DLL
        await PrepareCSharpMigrationDll(migrationsPath);

        OutputHelper.WriteLine("Running migration expected to fail...");
        var ex = await Assert.ThrowsAsync<Exception>(() =>
            RunMigrationsAsync(dbType, connectionString, migrationsPath)
        );

        OutputHelper.WriteLine($"Caught expected exception: {ex.Message}");

        // Check migrations *before* failure were applied
        await AssertMigrationAppliedAsync(dbType, connectionString, migrationsPath, version1); // C# V0
        await AssertTableExistsAsync(dbType, connectionString, "Users");
        // V2 and V4 come AFTER the failing SQL script in execution order, so they should NOT be applied
        // await AssertMigrationAppliedAsync(dbType, connectionString, migrationsPath, version3); // C# V2
        // await AssertTableExistsAsync(dbType, connectionString, "Settings");
        // await AssertMigrationAppliedAsync(dbType, connectionString, migrationsPath, version4); // C# V4
        // await AssertTableExistsAsync(dbType, connectionString, "Products");

        // Check FAILING migration was NOT applied (rolled back)
        await AssertMigrationNotAppliedAsync(dbType, connectionString, migrationsPath, versionFail); // SQL Fail
        // Table should not exist if creation failed
        await AssertTableDoesNotExistAsync(dbType, connectionString, "Fail"); // Assuming table name is "Fail"

        // Check migrations *after* failure were NOT applied (halted)
        await AssertMigrationNotAppliedAsync(dbType, connectionString, migrationsPath, version3); // C# V2
        await AssertTableDoesNotExistAsync(dbType, connectionString, "Settings");
        await AssertMigrationNotAppliedAsync(dbType, connectionString, migrationsPath, version4); // C# V4
        await AssertTableDoesNotExistAsync(dbType, connectionString, "Products");

        // Check VersionInfo count - should include ONLY successful migrations before failure
        var count = await GetVersionInfoRowCountAsync(dbType, connectionString, migrationsPath);
        Assert.Equal(1, count); // Only V0 should be recorded

        // Verify logs
        AssertLogContainsSubstring(LogLevel.Error, $"CRITICAL ERROR applying SQL migration {versionFail}");
        // AssertLogContainsSubstring(LogLevel.Information, "Transaction rollback attempted successfully"); // Log not generated
        AssertLogContainsSubstring(LogLevel.Error, "Rolling back transaction"); // Check handler logs rollback attempt
        AssertLogDoesNotContainSubstring(LogLevel.Information, $"Applying SQL migration: {version3}"); // Check V2 didn't start
        AssertLogDoesNotContainSubstring(LogLevel.Information, $"Applying C# migration: {version4}"); // Check V4 didn't start
    }
}
