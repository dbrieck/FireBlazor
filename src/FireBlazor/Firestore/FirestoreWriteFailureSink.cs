namespace FireBlazor;

/// <summary>
/// Optional sink for Firestore SDK write failures (set / update / delete / batch / transaction).
/// Hosts may assign <see cref="OnFailure"/> to forward permission-denied (etc.) to telemetry.
/// The callback must never throw; FireBlazor swallows callback exceptions.
/// </summary>
public static class FirestoreWriteFailureSink
{
    /// <summary>Optional; null means no-op.</summary>
    public static Action<FirestoreWriteFailure>? OnFailure { get; set; }

    public static void Notify(string operation, string path, FirebaseError error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(error);

        var handler = OnFailure;
        if (handler is null)
        {
            return;
        }

        try
        {
            handler(new FirestoreWriteFailure(operation, path ?? string.Empty, error));
        }
        catch
        {
            // Never fault write Result paths.
        }
    }
}

/// <param name="Operation">Lowercase op: set, update, delete, batch, transaction.</param>
/// <param name="Path">Document path, batch summary, or "(transaction)".</param>
/// <param name="Error">SDK error.</param>
public readonly record struct FirestoreWriteFailure(string Operation, string Path, FirebaseError Error);
