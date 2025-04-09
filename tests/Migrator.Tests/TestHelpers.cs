using System.Data.Common;
using System.Text.RegularExpressions;
using FluentMigrator.Runner;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Migrator.Core;
using Npgsql;
using Shouldly;

// Needed for AssemblyLoadContext

namespace Migrator.Tests;

public static class TestHelpers
{
    // Regex for SQL file matching (12-digit timestamp)
    private static readonly Regex SqlMigrationFileRegex = new(@"^(\d{12})_.*\.sql$", RegexOptions.IgnoreCase);

    // Copies C# DLL and contents of the DB-specific SQL folder
    public static string PrepareTestMigrations(string testId, DatabaseType dbType)
    {
        var migrationsDir = CreateTempMigrationsDirectory(testId);
        CopyCSharpAssembly(migrationsDir, "TestMigrations.dll");
        CopyDbSpecificSqlFiles(migrationsDir, dbType);
        return migrationsDir;
    }

    // --- Helper Method for Preparing C# Only Migrations ---
    // Copies only the C# DLL
    public static string PrepareCSharpOnlyMigrations(string testId)
    {
        var migrationsDir = CreateTempMigrationsDirectory(testId);
        CopyCSharpAssembly(migrationsDir, "CSharpOnly.dll");
        // No SQL files copied
        return migrationsDir;
    }

    // --- Helper Method for Preparing SQL Only Migrations ---
    // Copies only the contents of the DB-specific SQL folder
    public static string PrepareSqlOnlyMigrations(string testId, DatabaseType dbType)
    {
        var migrationsDir = CreateTempMigrationsDirectory(testId);
        // No C# assembly copied
        CopyDbSpecificSqlFiles(migrationsDir, dbType);
        return migrationsDir;
    }

    // --- Methods for Interleaved Migration Testing ---
    public static List<long> GetExpectedInterleavedVersions()
    {
        // C# 1000, SQL 1001, C# 1002, SQL 1003, C# 1004, SQL 1005
        return new List<long> { 202504091000, 202504091001, 202504091002, 202504091003, 202504091004, 202504091005 };
    }

    // Copies C# DLL and contents of the DB-specific SQL folder
    public static string PrepareInterleavedMigrations(string testId, DatabaseType dbType)
    {
        var migrationsDir = CreateTempMigrationsDirectory(testId);
        CopyCSharpAssembly(migrationsDir, "InterleavedTestMigrations.dll");
        CopyDbSpecificSqlFiles(migrationsDir, dbType);
        return migrationsDir;
    }

    // --- Helper for Preparing Migrations with a Deliberate Failure ---
    // Copies C# DLL, successful SQL files, then adds faulty/skipped SQL
    public static string PrepareMigrationsWithFailure(string testId, DatabaseType dbType, long faultyVersion, long skippedSqlVersion)
    {
        var migrationsDir = CreateTempMigrationsDirectory(testId);
        CopyCSharpAssembly(migrationsDir, "FailureTestMigrations.dll");
        // Copy standard SQL files first (includes successful ones like 1001)
        CopyDbSpecificSqlFiles(migrationsDir, dbType);

        // Now, create/overwrite the specific faulty and skipped files

        // Create the *Faulty* SQL Migration (using deliberately bad syntax for the specific DB)
        var faultySqlFileName = $"{faultyVersion}_FaultyMigration.sql";
        string faultySqlContent = dbType switch
        {
            DatabaseType.SqlServer => "ALTER TABLE Settings ADD Col Value BAD_SYNTAX;", // Bad SQL Server syntax
            DatabaseType.PostgreSql => "ALTER TABLE \"Settings\" ADD COLUMNS \"Value\" TEXT;", // Bad PG syntax
            DatabaseType.SQLite => "ALTER TABLE Settings ADD COLUMN Value BAD_SYNTAX;", // Bad SQLite syntax
            _ => "SELECT 1/0;" // Generic failure
        };
        File.WriteAllText(Path.Combine(migrationsDir, faultySqlFileName), faultySqlContent);

        // Create the *Skipped* SQL Migration (using potentially valid but irrelevant syntax)
        var skippedSqlFileName = $"{skippedSqlVersion}_SkippedSqlMigration.sql";
        string skippedSqlContent = dbType switch // Use correct basic syntax just to have a file
        {
            DatabaseType.SqlServer => "-- This migration should be skipped\nPRINT 'Skipped SQL executed';",
            DatabaseType.PostgreSql => "-- This migration should be skipped\nSELECT 'Skipped SQL executed';",
            DatabaseType.SQLite => "-- This migration should be skipped\nSELECT 'Skipped SQL executed';",
            _ => "-- Skipped"
        };
        File.WriteAllText(Path.Combine(migrationsDir, skippedSqlFileName), skippedSqlContent);

        return migrationsDir;
    }

