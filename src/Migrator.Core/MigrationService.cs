using FluentMigrator.Infrastructure;
using FluentMigrator.Runner.Exceptions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Migrator.Core.Abstractions;
using Migrator.Core.Factories;
using IMigrationContext = Migrator.Core.Abstractions.IMigrationContext;

namespace Migrator.Core;

public class MigrationService(ILogger<MigrationService> logger, IMigrationScopeFactory migrationScopeFactory)
{
    private readonly IMigrationScopeFactory _migrationScopeFactory =
        migrationScopeFactory ?? throw new ArgumentNullException(nameof(migrationScopeFactory));

    public async Task ExecuteMigrationsAsync(DatabaseType dbType, string connectionString, string migrationsPath)
    {
        logger.LogInformation("Starting migration process for DB: {DbType}, Path: {Path}", dbType, migrationsPath);

        ValidateMigrationsPath(migrationsPath);

        var sqlMigrationTasks = DiscoverSqlMigrations(migrationsPath);

        using var scope = _migrationScopeFactory.CreateMigrationScope(dbType, connectionString, migrationsPath);

        var context = ResolveMigrationContext(scope);

        try
        {
            var csharpMigrations = LoadCSharpMigrations(context, sqlMigrationTasks);
            var sortedJobs = PrepareMigrationJobs(context, csharpMigrations, sqlMigrationTasks, migrationsPath);
            if (sortedJobs.Count == 0)
            {
                logger.LogInformation("Migration process completed successfully.");
                return;
            }

            LoadAppliedVersionInfo(context);

            await ApplyMigrationJobsAsync(sortedJobs, context);

            logger.LogInformation("Migration process completed successfully.");
        }
        catch (Exception ex)
        {
            await HandleOuterExceptionAsync(ex);
        }
    }

    private void ValidateMigrationsPath(string migrationsPath)
    {
        if (Directory.Exists(migrationsPath))
        {
            return;
        }

        logger.LogError("Migrations directory not found: {Path}", migrationsPath);
        throw new DirectoryNotFoundException($"Migrations directory not found: {migrationsPath}");
    }

    private IMigrationContext ResolveMigrationContext(IServiceScope scope)
    {
        try
        {
            var context = scope.ServiceProvider.GetRequiredService<IMigrationContext>();
            logger.LogDebug("Resolved IMigrationContext from the migration scope.");
            return context;
        }
        catch (Exception ex)
        {
            var errorToThrow = ex.InnerException ?? ex;
            logger.LogError(errorToThrow, "Failed to resolve IMigrationContext or its dependencies from the provided scope. Error: {ErrorMessage}", errorToThrow.Message);
            throw new InvalidOperationException("Failed to resolve IMigrationContext from the provided scope. See inner exception for details.", errorToThrow);
        }
    }

    private IEnumerable<KeyValuePair<long, IMigrationInfo>> LoadCSharpMigrations(IMigrationContext context,
        List<MigrationTask> sqlMigrationTasks)
    {
        try
        {
            var csharpMigrations = context.Loader.LoadMigrations().ToList();
            logger.LogInformation("Loaded {Count} C# migrations using loader from dynamic scope.", csharpMigrations.Count);
            return csharpMigrations;
        }
        catch (MissingMigrationsException)
        {
            logger.LogInformation(
                sqlMigrationTasks.Count != 0
                    ? "No C# migration classes found, but SQL migrations were discovered. Proceeding with SQL only."
                    : "No C# migration classes found and no SQL migrations discovered.");

            return [];
        }
    }

    private List<MigrationJob> PrepareMigrationJobs(
        IMigrationContext context,
        IEnumerable<KeyValuePair<long, IMigrationInfo>> csharpMigrations,
        List<MigrationTask> sqlMigrationTasks,
        string migrationsPath)
    {
        var sortedJobs = context.JobFactory.CreateJobs(csharpMigrations, sqlMigrationTasks);
        logger.LogDebug("Total migration jobs created: {Count}.", sortedJobs.Count);

        if (sortedJobs.Count == 0)
        {
            logger.LogWarning("No migration jobs (C# or SQL) found to apply in {Path}. Migration process stopping.",
                migrationsPath);
        }

        return sortedJobs;
    }

    private void LoadAppliedVersionInfo(IMigrationContext context)
    {
        context.VersionLoader.LoadVersionInfo();
        var appliedVersions = context.VersionLoader.VersionInfo.AppliedMigrations().OrderBy(v => v).ToArray();
        logger.LogInformation("Initially applied versions: {Versions}",
            appliedVersions.Length > 0 ? string.Join(", ", appliedVersions) : "None");
    }

