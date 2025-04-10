using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Migrator.Tests.Base;

/// <summary>
/// A mock IContainer implementation specifically for managing SQLite database files
/// during testing, mimicking the interface used by Testcontainers for other databases.
/// This allows the MigrationTestBase to handle SQLite initialization and cleanup
/// uniformly with container-based databases.
/// </summary>
public sealed class SQLiteContainer : IDatabaseContainer // Implement both interfaces
{
    private readonly string _dbFilePath;
    private string _connectionString;

    public SQLiteContainer()
    {
        var dbFileName =
            // Generate a unique filename for each test instance
            $"TestDb_{Guid.NewGuid()}.sqlite";
        // Store the full path for file operations
        _dbFilePath = Path.Combine(Directory.GetCurrentDirectory(), dbFileName);
        // Construct the connection string
        _connectionString =
            $"Data Source={_dbFilePath};Cache=Shared"; // Use Cache=Shared for potential multi-connection scenarios if needed

        // Set dummy state properties required by IContainer
        Id = Guid.NewGuid().ToString("d"); // Provide a unique dummy ID
        Name = $"/sqlite_mock_{Id.Substring(0, 8)}";
        State = TestcontainersStates.Running; // Assume it's "running" once created
        Hostname = "localhost"; // SQLite runs locally
    }

    // --- IContainer/IDatabaseContainer Implementation ---

    public ILogger Logger { get; } = NullLogger.Instance;
    public string Id { get; }
    public string Name { get; }
    public string IpAddress { get; } = "127.0.0.1";
    public string MacAddress { get; } = Guid.CreateVersion7().ToString();
    public string Hostname { get; } // Required by IDatabaseContainer
    public IImage Image { get; } = new DockerImage("null/null");

    // Mock implementation - SQLite doesn't use ports like Docker containers
    public ushort GetMappedPublicPort(ushort privatePort) => 0;

    // Returns the dynamically generated SQLite connection string
    public string GetConnectionString() => _connectionString;

    // Represents the "state" of the file-based database
    public TestcontainersStates State { get; private set; }
    public TestcontainersHealthStatus Health { get; } = TestcontainersHealthStatus.Healthy;
    public long HealthCheckFailingStreak { get; } = 0;

#pragma warning disable
    public event EventHandler? Creating;
    public event EventHandler? Starting;
    public event EventHandler? Stopping;
    public event EventHandler? Pausing;
    public event EventHandler? Unpausing;
    public event EventHandler? Created;
    public event EventHandler? Started;
    public event EventHandler? Stopped;
    public event EventHandler? Paused;
    public event EventHandler? Unpaused;
#pragma warning restore

    public ushort GetMappedPublicPort(int containerPort)
    {
        throw new NotImplementedException();
    }

    public ushort GetMappedPublicPort(string containerPort)
    {
        throw new NotImplementedException();
    }

    public Task<long> GetExitCodeAsync(CancellationToken ct = new CancellationToken())
    {
        throw new NotImplementedException();
    }

    public Task<(string Stdout, string Stderr)> GetLogsAsync(DateTime since = new DateTime(),
        DateTime until = new DateTime(), bool timestampsEnabled = true,
        CancellationToken ct = new CancellationToken())
    {
        throw new NotImplementedException();
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        // "Starting" the container means ensuring a clean slate:
        // 1. Ensure the directory exists (though CurrentDirectory usually does)
        // 2. Delete the DB file if it exists from a previous run.
        if (File.Exists(_dbFilePath))
        {
            // Attempt cleanup before starting fresh
            try
            {
                SqliteConnection.ClearAllPools(); // Important before delete
                File.Delete(_dbFilePath);
            }
            catch (IOException ex)
            {
                // Log or handle error if deletion fails (e.g., file locked)
                Console.WriteLine($"[WARN] Pre-start delete failed for '{_dbFilePath}': {ex.Message}");
                // Depending on requirements, might want to throw here.
            }
        }

        State = TestcontainersStates.Running;
        return Task.CompletedTask; // No async process to start
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        // "Stopping" involves clearing connection pools to release file locks.
        try
        {
            SqliteConnection.ClearAllPools();
        }
        catch (Exception ex)
        {
            // Log potential errors during pool clearing
            Console.WriteLine($"[WARN] Error clearing SQLite pools for '{_dbFilePath}': {ex.Message}");
        }

        State = TestcontainersStates.Exited;
        return Task.CompletedTask;
    }

    public Task PauseAsync(CancellationToken ct = new CancellationToken())
    {
        throw new NotImplementedException();
    }

    public Task UnpauseAsync(CancellationToken ct = new CancellationToken())
    {
        throw new NotImplementedException();
    }

    public Task CopyAsync(byte[] fileContent, string filePath,
        UnixFileModes fileMode = UnixFileModes.None | UnixFileModes.OtherRead | UnixFileModes.GroupRead |
                                 UnixFileModes.UserWrite | UnixFileModes.UserRead,
        CancellationToken ct = new CancellationToken())
    {
        throw new NotImplementedException();
    }

    public Task CopyAsync(string source, string target,
        UnixFileModes fileMode = UnixFileModes.None | UnixFileModes.OtherRead | UnixFileModes.GroupRead |
                                 UnixFileModes.UserWrite | UnixFileModes.UserRead,
        CancellationToken ct = new CancellationToken())
    {
        throw new NotImplementedException();
    }

    public Task CopyAsync(DirectoryInfo source, string target,
        UnixFileModes fileMode = UnixFileModes.None | UnixFileModes.OtherRead | UnixFileModes.GroupRead |
                                 UnixFileModes.UserWrite | UnixFileModes.UserRead,
        CancellationToken ct = new CancellationToken())
    {
        throw new NotImplementedException();
    }

    public Task CopyAsync(FileInfo source, string target,
        UnixFileModes fileMode = UnixFileModes.None | UnixFileModes.OtherRead | UnixFileModes.GroupRead |
                                 UnixFileModes.UserWrite | UnixFileModes.UserRead,
        CancellationToken ct = new CancellationToken())
    {
        throw new NotImplementedException();
    }

    public Task<byte[]> ReadFileAsync(string filePath, CancellationToken ct = new CancellationToken())
    {
        throw new NotImplementedException();
    }

    public Task<ExecResult> ExecAsync(IList<string> command, CancellationToken ct = new CancellationToken())
    {
        throw new NotImplementedException();
    }

    public DateTime CreatedTime { get; } = DateTime.UtcNow;
    public DateTime StartedTime { get; } = DateTime.UtcNow;
    public DateTime StoppedTime { get; } = DateTime.UtcNow;
    public DateTime PausedTime { get; } = DateTime.UtcNow;
    public DateTime UnpausedTime { get; } = DateTime.UtcNow;

    // Dispose pattern to ensure file cleanup
    public async ValueTask DisposeAsync()
    {
        // Ensure pools are cleared before attempting delete
        await StopAsync();

        if (File.Exists(_dbFilePath))
        {
            try
            {
                File.Delete(_dbFilePath);
            }
            catch (IOException ex)
            {
                // Log error if cleanup fails
                Console.WriteLine($"[WARN] Dispose failed to delete '{_dbFilePath}': {ex.Message}");
            }
        }

        State = TestcontainersStates.Undefined; // Or another appropriate state post-disposal
    }
}
