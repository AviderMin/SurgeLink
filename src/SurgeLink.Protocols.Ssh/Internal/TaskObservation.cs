namespace SurgeLink.Protocols.Ssh.Internal;

/// <summary>
/// Marks abandoned tasks as observed so a later failure cannot surface as an
/// unobserved task exception. Used for user prompts that may outlive the connection attempt.
/// </summary>
internal static class TaskObservation
{
    /// <summary>Attaches a fault observer to <paramref name="task"/> without awaiting it.</summary>
    /// <param name="task">Task whose exception must never go unobserved.</param>
    public static void ObserveFaults(Task task)
    {
        ArgumentNullException.ThrowIfNull(task);

        if (task.IsCompleted)
        {
            _ = task.Exception;
            return;
        }

        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
