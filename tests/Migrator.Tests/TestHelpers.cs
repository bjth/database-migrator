using FluentMigrator.Runner;
using FluentMigrator.Runner.Initialization;
using FluentMigrator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Migrator.Core;
using Shouldly;
using System.Reflection;
using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Npgsql;
using Xunit; 
using System.Text.RegularExpressions;
using System.Runtime.Loader; // Needed for AssemblyLoadContext

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
        {
            solutionDir = Directory.GetParent(solutionDir)?.FullName;
        }
        if (solutionDir == null)
        {
            throw new DirectoryNotFoundException("Could not find solution directory (Migrator.sln).");
        }
        var exampleMigrationsProjectDir = Path.Combine(solutionDir, "src", "ExampleMigrations");
        // --- End Find --- 

        // --- Find Build Output Path (containing ExampleMigrations.dll) --- 
        string[] configurations = ["Debug", "Release"];
        string[] targetFrameworks = ["net9.0", "netstandard2.0"]; 
        string? foundBuildOutputPath = null;
        string mainDllName = "ExampleMigrations.dll";
        
        foreach (var config in configurations)
        {
            foreach (var tfm in targetFrameworks)
            {
                var potentialPath = Path.Combine(exampleMigrationsProjectDir, "bin", config, tfm);
                if (File.Exists(Path.Combine(potentialPath, mainDllName)))
                {
                    foundBuildOutputPath = potentialPath;
                    goto FoundBuildPath; 
                }
            }
        }

    FoundBuildPath:
        if (foundBuildOutputPath == null)
        {
            throw new DirectoryNotFoundException($"Could not find build output directory containing {mainDllName} for ExampleMigrations in Debug/Release for {string.Join('/', targetFrameworks)}.");
        }
        var sourceAssemblyPath = Path.Combine(foundBuildOutputPath, mainDllName);
        // --- End Find Build Output --- 

        // Determine database-specific subfolder for SQL files
        string dbSpecificSubfolder = dbType switch
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
        string arbitraryDllName = "MyCustomNamedMigrations.dll"; 
        File.Copy(sourceAssemblyPath, Path.Combine(migrationsDir, arbitraryDllName), true); // Overwrite if exists

        // Copy relevant SQL migrations (from db-specific build output subfolder) into the migrations subfolder
        if (Directory.Exists(dbSqlSourcePath))
        {
            foreach (var sourceSqlPath in Directory.EnumerateFiles(dbSqlSourcePath, "????????????_*.sql", SearchOption.TopDirectoryOnly))
            {
                var fileName = Path.GetFileName(sourceSqlPath);
                if (SqlMigrationFileRegex.IsMatch(fileName))
                {
                    var destPath = Path.Combine(migrationsDir, fileName); // Copy to migrations subfolder
                    File.Copy(sourceSqlPath, destPath);
                }
            }
        }
        // --- End Create/Copy --- 

        // Return the path to the migrations subfolder
        return migrationsDir;
    }

    public static void CleanupTestMigrations(string directoryPath)
    {
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
    public static async Task AssertDatabaseStateAfterMigrations(DatabaseType dbType, string connectionString, int expectedMigrationCount = 6)
    {
        var versionCheckServiceProvider = BuildVersionCheckServiceProvider(dbType, connectionString);
        using var scope = versionCheckServiceProvider.CreateScope();
        var versionLoader = scope.ServiceProvider.GetRequiredService<IVersionLoader>();

        // 1. Check VersionInfo table
        versionLoader.LoadVersionInfo();
        var appliedMigrations = versionLoader.VersionInfo.AppliedMigrations().ToList();

        // Expected 12-digit timestamps
        var expectedTimestamps = new List<long> { 
            202504091000, // M..._CreateInitialSchema 
            202504091001, // ..._AddUserEmail.sql
            202504091002, // M..._CreateSettingsTable
            202504091003, // ..._AddSettingValue.sql
            202504091004, // M..._CreateProductsTable
            202504091005  // ..._AddProductPrice.sql
        };

        // Allow for retries if DB is slow to update after migration
        int retryCount = 0;
        const int maxRetries = 5;
        while (appliedMigrations.Count < expectedMigrationCount && retryCount < maxRetries)
        {
            await Task.Delay(200); // Wait a bit
            versionLoader.LoadVersionInfo();
            appliedMigrations = versionLoader.VersionInfo.AppliedMigrations().ToList();
            retryCount++;
        }

        appliedMigrations.Count.ShouldBe(expectedMigrationCount, $"Expected {expectedMigrationCount} migrations, found {appliedMigrations.Count} after {retryCount} retries. Applied: [{string.Join(',', appliedMigrations)}]");
        foreach(var ts in expectedTimestamps)
        {
            appliedMigrations.ShouldContain(ts, $"Timestamp {ts} not found in applied migrations.");
        }

        // 2. Check actual schema/data using raw ADO.NET
        await using var connection = CreateDbConnection(dbType, connectionString);
        await connection.OpenAsync();

        // Define identifiers (handle quoting)
        Func<string, string> Quote = dbType switch 
        { 
            DatabaseType.PostgreSql => name => $"\"{name}\"",
            DatabaseType.SQLite => name => $"\"{name}\"",
            _ => name => $"[{name}]" // Default to SQL Server style
        };
        
        string usersTable = Quote("Users");
        string settingsTable = Quote("Settings");
        string productsTable = Quote("Products");
        string emailCol = Quote("Email");
        string usernameCol = Quote("Username");
        string keyCol = Quote("Key");
        string valueCol = Quote("Value");
        string nameCol = Quote("Name");
        string priceCol = Quote("Price");

        // Check tables exist (using original case for lookup)
        (await CheckTableExistsAsync(connection, "Users")).ShouldBeTrue("Table 'Users' should exist");
        (await CheckTableExistsAsync(connection, "Settings")).ShouldBeTrue("Table 'Settings' should exist");
        (await CheckTableExistsAsync(connection, "Products")).ShouldBeTrue("Table 'Products' should exist");

        // Check columns exist (using original case for lookup)
        (await CheckColumnExistsAsync(connection, "Users", "Email")).ShouldBeTrue("Column 'Email' on 'Users' should exist");
        (await CheckColumnExistsAsync(connection, "Settings", "Value")).ShouldBeTrue("Column 'Value' on 'Settings' should exist");
        (await CheckColumnExistsAsync(connection, "Products", "Price")).ShouldBeTrue("Column 'Price' on 'Products' should exist");

        // Check data
        await using (var command = connection.CreateCommand())
        {
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
                 rb.ScanIn([]).For.Migrations(); 
            })
            .AddLogging(lb => lb.SetMinimumLevel(LogLevel.Warning)); // Keep logging minimal for checks

        return serviceCollection.BuildServiceProvider(false);
    }

    private static DbConnection CreateDbConnection(DatabaseType dbType, string connectionString)
    {
        switch (dbType)
        {
            case DatabaseType.SqlServer: return new SqlConnection(connectionString);
            case DatabaseType.PostgreSql: return new NpgsqlConnection(connectionString);
            case DatabaseType.SQLite: return new SqliteConnection(connectionString);
            default: throw new ArgumentOutOfRangeException(nameof(dbType), "Unsupported database type for ADO.NET connection.");
        }
    }

    private static async Task<bool> CheckTableExistsAsync(DbConnection connection, string tableName)
    {
        // Using original case tableName parameter for lookups
        await using var command = connection.CreateCommand();
        if (connection is SqliteConnection)
        {
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@TableName";
            command.Parameters.Add(CreateParameter(command, "@TableName", tableName));
        }
        else if (connection is NpgsqlConnection)
        {
            command.CommandText = "SELECT COUNT(*) FROM pg_tables WHERE schemaname = 'public' AND tablename = @TableName";
            command.Parameters.Add(CreateParameter(command, "@TableName", tableName));
        }
        else // Assuming SQL Server
        {
            command.CommandText = $"SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = @TableName";
            command.Parameters.Add(CreateParameter(command, "@TableName", tableName));
        }
        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt32(result ?? 0) > 0;
    }

    private static async Task<bool> CheckColumnExistsAsync(DbConnection connection, string tableName, string columnName)
    {
        // Using original case tableName and columnName parameters for lookups
        await using var command = connection.CreateCommand();
         if (connection is SqliteConnection)
         {
            command.CommandText = $"SELECT COUNT(*) FROM pragma_table_info(@TableName) WHERE name=@ColumnName";
            command.Parameters.Add(CreateParameter(command, "@TableName", tableName)); 
            command.Parameters.Add(CreateParameter(command, "@ColumnName", columnName));
         }
         else if (connection is NpgsqlConnection)
         {
            command.CommandText = $"SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = 'public' AND table_name = @TableName AND column_name = @ColumnName";
            command.Parameters.Add(CreateParameter(command, "@TableName", tableName)); 
            command.Parameters.Add(CreateParameter(command, "@ColumnName", columnName));
         }
         else // Assuming SQL Server
         {
            command.CommandText = $"SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = @TableName AND COLUMN_NAME = @ColumnName";
            command.Parameters.Add(CreateParameter(command, "@TableName", tableName));
            command.Parameters.Add(CreateParameter(command, "@ColumnName", columnName));
         }
        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt32(result ?? 0) > 0;
    }

    private static DbParameter CreateParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        return parameter;
    }
} 