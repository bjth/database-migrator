using FluentMigrator.Infrastructure;

namespace Migrator.Core.Abstractions;

// Base record for a migration job
public abstract record MigrationJob(long Version, string Description);

// Represents a C# migration job
public record CSharpMigrationJob(long Version, string Description, IMigrationInfo MigrationInfo)
    : MigrationJob(Version, Description);

// Represents an SQL migration job
public record SqlMigrationJob(long Version, string Description, MigrationTask SqlTask)
    : MigrationJob(Version, Description);
