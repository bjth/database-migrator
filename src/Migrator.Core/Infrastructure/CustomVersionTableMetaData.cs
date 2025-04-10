using FluentMigrator.Runner.VersionTableInfo;

namespace Migrator.Core.Infrastructure;

/// <summary>
/// Custom metadata definition for the version table used by FluentMigrator.
/// Implements IVersionTableMetaData directly as recommended.
/// </summary>
[VersionTableMetaData]
public class CustomVersionTableMetaData : IVersionTableMetaData
{
    // --- Properties from Interface --- 

    // SchemaName: Default is null/empty string.
    // You had null, let's keep it null. FluentMigrator typically handles null/empty schema names correctly.
    public virtual string? SchemaName => null;

    // TableName: You customized this.
    public virtual string TableName => "VersionInfo"; // Your custom value

    // ColumnName: You customized this.
    public virtual string ColumnName => "Version"; // Your custom value

    // AppliedOnColumnName: You customized this.
    public virtual string AppliedOnColumnName => "AppliedOn"; // Your custom value

    // DescriptionColumnName: You customized this.
    public virtual string DescriptionColumnName => "Description"; // Your custom value

    // UniqueIndexName: You customized this.
    public virtual string UniqueIndexName => "UC_Version"; // Your custom value

    // OwnsSchema: Added from documentation example, default seems to be true.
    // This controls whether FluentMigrator attempts to create the schema if it doesn't exist.
    // Set to true if the schema might not exist, false if it's guaranteed to exist.
    public virtual bool OwnsSchema => true;

    // Set CreateWithPrimaryKey to false to prevent implicit index creation
    public virtual bool CreateWithPrimaryKey => false;
}
