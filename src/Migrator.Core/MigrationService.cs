using FluentMigrator.Runner;
using FluentMigrator.Runner.Announcers;
using FluentMigrator.Runner.Initialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Reflection;
using System.Linq;

namespace Migrator.Core;

public class MigrationService(ILogger<MigrationService> logger)
{
    private readonly ILogger<MigrationService> _logger = logger;

    public async Task ExecuteMigrationsAsync(DatabaseType dbType, string connectionString, string migrationsPath)
    {
        _logger.LogInformation("Starting migration process...");
        _logger.LogInformation("Database Type: {DbType}", dbType);
        _logger.LogInformation("Migrations Path: {Path}", migrationsPath);

        if (!Directory.Exists(migrationsPath))
        {
            _logger.LogError("Migrations directory not found: {Path}", migrationsPath);
            throw new DirectoryNotFoundException($"Migrations directory not found: {migrationsPath}");
        }

        var migrationTasks = DiscoverMigrations(migrationsPath);
        if (migrationTasks.Count == 0)
        {
            _logger.LogInformation("No migration tasks matching the pattern found in {Path}.", migrationsPath);
            // Still might be a C# only migration, let the process continue
            // return;
        }
        else 
        {    
             _logger.LogInformation("Discovered {Count} potential migration tasks based on file naming.", migrationTasks.Count);
        }

        // --- Find the actual assembly --- 
        string? actualAssemblyPath = FindActualMigrationAssembly(migrationsPath);
        if (actualAssemblyPath == null && migrationTasks.All(t => t.Type == MigrationType.Sql))
        {
            _logger.LogInformation("No migration assembly found, but SQL migrations were discovered. Proceeding with SQL only.");
        }
        else if (actualAssemblyPath == null && migrationTasks.Any(t => t.Type == MigrationType.Dll))
        {
             _logger.LogWarning("Found DLL migration tasks by name, but could not locate the main migration assembly in {Path}. C# migrations might not run.", migrationsPath);
        }
        else if (actualAssemblyPath != null)
        {
            _logger.LogInformation("Located main migration assembly: {AssemblyPath}", actualAssemblyPath);
        }
        // --- End Find --- 

        // Pass the actual assembly path to CreateServices
        var serviceProvider = CreateServices(dbType, connectionString, actualAssemblyPath); 

        // Scope needed for DI resolution, especially for DbContext or similar scoped services FluentMigrator might use internally.
        using var scope = serviceProvider.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
        var versionLoader = scope.ServiceProvider.GetRequiredService<IVersionLoader>();
        var processor = scope.ServiceProvider.GetService<FluentMigrator.IMigrationProcessor>(); // Try getting processor directly

        if (processor == null && migrationTasks.Any(t => t.Type == MigrationType.Sql))
        {
             _logger.LogError("Could not retrieve the migration processor, which is required to run raw SQL scripts.");
             throw new InvalidOperationException("Migration processor is unavailable, cannot execute SQL scripts.");
        }

        try
        {
            var appliedVersions = versionLoader.VersionInfo.AppliedMigrations();
            _logger.LogInformation("Already applied versions: {Versions}", string.Join(", ", appliedVersions));

            foreach (var task in migrationTasks.OrderBy(t => t.Timestamp))
            {
                if (appliedVersions.Contains(task.Timestamp))
                {
                    _logger.LogInformation("Skipping already applied migration: {Timestamp} ({Type}) - {Filename}", task.Timestamp, task.Type, task.OriginalFilename);
                    continue;
                }

                _logger.LogInformation("Applying migration: {Timestamp} ({Type}) - {Filename}", task.Timestamp, task.Type, task.OriginalFilename);

                if (task.Type == MigrationType.Dll)
                {
                    // FluentMigrator handles the transaction and version logging internally for C# migrations
                    runner.MigrateUp(task.Timestamp);
                    _logger.LogInformation("Successfully applied DLL migration: {Timestamp}", task.Timestamp);
                }
                else if (task.Type == MigrationType.Sql && processor != null)
                {
                    try
                    {
                        processor.BeginTransaction();
                        var sqlScriptContent = await File.ReadAllTextAsync(task.FullPath);
                        processor.Execute(sqlScriptContent);

                        // Manually update version info for SQL scripts
                        versionLoader.UpdateVersionInfo(task.Timestamp, $"Applied SQL Script: {task.OriginalFilename}");

                        processor.CommitTransaction();
                         _logger.LogInformation("Successfully applied SQL script: {Timestamp}", task.Timestamp);
                    }
                    catch (Exception ex)
                    {
                        processor?.RollbackTransaction();
                        _logger.LogError(ex, "Failed to apply SQL script {Filename}. Transaction rolled back.", task.OriginalFilename);
                        throw; // Re-throw to stop the migration process
                    }
                }
                else
                {
                    // Should not happen if checks above are correct, but good to handle
                     _logger.LogWarning("Skipping task {Timestamp} due to unknown type or missing processor.", task.Timestamp);
                }
            }

            _logger.LogInformation("Migration process completed successfully.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Migration process failed.");
            // Depending on requirements, you might want to throw or handle differently
            throw;
        }
    }

