using System.Reflection;
using FluentMigrator;
using FluentMigrator.Infrastructure;
using FluentMigrator.Runner;
using FluentMigrator.Runner.Exceptions;
using FluentMigrator.Runner.VersionTableInfo;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Migrator.Core;

// Define a custom version table metadata class (Ensure this matches your actual table)
#pragma warning disable CS0618 // Type or member is obsolete
public class CustomVersionTableMetaData : DefaultVersionTableMetaData
{
    // Return null explicitly to satisfy nullable context if needed, and suppress CS8603 if it persists elsewhere
    public override string? SchemaName => null;
    public override string TableName => "VersionInfo";
    public override string ColumnName => "Version";
    public override string AppliedOnColumnName => "AppliedOn";
    public override string DescriptionColumnName => "Description";
    public override string UniqueIndexName => "UC_Version";
}
#pragma warning restore CS0618 // Type or member is obsolete

public class MigrationService(ILogger<MigrationService> logger)
{
    public async Task ExecuteMigrationsAsync(DatabaseType dbType, string connectionString, string migrationsPath)
    {
        logger.LogInformation("Starting migration process (Interleaved C#/SQL with manual transactions)...");
        logger.LogInformation("Database Type: {DbType}", dbType);
        logger.LogInformation("Migrations Path: {Path}", migrationsPath);

        if (!Directory.Exists(migrationsPath))
        {
            logger.LogError("Migrations directory not found: {Path}", migrationsPath);
            throw new DirectoryNotFoundException($"Migrations directory not found: {migrationsPath}");
        }

        // 1. Discover SQL migration tasks first (needed for logging count)
        var sqlMigrationTasks = DiscoverSqlMigrations(migrationsPath);
        logger.LogInformation("Discovered {Count} SQL migration tasks based on file naming.", sqlMigrationTasks.Count);

        // 2. Create service provider, scanning assemblies for C# migrations
        // Ensure WithoutGlobalTransaction is NOT called here if it causes issues or is unavailable.
        var serviceProvider = CreateServices(dbType, connectionString, migrationsPath);
        ArgumentNullException.ThrowIfNull(serviceProvider, nameof(serviceProvider));

        using var scope = serviceProvider.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
        var loader = scope.ServiceProvider.GetRequiredService<IMigrationInformationLoader>();
        var versionLoader = scope.ServiceProvider.GetRequiredService<IVersionLoader>();
        // Get the processor which is crucial for manual transaction and SQL execution
        var processor = scope.ServiceProvider.GetService<IMigrationProcessor>();

        // Processor is essential for SQL and potentially for C# transaction control via ApplyMigrationUp
        if (processor == null)
        {
            logger.LogError("Migration processor is null. Cannot manage transactions or run SQL scripts. Aborting.");
            throw new InvalidOperationException("Migration processor is unavailable.");
        }

        try
        {
            // 3. Load C# migration metadata
            IEnumerable<KeyValuePair<long, IMigrationInfo>> csharpMigrations = [];
            try
            {
                csharpMigrations = [.. loader.LoadMigrations()];
                logger.LogInformation("Loaded {Count} C# migrations from assemblies.", csharpMigrations.Count());
            }
            catch (MissingMigrationsException)
            {
                if (sqlMigrationTasks.Count > 0)
                {
                    logger.LogWarning("No C# migration classes found in assemblies, but SQL migrations were discovered. Proceeding with SQL migrations only.");
                }
                else
                {
                    // If neither C# nor SQL migrations found, re-throw is appropriate, though the later check handles this too.
                    logger.LogWarning("No C# migration classes found in assemblies and no SQL migrations discovered.");
                    // Let the process continue to the `sortedMigrations.Count == 0` check below.
                }
            }

            // 4. Combine C# and SQL migrations into a single, sortable list
            // Define a local record or tuple to hold combined info
            var allMigrations =
                new List<(long Version, string Description, IMigrationInfo? CSharpInfo, MigrationTask? SqlTask)>();

            // Iterate over the KeyValuePair collection
            foreach (var kvp in csharpMigrations)
            {
                var migrationInfo = kvp.Value; // Get the MigrationInfo from the pair
                var migrationType = migrationInfo.Migration.GetType();
                var migrationAttribute = migrationType.GetCustomAttribute<MigrationAttribute>();

                if (migrationAttribute == null)
                {
                    logger.LogWarning("Could not find MigrationAttribute on type {TypeName}. Skipping this C# migration.", migrationType.Name);
                    continue;
                }

                long version = migrationAttribute.Version;
                string description = migrationAttribute.Description ?? $"C# Migration: {migrationType.Name}";

                // Add C# migration info using kvp.Key and migrationInfo
                allMigrations.Add((kvp.Key, description, migrationInfo, null));
                logger.LogTrace("Adding C# Migration: Version={Version}, Type={TypeName}", kvp.Key, migrationType.Name);
            }

            foreach (var sqlTask in sqlMigrationTasks.Where(sqlTask => sqlTask.Type == MigrationType.Sql))
            {
                allMigrations.Add((sqlTask.Timestamp, $"SQL Migration: {sqlTask.OriginalFilename}", null,
                    sqlTask)); // Add SQL task info
                logger.LogTrace("Adding SQL Migration: Version={Version}, File={FileName}", sqlTask.Timestamp,
                    sqlTask.OriginalFilename);
            }

            // 5. Sort all migrations by version number
            var sortedMigrations = allMigrations.OrderBy(m => m.Version).ToList();
            logger.LogDebug("Total migrations found (C# + SQL): {Count}. Sorted by version.", sortedMigrations.Count);

            // *** ADJUSTMENT: Check if *any* migrations (C# or SQL) exist ***
            if (sortedMigrations.Count == 0)
            {
                logger.LogWarning("No migrations (C# or SQL) found to apply in {Path}.", migrationsPath);
                logger.LogInformation("Migration process completed successfully (no migrations executed).");
                return; // Nothing more to do
            }

            // 6. Load initially applied versions
            versionLoader.LoadVersionInfo();
            var appliedVersions = versionLoader.VersionInfo.AppliedMigrations().ToArray();
            logger.LogInformation("Initially applied versions: {Versions}",
                appliedVersions.Length != 0 ? string.Join(", ", appliedVersions.OrderBy(v => v)) : "None");

            // 7. Iterate and apply migrations one by one, each in its own transaction
            logger.LogInformation("Applying migrations in interleaved order...");
            foreach (var migration in sortedMigrations)
            {
                if (versionLoader.VersionInfo.HasAppliedMigration(migration.Version))
                {
                    logger.LogInformation("Skipping already applied migration: {Version} - {Description}",
                        migration.Version, migration.Description);
                    continue;
                }

                var migrationTypeString = migration.CSharpInfo != null ? "C#" : "SQL";
                logger.LogInformation("Applying {Type} migration: {Version} - {Description}", migrationTypeString,
                    migration.Version, migration.Description);

                // Each migration attempt is wrapped in its own transaction and error handling block
                var success = false;
                try
                {
                    processor.BeginTransaction();
                    logger.LogDebug("Begun transaction for migration {Version}.", migration.Version);

                    // Check if it's a C# migration
                    if (migration.CSharpInfo != null)
                    {
                        // Use MigrateUp(version) instead of ApplyMigrationUp
                        runner.MigrateUp(migration.Version);
                        logger.LogDebug(
                            "FluentMigrator runner executed MigrateUp({Version}) for C# migration within transaction.",
                            migration.Version);
                    }
                    // Check if it's an SQL migration (via SqlTask being not null)
                    else if (migration.SqlTask is { Type: MigrationType.Sql })
                    {
                        var sqlScript = await File.ReadAllTextAsync(migration.SqlTask.FullPath);
                        processor.Execute(sqlScript); // Execute within the transaction

                        // Manually record the SQL migration AFTER successful execution
                        versionLoader.UpdateVersionInfo(migration.Version, migration.Description);
                        logger.LogInformation("Successfully applied SQL script {Filename}.",
                            migration.SqlTask.OriginalFilename);
                    }
                    else
                    {
                        throw new InvalidOperationException(
                            $"Invalid combined migration state for version {migration.Version}.");
                    }

                    // If we reached here, the migration step was successful
                    processor.CommitTransaction();
                    logger.LogDebug("Committed transaction for migration {Version}.", migration.Version);
                    success = true;
                }
                catch (Exception ex)
                {
                    // Adjust error source logic
                    var errorSource = migration.CSharpInfo != null
                        ? migration.CSharpInfo.Migration.GetType().Name
                        : migration.SqlTask != null
                            ? migration.SqlTask.OriginalFilename
                            : "Unknown Migration";

                    var currentTypeString =
                        migration.CSharpInfo != null ? "C#" : "SQL"; // Determine type for error message
                    var errorMessage =
                        $"CRITICAL ERROR applying {currentTypeString} migration {migration.Version} ({errorSource}). Halting execution.";

                    logger.LogError(ex, $"{errorMessage} Attempting transaction rollback.");

                    // Attempt to roll back the transaction (only if it wasn't already committed)
                    if (!success)
                    {
                        // Check if commit happened before exception
                        try
                        {
                            processor.RollbackTransaction();
                            logger.LogInformation(
                                "Transaction rollback attempted successfully for failed migration {Version}.",
                                migration.Version);
                        }
                        catch (Exception rollbackEx)
                        {
                            logger.LogError(rollbackEx,
                                "FATAL: Failed to rollback transaction after error in migration {Version}. Database might be in an inconsistent state.",
                                migration.Version);
                            // Log separately, but rethrow the original exception
                            await WriteErrorLogAsync(
                                $"Rollback Failure after {errorMessage}\nRollback Exception:\n{rollbackEx}");
                        }
                    }

                    // Write detailed error to log file, indicating halt
                    await WriteErrorLogAsync(
                        $"{errorMessage}\nMigration process stopped.\nUnderlying Exception:\n{ex}");

                    // Rethrow the original exception to stop the migration process completely
                    throw new Exception(errorMessage, ex);
                }
            } // End foreach migration

            logger.LogInformation("Migration process completed successfully.");
        }
        catch (Exception ex)
        {
            // Catch exceptions from setup or the main loop if they weren't caught inside
            if (!ex.Message.StartsWith("CRITICAL ERROR applying")) // Avoid double logging if already caught
            {
                const string errorMessage =
                    "An unexpected error occurred during the migration process setup or outer loop.";
                logger.LogError(ex, errorMessage);
                await WriteErrorLogAsync($"General Migration Error (Setup/Outer Loop):\n{ex}");
            }
            else
            {
                logger.LogDebug("Caught critical error exception from inner loop. Already logged.");
            }

            // Always rethrow to indicate overall failure
            throw;
        }
    }

