using FluentMigrator;
using FluentMigrator.Runner;
using Migrator.Core.Factories;
using Migrator.Core.Handlers;

namespace Migrator.Core.Abstractions;

/// <summary>
/// Defines the services required within the scope of a migration execution.
/// </summary>
public interface IMigrationContext : IDisposable
{
    IMigrationInformationLoader Loader { get; }
    IVersionLoader VersionLoader { get; }
    IMigrationProcessor Processor { get; }
    MigrationJobFactory JobFactory { get; }
    CSharpMigrationJobHandler CSharpHandler { get; }
    SqlMigrationJobHandler SqlHandler { get; }
}
