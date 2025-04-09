using FluentMigrator.Runner;
using FluentMigrator.Runner.Initialization;
using FluentMigrator.Runner.VersionTableInfo;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Reflection;
using System.Linq;
using FluentMigrator;
using FluentMigrator.Runner.Processors;
using FluentMigrator.Expressions;
using FluentMigrator.Model;

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

        // 1. Discover all potential migration tasks (SQL files) by filename
        var migrationTasks = DiscoverSqlMigrations(migrationsPath);
        // Log how many SQL tasks were found, but don't exit if none were found.
        // C# migrations might still exist in assemblies.
        _logger.LogInformation("Discovered {Count} SQL migration tasks based on file naming.", migrationTasks.Count);

        // 2. Create service provider, scanning *only actual assemblies* for C# migrations
        var serviceProvider = CreateServices(dbType, connectionString, migrationsPath);

        using var scope = serviceProvider.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
        var versionLoader = scope.ServiceProvider.GetRequiredService<IVersionLoader>();
        var processor = scope.ServiceProvider.GetService<FluentMigrator.IMigrationProcessor>();

        if (processor == null && migrationTasks.Any(t => t.Type == MigrationType.Sql))
        {
            _logger.LogError("Could not retrieve the migration processor, which is required to run raw SQL scripts.");
            throw new InvalidOperationException("Migration processor is unavailable, cannot execute SQL scripts.");
        }

        try
        {
            // Execute C# migrations first
            _logger.LogInformation("Applying C# migrations...");
            runner.MigrateUp();
            _logger.LogInformation("C# migrations applied successfully.");

            // Get applied versions AFTER C# migrations
            var appliedVersions = runner.MigrationLoader.LoadMigrations()
                                     .Select(kvp => kvp.Key)
                                     .ToHashSet();

            _logger.LogInformation("Already applied versions (including C# migrations run just now): {Versions}", string.Join(", ", appliedVersions.OrderBy(v => v)));

            // Now execute SQL migrations
            _logger.LogInformation("Applying SQL migrations...");
            foreach (var sqlMigration in migrationTasks.OrderBy(m => m.Timestamp))
            {
                // Check the IVersionLoader's current list, which includes C# migrations AND
                // any SQL migrations applied earlier in *this* loop/execution.
                if (versionLoader.VersionInfo.HasAppliedMigration(sqlMigration.Timestamp))
                {
                    _logger.LogInformation("Skipping already applied SQL migration: {Timestamp} - {Filename}", sqlMigration.Timestamp, sqlMigration.OriginalFilename);
                    continue;
                }

                _logger.LogInformation("Applying SQL migration: {Timestamp} - {Filename}", sqlMigration.Timestamp, sqlMigration.OriginalFilename);
                string sqlScript = await File.ReadAllTextAsync(sqlMigration.FullPath);

                // Execute SQL within a transaction managed by the processor
                // NOTE: FluentMigrator processors often handle transactions implicitly or require specific methods.
                // Assuming ProcessorBase.Execute handles this; adjust if specific transaction commands are needed.
                try
                {
                    // Start Transaction if not automatically handled
                    // processor.BeginTransaction(); 

                    processor.Execute(sqlScript);

                    // Record the SQL migration using the IVersionLoader obtained earlier
                    // This ensures FluentMigrator knows the SQL script was applied.
                    // The VersionLoader handles updating the version table in the database AND the in-memory list.
                    versionLoader.UpdateVersionInfo(sqlMigration.Timestamp, $"SQL Migration: {sqlMigration.OriginalFilename}");

                    // Commit Transaction if not automatically handled
                    // processor.CommitTransaction(); 
                     _logger.LogInformation("Successfully applied SQL script {Filename}.", sqlMigration.OriginalFilename);
                }
                catch (Exception ex)
                {
                     _logger.LogError(ex, "Failed to apply SQL script {Filename}. Transaction rolling back (if processor manages transactions).", sqlMigration.OriginalFilename);
                    // Rollback Transaction if not automatically handled
                    // if (processor.WasCommitted) // Check if processor handles transactions and if one was active
                    // {
                    //      processor.RollbackTransaction();
                    // }
                    throw new Exception($"Failed to apply SQL script {sqlMigration.OriginalFilename}. See inner exception.", ex);
                }
            }
             _logger.LogInformation("SQL migrations applied successfully.");

             _logger.LogInformation("Migration process completed successfully.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed during C# migration discovery or runner setup.");
            throw; // Rethrow to indicate failure
        }
    }

    // Reintroduced DiscoverMigrations to find all potential task files by name
    private List<MigrationTask> DiscoverSqlMigrations(string migrationsPath)
    {
        var tasks = new List<MigrationTask>();
        _logger.LogDebug("Scanning directory for SQL migration files: {Path}", migrationsPath);

        foreach (var file in Directory.EnumerateFiles(migrationsPath, "*.sql", SearchOption.TopDirectoryOnly))
        {
            _logger.LogTrace("Checking potential SQL file: {File}", file);
            // Use MigrationTask.TryParse to handle naming convention and parsing
            if (MigrationTask.TryParse(file, out var task) && task != null && task.Type == MigrationType.Sql)
            {
                _logger.LogDebug("Parsed SQL migration task: {Timestamp} from {Filename}", task.Timestamp, task.OriginalFilename);
                tasks.Add(task);
            }
            else
            {
                 _logger.LogDebug("Skipping file (does not match expected SQL migration pattern or failed parsing): {File}", file);
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
               .AddFilter((category, level) => level >= LogLevel.Debug)
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
                    default: throw new ArgumentOutOfRangeException(nameof(dbType));
                }

                // Set connection string
                rb.WithGlobalConnectionString(connectionString);

                // Scan only the found assemblies containing IMigration implementations
                if (actualAssemblies.Length > 0)
                {
                    _logger.LogInformation("Scanning {Count} actual assemblies for C# migrations.", actualAssemblies.Length);
                    foreach (var asm in actualAssemblies) { _logger.LogDebug(" - {AssemblyName}", asm.FullName); }
                    rb.ScanIn(actualAssemblies).For.Migrations(); 
                }
                else
                {
                    _logger.LogWarning("No actual assemblies containing migrations found in {Path}. Only SQL scripts will be considered.", migrationsPath);
                    rb.ScanIn([]).For.Migrations(); // Scan nothing if no assemblies found
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
        if (!Directory.Exists(path)) { 
            _logger.LogWarning("Migration assembly directory not found: {Path}", path);
            return assemblies; 
        }

        var dllFiles = Directory.GetFiles(path, "*.dll");
        _logger.LogDebug("Found {Count} DLL files in {Path}, checking which contain migrations...", dllFiles.Length, path);

        foreach (var dllFile in dllFiles)
        {
            // Example of skipping specific framework/runtime DLLs if necessary
            // if (Path.GetFileName(dllFile).StartsWith("System.") || Path.GetFileName(dllFile).StartsWith("Microsoft.")) continue;

            try
            {
                _logger.LogTrace("Attempting to load potential assembly: {DllFile}", dllFile);
                // Using LoadFrom context. Consider AssemblyLoadContext if isolation becomes a problem.
                var assembly = Assembly.LoadFrom(dllFile);

                // Check if assembly actually contains IMigration types (Interface from FluentMigrator namespace)
                if (assembly.GetExportedTypes().Any(t => typeof(IMigration).IsAssignableFrom(t) && !t.IsAbstract && t.IsClass))
                {
                    assemblies.Add(assembly);
                    _logger.LogDebug("Loaded assembly containing migrations: {AssemblyName}", assembly.FullName); // Changed to Debug level
                }
                else
                {
                    _logger.LogTrace("Assembly loaded but contains no public, non-abstract IMigration types: {AssemblyName}", assembly.FullName);
                }
            }
            catch (BadImageFormatException)
            {
                _logger.LogDebug("Skipping file as it's not a valid .NET assembly: {DllFile}", dllFile);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load assembly {DllFile}. It will not be scanned.", dllFile);
            }
        } // End foreach

        _logger.LogInformation("Identified {Count} actual assemblies containing C# migrations to scan.", assemblies.Count);
        return assemblies;
    } // End LoadActualAssembliesFromPath

} 