    // Helper method to write errors to a log file
    private async Task WriteErrorLogAsync(string message)
    {
        try
        {
            // Define log directory and ensure it exists
            var logDir = Path.Combine(Directory.GetCurrentDirectory(), "logs");
            Directory.CreateDirectory(logDir);

            // Log file inside the logs directory
            var logFilePath = Path.Combine(logDir, "migration-error.log");
            await File.AppendAllTextAsync(logFilePath, $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC - {message}\n---\n");
        }
        catch (Exception logEx)
        {
            logger.LogError(logEx, "Failed to write to migration-error.log");
        }
    }

    // Reintroduced DiscoverMigrations to find all potential task files by name
    private List<MigrationTask> DiscoverSqlMigrations(string migrationsPath)
    {
        var tasks = new List<MigrationTask>();
        logger.LogDebug("Scanning directory for SQL migration files: {Path}", migrationsPath);

        foreach (var file in Directory.EnumerateFiles(migrationsPath, "*.sql", SearchOption.TopDirectoryOnly))
        {
            logger.LogTrace("Checking potential SQL file: {File}", file);
            // Use MigrationTask.TryParse to handle naming convention and parsing
            if (MigrationTask.TryParse(file, out var task) && task is { Type: MigrationType.Sql })
            {
                logger.LogDebug("Parsed SQL migration task: {Timestamp} from {Filename}", task.Timestamp,
                    task.OriginalFilename);
                tasks.Add(task);
            }
            else
            {
                logger.LogDebug(
                    "Skipping file (does not match expected SQL migration pattern or failed parsing): {File}", file);
            }
        }

        return tasks;
    }

    // CreateServices now only scans *actual* assemblies, not SQL resources
    private IServiceProvider CreateServices(DatabaseType dbType, string connectionString, string migrationsPath)
    {
        var actualAssemblies = LoadActualAssembliesFromPath(migrationsPath).ToArray();

        var serviceCollection = new ServiceCollection()
            .AddLogging(lb => lb
                .AddConsole()
                .AddFilter((_, level) => level >= LogLevel.Debug)
            )
            .AddFluentMigratorCore()
            .ConfigureRunner(rb =>
            {
                // Configure DB Provider based on dbType
                switch (dbType)
                {
                    case DatabaseType.SqlServer: rb.AddSqlServer(); break;
                    case DatabaseType.PostgreSql: rb.AddPostgres(); break;
                    case DatabaseType.SQLite: rb.AddSQLite(); break;
                    case DatabaseType.Unknown:
                    default: throw new ArgumentOutOfRangeException(nameof(dbType));
                }

                // Set connection string
                rb.WithGlobalConnectionString(connectionString);

                // Scan only the found assemblies containing IMigration implementations
                if (actualAssemblies.Length > 0)
                {
                    logger.LogInformation("Scanning {Count} actual assemblies for C# migrations.",
                        actualAssemblies.Length);
                    foreach (var asm in actualAssemblies)
                    {
                        logger.LogDebug(" - {AssemblyName}", asm.FullName);
                    }

                    rb.ScanIn(actualAssemblies).For.Migrations();
                }
                else
                {
                    logger.LogWarning(
                        "No actual assemblies containing migrations found in {Path}. Only SQL scripts will be considered.",
                        migrationsPath);
                    rb.ScanIn().For.Migrations(); // Scan nothing if no assemblies found
                }
            })
            // Register custom version table metadata
            .AddSingleton<IVersionTableMetaData, CustomVersionTableMetaData>();

        // Build and return the service provider
        return serviceCollection.BuildServiceProvider(false);
    }

    // Load assemblies from path, excluding those named like migrations,
    // and verifying they actually contain IMigration types.
    private List<Assembly> LoadActualAssembliesFromPath(string path)
    {
        var assemblies = new List<Assembly>();
        if (!Directory.Exists(path))
        {
            logger.LogWarning("Migration assembly directory not found: {Path}", path);
            return assemblies;
        }

        var dllFiles = Directory.GetFiles(path, "*.dll");
        logger.LogDebug("Found {Count} DLL files in {Path}, checking which contain migrations...", dllFiles.Length,
            path);

        foreach (var dllFile in dllFiles)
        {
            try
            {
                logger.LogTrace("Attempting to load potential assembly: {DllFile}", dllFile);
                // Using LoadFrom context. Consider AssemblyLoadContext if isolation becomes a problem.
                var assembly = Assembly.LoadFrom(dllFile);

                // Check if assembly actually contains IMigration types (Interface from FluentMigrator namespace)
                if (assembly.GetExportedTypes()
                    .Any(t => typeof(IMigration).IsAssignableFrom(t) && t is { IsAbstract: false, IsClass: true }))
                {
                    assemblies.Add(assembly);
                    logger.LogDebug("Loaded assembly containing migrations: {AssemblyName}",
                        assembly.FullName); // Changed to Debug level
                }
                else
                {
                    logger.LogTrace(
                        "Assembly loaded but contains no public, non-abstract IMigration types: {AssemblyName}",
                        assembly.FullName);
                }
            }
            catch (BadImageFormatException)
            {
                logger.LogDebug("Skipping file as it's not a valid .NET assembly: {DllFile}", dllFile);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to load assembly {DllFile}. It will not be scanned.", dllFile);
            }
        }

        logger.LogInformation("Identified {Count} actual assemblies containing C# migrations to scan.",
            assemblies.Count);

        return assemblies;
    }
}
