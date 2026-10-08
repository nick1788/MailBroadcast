using System.Text.Json;
using MailBroadcast.Worker.Models;

// File: JsonBroadcastStateStore.cs — stores worker progress in a local JSON file using replacement writes.
namespace MailBroadcast.Worker.Services;

/// <summary>Persists broadcast state as JSON, replacing the destination only after a complete temporary write.</summary>
public sealed class JsonBroadcastStateStore : IBroadcastStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;

    /// <summary>Creates a file-backed store using the configured state-file path.</summary>
    /// <param name="options">Broadcast settings containing the state location.</param>
    public JsonBroadcastStateStore(Microsoft.Extensions.Options.IOptions<Configuration.BroadcastOptions> options)
    {
        _path = Path.GetFullPath(options.Value.StatePath);
    }

    /// <summary>Reads saved state or initializes a new state object when the file is missing or empty.</summary>
    /// <param name="cancellationToken">Token used to cancel file access and JSON deserialization.</param>
    /// <returns>The deserialized state, or a new empty state.</returns>
    public async Task<BroadcastState> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path)) return new BroadcastState();
        await using var stream = File.OpenRead(_path);
        if (await IsEmptyOrWhitespaceAsync(stream, cancellationToken)) return new BroadcastState();
        return await JsonSerializer.DeserializeAsync<BroadcastState>(stream, JsonOptions, cancellationToken)
            ?? new BroadcastState();
    }

    private static async Task<bool> IsEmptyOrWhitespaceAsync(FileStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        while (true)
        {
            var bytesRead = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (bytesRead == 0)
            {
                stream.Position = 0;
                return true;
            }

            for (var index = 0; index < bytesRead; index++)
            {
                if (buffer[index] != (byte)' ' && buffer[index] != (byte)'\t'
                    && buffer[index] != (byte)'\r' && buffer[index] != (byte)'\n')
                {
                    stream.Position = 0;
                    return false;
                }
            }
        }
    }

    /// <summary>Writes a complete state snapshot and atomically replaces the prior state file.</summary>
    /// <param name="state">The state snapshot to serialize.</param>
    /// <param name="cancellationToken">Token used to cancel directory/file operations.</param>
    /// <returns>A task representing the persistence operation.</returns>
    public async Task SaveAsync(BroadcastState state, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temporaryPath = _path + ".tmp";
        // Write-through to a sibling temporary file, then replace the visible state only after serialization finishes.
        await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None,
                         4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, state, JsonOptions, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        File.Move(temporaryPath, _path, overwrite: true);
    }
}