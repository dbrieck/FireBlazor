using System.Diagnostics;
using Microsoft.AspNetCore.Components;

namespace FireBlazor.Components;

/// <summary>
/// Base class for Blazor components that use Firebase real-time subscriptions.
/// Automatically manages subscription lifecycle and cleanup.
/// Thread-safe for concurrent subscription management.
/// </summary>
/// <example>
/// <code>
/// public class UserList : FirebaseComponentBase
/// {
///     [Inject] public IFirebase Firebase { get; set; } = default!;
///
///     private IReadOnlyList&lt;DocumentSnapshot&lt;User&gt;&gt; _users = [];
///
///     protected override void OnInitialized()
///     {
///         // Subscribe will be automatically cleaned up on Dispose
///         Subscribe(Firebase.Firestore.Collection&lt;User&gt;().OnSnapshot(
///             users => { _users = users; StateHasChanged(); },
///             error => Console.WriteLine(error)
///         ));
///     }
/// }
/// </code>
/// </example>
public abstract class FirebaseComponentBase : ComponentBase, IDisposable
{
    private readonly object _lock = new();
    private readonly List<Func<ValueTask>> _subscriptions = [];
    private bool _disposed;

    /// <summary>
    /// Gets the count of active subscriptions (for testing purposes).
    /// </summary>
    protected int SubscriptionCount
    {
        get
        {
            lock (_lock)
            {
                return _subscriptions.Count;
            }
        }
    }

    /// <summary>
    /// Gets the list of active subscriptions (for testing purposes).
    /// Returns a snapshot copy to avoid threading issues.
    /// </summary>
    protected IReadOnlyList<Func<ValueTask>> Subscriptions
    {
        get
        {
            lock (_lock)
            {
                return [.. _subscriptions];
            }
        }
    }

    /// <summary>
    /// Registers an unsubscribe action to be called when the component is disposed.
    /// Use this to manage real-time subscriptions from Firestore, Auth, etc.
    /// Thread-safe: can be called from any thread.
    /// </summary>
    /// <param name="unsubscribe">The unsubscribe action returned by OnSnapshot methods</param>
    protected void Subscribe(Func<ValueTask> unsubscribe)
    {
        lock (_lock)
        {
            if (_disposed) return;
            _subscriptions.Add(unsubscribe);
        }
    }

    /// <summary>
    /// Clears all subscriptions without disposing the component.
    /// Useful when you need to reset subscriptions (e.g., when query parameters change).
    /// Thread-safe: can be called from any thread.
    /// </summary>
    protected void ClearSubscriptions()
    {
        List<Func<ValueTask>> subscriptionsToUnsubscribe;

        lock (_lock)
        {
            subscriptionsToUnsubscribe = [.. _subscriptions];
            _subscriptions.Clear();
        }

        // Unsubscribe outside the lock to avoid potential deadlocks.
        // Fire-and-forget: sync component dispose cannot block on WASM.
        foreach (var unsubscribe in subscriptionsToUnsubscribe)
        {
            _ = UnsubscribeSafeAsync(unsubscribe);
        }
    }

    /// <summary>
    /// Disposes the component and unsubscribes from all active subscriptions.
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Disposes the component and unsubscribes from all active subscriptions.
    /// </summary>
    /// <param name="disposing">True if called from Dispose(), false if from finalizer</param>
    protected virtual void Dispose(bool disposing)
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }

        if (disposing)
        {
            ClearSubscriptions();
        }
    }

    private static async Task UnsubscribeSafeAsync(Func<ValueTask> unsubscribe)
    {
        try
        {
            await unsubscribe().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FireBlazor] Error during unsubscribe: {ex.Message}");
        }
    }
}