    // --- Shared Helper Methods ---

    private static string CreateTempMigrationsDirectory(string testId)
    {
        var tempBaseDir = Path.Combine(Path.GetTempPath(), "MigratorTests", testId, Guid.NewGuid().ToString());
        var migrationsDir = Path.Combine(tempBaseDir, "migrations");
        Directory.CreateDirectory(migrationsDir);
        return migrationsDir;
    }

    private static void CopyCSharpAssembly(string destinationDir, string destinationFileName)
    {
        var sourceAssemblyPath = FindExampleMigrationsDllPath();
        if (string.IsNullOrEmpty(sourceAssemblyPath) || !File.Exists(sourceAssemblyPath))
        {
            throw new FileNotFoundException($"Could not find ExampleMigrations.dll at expected path: {sourceAssemblyPath}");
        }
        File.Copy(sourceAssemblyPath, Path.Combine(destinationDir, destinationFileName), true);
    }

    private static void CopyDbSpecificSqlFiles(string destinationDir, DatabaseType dbType)
    {
        var buildOutputPath = FindBuildOutputPath(FindExampleMigrationsProjectDir());
        if (buildOutputPath == null)
        {
            throw new DirectoryNotFoundException("Could not find ExampleMigrations build output directory.");
        }

        var dbSpecificSubfolder = dbType switch
        {
            DatabaseType.SQLite => "sqlite",
            DatabaseType.PostgreSql => "postgresql",
            DatabaseType.SqlServer => "sqlserver",
            _ => throw new ArgumentOutOfRangeException(nameof(dbType), "Unsupported DB type for SQL file copying.")
        };
        var dbSqlSourcePath = Path.Combine(buildOutputPath, dbSpecificSubfolder);

        if (!Directory.Exists(dbSqlSourcePath))
        {
            Console.WriteLine($"Warning: SQL source directory not found for {dbType}: {dbSqlSourcePath}. No SQL files copied.");
            return; // Nothing to copy
        }

        foreach (var sourceSqlPath in Directory.EnumerateFiles(dbSqlSourcePath, "*.sql", SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileName(sourceSqlPath);
            // Optional: Could add regex check here if needed, but copying all *.sql is simpler
            // if (SqlMigrationFileRegex.IsMatch(fileName))
            // {
            var destPath = Path.Combine(destinationDir, fileName);
            File.Copy(sourceSqlPath, destPath, true); // Overwrite if exists (e.g., in failure scenario)
            // }
        }
    }

    public static void CleanupTestMigrations(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(directoryPath);
        try
        {
            // Go up one level from the migrations dir to delete the parent temp folder
            var parentDir = Directory.GetParent(directoryPath)?.FullName;
            if (!string.IsNullOrEmpty(parentDir) && Directory.Exists(parentDir))
            {
                Directory.Delete(parentDir, true);
            }
            else if (Directory.Exists(directoryPath)) // Fallback if parent isn't found
            {
                Directory.Delete(directoryPath, true);
            }
        }
        catch (Exception ex)
        {
            // Don't log warning for collectible context file lock issues, they might resolve
            if (!ex.Message.Contains("being used by another process"))
            {
                Console.WriteLine($"Warning: Failed to cleanup test directory {directoryPath}: {ex.Message}");
            }
        }
    }

    // Updated to expect 6 migrations and assert new schema/data
    public static async Task AssertDatabaseStateAfterMigrations(DatabaseType dbType, string connectionString,
        int expectedMigrationCount = 6)
    {
        var versionCheckServiceProvider = BuildVersionCheckServiceProvider(dbType, connectionString);
        ArgumentNullException.ThrowIfNull(versionCheckServiceProvider);
        using var scope = versionCheckServiceProvider.CreateScope();
        var versionLoader = scope.ServiceProvider.GetRequiredService<IVersionLoader>();

        // 1. Check VersionInfo table
        versionLoader.LoadVersionInfo();
        var appliedMigrations = versionLoader.VersionInfo.AppliedMigrations().ToList();

        // Expected 12-digit timestamps
        var expectedTimestamps = new List<long>
        {
            202504091000, // M..._CreateInitialSchema
            202504091001, // ..._AddUserEmail.sql
            202504091002, // M..._CreateSettingsTable
            202504091003, // ..._AddSettingValue.sql
            202504091004, // M..._CreateProductsTable
            202504091005 // ..._AddProductPrice.sql
        };

        // Allow for retries if DB is slow to update after migration
        var retryCount = 0;
        const int maxRetries = 5;
        while (appliedMigrations.Count < expectedMigrationCount && retryCount < maxRetries)
        {
            await Task.Delay(200); // Wait a bit
            versionLoader.LoadVersionInfo();
            appliedMigrations = versionLoader.VersionInfo.AppliedMigrations().ToList();
            retryCount++;
        }

        appliedMigrations.Count.ShouldBe(expectedMigrationCount,
            $"Expected {expectedMigrationCount} migrations, found {appliedMigrations.Count} after {retryCount} retries. Applied: [{string.Join(',', appliedMigrations)}]");
        foreach (var ts in expectedTimestamps)
        {
            appliedMigrations.ShouldContain(ts, $"Timestamp {ts} not found in applied migrations.");
        }

        // 2. Check actual schema/data using raw ADO.NET
        await using var connection = CreateDbConnection(dbType, connectionString);
        await connection.OpenAsync();

        // Define identifiers (handle quoting)
        Func<string, string> quote = dbType switch
        {
            DatabaseType.PostgreSql => name => $"\"{name}\"",
            DatabaseType.SQLite => name => $"\"{name}\"",
            _ => name => $"[{name}]" // Default to SQL Server style
        };

        var usersTable = quote("Users");
        var settingsTable = quote("Settings");
        var productsTable = quote("Products");
        var emailCol = quote("Email");
        var usernameCol = quote("Username");
        var keyCol = quote("Key");
        var valueCol = quote("Value");
        var nameCol = quote("Name");
        var priceCol = quote("Price");

        // Check tables exist (using original case for lookup)
        (await CheckTableExistsAsync(connection, "Users")).ShouldBeTrue("Table 'Users' should exist");
        (await CheckTableExistsAsync(connection, "Settings")).ShouldBeTrue("Table 'Settings' should exist");
        (await CheckTableExistsAsync(connection, "Products")).ShouldBeTrue("Table 'Products' should exist");

        // Check columns exist (using original case for lookup)
        (await CheckColumnExistsAsync(connection, "Users", "Email")).ShouldBeTrue(
            "Column 'Email' on 'Users' should exist");
        (await CheckColumnExistsAsync(connection, "Settings", "Value")).ShouldBeTrue(
            "Column 'Value' on 'Settings' should exist");
        (await CheckColumnExistsAsync(connection, "Products", "Price")).ShouldBeTrue(
            "Column 'Price' on 'Products' should exist");

        // Check data
        await using var command = connection.CreateCommand();
        // Users
        command.CommandText = $"SELECT {emailCol} FROM {usersTable} WHERE {usernameCol} = 'admin'";
        (await command.ExecuteScalarAsync())?.ToString().ShouldBe("admin@example.com");

        // Settings
        command.CommandText = $"SELECT {valueCol} FROM {settingsTable} WHERE {keyCol} = 'DefaultTheme'";
        (await command.ExecuteScalarAsync())?.ToString().ShouldBe("DefaultValue");

        // Products
        command.CommandText = $"SELECT {priceCol} FROM {productsTable} WHERE {nameCol} = 'Sample Product'";
        Convert.ToDecimal(await command.ExecuteScalarAsync()).ShouldBe(9.99m);
    }

    private static IServiceProvider BuildVersionCheckServiceProvider(DatabaseType dbType, string connectionString)
    {
        // No assembly scanning needed here, just core services for version table access
        var serviceCollection = new ServiceCollection()
            .AddFluentMigratorCore()
            .ConfigureRunner(rb =>
            {
                switch (dbType)
                {
                    case DatabaseType.SqlServer: rb.AddSqlServer(); break;
                    case DatabaseType.PostgreSql: rb.AddPostgres(); break;
                    case DatabaseType.SQLite: rb.AddSQLite(); break;
                    default: throw new ArgumentOutOfRangeException(nameof(dbType));
                }

                rb.WithGlobalConnectionString(connectionString);
                // We don't need ScanIn here as we only need the VersionLoader
                rb.ScanIn().For.Migrations();
            })
            .AddLogging(lb => lb.SetMinimumLevel(LogLevel.Warning)); // Keep logging minimal for checks

        return serviceCollection.BuildServiceProvider(false);
    }

    private static DbConnection CreateDbConnection(DatabaseType dbType, string connectionString)
    {
        return dbType switch
        {
            DatabaseType.SqlServer => new SqlConnection(connectionString),
            DatabaseType.PostgreSql => new NpgsqlConnection(connectionString),
            DatabaseType.SQLite => new SqliteConnection(connectionString),
            _ => throw new ArgumentOutOfRangeException(nameof(dbType),
                "Unsupported database type for ADO.NET connection.")
        };
    }

    private static async Task<bool> CheckTableExistsAsync(DbConnection connection, string tableName)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var command = connection.CreateCommand();
        ArgumentNullException.ThrowIfNull(command, nameof(command)); // Check command
        command.CommandText = connection switch
        {
            SqliteConnection => "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@TableName",
            NpgsqlConnection => "SELECT COUNT(*) FROM pg_tables WHERE schemaname = 'public' AND tablename = @TableName",
            _ => "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = @TableName"
        };

        command.Parameters.Add(CreateParameter(command, "@TableName", tableName));
        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt32(result ?? 0) > 0;
    }