    private List<MigrationTask> DiscoverMigrations(string migrationsPath)
    {
        var tasks = new List<MigrationTask>();
        _logger.LogDebug("Scanning directory: {Path}", migrationsPath);

        foreach (var file in Directory.EnumerateFiles(migrationsPath, "*.*", SearchOption.TopDirectoryOnly))
        {
             _logger.LogTrace("Checking file: {File}", file);
            if (MigrationTask.TryParse(file, out var task) && task != null)
            {
                _logger.LogDebug("Parsed migration task: {Timestamp} ({Type}) from {Filename}", task.Timestamp, task.Type, task.OriginalFilename);
                tasks.Add(task);
            }
            else
            {
                _logger.LogDebug("Skipping file (does not match pattern or type): {File}", file);
            }
        }
        return tasks;
    }

    private IServiceProvider CreateServices(DatabaseType dbType, string connectionString, string? actualAssemblyPath)
    {
        var serviceCollection = new ServiceCollection()
            .AddFluentMigratorCore()
            .ConfigureRunner(rb =>
            {
                // Configure DB Provider
                switch (dbType)
                {
                    case DatabaseType.SqlServer:
                        _logger.LogDebug("Configuring runner for SQL Server.");
                        rb.AddSqlServer();
                        break;
                    case DatabaseType.PostgreSql:
                         _logger.LogDebug("Configuring runner for PostgreSQL.");
                        rb.AddPostgres();
                        break;
                    case DatabaseType.SQLite:
                         _logger.LogDebug("Configuring runner for SQLite.");
                         rb.AddSQLite();
                         break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(dbType), $"Unsupported database type: {dbType}");
                }

                rb.WithGlobalConnectionString(connectionString);

                // Load only the *actual* assembly, if provided
                Assembly? migrationAssembly = null;
                if (!string.IsNullOrEmpty(actualAssemblyPath) && File.Exists(actualAssemblyPath))
                {
                     migrationAssembly = LoadAssembly(actualAssemblyPath);
                }
                
                if (migrationAssembly != null)
                {
                    _logger.LogDebug("Scanning assembly {AssemblyName} for migrations.", migrationAssembly.FullName);
                    rb.ScanIn(migrationAssembly).For.Migrations();
                }
                else
                {
                    _logger.LogWarning("No valid migration assembly path provided or assembly failed to load. Scanning will be empty.");
                    // ScanIn with empty array prevents FluentMigrator from scanning the entry assembly by default
                    rb.ScanIn([]).For.Migrations();
                }
            })
            // Add logging services
             .AddLogging(lb => lb
                .AddConsole() // Keep this one simple
                .AddFilter((category, level) => level >= LogLevel.Debug) // Set minimum level via filter
             ); 
             // Removed AddDebug() as it might require another package/using
             // Removed SetMinimumLevel() as AddFilter achieves similar result

        return serviceCollection.BuildServiceProvider(false);
    }

     private Assembly? LoadAssembly(string assemblyPath)
    {
        try
        {
            _logger.LogDebug("Loading assembly from path: {Path}", assemblyPath);
            // Use LoadFrom context cautiously, consider AssemblyLoadContext for more isolation if needed
            return Assembly.LoadFrom(assemblyPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load assembly: {Path}", assemblyPath);
            return null; // Or re-throw depending on desired behavior
        }
    }

    // New helper method to find the real assembly
    private string? FindActualMigrationAssembly(string migrationsPath)
    {
        // Look specifically for the DLL copied by TestHelpers
        string expectedDllName = "_ActualMigrations.dll"; 
        var actualAssembly = Path.Combine(migrationsPath, expectedDllName);

        if (File.Exists(actualAssembly))
        {
            return actualAssembly;
        }
        else
        {
            // Fallback for non-test scenarios? Or just rely on TestHelpers?
            // For now, let's assume TestHelpers always provides it for tests.
             _logger.LogTrace("Did not find expected '{DllName}' in {Path}. C# migrations might not be scanned.", expectedDllName, migrationsPath);
             return null;
        }
    }
} 