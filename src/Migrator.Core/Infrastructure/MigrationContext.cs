using FluentMigrator;
using FluentMigrator.Runner;
using Migrator.Core.Abstractions;
using Migrator.Core.Factories;
using Migrator.Core.Handlers;

namespace Migrator.Core.Infrastructure;

/// <summary>
/// Holds the resolved services needed during the migration execution process within a scope.
/// </summary>
public class MigrationContext(
    IMigrationInformationLoader loader,
    IVersionLoader versionLoader,
    IMigrationProcessor processor,
    MigrationJobFactory jobFactory,
    CSharpMigrationJobHandler csharpHandler,
    SqlMigrationJobHandler sqlHandler)
    : IMigrationContext
{
    public IMigrationInformationLoader Loader { get; } = loader;
    public IVersionLoader VersionLoader { get; } = versionLoader;
    public IMigrationProcessor Processor { get; } = processor;
    public MigrationJobFactory JobFactory { get; } = jobFactory;
    public CSharpMigrationJobHandler CSharpHandler { get; } = csharpHandler;
    public SqlMigrationJobHandler SqlHandler { get; } = sqlHandler;

    void IDisposable.Dispose()
    {
        GC.SuppressFinalize(this);
    }
}
