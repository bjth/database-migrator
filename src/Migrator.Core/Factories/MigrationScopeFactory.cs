using System.Reflection;
using FluentMigrator;
using FluentMigrator.Runner;
using FluentMigrator.Runner.Initialization;
using FluentMigrator.Runner.Processors;
using FluentMigrator.Runner.VersionTableInfo;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Migrator.Core.Abstractions;
using Migrator.Core.Handlers;
using Migrator.Core.Infrastructure;

namespace Migrator.Core.Factories;

/// <summary>
/// Creates fully configured IServiceScope instances for migration runs,
/// dynamically loading assemblies and configuring FluentMigrator based on runtime parameters.
/// </summary>
public class MigrationScopeFactory(ILoggerFactory loggerFactory) : IMigrationScopeFactory
{
    private readonly ILoggerFactory _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));

    public IServiceScope CreateMigrationScope(DatabaseType dbType, string connectionString, string migrationsPath)
    {
        var migrationScopeLogger = _loggerFactory.CreateLogger<MigrationScopeFactory>();
        migrationScopeLogger.LogDebug("Creating migration scope for DbType: {DbType}, Path: {Path}", dbType, migrationsPath);

        var migrationAssemblies = LoadActualAssembliesFromPath(migrationsPath, migrationScopeLogger);
        var services = new ServiceCollection();

        // *** IMPORTANT: Provide the globally configured ILoggerFactory ***
        // This ensures services resolved within this scope get loggers from the main config.
        services.AddSingleton(_loggerFactory);

        // Register custom metadata implementation for DI
        services.AddScoped<IVersionTableMetaData, CustomVersionTableMetaData>();
        services.AddScoped<IMigrationContext, MigrationContext>();
        services.AddScoped<CSharpMigrationJobHandler>();
        services.AddScoped<SqlMigrationJobHandler>();
        services.AddScoped<MigrationJobFactory>();

        // 4. Add and Configure FluentMigrator dynamically
        services.AddFluentMigratorCore()
            .AddLogging(lb => lb.AddFluentMigratorConsole()) // Keep FM Console logging if desired
            .ConfigureRunner(rb =>
            {
                switch (dbType)
                {
                    case DatabaseType.SqlServer: rb.AddSqlServer(); break;
                    case DatabaseType.PostgreSql: rb.AddPostgres(); break;
                    case DatabaseType.SQLite: rb.AddSQLite(); break;
                    case DatabaseType.Unknown:
                    default:
                        migrationScopeLogger.LogError("Unsupported DatabaseType specified: {DbType}", dbType);
                        throw new ArgumentOutOfRangeException(nameof(dbType), $"Unsupported DatabaseType: {dbType}");
                }
                rb.WithGlobalConnectionString(connectionString);
                if (migrationAssemblies.Count > 0)
                {
                    migrationScopeLogger.LogInformation("Scanning {Count} dynamically loaded assemblies for C# migrations.", migrationAssemblies.Count);
                    rb.ScanIn([.. migrationAssemblies]).For.Migrations();
                }
                else
                {
                    migrationScopeLogger.LogWarning("No assemblies containing migrations found in {Path}. Only SQL scripts will be considered if discovered.", migrationsPath);
                }
            })
            .Configure<FluentMigratorLoggerOptions>(options => { options.ShowSql = true; options.ShowElapsedTime = true; })
            .Configure<ProcessorOptions>(opt => { opt.Timeout = TimeSpan.FromSeconds(120); });

        // 6. Build the temporary ServiceProvider and create the scope
        var serviceProvider = services.BuildServiceProvider(true);
        migrationScopeLogger.LogDebug("Migration scope provider built successfully.");
        // Return the created scope, caller is responsible for disposal
        return serviceProvider.CreateScope();
    }

    // Private helper to load assemblies (re-introduced from previous MigrationService)
    private List<Assembly> LoadActualAssembliesFromPath(string path, ILogger logger)
    {
        var assemblies = new List<Assembly>();
        if (!Directory.Exists(path))
        {
            logger.LogWarning("Migration assembly directory not found: {Path}", path);
            return assemblies;
        }

        var dllFiles = Directory.GetFiles(path, "*.dll");
        logger.LogDebug("Found {Count} DLL files in {Path}, checking which contain migrations...", dllFiles.Length, path);

        foreach (var dllFile in dllFiles)
        {
            try
            {
                logger.LogTrace("Attempting to load potential assembly: {DllFile}", dllFile);
                // Using LoadFrom context. Consider AssemblyLoadContext if isolation/unloading is needed.
                var assembly = Assembly.LoadFrom(dllFile);

                // Check if assembly actually contains public IMigration types
                if (assembly.GetExportedTypes().Any(t => typeof(IMigration).IsAssignableFrom(t) && t is { IsAbstract: false, IsClass: true }))
                {
                    assemblies.Add(assembly);
                    logger.LogDebug("Identified assembly containing migrations: {AssemblyName}", assembly.FullName);
                }
                else
                {
                    logger.LogTrace("Assembly loaded but contains no public, non-abstract IMigration types: {AssemblyName}", assembly.FullName);
                }
            }
            catch (BadImageFormatException)
            {
                logger.LogDebug("Skipping file as it's not a valid .NET assembly: {DllFile}", dllFile);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to load assembly {DllFile}. It will not be scanned for migrations.", dllFile);
            }
        }
        logger.LogInformation("Completed assembly scan. Identified {Count} assemblies containing C# migrations in {Path}.", assemblies.Count, path);
        return assemblies;
    }
}
