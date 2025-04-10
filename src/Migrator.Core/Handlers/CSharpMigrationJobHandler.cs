using FluentMigrator.Runner;
using Microsoft.Extensions.Logging;
using Migrator.Core.Abstractions;

namespace Migrator.Core.Handlers;

/// <summary>
/// Handles the execution of C# migration jobs using the FluentMigrator runner.
/// </summary>
public class CSharpMigrationJobHandler(IMigrationRunner runner, ILogger<CSharpMigrationJobHandler> logger) : IMigrationJobHandler
{
    private readonly IMigrationRunner _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    private readonly ILogger<CSharpMigrationJobHandler> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public Task ExecuteAsync(MigrationJob job)
    {
        if (job is not CSharpMigrationJob csharpJob)
        {
            _logger.LogError("Invalid job type provided to CSharpMigrationJobHandler. Expected CSharpMigrationJob, got {JobType}", job.GetType().Name);
            throw new ArgumentException("Job must be CSharpMigrationJob", nameof(job));
        }

        _logger.LogDebug("Executing C# migration {Version} via runner.", csharpJob.Version);
        _runner.MigrateUp(csharpJob.Version);
        return Task.CompletedTask;
    }
}
