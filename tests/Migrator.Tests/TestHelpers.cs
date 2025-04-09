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

    public static string PrepareTestMigrations(string testId, DatabaseType dbType)
    {
        var tempBaseDir = Path.Combine(Path.GetTempPath(), "MigratorTests", testId, Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempBaseDir);
        // Create a dedicated subfolder for the migrations
        var migrationsDir = Path.Combine(tempBaseDir, "migrations");
        Directory.CreateDirectory(migrationsDir);

        // --- Find ExampleMigrations project build output --- 
        var currentDir = AppContext.BaseDirectory;
        var solutionDir = currentDir;
        while (solutionDir != null && !Directory.GetFiles(solutionDir, "Migrator.sln").Any())
            solutionDir = Directory.GetParent(solutionDir)?.FullName;
        if (solutionDir == null)
            throw new DirectoryNotFoundException("Could not find solution directory (Migrator.sln).");
        var exampleMigrationsProjectDir = Path.Combine(solutionDir, "src", "ExampleMigrations");
        // --- End Find --- 

        // --- Find Build Output Path (containing ExampleMigrations.dll) --- 
        string[] configurations = ["Debug", "Release"];
        string[] targetFrameworks = ["net9.0", "netstandard2.0"];
        string? foundBuildOutputPath = null;
        var mainDllName = "ExampleMigrations.dll";

        foreach (var config in configurations)
        foreach (var tfm in targetFrameworks)
        {
            var potentialPath = Path.Combine(exampleMigrationsProjectDir, "bin", config, tfm);
            if (File.Exists(Path.Combine(potentialPath, mainDllName)))
            {
                foundBuildOutputPath = potentialPath;
                goto FoundBuildPath;
            }
        }

        FoundBuildPath:
        if (foundBuildOutputPath == null)
            throw new DirectoryNotFoundException(
                $"Could not find build output directory containing {mainDllName} for ExampleMigrations in Debug/Release for {string.Join('/', targetFrameworks)}.");
        var sourceAssemblyPath = Path.Combine(foundBuildOutputPath, mainDllName);
        // --- End Find Build Output --- 

        // Determine database-specific subfolder for SQL files
        var dbSpecificSubfolder = dbType switch
        {
            DatabaseType.SQLite => "sqlite",
            DatabaseType.PostgreSql => "postgresql",
            DatabaseType.SqlServer => "sqlserver",
            _ => throw new ArgumentOutOfRangeException(nameof(dbType), "Unsupported DB type for test preparation.")
        };
        var dbSqlSourcePath = Path.Combine(foundBuildOutputPath, dbSpecificSubfolder);

        // --- Create/Copy Migration Files --- 
        // No longer need AssemblyLoadContext or dummy DLLs here 
        /*
        var alc = new AssemblyLoadContext(name: $"ExampleMigrationsContext_{testId}", isCollectible: true);
        try
        {
            Assembly migrationAssembly = alc.LoadFromAssemblyPath(sourceAssemblyPath);

            // Create dummy DLL files named correctly for C# migrations
            var csMigrationTypes = migrationAssembly.GetExportedTypes()
                .Where(t => !t.IsAbstract && t.IsSubclassOf(typeof(Migration)) && t.GetCustomAttribute<MigrationAttribute>() != null);

            foreach(var migrationType in csMigrationTypes)
            {
                var migrationAttr = migrationType.GetCustomAttribute<MigrationAttribute>();
                if (migrationAttr != null)
                {
                    // Use 12 digits for the timestamp in the filename
                    string timestampStr = migrationAttr.Version.ToString("D12");
                    string dummyFileName = $"{timestampStr}_{migrationType.Name}.dll";
                    string destPath = Path.Combine(tempDir, dummyFileName);
                    // Create empty file with correct name - content is irrelevant for discovery
                    File.Create(destPath).Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            alc.Unload(); // Ensure unload on error
            throw new InvalidOperationException($"Failed during reflection/dummy file creation for {sourceAssemblyPath}.", ex);
        }
        finally
        {
            // Important: Unload the context to release the assembly file lock
            if (alc.IsCollectible)
            {
                alc.Unload();
            }
        }
        */

        // Copy the *actual* assembly containing C# migrations into the migrations subfolder
        var arbitraryDllName = "MyCustomNamedMigrations.dll";
        File.Copy(sourceAssemblyPath, Path.Combine(migrationsDir, arbitraryDllName), true); // Overwrite if exists

        // Copy relevant SQL migrations (from db-specific build output subfolder) into the migrations subfolder
        if (Directory.Exists(dbSqlSourcePath))
            foreach (var sourceSqlPath in Directory.EnumerateFiles(dbSqlSourcePath, "????????????_*.sql",
                         SearchOption.TopDirectoryOnly))
            {
                var fileName = Path.GetFileName(sourceSqlPath);
                if (SqlMigrationFileRegex.IsMatch(fileName))
                {
                    var destPath = Path.Combine(migrationsDir, fileName); // Copy to migrations subfolder
                    File.Copy(sourceSqlPath, destPath);
                }
            }
        // --- End Create/Copy --- 

        // Return the path to the migrations subfolder
        return migrationsDir;
    }

    public static void CleanupTestMigrations(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(directoryPath);
        try
        {
            // Go up one level from the migrations dir to delete the parent temp folder
            var parentDir = Directory.GetParent(directoryPath)?.FullName;
            if (!string.IsNullOrEmpty(parentDir) && Directory.Exists(parentDir))
                Directory.Delete(parentDir, true);
            else if (Directory.Exists(directoryPath)) // Fallback if parent isn't found
                Directory.Delete(directoryPath, true);
        }
        catch (Exception ex)
        {
            // Don't log warning for collectible context file lock issues, they might resolve
            if (!ex.Message.Contains("being used by another process"))
                Console.WriteLine($"Warning: Failed to cleanup test directory {directoryPath}: {ex.Message}");
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
            appliedMigrations.ShouldContain(ts, $"Timestamp {ts} not found in applied migrations.");

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
        (await command.ExecuteScalarAsync())?.ToString().ShouldBe("dark");

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

    // --- Methods for Interleaved Migration Testing ---

    /// <summary>
    ///     Returns the expected ordered list of migration timestamps for the interleaved test scenario.
    /// </summary>
    public static List<long> GetExpectedInterleavedVersions()
    {
        return
        [
            202504091000, // M..._CreateInitialSchema (C#)
            202504091001, // ..._AddUserEmail.sql (SQL)
            202504091002, // M..._CreateSettingsTable (C#)
            202504091003, // ..._AddSettingValue.sql (SQL)
            202504091004, // M..._CreateProductsTable (C#)
            202504091005
        ];
    }

    /// <summary>
    ///     Prepares a temporary directory structure containing the ExampleMigrations DLL
    ///     and specific SQL files with interleaved timestamps for testing.
    /// </summary>
    /// <param name="testId">Unique identifier for the test run.</param>
    /// <returns>Path to the created migrations' directory.</returns>
    public static string PrepareInterleavedMigrations(string testId)
    {
        var tempBaseDir = Path.Combine(Path.GetTempPath(), "MigratorTests", testId, Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempBaseDir);
        var migrationsDir = Path.Combine(tempBaseDir, "migrations");
        Directory.CreateDirectory(migrationsDir);

        // --- Locate and Copy ExampleMigrations.dll ---
        var sourceAssemblyPath = FindExampleMigrationsDllPath(); // Extracted logic
        if (string.IsNullOrEmpty(sourceAssemblyPath) || !File.Exists(sourceAssemblyPath))
            throw new FileNotFoundException(
                $"Could not find ExampleMigrations.dll at expected path: {sourceAssemblyPath}");
        // Copy the actual DLL
        var arbitraryDllName = "InterleavedTestMigrations.dll";
        File.Copy(sourceAssemblyPath, Path.Combine(migrationsDir, arbitraryDllName), true);
        // --- End Copy DLL ---

        // --- Create Specific Interleaved SQL Files (SQLite Syntax) ---
        // These timestamps interleave with the C# migrations in ExampleMigrations.dll

        // 202504091001_AddUserEmail.sql (Remains the same)
        File.WriteAllText(Path.Combine(migrationsDir, "202504091001_AddUserEmail.sql"),
            "ALTER TABLE \"Users\" ADD COLUMN \"Email\" TEXT; UPDATE \"Users\" SET \"Email\" = 'admin@example.com' WHERE \"Username\" = 'admin';"
        );

        // 202504091003_AddSettingValue.sql (Update WHERE clause)
        File.WriteAllText(Path.Combine(migrationsDir, "202504091003_AddSettingValue.sql"),
            "ALTER TABLE \"Settings\" ADD COLUMN \"Value\" TEXT; UPDATE \"Settings\" SET \"Value\" = 'DefaultValue' WHERE \"Key\" = 'DefaultTheme';"
        );

        // 202504091005_AddProductPrice.sql (Remains the same)
        File.WriteAllText(Path.Combine(migrationsDir, "202504091005_AddProductPrice.sql"),
            "ALTER TABLE \"Products\" ADD COLUMN \"Price\" REAL; UPDATE \"Products\" SET \"Price\" = 99.99 WHERE \"Name\" = 'Sample Product';"
        );
        // --- End Create SQL Files ---

        return migrationsDir;
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
        readerProduct.GetDouble(1).ShouldBe(99.99);
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
            if (retryCount > 0) await Task.Delay(200); // Wait only on retry
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

    // --- Helper to find ExampleMigrations.dll path (extracted from PrepareTestMigrations) ---
    private static string? FindExampleMigrationsDllPath()
    {
        // --- Find ExampleMigrations project build output ---
        var currentDir = AppContext.BaseDirectory;
        var solutionDir = currentDir;
        while (solutionDir != null && !Directory.GetFiles(solutionDir, "Migrator.sln").Any())
            solutionDir = Directory.GetParent(solutionDir)?.FullName;
        if (solutionDir == null)
        {
            Console.WriteLine("Error: Could not find solution directory (Migrator.sln).");
            return null; // Indicate failure
        }

        var exampleMigrationsProjectDir = Path.Combine(solutionDir, "src", "ExampleMigrations");
        // --- End Find ---

        // --- Find Build Output Path (containing ExampleMigrations.dll) ---
        string[] configurations = ["Debug", "Release"];
        // Check common TFMs - adjust if your project uses different ones
        string[] targetFrameworks = ["net9.0", "net8.0", "netstandard2.0"];
        var mainDllName = "ExampleMigrations.dll";

        foreach (var config in configurations)
        foreach (var tfm in targetFrameworks)
        {
            var potentialPath = Path.Combine(exampleMigrationsProjectDir, "bin", config, tfm);
            if (File.Exists(Path.Combine(potentialPath, mainDllName)))
                return Path.Combine(potentialPath, mainDllName); // Return full path to DLL
        }

        Console.WriteLine(
            $"Error: Could not find {mainDllName} in {exampleMigrationsProjectDir}/bin/[Debug|Release]/[{string.Join('|', targetFrameworks)}]");
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

    // --- Methods for Failure Handling Testing ---

    /// <summary>
    ///     Prepares a migrations directory including a faulty SQL script.
    ///     Creates the initial C# migrations (via DLL copy) and the first SQL migration (1001),
    ///     then adds a faulty SQL script (faultyVersion, e.g., 1003)
    ///     and a subsequent valid SQL script (skippedSqlVersion, e.g., 1005) that should be skipped.
    ///     Note: C# migrations 1000, 1002, 1004 are included via the DLL.
    /// </summary>
    public static string PrepareMigrationsWithFailure(string testId, long faultyVersion, long skippedSqlVersion)
    {
        var tempBaseDir = Path.Combine(Path.GetTempPath(), "MigratorTests", testId, Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempBaseDir);
        var migrationsDir = Path.Combine(tempBaseDir, "migrations");
        Directory.CreateDirectory(migrationsDir);

        // 1. Copy ExampleMigrations DLL (contains C# migrations 1000, 1002, 1004)
        var sourceAssemblyPath = FindExampleMigrationsDllPath();
        if (string.IsNullOrEmpty(sourceAssemblyPath) || !File.Exists(sourceAssemblyPath))
            throw new FileNotFoundException(
                $"Could not find ExampleMigrations.dll at expected path: {sourceAssemblyPath}");
        File.Copy(sourceAssemblyPath, Path.Combine(migrationsDir, "FailureTestMigrations.dll"), true);

        // 2. Create the first successful SQL migration (1001)
        File.WriteAllText(Path.Combine(migrationsDir, "202504091001_AddUserEmail.sql"),
            "ALTER TABLE \"Users\" ADD COLUMN \"Email\" TEXT; UPDATE \"Users\" SET \"Email\" = 'admin@example.com' WHERE \"Username\" = 'admin';"
        );

        // 3. Create the FAULTY SQL migration (using faultyVersion, e.g., 1003)
        File.WriteAllText(Path.Combine(migrationsDir, $"{faultyVersion}_FaultyScript.sql"),
            "ALTER TABLE NonExistentTable ADD COLUMN Oops TEXT;" // Invalid SQL
        );

        // 4. Create a subsequent, VALID SQL migration that should NOT be run (using skippedSqlVersion, e.g., 1005)
        File.WriteAllText(Path.Combine(migrationsDir, $"{skippedSqlVersion}_ShouldNotRun.sql"),
            "CREATE TABLE \"SkippedTable\" (Id INTEGER PRIMARY KEY);"
        );

        return migrationsDir;
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
                // Assuming the Version column is BIGINT or similar
                versions.Add(reader.GetInt64(0));
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
            (await CheckTableExistsAsync(connection, "SkippedTable")).ShouldBeFalse(
                "Table 'SkippedTable' should NOT exist as its migration 202504091005 should have been skipped.");

        // Also assert that structures expected from skipped C# migrations (e.g., 1004) are NOT present
        (await CheckTableExistsAsync(connection, "Products")).ShouldBeFalse(
            "Table 'Products' should NOT exist as its C# migration 202504091004 should have been skipped.");
    }
}