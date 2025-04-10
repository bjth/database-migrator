using CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Migrator.Core;
using Migrator.Core.Factories;
using Serilog;
using Serilog.Events;

namespace Migrator.Runner;

internal static class Program
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
                    Log.Information("Using DB Type: {DbType}, Connection: ***, Path: {Path}", opts.DatabaseType, opts.MigrationsPath);
                    await RunMigrations(opts);
                    Log.Information("Migrator.Runner finished successfully.");
                    return 0; // Success exit code
                },
                async errs =>
                {
                    Log.Error("Argument parsing failed:");
                    foreach (var err in errs)
                    {
                        Log.Error("- Error: {ErrorDetails}", err.ToString());
                    }

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

        services.AddLogging(configure => configure.AddSerilog(Log.Logger));
        services.AddSingleton<ILoggerFactory>(sp => new Serilog.Extensions.Logging.SerilogLoggerFactory(Log.Logger, true));

        services.AddSingleton<IMigrationScopeFactory, MigrationScopeFactory>();
        services.AddScoped<MigrationService>();

        await using var serviceProvider = services.BuildServiceProvider();
        using var scope = serviceProvider.CreateScope();
        var migrationService = scope.ServiceProvider.GetRequiredService<MigrationService>();
        Log.Information("Executing migrations...");
        await migrationService.ExecuteMigrationsAsync(opts.DatabaseType, opts.ConnectionString, opts.MigrationsPath);
        Log.Information("Migration execution task completed.");
    }
}
