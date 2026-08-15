using System.Collections.Concurrent;
using Autodesk.Revit.UI;

namespace BIM.DatViet.Infrastructure;

/// <summary>
/// Bridges the modeless abutment window back into a valid Revit API context.
/// A modal ExternalCommand blocks other ExternalEvent consumers, including the
/// 765T inspection runtime, for the entire lifetime of the dialog.
/// </summary>
public sealed class AbutmentRevitExternalEventDispatcher : IExternalEventHandler, IDisposable
{
    private readonly ConcurrentQueue<WorkItem> _pending = new();
    private readonly ExternalEvent _externalEvent;
    private int _disposed;

    public AbutmentRevitExternalEventDispatcher()
    {
        _externalEvent = ExternalEvent.Create(this);
    }

    public Task InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (Volatile.Read(ref _disposed) != 0)
            return Task.FromException(new ObjectDisposedException(nameof(AbutmentRevitExternalEventDispatcher)));

        var completion = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new WorkItem(action, completion);
        _pending.Enqueue(item);
        try
        {
            var request = _externalEvent.Raise();
            if (request == ExternalEventRequest.Denied)
            {
                item.Cancelled = true;
                completion.TrySetException(new InvalidOperationException(
                    "Revit từ chối ExternalEvent của công cụ rải thép mố."));
            }
        }
        catch (Exception exception)
        {
            item.Cancelled = true;
            completion.TrySetException(exception);
        }

        return completion.Task;
    }

    public void Execute(UIApplication app)
    {
        while (_pending.TryDequeue(out var item))
        {
            if (item.Cancelled) continue;
            if (Volatile.Read(ref _disposed) != 0)
            {
                item.Completion.TrySetException(
                    new ObjectDisposedException(nameof(AbutmentRevitExternalEventDispatcher)));
                continue;
            }

            try
            {
                item.Action();
                item.Completion.TrySetResult(null);
            }
            catch (Exception exception)
            {
                item.Completion.TrySetException(exception);
            }
        }
    }

    public string GetName() => "BIM-DatViet Abutment Rebar Modeless Dispatcher";

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        while (_pending.TryDequeue(out var item))
        {
            item.Completion.TrySetException(
                new ObjectDisposedException(nameof(AbutmentRevitExternalEventDispatcher)));
        }
        _externalEvent.Dispose();
    }

    private sealed class WorkItem(
        Action action,
        TaskCompletionSource<object?> completion)
    {
        public Action Action { get; } = action;
        public TaskCompletionSource<object?> Completion { get; } = completion;
        public bool Cancelled { get; set; }
    }
}
