using System.Diagnostics;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Storage;
using DramaBoard.FirstBoard;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using SegmentStore = Atelia.RbfSegmentStore.RbfSegmentStore;

namespace DramaBoard.FirstBoard.Persistence.Process;

internal sealed record CommitMeasurement(string Operation, double ElapsedMilliseconds,
    long CurrentThreadAllocatedBytes, bool ReturnedNormally);

/// <summary>Transparent diagnostics around the synchronous consumer commit surface.</summary>
internal sealed class MeasuredHistory(IOccurrenceHistory<FirstBoardWorld, FirstBoardFact> inner)
    : IOccurrenceHistory<FirstBoardWorld, FirstBoardFact>
{
    private readonly List<CommitMeasurement> _measurements = [];
    public IReadOnlyList<CommitMeasurement> Measurements => _measurements;
    public FirstBoardWorld State => inner.State;
    public KernelCursor Cursor => inner.Cursor;
    public OccurrenceEvent<FirstBoardFact>? PendingEvent => inner.PendingEvent;

    public void CommitEvent(OccurrenceEvent<FirstBoardFact> occurrence) =>
        Measure("CommitEvent", () => inner.CommitEvent(occurrence));

    public void CommitState(FirstBoardWorld nextState, KernelCursor nextCursor) =>
        Measure("CommitState", () => inner.CommitState(nextState, nextCursor));

    private void Measure(string operation, Action commit)
    {
        bool returned = false;
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();
        try
        {
            commit();
            returned = true;
        }
        finally
        {
            double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            _measurements.Add(new(operation, elapsed, allocated, returned));
        }
    }
}

internal static class PersistenceMetrics
{
    /// <summary>Reads a closed save using public low-level storage APIs. The high-level
    /// consumer API does not expose these counts. Addresses apply only to this inspection.</summary>
    public static object Inspect(string path)
    {
        string root = Path.GetFullPath(path);
        using EventHistoryRepository repository = EventHistoryRepository.OpenReadOnlyExisting(root);
        using SegmentStore segments = SegmentStore.OpenReadOnlyExisting(Path.Combine(root, "state"));
        var states = new StateRevisionStore(segments);
        var revisions = new List<object>();
        int ordinal = 0;
        foreach (GraphFrame frame in repository.ReadFrames("main"))
        {
            StateRevision revision = states.Read(frame.RevisionAddress);
            revisions.Add(new
            {
                Ordinal = ordinal++,
                Role = frame.Kind.ToString(),
                DiagnosticRevisionAddress = frame.RevisionAddress.ToString(),
                HeadMapKind = revision.ObjectHeadMapKind.ToString(),
                BaseObjects = revision.LocalObjects.Count(record => record.Kind == ObjectVersionKind.Base),
                DeltaObjects = revision.LocalObjects.Count(record => record.Kind == ObjectVersionKind.Delta),
                RemovedObjects = revision.RemovedObjectIds.Count,
                LocalObjectBodyBytes = revision.LocalObjects.Sum(record => (long)record.Body.Length),
            });
        }
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(file => new { Path = Path.GetRelativePath(root, file).Replace('\\', '/'), Bytes = new FileInfo(file).Length })
            .ToArray();
        return new
        {
            Scope = "Low-level diagnostic via public EventHistory ReadFrames and StateRevisionStore.Read; main branch logical frames only. File bytes include all save files, not just that branch. Raw local object body bytes exclude frame/map/schema overhead.",
            TotalFileBytes = files.Sum(file => file.Bytes),
            Files = files,
            Revisions = revisions,
        };
    }
}
