using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Logging;
using Migrator.Core;
using Migrator.Tests.Base;
using Testcontainers.MsSql;
using Xunit.Abstractions;

namespace Migrator.Tests;

public class SqlServerMigrationTests : MigrationTestBase
{
    public SqlServerMigrationTests(ITestOutputHelper outputHelper)
        : base(outputHelper)
    { }

    protected override DatabaseType GetTestDatabaseType() => DatabaseType.SqlServer;

    protected override IContainer BuildTestContainer()
    {
        return new MsSqlBuilder()
            .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
            .WithPassword("yourStrong(!)Password")
            .Build();
    }

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
        long version1 = 202504091000L;
        long version2 = 202504091002L;
        long version3 = 202504091004L;

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

        await PrepareSqlFile(migrationsPath, versionFail, "CREATE TABLE Fail;;");
        await PrepareCSharpMigrationDll(migrationsPath);

        OutputHelper.WriteLine("Running migration expected to fail...");
        var ex = await Assert.ThrowsAsync<Exception>(() =>
            RunMigrationsAsync(dbType, connectionString, migrationsPath)
        );

        OutputHelper.WriteLine($"Caught expected exception: {ex.Message}");

        await AssertMigrationAppliedAsync(dbType, connectionString, migrationsPath, version1);
        await AssertTableExistsAsync(dbType, connectionString, "Users");
        await AssertMigrationNotAppliedAsync(dbType, connectionString, migrationsPath, versionFail);
        await AssertTableDoesNotExistAsync(dbType, connectionString, "Fail");

        await AssertMigrationNotAppliedAsync(dbType, connectionString, migrationsPath, version3);
        await AssertTableDoesNotExistAsync(dbType, connectionString, "Settings");
        await AssertMigrationNotAppliedAsync(dbType, connectionString, migrationsPath, version4);
        await AssertTableDoesNotExistAsync(dbType, connectionString, "Products");

        var count = await GetVersionInfoRowCountAsync(dbType, connectionString, migrationsPath);
        Assert.Equal(1, count);

        AssertLogContainsSubstring(LogLevel.Error, $"CRITICAL ERROR applying SQL migration {versionFail}");
        AssertLogContainsSubstring(LogLevel.Error, "Rolling back transaction");
        AssertLogDoesNotContainSubstring(LogLevel.Information, $"Applying SQL migration: {version3}");
        AssertLogDoesNotContainSubstring(LogLevel.Information, $"Applying C# migration: {version4}");
    }

    [Fact]
    public async Task SqlServer_Runs_Successfully_With_Empty_Folder()
    {
        var dbType = GetTestDatabaseType();
        var connectionString = GetConnectionString();
        var migrationsPath = GetTestSpecificMigrationsPath("EmptyFolder_SS", dbType);
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
    public async Task SqlServer_Throws_Exception_For_NonExistent_Folder()
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
