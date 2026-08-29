using EitHost.Core.Application.Realtime;

namespace EitHost.App.ViewModels.Workspaces;

internal sealed class RealtimeSessionController : IDisposable
{
    private readonly Dictionary<string, RealtimeRunState> states =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> activeSetLabels =
        new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? lastCancellation;
    private Task? lastTask;
    private bool disposed;

    internal IReadOnlyCollection<RealtimeRunState> States => states.Values;

    internal int ActiveSetCount => activeSetLabels.Count;

    internal bool IsAnyActive =>
        states.Values.Any(state => state.IsActive)
        || lastTask is { IsCompleted: false };

    internal bool HasUnfinishedTask => lastTask is { IsCompleted: false };

    internal Task? LastTask => lastTask;

    internal bool IsSetActive(string setLabel) => activeSetLabels.Contains(setLabel);

    internal bool TryGetState(string setLabel, out RealtimeRunState state) =>
        states.TryGetValue(setLabel, out state!);

    internal RealtimeRunState CreateState(
        string setLabel,
        Action<RealtimeRunState> runSnapshotChanged,
        Action<ReferenceReconstructionSnapshot> referenceSnapshotChanged)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(setLabel);
        ArgumentNullException.ThrowIfNull(runSnapshotChanged);
        ArgumentNullException.ThrowIfNull(referenceSnapshotChanged);

        if (states.TryGetValue(setLabel, out var existing) && existing.IsActive)
        {
            throw new InvalidOperationException($"{setLabel} 实时成像已经在运行。");
        }

        var state = new RealtimeRunState(setLabel);
        state.RunCoordinator.EventPublished += _ => runSnapshotChanged(state);
        state.ReferenceSnapshotChanged += referenceSnapshotChanged;
        states[setLabel] = state;
        lastCancellation = state.Cancellation;
        return state;
    }

    internal Task Start(
        RealtimeRunState state,
        Func<CancellationToken, Task> runLoop)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(runLoop);
        if (!states.TryGetValue(state.SetLabel, out var registered)
            || !ReferenceEquals(registered, state))
        {
            throw new InvalidOperationException($"{state.SetLabel} 实时状态未登记到当前会话控制器。");
        }

        state.Task = state.RunCoordinator.Start(runLoop);
        activeSetLabels.Add(state.SetLabel);
        lastCancellation = state.Cancellation;
        lastTask = state.Task;
        return state.Task;
    }

    internal RealtimeStopRequest RequestStop(string? setLabel)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var selectedStates = GetStatesToStop(setLabel);
        var newlyRequested = new List<RealtimeRunState>(selectedStates.Count);
        foreach (var state in selectedStates)
        {
            if (state.IsStopRequested)
            {
                continue;
            }

            state.RequestStop();
            newlyRequested.Add(state);
        }

        var legacyCancellationRequested = false;
        if (selectedStates.Count == 0
            && lastCancellation is not null
            && lastTask is { IsCompleted: false })
        {
            lastCancellation.Cancel();
            legacyCancellationRequested = true;
        }

        return new RealtimeStopRequest(selectedStates, newlyRequested, legacyCancellationRequested);
    }

    internal IReadOnlyList<RealtimeRunState> GetStatesToStop(string? setLabel)
    {
        if (!string.IsNullOrWhiteSpace(setLabel)
            && states.TryGetValue(setLabel, out var selected)
            && selected.IsActive)
        {
            return [selected];
        }

        return states.Values
            .Where(state => state.IsActive)
            .ToArray();
    }

    internal bool Complete(RealtimeRunState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!states.TryGetValue(state.SetLabel, out var existing)
            || !ReferenceEquals(existing, state))
        {
            return false;
        }

        state.RunCoordinator.Dispose();
        states.Remove(state.SetLabel);
        activeSetLabels.Remove(state.SetLabel);
        if (ReferenceEquals(state.Task, lastTask))
        {
            lastCancellation = null;
            lastTask = null;
        }

        return true;
    }

    internal void MarkLoopStopped(string setLabel)
    {
        activeSetLabels.Remove(setLabel);
    }

    internal IReadOnlyList<Task> GetTrackedTasks()
    {
        var tasks = states.Values
            .Select(state => state.Task)
            .Where(task => task is not null)
            .Cast<Task>()
            .ToArray();
        return tasks.Length == 0 && lastTask is not null ? [lastTask] : tasks;
    }

    internal void ClearFailedStart()
    {
        var unstartedStates = states.Values
            .Where(state => state.Task is null)
            .ToArray();
        foreach (var state in unstartedStates)
        {
            state.RunCoordinator.Dispose();
            states.Remove(state.SetLabel);
            activeSetLabels.Remove(state.SetLabel);
            if (ReferenceEquals(state.Cancellation, lastCancellation))
            {
                lastCancellation = null;
            }
        }

        if (lastTask is { IsCompleted: true })
        {
            lastTask = null;
        }
    }

    internal void SetLegacyTask(CancellationTokenSource cancellation, Task task)
    {
        ArgumentNullException.ThrowIfNull(cancellation);
        ArgumentNullException.ThrowIfNull(task);
        lastCancellation = cancellation;
        lastTask = task;
    }

    internal void RegisterStateForTest(RealtimeRunState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        states.Add(state.SetLabel, state);
    }

    internal void MarkSetActiveForTest(string setLabel)
    {
        activeSetLabels.Add(setLabel);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        var currentStates = states.Values.ToArray();
        foreach (var state in currentStates)
        {
            state.RequestStop();
        }

        if (currentStates.Length == 0)
        {
            lastCancellation?.Cancel();
        }

        lastCancellation = null;
        lastTask = null;
        states.Clear();
        activeSetLabels.Clear();
    }
}

internal sealed record RealtimeStopRequest(
    IReadOnlyList<RealtimeRunState> States,
    IReadOnlyList<RealtimeRunState> NewlyRequestedStates,
    bool LegacyCancellationRequested);
