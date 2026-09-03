using Dng.Sdk.Errors;
using Dng.Sdk.Primitives;

namespace Dng.Sdk.Tasks;

/// <summary>
/// Cooperative cancellation + progress reporter. Mirrors
/// <c>dng_abort_sniffer</c>. Backed by a <see cref="CancellationToken"/> so
/// .NET-native cancellation patterns (linked tokens, timeouts, parent task
/// cancellation) all flow through naturally.
/// </summary>
public sealed class AbortSniffer
{
    public static AbortSniffer None { get; } = new(CancellationToken.None, null);

    private readonly CancellationToken _token;
    private readonly IProgress<double>? _progress;

    public AbortSniffer(CancellationToken token, IProgress<double>? progress = null)
    {
        _token = token;
        _progress = progress;
    }

    /// <summary>
    /// Throws <see cref="DngException"/>(<see cref="DngError.UserCanceled"/>)
    /// if the underlying token has been cancelled.
    /// </summary>
    public void Sniff()
    {
        if (_token.IsCancellationRequested)
            throw new DngException(DngError.UserCanceled, "Operation cancelled");
    }

    public CancellationToken Token => _token;

    public void Report(double fractionComplete)
    {
        _progress?.Report(System.Math.Clamp(fractionComplete, 0.0, 1.0));
        Sniff();
    }
}

/// <summary>
/// A unit of work that processes one tile-shaped area. Mirrors
/// <c>dng_area_task</c>.
///
/// <para>The runner invokes <see cref="Process"/> on each tile rectangle
/// returned by a <see cref="Dng.Sdk.Imaging.TileIterator"/> over
/// <see cref="UnitArea"/>. Implementations must be thread-safe across tiles
/// because <see cref="AreaTaskRunner.Run"/> dispatches tiles in parallel.</para>
/// </summary>
public interface IAreaTask
{
    /// <summary>
    /// Tile size to dispatch. Returning a large rect effectively serializes
    /// the task (one tile == whole area).
    /// </summary>
    DngPoint MaxTileSize(DngPoint imageSize);

    /// <summary>
    /// Per-tile compute. Called once per tile; may execute in parallel with
    /// other tile invocations. Throwing <see cref="DngException"/> from a
    /// tile cancels remaining work.
    /// </summary>
    void Process(int threadIndex, DngRect tile);
}

/// <summary>
/// Runs an <see cref="IAreaTask"/> over a rectangle using <see cref="Parallel"/>.
/// </summary>
public static class AreaTaskRunner
{
    /// <summary>
    /// Execute <paramref name="task"/> over <paramref name="area"/>. Returns
    /// after every tile has completed (or one has thrown).
    ///
    /// <para><paramref name="sniffer"/> is checked between tile dispatches
    /// and progress is reported as <c>(tilesDone / totalTiles)</c>.</para>
    /// </summary>
    public static void Run(IAreaTask task, DngRect area, AbortSniffer? sniffer = null,
                           int? maxDegreeOfParallelism = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        sniffer ??= AbortSniffer.None;

        if (area.IsEmpty) return;

        var tileSize = task.MaxTileSize(area.Size);
        var tiles = Dng.Sdk.Imaging.TileIterator.Enumerate(tileSize, area);

        int dop = maxDegreeOfParallelism ?? Environment.ProcessorCount;
        var options = new ParallelOptions
        {
            CancellationToken = sniffer.Token,
            MaxDegreeOfParallelism = System.Math.Max(1, dop),
        };

        int completed = 0;
        try
        {
            Parallel.For(0, tiles.Count, options, (i, _) =>
            {
                // Thread.CurrentThread.ManagedThreadId would over-count and
                // not be stable; expose the loop index as "thread index" the
                // way the C++ code does (consumers use it to index per-thread
                // scratch buffers).
                task.Process(Environment.CurrentManagedThreadId, tiles[i]);

                int done = System.Threading.Interlocked.Increment(ref completed);
                sniffer.Report((double)done / tiles.Count);
            });
        }
        catch (OperationCanceledException) when (sniffer.Token.IsCancellationRequested)
        {
            throw new DngException(DngError.UserCanceled, "Operation cancelled");
        }
        catch (AggregateException agg) when (agg.InnerExceptions.Count == 1 && agg.InnerException is DngException d)
        {
            throw d;
        }
    }
}
