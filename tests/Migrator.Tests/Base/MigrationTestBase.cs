using System.Data.Common;
using System.Threading.Tasks;
using DotNet.Testcontainers.Containers;
using FluentMigrator.Runner;
using FluentMigrator.Runner.Processors;
using FluentMigrator.Runner.VersionTableInfo;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Migrator.Core;
using Migrator.Core.Abstractions;
using Migrator.Core.Factories;
using Migrator.Core.Handlers;
using Migrator.Core.Infrastructure;
using Migrator.Tests.Logging;
using Npgsql;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Migrator.Tests.Base;

/// <summary>
/// Base class for migration integration tests, managing container lifetime per test.
/// </summary>
public abstract class MigrationTestBase : IAsyncLifetime
{
    protected readonly ITestOutputHelper OutputHelper;
    protected IServiceProvider ServiceProvider = null!;
    protected ILoggerFactory TestLoggerFactory = null!;
    protected readonly string BaseMigrationsPath;
    private readonly InMemoryLoggerProvider _inMemoryLoggerProvider;

    private IContainer _container = null!;
    private string _connectionString = string.Empty;
    private DatabaseType _dbType;

    protected abstract IContainer BuildTestContainer();
    protected abstract DatabaseType GetTestDatabaseType();

    protected MigrationTestBase(ITestOutputHelper outputHelper)
    {
        OutputHelper = outputHelper;
        _inMemoryLoggerProvider = new InMemoryLoggerProvider(outputHelper.WriteLine);

        TestLoggerFactory = LoggerFactory.Create(builder =>
        {
            builder
                .AddProvider(_inMemoryLoggerProvider)
                .SetMinimumLevel(LogLevel.Trace);
        });

        BaseMigrationsPath = Path.Combine(AppContext.BaseDirectory, "TestMigrations");
        Directory.CreateDirectory(BaseMigrationsPath);
    }

    private void ConfigureServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton(TestLoggerFactory);
        services.AddLogging();

        services.AddScoped<IMigrationScopeFactory, MigrationScopeFactory>();
        services.AddScoped<MigrationService>();

        services.AddFluentMigratorCore()
            .ConfigureRunner(rb =>
            {
                switch (_dbType)
                {
                    case DatabaseType.SqlServer: rb.AddSqlServer(); break;
                    case DatabaseType.PostgreSql: rb.AddPostgres(); break;
                    case DatabaseType.SQLite: rb.AddSQLite(); break;
                    default: throw new InvalidOperationException($"Unsupported DB type: {_dbType}");
                }
            })
            .AddLogging(lb => lb.AddFluentMigratorConsole())
            .Configure<FluentMigratorLoggerOptions>(options => { options.ShowSql = true; options.ShowElapsedTime = true; })
            .Configure<ProcessorOptions>(opt => { opt.Timeout = TimeSpan.FromSeconds(120); });

        services.AddScoped<CustomVersionTableMetaData>();
        services.AddScoped<IVersionTableMetaData, CustomVersionTableMetaData>();

        services.AddScoped<IMigrationContext, MigrationContext>();

