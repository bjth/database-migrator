using System.IO;
using FluentMigrator;
using FluentMigrator.Runner;
using Microsoft.Extensions.Logging;
using Migrator.Core.Abstractions;

namespace Migrator.Core.Handlers;

/// <summary>
/// Handles the execution of SQL migration jobs by reading the script file
/// and executing it via the FluentMigrator processor.
/// </summary>
public class SqlMigrationJobHandler(IMigrationProcessor processor, IVersionLoader versionLoader, ILogger<SqlMigrationJobHandler> logger) : IMigrationJobHandler
{
    private readonly IMigrationProcessor _processor = processor ?? throw new ArgumentNullException(nameof(processor));
    private readonly IVersionLoader _versionLoader = versionLoader ?? throw new ArgumentNullException(nameof(versionLoader));
    private readonly ILogger<SqlMigrationJobHandler> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task ExecuteAsync(MigrationJob job)
    {
        if (job is not SqlMigrationJob sqlJob)
        {
            _logger.LogError("Invalid job type provided to SqlMigrationJobHandler. Expected SqlMigrationJob, got {JobType}", job.GetType().Name);
            throw new ArgumentException("Job must be SqlMigrationJob", nameof(job));
        }

        _logger.LogDebug("Executing SQL migration {Version} from file {FileName}.", sqlJob.Version, sqlJob.SqlTask.OriginalFilename);
        var sqlScript = await File.ReadAllTextAsync(sqlJob.SqlTask.FullPath);

        _logger.LogDebug("Beginning transaction for SQL script {Version}...", sqlJob.Version);
        _processor.BeginTransaction();
        try
        {
            _processor.Execute(sqlScript);
            _logger.LogDebug("Committing transaction for SQL script {Version}...", sqlJob.Version);
            _processor.CommitTransaction();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing SQL script {Version} ({FileName}). Rolling back transaction.",
                sqlJob.Version, sqlJob.SqlTask.OriginalFilename);
            _processor.RollbackTransaction();
            throw;
        }
    }
}
