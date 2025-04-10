using DotNet.Testcontainers.Containers; // For IContainer
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Migrator.Core;
using Migrator.Tests.Base; // Ensure base namespace is included
using Xunit.Abstractions;

namespace Migrator.Tests;

// Remove IClassFixture, Inherit from non-generic MigrationTestBase
public class SqliteMigrationTests : MigrationTestBase
{
    // Constructor now only takes ITestOutputHelper
    public SqliteMigrationTests(ITestOutputHelper outputHelper)
        : base(outputHelper) // Pass output helper to base
    { }

    // Implement abstract methods from base class
    protected override DatabaseType GetTestDatabaseType() => DatabaseType.SQLite;

    // Override BuildTestContainer to return our custom SQLiteContainer
    protected override IContainer BuildTestContainer()
    {
        // No actual container process, just our wrapper for file management
        return new SQLiteContainer();
    }

    // Example Test Method (Adapt your existing tests)
    [Fact]
    public async Task Test_SQLite_AppliesMigrationsSuccessfully()
    {
        // Arrange
        var dbType = GetTestDatabaseType(); // Use base method
        var connectionString = GetConnectionString(); // Use connection string managed by base class / SQLiteContainer
        var migrationsPath = GetTestSpecificMigrationsPath("BasicRun_SQLite", dbType); // Use base method

        // No need for manual file deletion or connection string creation here;
        // MigrationTestBase.InitializeAsync (via SQLiteContainer.StartAsync) handles cleanup before the test
        // MigrationTestBase.DisposeAsync (via SQLiteContainer.DisposeAsync) handles cleanup after the test

        await PrepareCSharpMigrationDll(migrationsPath); // Use base method

        // Act
        await RunMigrationsAsync(dbType, connectionString, migrationsPath); // Use base method

        // Assert
        // Verify all C# migrations in the test DLL were applied
        await AssertMigrationAppliedAsync(dbType, connectionString, migrationsPath, 202504091000L);
        await AssertMigrationAppliedAsync(dbType, connectionString, migrationsPath, 202504091002L);
        await AssertMigrationAppliedAsync(dbType, connectionString, migrationsPath, 202504091004L);

        // Verify tables created by these migrations exist
        await AssertTableExistsAsync(dbType, connectionString, "Users"); // Use base method
        await AssertTableExistsAsync(dbType, connectionString, "Settings"); // Use base method
        await AssertTableExistsAsync(dbType, connectionString, "Products"); // Use base method

        // Verify VersionInfo count
        var finalCount = await GetVersionInfoRowCountAsync(dbType, connectionString, migrationsPath);
        Assert.Equal(3, finalCount); // Expecting 3 C# migrations

        // No need for manual finally block cleanup; MigrationTestBase.DisposeAsync handles it
    }

    // Add back other SQLite specific tests if any, adapting them to use base class helpers
    // Example:
    // [Fact]
    // public async Task SQLite_AnotherTest()
    // {
    //    var dbType = GetTestDatabaseType();
    //    var dbName = $"TestDb_{Guid.NewGuid()}.sqlite";
    //    var dbPath = Path.Combine(Directory.GetCurrentDirectory(), dbName);
    //    var connectionString = $"Data Source={dbPath};Cache=Shared";
    //    var migrationsPath = GetTestSpecificMigrationsPath("AnotherTest", dbType);
    //    ...
    //    await RunMigrationsAsync(dbType, connectionString, migrationsPath);
    //    ...
    //    // finally block to clear pool and delete file
    // }
}