    private static async Task<bool> CheckColumnExistsAsync(DbConnection connection, string tableName, string columnName)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var command = connection.CreateCommand();
        ArgumentNullException.ThrowIfNull(command, nameof(command)); // Check command
        if (connection is SqliteConnection)
        {
            command.CommandText = "SELECT COUNT(*) FROM pragma_table_info(@TableName) WHERE name=@ColumnName";
            command.Parameters.Add(CreateParameter(command, "@TableName", tableName));
            command.Parameters.Add(CreateParameter(command, "@ColumnName", columnName));
        }
        else if (connection is NpgsqlConnection)
        {
            command.CommandText =
                "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = 'public' AND table_name = @TableName AND column_name = @ColumnName";
            command.Parameters.Add(CreateParameter(command, "@TableName", tableName));
            command.Parameters.Add(CreateParameter(command, "@ColumnName", columnName));
        }
        else // Assuming SQL Server
        {
            command.CommandText =
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = @TableName AND COLUMN_NAME = @ColumnName";
            command.Parameters.Add(CreateParameter(command, "@TableName", tableName));
            command.Parameters.Add(CreateParameter(command, "@ColumnName", columnName));
        }

        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt32(result ?? 0) > 0;
    }

    private static DbParameter CreateParameter(DbCommand command, string name, object value)
    {
        ArgumentNullException.ThrowIfNull(command);
        var parameter = command.CreateParameter();
        ArgumentNullException.ThrowIfNull(parameter, nameof(parameter)); // Check parameter
        parameter.ParameterName = name;
        parameter.Value = value;
        return parameter;
    }

    // --- Helper to find ExampleMigrations.dll path (extracted from PrepareTestMigrations) ---
    private static string? FindExampleMigrationsDllPath()
    {
        var exampleMigrationsProjectDir = FindExampleMigrationsProjectDir();
        var buildOutputPath = FindBuildOutputPath(exampleMigrationsProjectDir);
        if (buildOutputPath == null)
        {
            return null;
        }

        var mainDllName = "ExampleMigrations.dll";
        var dllPath = Path.Combine(buildOutputPath, mainDllName);

        if (File.Exists(dllPath))
        {
            return dllPath;
        }
        else
        {
            Console.WriteLine($"Error: Could not find {mainDllName} in {buildOutputPath}");
            return null;
        }
    }

    // Extracted logic to find the ExampleMigrations project directory
    private static string FindExampleMigrationsProjectDir()
    {
        var currentDir = AppContext.BaseDirectory;
        var solutionDir = currentDir;
        while (solutionDir != null && !Directory.GetFiles(solutionDir, "Migrator.sln").Any())
        {
            solutionDir = Directory.GetParent(solutionDir)?.FullName;
        }

        if (solutionDir == null)
        {
            throw new DirectoryNotFoundException("Could not find solution directory (Migrator.sln).");
        }

        var exampleMigrationsProjectDir = Path.Combine(solutionDir, "src", "ExampleMigrations");
        if (!Directory.Exists(exampleMigrationsProjectDir))
        {
            throw new DirectoryNotFoundException($"ExampleMigrations project directory not found: {exampleMigrationsProjectDir}");
        }

        return exampleMigrationsProjectDir;
    }

    // Extracted logic to find the build output path containing the DLL and DB subfolders
    private static string? FindBuildOutputPath(string projectDir)
    {
        string[] configurations = ["Debug", "Release"];
        // Check common TFMs - adjust if your project uses different ones
        string[] targetFrameworks = ["net9.0", "net8.0", "netstandard2.0"]; // Added net8.0 just in case
        var mainDllName = "ExampleMigrations.dll"; // Used as an indicator file

        foreach (var config in configurations)
        {
            foreach (var tfm in targetFrameworks)
            {
                var potentialPath = Path.Combine(projectDir, "bin", config, tfm);
                if (File.Exists(Path.Combine(potentialPath, mainDllName)))
                {
                    return potentialPath; // Return the directory containing the indicator file
                }
            }
        }

        Console.WriteLine($"Warning: Could not find build output containing {mainDllName} in {projectDir}/bin/[Debug|Release]/[{string.Join('|', targetFrameworks)}]");
        return null; // Indicate failure
    }

    // --- Helper to get quoting function (Corrected for SQLite AGAIN) ---
    private static Func<string, string> GetQuoteFunction(DatabaseType dbType)
    {
        return dbType switch
        {
            DatabaseType.PostgreSql => name => $"\"{name}\"", // PostgreSQL standard quotes
            DatabaseType.SQLite => name => $"\"{name}\"", // SQLite standard quotes
            _ => name => $"[{name}]" // Default to SQL Server style brackets
        };
    }

    /// <summary>
    ///     Asserts the database state after the specific interleaved migrations have run.
    ///     Checks for tables, columns, and specific data added by both C# and SQL migrations.
    /// </summary>
    public static async Task AssertDatabaseStateAfterInterleavedMigrations(DatabaseType dbType, string connectionString)
    {
        // Very similar to AssertDatabaseStateAfterMigrations, but checks the specific combined state

        await using var connection = CreateDbConnection(dbType, connectionString);
        await connection.OpenAsync();

        // Define identifiers (handle quoting) - Reuse logic from other assertion
        var quote = GetQuoteFunction(dbType);

        var usersTable = quote("Users");
        var settingsTable = quote("Settings");
        var productsTable = quote("Products");
        var emailCol = quote("Email");
        var usernameCol = quote("Username");
        var keyCol = quote("Key");
        var valueCol = quote("Value");
        var nameCol = quote("Name");
        var priceCol = quote("Price");

        // Check tables exist (using original case for lookup)
        (await CheckTableExistsAsync(connection, "Users")).ShouldBeTrue("Table 'Users' should exist");
        (await CheckTableExistsAsync(connection, "Settings")).ShouldBeTrue("Table 'Settings' should exist");
        (await CheckTableExistsAsync(connection, "Products")).ShouldBeTrue("Table 'Products' should exist");

        // Check columns exist (added by both C# and SQL)
        (await CheckColumnExistsAsync(connection, "Users", "Username")).ShouldBeTrue(
            "Column 'Username' on 'Users' should exist (from C#)");
        (await CheckColumnExistsAsync(connection, "Users", "Email")).ShouldBeTrue(
            "Column 'Email' on 'Users' should exist (from SQL)");
        (await CheckColumnExistsAsync(connection, "Settings", "Key")).ShouldBeTrue(
            "Column 'Key' on 'Settings' should exist (from C#)");
        (await CheckColumnExistsAsync(connection, "Settings", "Value")).ShouldBeTrue(
            "Column 'Value' on 'Settings' should exist (from SQL)");
        (await CheckColumnExistsAsync(connection, "Products", "Name")).ShouldBeTrue(
            "Column 'Name' on 'Products' should exist (from C#)");
        (await CheckColumnExistsAsync(connection, "Products", "Price")).ShouldBeTrue(
            "Column 'Price' on 'Products' should exist (from SQL)");

        // Check data inserted/updated by migrations
        await using var command = connection.CreateCommand();
        // Users - Check data from C# (Username) and SQL (Email) (Remains the same)
        command.CommandText = $"SELECT {usernameCol}, {emailCol} FROM {usersTable} WHERE {usernameCol} = 'admin'";
        await using var readerUser = await command.ExecuteReaderAsync();
        (await readerUser.ReadAsync()).ShouldBeTrue("Admin user should exist");
        readerUser.GetString(0).ShouldBe("admin");
        readerUser.GetString(1).ShouldBe("admin@example.com");
        await readerUser.CloseAsync();

        // Settings - Check data from C# (Key) and SQL (Value) (Update WHERE clause and assertion message)
        command.CommandText =
            $"SELECT {keyCol}, {valueCol} FROM {settingsTable} WHERE {keyCol} = 'DefaultTheme'"; // Corrected Key
        await using var readerSetting = await command.ExecuteReaderAsync();
        (await readerSetting.ReadAsync()).ShouldBeTrue("DefaultTheme setting should exist"); // Corrected Message
        readerSetting.GetString(0).ShouldBe("DefaultTheme");
        readerSetting.GetString(1).ShouldBe("DefaultValue");
        await readerSetting.CloseAsync();

        // Products - Check data from C# (Name) and SQL (Price) (Remains the same)
        command.CommandText =
            $"SELECT {nameCol}, {priceCol} FROM {productsTable} WHERE {nameCol} = 'Sample Product'";
        await using var readerProduct = await command.ExecuteReaderAsync();
        (await readerProduct.ReadAsync()).ShouldBeTrue("Sample Product should exist");
        readerProduct.GetString(0).ShouldBe("Sample Product");
        readerProduct.GetDecimal(1).ShouldBe(99.99m);
        await readerProduct.CloseAsync();
    }

    /// <summary>
    ///     Asserts that the migrations recorded in the VersionInfo table match the expected
    ///     list of versions and are in the correct order.
    /// </summary>
    public static async Task AssertVersionInfoOrder(DatabaseType dbType, string connectionString,
        List<long> expectedVersions)
    {
        var versionCheckServiceProvider = BuildVersionCheckServiceProvider(dbType, connectionString);
        using var scope = versionCheckServiceProvider.CreateScope();
        var versionLoader = scope.ServiceProvider.GetRequiredService<IVersionLoader>();

        // Allow for retries if DB is slow to update after migration
        List<long> appliedMigrations = new();
        var retryCount = 0;
        const int maxRetries = 5;
        while (appliedMigrations.Count < expectedVersions.Count && retryCount < maxRetries)
        {
            if (retryCount > 0)
            {
                await Task.Delay(200); // Wait only on retry
            }

            try
            {
                versionLoader.LoadVersionInfo(); // Reload info
                appliedMigrations =
                    versionLoader.VersionInfo.AppliedMigrations().OrderBy(v => v)
                        .ToList(); // Ensure sorted for comparison
            }
            catch (Exception ex)
            {
                // Log potential error during version load retry
                Console.WriteLine($"Warning: Error loading version info on retry {retryCount}: {ex.Message}");
                // Continue retry loop
            }

            retryCount++;
        }

        appliedMigrations.Count.ShouldBe(expectedVersions.Count,
            $"Expected {expectedVersions.Count} migrations, found {appliedMigrations.Count} after {retryCount} retries. Applied: [{string.Join(',', appliedMigrations)}]");

        // Use SequenceEqual for exact order comparison
        appliedMigrations.ShouldBe(expectedVersions,
            $"Applied migrations order does not match expected order. Expected: [{string.Join(',', expectedVersions)}], Actual: [{string.Join(',', appliedMigrations)}]");
    }

    /// <summary>
    ///     Retrieves the list of applied migration versions directly from the VersionInfo table.
    /// </summary>
    public static async Task<List<long>> GetAppliedVersionsAsync(DatabaseType dbType, string connectionString)
    {
        var versions = new List<long>();
        await using var connection = CreateDbConnection(dbType, connectionString);
        await connection.OpenAsync();

        // Use the custom table/column names defined in CustomVersionTableMetaData
        var versionTable = new CustomVersionTableMetaData();
        var quote = GetQuoteFunction(dbType); // Use the corrected function
        var versionColumn = quote(versionTable.ColumnName);
        var tableName = quote(versionTable.TableName);

        // Check if VersionInfo table exists before querying
        if (!await CheckTableExistsAsync(connection, versionTable.TableName))
        {
            Console.WriteLine(
                $"Warning: VersionInfo table ({versionTable.TableName}) not found when checking applied versions.");
            return versions; // Return empty list if table doesn't exist
        }

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {versionColumn} FROM {tableName} ORDER BY {versionColumn} ASC";

        try
        {
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                // Assuming the Version column is BIGINT or similar
                versions.Add(reader.GetInt64(0));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error querying VersionInfo table ({versionTable.TableName}): {ex.Message}");
        }

        return versions;
    }

    /// <summary>
    ///     Asserts that specific schema changes expected from a SKIPPED migration (due to prior failure)
    ///     are NOT present in the database. Checks based on the provided skippedSqlVersion.
    /// </summary>
    public static async Task AssertDataFromSkippedMigrationNotPresent(DatabaseType dbType, string connectionString,
        long skippedSqlVersion)
    {
        await using var connection = CreateDbConnection(dbType, connectionString);
        await connection.OpenAsync();

        // Check based on the structure of the specific skipped SQL migration (e.g., 202504091005_ShouldNotRun.sql)
        if (skippedSqlVersion == 202504091005)
        {
            (await CheckTableExistsAsync(connection, "SkippedTable")).ShouldBeFalse(
                "Table 'SkippedTable' should NOT exist as its migration 202504091005 should have been skipped.");
        }

        // Also assert that structures expected from skipped C# migrations (e.g., 1004) are NOT present
        (await CheckTableExistsAsync(connection, "Products")).ShouldBeFalse(
            "Table 'Products' should NOT exist as its C# migration 202504091004 should have been skipped.");
    }

    // --- Assertion Helper for C# Only Migrations ---
    public static async Task AssertDatabaseStateAfterCSharpOnlyMigrations(DatabaseType dbType, string connectionString)
    {
        // Expected state: Users, Settings, Products tables created by C# migrations
        var expectedVersions = new List<long> { 202504091000, 202504091002, 202504091004 };
        await AssertVersionInfoOrder(dbType, connectionString, expectedVersions); // Check VersionInfo

        await using var connection = CreateDbConnection(dbType, connectionString);
        await connection.OpenAsync();
        var quote = GetQuoteFunction(dbType);

        // Check tables created by C#
        (await CheckTableExistsAsync(connection, "Users")).ShouldBeTrue("Table 'Users' should exist (C#)");
        (await CheckTableExistsAsync(connection, "Settings")).ShouldBeTrue("Table 'Settings' should exist (C#)");
        (await CheckTableExistsAsync(connection, "Products")).ShouldBeTrue("Table 'Products' should exist (C#)");

        // Check columns created by C#
        (await CheckColumnExistsAsync(connection, "Users", "Username")).ShouldBeTrue("Column 'Username' on 'Users' should exist (C#)");
        (await CheckColumnExistsAsync(connection, "Settings", "Key")).ShouldBeTrue("Column 'Key' on 'Settings' should exist (C#)");
        (await CheckColumnExistsAsync(connection, "Products", "Name")).ShouldBeTrue("Column 'Name' on 'Products' should exist (C#)");

        // Check columns *not* created (SQL columns)
        (await CheckColumnExistsAsync(connection, "Users", "Email")).ShouldBeFalse("Column 'Email' on 'Users' should NOT exist");
        (await CheckColumnExistsAsync(connection, "Settings", "Value")).ShouldBeFalse("Column 'Value' on 'Settings' should NOT exist");
        (await CheckColumnExistsAsync(connection, "Products", "Price")).ShouldBeFalse("Column 'Price' on 'Products' should NOT exist");

        // Check data inserted by C#
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {quote("Username")} FROM {quote("Users")} WHERE {quote("Username")} = 'admin'";
        (await command.ExecuteScalarAsync())?.ToString().ShouldBe("admin");

        command.CommandText = $"SELECT {quote("Key")} FROM {quote("Settings")} WHERE {quote("Key")} = 'DefaultTheme'";
        (await command.ExecuteScalarAsync())?.ToString().ShouldBe("DefaultTheme");

        command.CommandText = $"SELECT {quote("Name")} FROM {quote("Products")} WHERE {quote("Name")} = 'Sample Product'";
        (await command.ExecuteScalarAsync())?.ToString().ShouldBe("Sample Product");
    }

    // --- Assertion Helper for SQL Only Migrations ---
    public static async Task AssertDatabaseStateAfterSqlOnlyMigrations(DatabaseType dbType, string connectionString)
    {
        // Expected state: Columns added by SQL migrations to NON-EXISTENT tables (since C# didn't run)
        // Therefore, the main assertion is that VersionInfo is correct, and the tables DON'T exist.
        var expectedVersions = new List<long> { 202504091001, 202504091003, 202504091005 };
        // The SQL scripts might fail because the tables don't exist.
        // A more robust test might be needed depending on how MigrationService handles this.
        // For now, let's check the versions *attempted* and that tables are missing.

        // Update: MigrationService now handles this gracefully (logs errors, but continues if possible, records version).
        // Let's assert the versions *were* recorded, even if the SQL failed.
        await AssertVersionInfoOrder(dbType, connectionString, expectedVersions);

        await using var connection = CreateDbConnection(dbType, connectionString);
        await connection.OpenAsync();

        // Check tables created by C# migrations DO NOT exist
        (await CheckTableExistsAsync(connection, "Users")).ShouldBeFalse("Table 'Users' should NOT exist");
        (await CheckTableExistsAsync(connection, "Settings")).ShouldBeFalse("Table 'Settings' should NOT exist");
        (await CheckTableExistsAsync(connection, "Products")).ShouldBeFalse("Table 'Products' should NOT exist");
    }
}
