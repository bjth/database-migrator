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
    Dll,         // Might be useful if we parse DLL names later
    Sql          // Used for SQL files
}

public record MigrationTask
{
    public long Timestamp { get; init; }
    public MigrationType Type { get; init; }
    public string FullPath { get; init; } = string.Empty;
    public string OriginalFilename { get; init; } = string.Empty;

    public static bool TryParse(string filePath, out MigrationTask? task)
    {
        task = null;
        var fileName = Path.GetFileName(filePath);
        if (string.IsNullOrEmpty(fileName) || fileName.Length < 14) // YYYYMMDDHHMM_ + at least one char + .ext
        {
            return false;
        }

        if (!long.TryParse(fileName.AsSpan(0, 12), NumberStyles.None, CultureInfo.InvariantCulture, out var timestamp))
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
            FullPath = Path.GetFullPath(filePath), // Store the full path
            OriginalFilename = fileName
        };
        return true;
    }

    // Helper to check if a filename matches the expected timestamped pattern
    public static bool IsTimestampedMigrationFile(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        // Use the same logic as TryParse for consistency
        if (string.IsNullOrEmpty(fileName) || fileName.Length < 14) return false;
        if (!long.TryParse(fileName.AsSpan(0, 12), NumberStyles.None, CultureInfo.InvariantCulture, out _)) return false;
        if (!fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && 
            !fileName.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)) return false;
        
        return true;
    }
} 