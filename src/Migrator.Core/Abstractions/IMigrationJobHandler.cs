namespace Migrator.Core.Abstractions;

/// <summary>
/// Interface for handlers that execute a specific type of migration job.
/// </summary>
public interface IMigrationJobHandler
{
    /// <summary>
    /// Executes the migration job.
    /// </summary>
    /// <param name="job">The migration job to execute.</param>
    Task ExecuteAsync(MigrationJob job);
}