        ServiceProvider = services.BuildServiceProvider(validateScopes: true);
    }

    public virtual async Task InitializeAsync()
    {
        var logger = TestLoggerFactory.CreateLogger($"{GetType().Name}.InitializeAsync");
        logger.LogInformation("=== Starting Test Initialize ===");

        _dbType = GetTestDatabaseType();
        logger.LogInformation("Test Database Type: {DbType}", _dbType);

        logger.LogInformation("Building container/wrapper for {DbType}...", _dbType);
        _container = BuildTestContainer();
        if (_container == null)
        {
            throw new InvalidOperationException($"BuildTestContainer returned null for DbType: {_dbType}. This should not happen.");
        }

        logger.LogInformation("Starting container/wrapper for {DbType}...", _dbType);
        await _container.StartAsync();
        logger.LogInformation("Container/wrapper {ContainerName} ({ContainerId}) started.", _container.Name, _container.Id);

        if (_container is IDatabaseContainer dbContainer)
        {
            _connectionString = dbContainer.GetConnectionString();
            if (string.IsNullOrEmpty(_connectionString))
            {
                throw new InvalidOperationException($"GetConnectionString returned null or empty for DbType: {_dbType}.");
            }
            logger.LogInformation("Retrieved Connection String for {DbType} (Details may be omitted).", _dbType);
        }
        else
        {
            throw new InvalidOperationException($"Container for {_dbType} does not implement IDatabaseContainer.");
        }

        logger.LogInformation("Configuring ServiceProvider for test (Using DbType: {DbType})...", _dbType);
        ConfigureServiceProvider();
        logger.LogInformation("ServiceProvider configured.");

        logger.LogInformation("Starting pre-test database schema cleanup for {DbType}...", _dbType);
        string versionTableName = "";

        try
        {
            string? schemaName = null;
            using (var scope = ServiceProvider.CreateScope())
            {
                var versionTableMeta = scope.ServiceProvider.GetRequiredService<IVersionTableMetaData>();
                versionTableName = versionTableMeta.TableName;
                schemaName = versionTableMeta.SchemaName;
                logger.LogDebug("Resolved VersionTableMetaData: Schema='{Schema}', Table='{Table}'", schemaName ?? "(default)", versionTableName);
            }

            if (string.IsNullOrWhiteSpace(versionTableName))
            {
                throw new InvalidOperationException("Could not resolve version table name. Aborting cleanup.");
            }

            await using var connection = await CreateDbConnectionAsync(_dbType, _connectionString);
            await using var command = connection.CreateCommand();

            if (_dbType == DatabaseType.PostgreSql)
            {
                logger.LogInformation("Dropping existing user tables in public schema (PostgreSQL)...");
                var getUserTablesSql = $"SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND table_name != '{versionTableName}';";
                command.CommandText = getUserTablesSql;
                var tablesToDrop = new List<string>();
                try
                {
                    await using var reader = await command.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        tablesToDrop.Add(reader.GetString(0));
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to query user tables for cleanup. Some tables might persist.");
                }

                if (tablesToDrop.Count > 0)
                {
                    foreach (var tableNameToDrop in tablesToDrop)
                    {
                        try
                        {
                            var dropUserTableSql = $"DROP TABLE IF EXISTS public.\"{tableNameToDrop}\" CASCADE;";
                            command.CommandText = dropUserTableSql;
                            logger.LogDebug("Executing user table drop: {Sql}", dropUserTableSql);
                            await command.ExecuteNonQueryAsync();
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "Failed to drop user table '{TableName}'. It might persist.", tableNameToDrop);
                        }
                    }
                    logger.LogInformation("Finished dropping {Count} user tables.", tablesToDrop.Count);
                }
                else
                {
                    logger.LogInformation("No user tables found in public schema to drop.");
                }
            }

            logger.LogInformation("Dropping VersionInfo table ('{Table}')...", versionTableName);
            string dropVersionTableSql;
            switch (_dbType)
            {
                case DatabaseType.PostgreSql:
                    dropVersionTableSql = string.IsNullOrWhiteSpace(schemaName)
                        ? $"DROP TABLE IF EXISTS \"{versionTableName}\" CASCADE;"
                        : $"DROP TABLE IF EXISTS \"{schemaName}\".\"{versionTableName}\" CASCADE;";
                    break;
                case DatabaseType.SqlServer:
                    schemaName = string.IsNullOrWhiteSpace(schemaName) ? "dbo" : schemaName;
                    dropVersionTableSql = $"IF OBJECT_ID('[{schemaName}].[{versionTableName}]', 'U') IS NOT NULL DROP TABLE [{schemaName}].[{versionTableName}];";
                    break;
                case DatabaseType.SQLite:
                    dropVersionTableSql = $"DROP TABLE IF EXISTS \"{versionTableName}\";";
                    break;
                default:
                    logger.LogWarning("Version table cleanup not implemented for {DbType}.", _dbType);
                    throw new NotSupportedException($"Cleanup not implemented for {_dbType}");
            }
            command.CommandText = dropVersionTableSql;
            logger.LogDebug("Executing VersionInfo table drop: {Sql}", dropVersionTableSql);
            await command.ExecuteNonQueryAsync();
            logger.LogInformation("Successfully dropped version table '{Table}' (if it existed).", versionTableName);

        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during pre-test database schema cleanup for {DbType} (Table: {Table}). Test execution might be affected.", _dbType, versionTableName ?? "<unknown>");
            throw;
        }
        finally
        {
            logger.LogInformation("Database schema cleanup finished for {DbType}.", _dbType);
        }
        logger.LogInformation("=== Finished Test Initialize ===");
    }

    public virtual async Task DisposeAsync()
    {
        var logger = TestLoggerFactory.CreateLogger($"{GetType().Name}.DisposeAsync");
        logger.LogInformation("=== Starting Test Dispose ===");

        if (ServiceProvider is IAsyncDisposable asyncDisposableProvider)
        {
            logger.LogDebug("Disposing ServiceProvider asynchronously...");
            await asyncDisposableProvider.DisposeAsync();
        }
        else if (ServiceProvider is IDisposable disposableProvider)
        {
            logger.LogDebug("Disposing ServiceProvider...");
            disposableProvider.Dispose();
        }

        if (_container != null)
        {
            logger.LogInformation("Stopping and disposing container/wrapper {ContainerName} ({ContainerId})...", _container.Name, _container.Id);
            try
            {
                await _container.StopAsync();
                await _container.DisposeAsync();
                logger.LogInformation("Container/wrapper stopped and disposed.");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error stopping or disposing container/wrapper {ContainerName} ({ContainerId}).", _container.Name, _container.Id);
            }
        }
        else
        {
            logger.LogWarning("Dispose: Container was unexpectedly null for {DbType}. Cleanup might be incomplete.", _dbType);
        }

        logger.LogInformation("=== Finished Test Dispose ===");
    }

    protected string GetConnectionString()
    {
        if (string.IsNullOrEmpty(_connectionString))
        {
            throw new InvalidOperationException("Connection string not initialized. Ensure InitializeAsync has run.");
        }
        return _connectionString;
    }

    protected string GetTestSpecificMigrationsPath(string testId, DatabaseType dbType)
    {
        var path = Path.Combine(BaseMigrationsPath, $"{dbType}_{testId}");

        if (Directory.Exists(path))
        {
            OutputHelper.WriteLine($"Clearing existing test migration directory: {path}");
            Directory.Delete(path, recursive: true);
        }

        Directory.CreateDirectory(path);
        OutputHelper.WriteLine($"Using migrations path: {path}");
        return path;
    }

    protected async Task RunMigrationsAsync(DatabaseType dbType, string connectionString, string migrationsPath)
    {
        using var scope = ServiceProvider.CreateScope();
        var migrationService = scope.ServiceProvider.GetRequiredService<MigrationService>();
        var logger = TestLoggerFactory.CreateLogger(GetType());
        logger.LogInformation("Executing migrations (Base Class) for DB={DbType}, Path={Path}", dbType, migrationsPath);
        OutputHelper.WriteLine($"Executing migrations for {dbType} at {migrationsPath}...");
        await migrationService.ExecuteMigrationsAsync(dbType, connectionString, migrationsPath);
        OutputHelper.WriteLine("Migration execution completed.");
        logger.LogInformation("Migration execution completed (Base Class).");
    }

    protected Task AssertMigrationAppliedAsync(DatabaseType dbType, string connectionString, string migrationsPath,
        long expectedVersion)
    {
        var logger = TestLoggerFactory.CreateLogger(GetType());
        logger.LogDebug("Asserting migration {Version} applied for {DbType}...", expectedVersion, dbType);
        OutputHelper.WriteLine($"Asserting migration {expectedVersion} applied...");

        using var scope = ServiceProvider.CreateScope();
        var scopedProvider = scope.ServiceProvider;

        var assertScopeFactory = scopedProvider.GetRequiredService<IMigrationScopeFactory>();
        using var assertScope = assertScopeFactory.CreateMigrationScope(dbType, connectionString, migrationsPath);
        try
        {
            var versionLoader = assertScope.ServiceProvider.GetRequiredService<IVersionLoader>();
            versionLoader.LoadVersionInfo();

            Assert.True(versionLoader.VersionInfo.HasAppliedMigration(expectedVersion),
                $"Migration {expectedVersion} should have been applied.");
            OutputHelper.WriteLine($"Assertion SUCCESS: Migration {expectedVersion} was applied.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Assertion failed for migration {Version}", expectedVersion);
            OutputHelper.WriteLine($"Assertion FAILED for migration {expectedVersion}: {ex.Message}");
            throw;
        }

        return Task.CompletedTask;
    }

    protected Task AssertMigrationNotAppliedAsync(DatabaseType dbType, string connectionString,
        string migrationsPath, long expectedMissingVersion)
    {
        var logger = TestLoggerFactory.CreateLogger(GetType());
        logger.LogDebug("Asserting migration {Version} NOT applied for {DbType}...", expectedMissingVersion, dbType);
        OutputHelper.WriteLine($"Asserting migration {expectedMissingVersion} NOT applied...");

        using var scope = ServiceProvider.CreateScope();
        var scopedProvider = scope.ServiceProvider;

        var assertScopeFactory = scopedProvider.GetRequiredService<IMigrationScopeFactory>();
        using var assertScope = assertScopeFactory.CreateMigrationScope(dbType, connectionString, migrationsPath);
        try
        {
            var versionLoader = assertScope.ServiceProvider.GetRequiredService<IVersionLoader>();
            versionLoader.LoadVersionInfo();
            Assert.False(versionLoader.VersionInfo.HasAppliedMigration(expectedMissingVersion),
                $"Migration {expectedMissingVersion} should NOT have been applied.");
            OutputHelper.WriteLine($"Assertion SUCCESS: Migration {expectedMissingVersion} was NOT applied.");
        }
        catch (Xunit.Sdk.FalseException ex)
        {
            logger.LogError(ex, "Assertion (for NOT applied) failed for migration {Version}", expectedMissingVersion);
            using var errorScope = assertScopeFactory.CreateMigrationScope(dbType, connectionString, migrationsPath);
            var errorContext = errorScope.ServiceProvider.GetRequiredService<IMigrationContext>();
            var errorVersionLoader = errorContext.VersionLoader;
            errorVersionLoader.LoadVersionInfo();
            var appliedVersions = string.Join(", ", errorVersionLoader.VersionInfo.AppliedMigrations().OrderBy(v => v));
            logger.LogInformation("Failure Details: Currently applied migrations reported by VersionLoader: [{AppliedVersions}]", appliedVersions);
            throw;
        }

        return Task.CompletedTask;
    }

    protected void CleanupMigrationsPath(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            Directory.Delete(path, true);
            OutputHelper.WriteLine($"Cleaned up migrations path: {path}");
        }
        catch (Exception ex)
        {
            OutputHelper.WriteLine($"WARNING: Failed to clean up migrations path '{path}': {ex.Message}");
        }
    }

    protected async Task<DbConnection> CreateDbConnectionAsync(DatabaseType dbType, string connectionString)
    {
        var cs = GetConnectionString();
        connectionString = cs;
        dbType = _dbType;

        DbConnection connection = dbType switch
        {
            DatabaseType.SqlServer => new SqlConnection(connectionString),
            DatabaseType.PostgreSql => new NpgsqlConnection(connectionString),
            DatabaseType.SQLite => new SqliteConnection(connectionString),
            _ => throw new ArgumentOutOfRangeException(nameof(dbType),
                $"Database type {dbType} not supported for connections.")
        };
        await connection.OpenAsync();
        return connection;
    }

    protected async Task<long> GetVersionInfoRowCountAsync(DatabaseType dbType, string connectionString,
        string migrationsPath)
    {
        var logger = TestLoggerFactory.CreateLogger(GetType());
        using var scope = ServiceProvider.CreateScope();
        var scopedProvider = scope.ServiceProvider;
        var scopeFactory = scopedProvider.GetRequiredService<IMigrationScopeFactory>();

        using var migrationScope = scopeFactory.CreateMigrationScope(_dbType, _connectionString, migrationsPath);
        var versionTableMeta = migrationScope.ServiceProvider.GetRequiredService<IVersionTableMetaData>();
        var tableName = versionTableMeta.TableName;
        var schemaName = versionTableMeta.SchemaName;

        return await GetTableRowCountAsync(_dbType, _connectionString, tableName, schemaName);
    }

    protected async Task<long> GetTableRowCountAsync(DatabaseType dbType, string connectionString, string tableName,
            string? schemaName = null)
    {
        OutputHelper.WriteLine($"Counting rows in table '{tableName}' for {dbType}...");
        await using var connection = await CreateDbConnectionAsync(dbType, connectionString);
        await using var command = connection.CreateCommand();
        string sql;
        switch (dbType)
        {
            case DatabaseType.SqlServer:
                schemaName ??= "dbo";
                sql = $"SELECT COUNT(*) FROM [{schemaName}].[{tableName}];";
                command.Parameters.Add(new SqlParameter("@SchemaName", schemaName));
                command.Parameters.Add(new SqlParameter("@TableName", tableName));
                break;
            case DatabaseType.PostgreSql:
                schemaName ??= "public";
                sql = $"SELECT COUNT(*) FROM \"{schemaName}\".\"{tableName}\";";
                command.Parameters.Add(new NpgsqlParameter("@SchemaName", schemaName));
                command.Parameters.Add(new NpgsqlParameter("@TableName", tableName));
                break;
            case DatabaseType.SQLite:
                sql = $"SELECT COUNT(*) FROM \"{tableName}\";";
                command.Parameters.Add(new SqliteParameter("@TableName", tableName));
                break;
            default:
                OutputHelper.WriteLine($"----> Row count SKIPPED: Not implemented for {dbType}.");
                Assert.Fail($"Row count not implemented for {dbType}.");
                return -1;
        }
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync();
        var count = Convert.ToInt64(result ?? 0L);
        OutputHelper.WriteLine($"----> Row count for '{tableName}': {count}");
        return count;
    }

    protected async Task PrepareSqlFile(string path, long version, string content)
    {
        Directory.CreateDirectory(path);
        var fileName = $"{version}_TestScript.sql";
        var filePath = Path.Combine(path, fileName);
        await File.WriteAllTextAsync(filePath, content);
        OutputHelper.WriteLine($"Prepared SQL file: {filePath}");
    }

    protected async Task PrepareCSharpMigrationDll(string migrationsPath)
    {
        const string sourceDllName = "ExampleMigrations.dll";
        var sourceDllPath = Path.Combine(AppContext.BaseDirectory, sourceDllName);
        var targetDllPath = Path.Combine(migrationsPath, sourceDllName);

        if (!File.Exists(sourceDllPath))
        {
            OutputHelper.WriteLine($"Searching for migration DLL in: {AppContext.BaseDirectory}");
            throw new FileNotFoundException($"Required migration DLL '{sourceDllName}' not found at the expected location: {sourceDllPath}. Ensure the 'ExampleMigrations' project is built and referenced correctly by the 'Migrator.Tests' project.", sourceDllPath);
        }

        try
        {
            Directory.CreateDirectory(migrationsPath);
            File.Copy(sourceDllPath, targetDllPath, true);
            OutputHelper.WriteLine($"Prepared C# migration DLL: Copied '{sourceDllName}' to '{migrationsPath}'");
        }
        catch (Exception ex)
        {
            OutputHelper.WriteLine($"ERROR copying C# migration DLL from '{sourceDllPath}' to '{targetDllPath}': {ex.Message}");
            throw;
        }
        await Task.CompletedTask;
    }

    protected void ClearCapturedLogs()
    {
        _inMemoryLoggerProvider.ClearLogEntries();
    }

    protected List<InMemoryLogEntry> GetCapturedLogs()
    {
        return (List<InMemoryLogEntry>)_inMemoryLoggerProvider.GetLogEntries();
    }

    protected void AssertLogContainsSubstring(LogLevel expectedLevel, string expectedSubstring, string? message = null)
    {
        var logs = GetCapturedLogs();
        Assert.Contains(logs, log => log.Level == expectedLevel && log.Message.Contains(expectedSubstring, StringComparison.OrdinalIgnoreCase));
    }

    protected void AssertLogDoesNotContainSubstring(LogLevel level, string substring, string? message = null)
    {
        var logs = GetCapturedLogs();
        Assert.DoesNotContain(logs, log => log.Level == level && log.Message.Contains(substring, StringComparison.OrdinalIgnoreCase));
    }

    protected async Task AssertTableExistsAsync(DatabaseType dbType, string connectionString, string tableName,
       string? schemaName = null)
    {
        OutputHelper.WriteLine($"Asserting table '{tableName}' exists for {dbType}...");
        dbType = _dbType;
        connectionString = GetConnectionString();

        await using var connection = await CreateDbConnectionAsync(dbType, connectionString);
        await using var command = connection.CreateCommand();

        string sql;
        switch (dbType)
        {
            case DatabaseType.SqlServer:
                schemaName ??= "dbo";
                sql =
                    $"SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = @SchemaName AND TABLE_NAME = @TableName";
                command.Parameters.Add(new SqlParameter("@SchemaName", schemaName));
                command.Parameters.Add(new SqlParameter("@TableName", tableName));
                break;
            case DatabaseType.PostgreSql:
                schemaName ??= "public";
                sql =
                    $"SELECT 1 FROM information_schema.tables WHERE table_schema = @SchemaName AND table_name = @TableName";
                command.Parameters.Add(new NpgsqlParameter("@SchemaName", schemaName));
                command.Parameters.Add(new NpgsqlParameter("@TableName", tableName));
                break;
            case DatabaseType.SQLite:
                sql = $"SELECT 1 FROM sqlite_master WHERE type='table' AND name=@TableName";
                command.Parameters.Add(new SqliteParameter("@TableName", tableName));
                break;
            default:
                OutputHelper.WriteLine($"----> Assertion SKIPPED: Table existence check not implemented for {dbType}.");
                Assert.Fail($"Table existence check not implemented for {dbType}.");
                return;
        }

        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync();
        Assert.True(result != null && result != DBNull.Value,
            $"Table '{schemaName ?? ""}'.'{tableName}' should exist but was not found.");
        OutputHelper.WriteLine($"----> Assertion successful: Table '{schemaName ?? ""}'.'{tableName}' exists.");
    }

    protected async Task AssertTableDoesNotExistAsync(DatabaseType dbType, string connectionString, string tableName,
        string? schemaName = null)
    {
        OutputHelper.WriteLine($"Asserting table '{tableName}' does NOT exist for {dbType}...");
        dbType = _dbType;
        connectionString = GetConnectionString();

        await using var connection = await CreateDbConnectionAsync(dbType, connectionString);
        await using var command = connection.CreateCommand();

        string sql;
        switch (dbType)
        {
            case DatabaseType.SqlServer:
                schemaName ??= "dbo";
                sql =
                    $"SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = @SchemaName AND TABLE_NAME = @TableName";
                command.Parameters.Add(new SqlParameter("@SchemaName", schemaName));
                command.Parameters.Add(new SqlParameter("@TableName", tableName));
                break;
            case DatabaseType.PostgreSql:
                schemaName ??= "public";
                sql =
                    $"SELECT 1 FROM information_schema.tables WHERE table_schema = @SchemaName AND table_name = @TableName";
                command.Parameters.Add(new NpgsqlParameter("@SchemaName", schemaName));
                command.Parameters.Add(new NpgsqlParameter("@TableName", tableName));
                break;
            case DatabaseType.SQLite:
                sql = $"SELECT 1 FROM sqlite_master WHERE type='table' AND name=@TableName";
                command.Parameters.Add(new SqliteParameter("@TableName", tableName));
                break;
            default:
                OutputHelper.WriteLine(
                    $"----> Assertion SKIPPED: Table non-existence check not implemented for {dbType}.");
                Assert.Fail($"Table non-existence check not implemented for {dbType}.");
                return;
        }

        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync();
        Assert.True(result == null || result == DBNull.Value,
            $"Table '{schemaName ?? ""}'.'{tableName}' should NOT exist but was found.");
        OutputHelper.WriteLine($"----> Assertion successful: Table '{schemaName ?? ""}'.'{tableName}' does not exist.");
    }

}
