using FluentMigrator.Runner.VersionTableInfo;

namespace Migrator.Core.Infrastructure;

[VersionTableMetaData]
public class CustomVersionTableMetaData : IVersionTableMetaData
{
    public virtual string? SchemaName => null;

    public virtual string TableName => "VersionInfo";

    public virtual string ColumnName => "Version";

    public virtual string AppliedOnColumnName => "AppliedOn";

    public virtual string DescriptionColumnName => "Description";

    public virtual string UniqueIndexName => "UC_Version";

    public virtual bool OwnsSchema => true;

    public virtual bool CreateWithPrimaryKey => false;
}
