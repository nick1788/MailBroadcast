using MailBroadcast.Worker.Models;

// File: IBroadcastStateStore.cs — persistence boundary for durable scheduling and recipient progress.
namespace MailBroadcast.Worker.Services;

/// <summary>Defines asynchronous access to durable broadcast processing state.</summary>
public interface IBroadcastStateStore
{
    /// <summary>Loads the current state, returning an empty state when none has been saved.</summary>
    /// <param name="cancellationToken">Token used to cancel the load operation.</param>
    /// <returns>The persisted broadcast state.</returns>
    Task<BroadcastState> LoadAsync(CancellationToken cancellationToken);

    /// <summary>Persists the supplied state so restarts retain attempt caps and delivery outcomes.</summary>
    /// <param name="state">The state snapshot to save.</param>
    /// <param name="cancellationToken">Token used to cancel the save operation.</param>
    /// <returns>A task representing the save operation.</returns>
    Task SaveAsync(BroadcastState state, CancellationToken cancellationToken);
}