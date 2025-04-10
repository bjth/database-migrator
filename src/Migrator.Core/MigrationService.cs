using FluentMigrator.Infrastructure;
using FluentMigrator.Runner;
using FluentMigrator.Runner.Exceptions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Migrator.Core.Abstractions;
using Migrator.Core.Factories;
using IMigrationContext = Migrator.Core.Abstractions.IMigrationContext;

namespace Migrator.Core;

// Inject IMigrationScopeFactory
public class MigrationService(ILogger<MigrationService> logger, IMigrationScopeFactory migrationScopeFactory)
{
    // Store IMigrationScopeFactory
    private readonly IMigrationScopeFactory _migrationScopeFactory =
        migrationScopeFactory ?? throw new ArgumentNullException(nameof(migrationScopeFactory));

    public async Task ExecuteMigrationsAsync(DatabaseType dbType, string connectionString, string migrationsPath)
    {
        logger.LogInformation("Starting migration process for DB: {DbType}, Path: {Path}", dbType, migrationsPath);
        // logger.LogDebug("Connection String: {ConnectionString}", connectionString); // Avoid logging by default

        // Initial Validation
        ValidateMigrationsPath(migrationsPath);

        // Discover SQL tasks early
        var sqlMigrationTasks = DiscoverSqlMigrations(migrationsPath);

        // Create Scope and Context
        using var scope = _migrationScopeFactory.CreateMigrationScope(dbType, connectionString, migrationsPath);

        var context = ResolveMigrationContext(scope);

        try
        {
            // Prepare jobs
            var csharpMigrations = LoadCSharpMigrations(context, sqlMigrationTasks);
            var sortedJobs = PrepareMigrationJobs(context, csharpMigrations, sqlMigrationTasks, migrationsPath);
            if (sortedJobs.Count == 0)
            {
                return; // Exit if no jobs
            }

            // Load version info
            LoadAppliedVersionInfo(context);

            // Apply jobs
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
            // Revert to resolving the context directly, assuming it's registered
            var context = scope.ServiceProvider.GetRequiredService<IMigrationContext>();
            logger.LogDebug("Resolved IMigrationContext from the migration scope.");
            return context;
        }
        catch (Exception ex) // Catch potential resolution errors or errors during dependency creation
        {
            // Log the failure and throw a more specific exception
            // If an inner exception exists, it's likely the more relevant error (e.g., DB issue during FM setup)
            var errorToThrow = ex.InnerException ?? ex;
            logger.LogError(errorToThrow, "Failed to resolve IMigrationContext or its dependencies from the provided scope. Error: {ErrorMessage}", errorToThrow.Message);
            // Wrap in a new exception to clearly indicate the failure point
            throw new InvalidOperationException("Failed to resolve IMigrationContext from the provided scope. See inner exception for details.", errorToThrow);
        }
    }

    private IEnumerable<KeyValuePair<long, IMigrationInfo>> LoadCSharpMigrations(IMigrationContext context,
        List<MigrationTask> sqlMigrationTasks)
    {
        try
        {
            var csharpMigrations = context.Loader.LoadMigrations().ToList(); // Materialize
            logger.LogInformation("Loaded {Count} C# migrations using loader from dynamic scope.", csharpMigrations.Count);
            return csharpMigrations;
        }
        catch (MissingMigrationsException)
        {
            logger.LogWarning(
                sqlMigrationTasks.Count != 0
                    ? "No C# migration classes found, but SQL migrations were discovered. Proceeding with SQL only."
                    : "No C# migration classes found and no SQL migrations discovered.");

            return [];
        }
        // Consider catching other potential exceptions from LoadMigrations if necessary
    }

    private List<MigrationJob> PrepareMigrationJobs(
        IMigrationContext context,
        IEnumerable<KeyValuePair<long, IMigrationInfo>> csharpMigrations,
        List<MigrationTask> sqlMigrationTasks,
        string migrationsPath) // Added path for logging
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
        var appliedInThisRun = new HashSet<long>(); // Track versions applied in this execution

