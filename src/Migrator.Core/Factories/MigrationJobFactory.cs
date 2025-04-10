using System.Reflection;
using FluentMigrator;
using FluentMigrator.Infrastructure;
using Microsoft.Extensions.Logging;
using Migrator.Core.Abstractions;

namespace Migrator.Core.Factories;

/// <summary>
/// Factory responsible for creating MigrationJob objects from discovered migrations.
/// </summary>
public class MigrationJobFactory(ILogger<MigrationJobFactory> logger)
{
    public List<MigrationJob> CreateJobs(
        IEnumerable<KeyValuePair<long, IMigrationInfo>> csharpMigrations,
        IEnumerable<MigrationTask> sqlMigrationTasks)
    {
        var csharpJobs = new List<CSharpMigrationJob>();
        var sqlJobs = new List<SqlMigrationJob>();

        // Create C# Jobs
        foreach (var kvp in csharpMigrations)
        {
            var migrationInfo = kvp.Value;
            var migrationType = migrationInfo.Migration.GetType();
            var migrationAttribute = migrationType.GetCustomAttribute<MigrationAttribute>();

            if (migrationAttribute == null)
            {
                logger.LogWarning("Could not find MigrationAttribute on type {TypeName}. Skipping this C# migration.", migrationType.Name);
                continue;
            }

            var version = kvp.Key; // Use key from loader result
            var description = migrationAttribute.Description ?? $"C# Migration: {migrationType.Name}";

            csharpJobs.Add(new CSharpMigrationJob(version, description, migrationInfo));
            logger.LogTrace("Created C# Migration Job: Version={Version}, Type={TypeName}", version, migrationType.Name);
        }

        // Create SQL Jobs
        foreach (var sqlTask in sqlMigrationTasks.Where(sqlTask => sqlTask.Type == MigrationType.Sql))
        {
            var version = sqlTask.Timestamp;
            var description = $"SQL Migration: {sqlTask.OriginalFilename}";
            sqlJobs.Add(new SqlMigrationJob(version, description, sqlTask));
            logger.LogTrace("Created SQL Migration Job: Version={Version}, File={FileName}", version, sqlTask.OriginalFilename);
        }

        // Check for duplicate versions across C# and SQL migrations
        var csharpVersions = csharpJobs.Select(j => j.Version).ToHashSet();
        var sqlVersions = sqlJobs.Select(j => j.Version);
        var duplicateVersions = sqlVersions.Where(v => csharpVersions.Contains(v)).ToList();

        if (duplicateVersions.Any())
        {
            var duplicatesString = string.Join(", ", duplicateVersions);
            logger.LogError("Duplicate migration versions found between C# and SQL migrations: {DuplicateVersions}", duplicatesString);
            throw new InvalidOperationException($"Duplicate migration versions found between C# and SQL migrations: {duplicatesString}. Each migration must have a unique version number.");
        }

        // Combine and sort all jobs by version number
        var allJobs = csharpJobs.Cast<MigrationJob>().Concat(sqlJobs.Cast<MigrationJob>()).ToList();
        var sortedJobs = allJobs.OrderBy(m => m.Version).ToList();
        logger.LogDebug("Created and sorted {Count} migration jobs (C# + SQL).", sortedJobs.Count);

        return sortedJobs;
    }
}
