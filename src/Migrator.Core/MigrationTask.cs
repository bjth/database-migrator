using System.Globalization;

namespace Migrator.Core;

public enum DatabaseType
{
    Unknown = 0,
    SqlServer,
    PostgreSql,
    SQLite
}

// Restore the original enum definition
public enum MigrationType
{
    Unknown = 0, // Keep unknown for default/error cases
    Dll,
    Sql
}

public record MigrationTask
{
    public long Timestamp { get; private init; }
    public MigrationType Type { get; private init; }
    public string FullPath { get; private init; } = string.Empty;
    public string OriginalFilename { get; private init; } = string.Empty;

    public static bool TryParse(string filePath, out MigrationTask? task)
    {
        task = null;
        var fileName = Path.GetFileName(filePath);
        if (string.IsNullOrEmpty(fileName) || fileName.Length < 14)
        {
            return false;
        }

        if (!long.TryParse(fileName.AsSpan(0, 12), NumberStyles.None, CultureInfo.InvariantCulture,
                out var timestamp))
        {
            return false;
        }

        MigrationType type;
        if (fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            type = MigrationType.Dll;
        }
        else if (fileName.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
        {
            type = MigrationType.Sql;
        }
        else
        {
            return false; // Unsupported file type
        }

        task = new MigrationTask
        {
            Timestamp = timestamp,
            Type = type,
            FullPath = Path.GetFullPath(filePath),
            OriginalFilename = fileName
        };
        return true;
    }
}
