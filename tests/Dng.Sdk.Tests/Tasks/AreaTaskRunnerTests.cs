using System.Collections.Concurrent;
using Dng.Sdk.Errors;
using Dng.Sdk.Primitives;
using Dng.Sdk.Tasks;

namespace Dng.Sdk.Tests.Tasks;

public class AreaTaskRunnerTests
{
    private sealed class CountingTask(DngPoint tileSize) : IAreaTask
    {
        public ConcurrentBag<DngRect> Tiles { get; } = [];
        public DngPoint MaxTileSize(DngPoint imageSize) => tileSize;
        public void Process(int threadIndex, DngRect tile) => Tiles.Add(tile);
    }

    [Fact]
    public void Runs_every_tile_once()
    {
        var task = new CountingTask(new DngPoint(8, 8));
        AreaTaskRunner.Run(task, new DngRect(0, 0, 16, 16));
        Assert.Equal(4, task.Tiles.Count); // 2x2 tile grid
    }

    [Fact]
    public void Cancellation_throws_user_cancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var sniffer = new AbortSniffer(cts.Token);
        var task = new CountingTask(new DngPoint(8, 8));
        var ex = Assert.Throws<DngException>(() =>
            AreaTaskRunner.Run(task, new DngRect(0, 0, 32, 32), sniffer));
        Assert.Equal(DngError.UserCanceled, ex.ErrorCode);
    }

    [Fact]
    public void Exception_in_tile_propagates()
    {
        var bomb = new BombTask();
        var ex = Assert.Throws<DngException>(() =>
            AreaTaskRunner.Run(bomb, new DngRect(0, 0, 8, 8)));
        Assert.Equal(DngError.Unknown, ex.ErrorCode);
    }

    [Fact]
    public void Empty_area_is_noop()
    {
        var task = new CountingTask(new DngPoint(8, 8));
        AreaTaskRunner.Run(task, default);
        Assert.Empty(task.Tiles);
    }

    [Fact]
    public void Progress_reports_to_completion()
    {
        var progress = new ProgressBag();
        var sniffer = new AbortSniffer(CancellationToken.None, progress);
        var task = new CountingTask(new DngPoint(8, 8));
        AreaTaskRunner.Run(task, new DngRect(0, 0, 16, 16), sniffer);
        Assert.NotEmpty(progress.Reports);
        // The reports are appended under lock so insertion order can interleave
        // across threads — 1.0 always appears (the tile that increments `completed`
        // to N reports N/N) but may not be the last entry in the list.
        Assert.Contains(1.0, progress.Reports);
    }

    private sealed class BombTask : IAreaTask
    {
        public DngPoint MaxTileSize(DngPoint imageSize) => imageSize;
        public void Process(int threadIndex, DngRect tile) =>
            throw new DngException(DngError.Unknown, "boom");
    }

    private sealed class ProgressBag : IProgress<double>
    {
        private readonly System.Collections.Generic.List<double> _reports = [];
        private readonly object _gate = new();
        public System.Collections.Generic.IReadOnlyList<double> Reports
        {
            get { lock (_gate) return [.. _reports]; }
        }
        public void Report(double value)
        {
            lock (_gate) _reports.Add(value);
        }
    }
}
