using CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Migrator.Core;
using Serilog;
using Serilog.Events;

namespace Migrator.Runner;

internal class Program
{
    private static async Task<int> Main(string[] args)
    {
        // Basic Serilog configuration done after parsing args
        try
        {
            var parserResult = Parser.Default.ParseArguments<Options>(args);

            await parserResult.MapResult(
                async opts =>
                {
                    var logLevel = opts.Verbose ? LogEventLevel.Debug : LogEventLevel.Information;
                    ConfigureSerilog(logLevel);
                    Log.Information("Starting Migrator.Runner...");
                    await RunMigrations(opts);
                    Log.Information("Migrator.Runner finished successfully.");
                    return 0; // Success exit code
                },
                async errs =>
                {
                    Log.Error("Argument parsing failed:");
                    foreach (var err in errs) Log.Error("- {ErrorType}: {Details}", err.Tag, err.ToString());
                    Log.Warning("Use --help for usage information.");
                    await Task.CompletedTask; // Need async lambda
                    return 1; // Failure exit code
                });

            // Ensure we have a return path
            return parserResult.Tag == ParserResultType.Parsed ? 0 : 1;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "An unhandled exception occurred during migration execution.");
            return -1; // Unhandled exception exit code
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }

    private static void ConfigureSerilog(LogEventLevel level)
    {
        Log.CloseAndFlush(); // Close the bootstrap logger
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(level)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning) // Reduce noise from framework logs
            .Enrich.FromLogContext()
            .WriteTo.Console(outputTemplate:
                "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }

    private static async Task RunMigrations(Options opts)
    {
        var services = new ServiceCollection();

        services.AddLogging(configure => configure.AddSerilog());
        services.AddSingleton<MigrationService>();

        var serviceProvider = services.BuildServiceProvider();
        ArgumentNullException.ThrowIfNull(serviceProvider, nameof(serviceProvider));

        var migrationService = serviceProvider.GetRequiredService<MigrationService>();
        ArgumentNullException.ThrowIfNull(migrationService, nameof(migrationService));

        await migrationService.ExecuteMigrationsAsync(opts.DatabaseType, opts.ConnectionString, opts.MigrationsPath);

        Log.Information("Migration process completed successfully.");
    }
}