using CommandLine;
using Migrator.Core;

// Add this using

namespace Migrator.Runner;

public class Options
{
    [Option('t', "type", Required = true, HelpText = "Database type (SqlServer or PostgreSql).")]
    public DatabaseType DatabaseType { get; set; }

    [Option('c', "connection", Required = true, HelpText = "Database connection string.")]
    public string ConnectionString { get; set; } = string.Empty;

    [Option('p', "path", Required = true, HelpText = "Path to the folder containing migration DLLs and SQL scripts.")]
    public string MigrationsPath { get; set; } = string.Empty;

    [Option('v', "verbose", Default = false, HelpText = "Enable verbose logging.")]
    public bool Verbose { get; set; }

    // Add other options like LogLevel if needed later
}