    private async Task ApplyMigrationJobsAsync(List<MigrationJob> sortedJobs, IMigrationContext context)
    {
        logger.LogInformation("Applying migrations in interleaved order...");
        var appliedInThisRun = new HashSet<long>();

        foreach (var job in sortedJobs)
        {
            if (context.VersionLoader.VersionInfo.HasAppliedMigration(job.Version))
            {
                logger.LogInformation("Skipping already applied migration (from previous run): {Version} - {Description}",
                    job.Version, job.Description);
                appliedInThisRun.Add(job.Version);
                continue;
            }

            if (appliedInThisRun.Contains(job.Version))
            {
                logger.LogWarning("Skipping migration {Version} - {Description} as a migration with the same version was already applied in this run.",
                                 job.Version, job.Description);
                continue;
            }

            var allAppliedVersions =
                context.VersionLoader.VersionInfo.AppliedMigrations().Concat(appliedInThisRun).ToList();

            var currentMaxApplied = allAppliedVersions.Count != 0 ? allAppliedVersions.Max() : 0L;

            if (currentMaxApplied > 0 && job.Version < currentMaxApplied)
            {
                logger.LogWarning("Applying out-of-order migration: Version {JobVersion} is being applied after a higher version {MaxAppliedVersion} has already been applied.",
                                 job.Version, currentMaxApplied);
            }

            await ApplySingleJobAsync(job, context);
            appliedInThisRun.Add(job.Version);
        }
    }

    private async Task ApplySingleJobAsync(MigrationJob job, IMigrationContext context)
    {
        var migrationTypeString = job is CSharpMigrationJob ? "C#" : "SQL";
        logger.LogInformation("Applying {Type} migration: {Version} - {Description}", migrationTypeString,
            job.Version, job.Description);

        try
        {
            await ExecuteJobHandlerAsync(job, context);
        }
        catch (Exception ex)
        {
            await HandleJobExecutionErrorAsync(ex, job, migrationTypeString);
            throw;
        }
    }

    private async Task ExecuteJobHandlerAsync(MigrationJob job, IMigrationContext context)
    {
        switch (job)
        {
            case CSharpMigrationJob csharpJob:
                await context.CSharpHandler.ExecuteAsync(csharpJob);
                break;
            case SqlMigrationJob sqlJob:
                await context.SqlHandler.ExecuteAsync(sqlJob);
                context.VersionLoader.UpdateVersionInfo(sqlJob.Version, sqlJob.Description);
                logger.LogDebug("Recorded version {Version} for SQL migration (after execution).", job.Version);
                break;
            default:
                logger.LogError("Encountered unknown MigrationJob type: {JobType} for version {Version}",
                    job.GetType().Name, job.Version);
                throw new InvalidOperationException(
                    $"Unknown MigrationJob type '{job.GetType().Name}' for version {job.Version}.");
        }
    }

    private async Task HandleJobExecutionErrorAsync(Exception ex, MigrationJob job, string migrationTypeString)
    {
        var errorSource = job switch
        {
            CSharpMigrationJob cj => cj.MigrationInfo.Migration.GetType().Name,
            SqlMigrationJob sj => sj.SqlTask.OriginalFilename,
            _ => "Unknown Migration Job"
        };
        var errorMessage =
            $"CRITICAL ERROR applying {migrationTypeString} migration {job.Version} ({errorSource}). Halting execution.";
        logger.LogError(ex, errorMessage);

        await WriteErrorLogAsync($"{errorMessage}\nUnderlying Exception:\n{ex}");
        throw new Exception(errorMessage, ex);
    }

    private async Task HandleOuterExceptionAsync(Exception ex)
    {
        if (ex is not InvalidOperationException && !ex.Message.StartsWith("CRITICAL ERROR"))
        {
            const string errorMessage = "An unexpected error occurred during the migration process.";
            logger.LogError(ex, errorMessage);
            await WriteErrorLogAsync($"General Migration Error:\n{ex}");
            throw new Exception(errorMessage, ex);
        }

        logger.LogDebug(ex, "Re-throwing specific exception caught during migration process.");
        throw ex;
    }

    private async Task WriteErrorLogAsync(string message)
    {
        try
        {
            var logDir = Path.Combine(Directory.GetCurrentDirectory(), "logs");
            Directory.CreateDirectory(logDir);
            var logFilePath = Path.Combine(logDir, "migration-error.log");
            var logMessage = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC - {message}\n---\n";
            await File.AppendAllTextAsync(logFilePath, logMessage);
            logger.LogTrace("Error details written to {LogFilePath}", logFilePath);
        }
        catch (Exception logEx)
        {
            logger.LogError(logEx, "CRITICAL: Failed to write details to migration-error.log");
        }
    }

    private List<MigrationTask> DiscoverSqlMigrations(string migrationsPath)
    {
        var tasks = new List<MigrationTask>();
        logger.LogDebug("Scanning directory for SQL migration files: {Path}", migrationsPath);
        try
        {
            foreach (var file in Directory.EnumerateFiles(migrationsPath, "*.sql", SearchOption.TopDirectoryOnly))
            {
                logger.LogTrace("Checking potential SQL file: {File}", file);
                if (MigrationTask.TryParse(file, out var task) && task is { Type: MigrationType.Sql })
                {
                    logger.LogDebug("Parsed SQL migration task: {Timestamp} from {Filename}", task.Timestamp,
                        task.OriginalFilename);
                    tasks.Add(task);
                }
                else
                {
                    logger.LogDebug(
                        "Skipping file (does not match expected SQL migration pattern or failed parsing): {File}",
                        file);
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
            logger.LogError("SQL discovery failed: Directory not found at {Path}", migrationsPath);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during SQL file discovery in {Path}", migrationsPath);
        }
        finally
        {
            logger.LogInformation("SQL file scan complete. Discovered {Count} SQL migration tasks in {Path}.", tasks.Count, migrationsPath);
        }

        return tasks;
    }
}
