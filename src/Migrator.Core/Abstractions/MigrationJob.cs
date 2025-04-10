using FluentMigrator.Infrastructure;

namespace Migrator.Core.Abstractions;

public abstract record MigrationJob(long Version, string Description);

public record CSharpMigrationJob(long Version, string Description, IMigrationInfo MigrationInfo)
    : MigrationJob(Version, Description);

public record SqlMigrationJob(long Version, string Description, MigrationTask SqlTask)
    : MigrationJob(Version, Description);
