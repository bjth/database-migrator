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
public sealed class SQLiteContainer : IDatabaseContainer
{
    private readonly string _dbFilePath;
    private readonly string _connectionString;

    public SQLiteContainer()
    {
        var dbFileName =
            $"TestDb_{Guid.NewGuid()}.sqlite";
        _dbFilePath = Path.Combine(Directory.GetCurrentDirectory(), dbFileName);
        _connectionString =
            $"Data Source={_dbFilePath};Cache=Shared";

        Id = Guid.NewGuid().ToString("d");
        Name = $"/sqlite_mock_{Id[..8]}";
        State = TestcontainersStates.Running;
        Hostname = "localhost";
    }

    // --- IContainer/IDatabaseContainer Implementation ---

    public ILogger Logger { get; } = NullLogger.Instance;
    public string Id { get; }
    public string Name { get; }
    public string IpAddress { get; } = "127.0.0.1";
    public string MacAddress { get; } = Guid.CreateVersion7().ToString();
    public string Hostname { get; }
    public IImage Image { get; } = new DockerImage("null/null");

    public ushort GetMappedPublicPort(ushort privatePort) => 0;

    public string GetConnectionString() => _connectionString;

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
        if (File.Exists(_dbFilePath))
        {
            try
            {
                SqliteConnection.ClearAllPools();
                File.Delete(_dbFilePath);
            }
            catch (IOException ex)
            {
                Console.WriteLine($"[WARN] Pre-start delete failed for '{_dbFilePath}': {ex.Message}");
            }
        }

        State = TestcontainersStates.Running;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        try
        {
            SqliteConnection.ClearAllPools();
        }
        catch (Exception ex)
        {
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

    public async ValueTask DisposeAsync()
    {
        await StopAsync();

        if (File.Exists(_dbFilePath))
        {
            try
            {
                File.Delete(_dbFilePath);
            }
            catch (IOException ex)
            {
                Console.WriteLine($"[WARN] Dispose failed to delete '{_dbFilePath}': {ex.Message}");
            }
        }

        State = TestcontainersStates.Undefined;
    }
}