        foreach (var job in sortedJobs)
        {
            // Check 1: Already applied in a previous run (from initial load)
            if (context.VersionLoader.VersionInfo.HasAppliedMigration(job.Version))
            {
                logger.LogInformation("Skipping already applied migration (from previous run): {Version} - {Description}",
                    job.Version, job.Description);
                // Ensure it's tracked for this run too, in case logic relies on it
                appliedInThisRun.Add(job.Version);
                continue;
            }

            // Check 2: Already applied earlier in *this* run (e.g., duplicate SQL script version)
            if (appliedInThisRun.Contains(job.Version))
            {
                logger.LogWarning("Skipping migration {Version} - {Description} as a migration with the same version was already applied in this run.",
                                 job.Version, job.Description);
                continue;
            }

            // --> Check 3: Out-of-order check (NEW) <--
            // Get the max applied version considering both previous runs and what's been done in this run so far
            var allAppliedVersions = context.VersionLoader.VersionInfo.AppliedMigrations().Concat(appliedInThisRun);
            var currentMaxApplied = allAppliedVersions.Any() ? allAppliedVersions.Max() : 0L; // Use 0L for long type

            if (currentMaxApplied > 0 && job.Version < currentMaxApplied) // Only warn if a previous migration exists
            {
                logger.LogWarning("Applying out-of-order migration: Version {JobVersion} is being applied after a higher version {MaxAppliedVersion} has already been applied.",
                                 job.Version, currentMaxApplied);
            }

            // Proceed to apply the job
            try
            {
                await ApplySingleJobAsync(job, context);
                // Mark as applied *for this run* after successful execution
                appliedInThisRun.Add(job.Version);
            }
            catch (Exception ex) // ApplySingleJobAsync now re-throws critical errors
            {
                // Logged and handled within ApplySingleJobAsync/HandleJobExecutionErrorAsync
                // Re-throw to stop the entire migration process as per the handling logic
                throw;
            }
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

            // If it's an SQL job, update the version info here after successful script execution
            if (job is SqlMigrationJob sqlJob)
            {
                context.VersionLoader.UpdateVersionInfo(sqlJob.Version, sqlJob.Description);
                logger.LogDebug("Recorded version {Version} for SQL migration (after execution).", job.Version);
            }
        }
        catch (Exception ex)
        {
            await HandleJobExecutionErrorAsync(ex, job, context, migrationTypeString, false);
            // Re-throw the critical exception to stop the overall process
            throw; // The handler already created the specific exception to throw
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
                break;
            default:
                // This case should ideally not be hit if JobFactory is correct, but good for safety
                logger.LogError("Encountered unknown MigrationJob type: {JobType} for version {Version}",
                    job.GetType().Name, job.Version);
                throw new InvalidOperationException(
                    $"Unknown MigrationJob type '{job.GetType().Name}' for version {job.Version}.");
        }
    }

    private async Task HandleJobExecutionErrorAsync(Exception ex, MigrationJob job, IMigrationContext context,
        string migrationTypeString, bool success)
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

        // Log details about the original error before throwing
        await WriteErrorLogAsync($"{errorMessage}\nUnderlying Exception:\n{ex}");
        // Throw a new exception that signals a critical migration failure
        throw new Exception(errorMessage, ex);
    }

    private async Task HandleOuterExceptionAsync(Exception ex)
    {
        // Catch exceptions from setup/loading or the main loop if they weren't caught inside
        if (ex is not InvalidOperationException && !ex.Message.StartsWith("CRITICAL ERROR"))
        {
            const string errorMessage = "An unexpected error occurred during the migration process.";
            logger.LogError(ex, errorMessage);
            await WriteErrorLogAsync($"General Migration Error:\n{ex}");
            // Re-throw general errors as a new exception to indicate overall failure clearly
            throw new Exception(errorMessage, ex);
        }

        // Log and re-throw specific exceptions (like context resolution failure or critical migration errors)
        logger.LogDebug(ex, "Re-throwing specific exception caught during migration process.");
        throw ex; // Re-throw the original specific exception
    }

    // Helper method to write errors to a log file
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

    // Discover SQL migrations by file name
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

        logger.LogInformation("SQL file scan complete. Discovered {Count} SQL migration tasks in {Path}.", tasks.Count,
            migrationsPath);
        return tasks;
    }
}
