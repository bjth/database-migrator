using Microsoft.Extensions.DependencyInjection;

namespace Migrator.Core.Factories;

/// <summary>
/// Defines a factory for creating IServiceScope instances specifically configured
/// for running database migrations based on runtime parameters.
/// </summary>
public interface IMigrationScopeFactory
{
    /// <summary>
    /// Creates a new service scope configured with FluentMigrator services
    /// based on the provided database type, connection string, and migrations path.
    /// Assemblies within the migrations path will be scanned for C# migrations.
    /// </summary>
    /// <param name="dbType">The type of database to target.</param>
    /// <param name="connectionString">The connection string for the target database.</param>
    /// <param name="migrationsPath">The path to the directory containing migration assemblies (.dll) and SQL scripts (.sql).</param>
    /// <returns>A configured IServiceScope ready for migration execution.</returns>
    IServiceScope CreateMigrationScope(DatabaseType dbType, string connectionString, string migrationsPath);
}
