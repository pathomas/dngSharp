using DngSharp.Dng.Sdk.Pipeline;
using DngSharp.Dng.Sdk.Tasks;

namespace DngSharp.Dng.Sdk.Imaging.Opcodes;

/// <summary>
/// Splits a range of row indices into fixed-height bands and dispatches
/// each band through <see cref="ParallelWork"/>. Used by the per-pixel
/// opcode appliers, whose loops are naturally row-major and whose pixels
/// are mutually independent — so any band partitioning yields output
/// byte-identical to the serial loop.
/// </summary>
internal static class RowBandRunner
{
    /// <summary>
    /// Band height in (pitched) rows. 64 keeps ~60+ bands for a typical
    /// sensor height, which balances well across 4–16 threads while
    /// keeping per-band dispatch overhead negligible.
    /// </summary>
    public const int BandRows = 64;

    /// <summary>
    /// Invoke <paramref name="body"/>(start, endExclusive) for consecutive
    /// bands covering <c>[0, rowCount)</c>.
    /// </summary>
    public static void Run(uint rowCount, DngHost? host, Action<uint, uint> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (rowCount == 0) return;

        int bands = (int)((rowCount + BandRows - 1) / BandRows);
        ParallelWork.For(0, bands, host?.Sniffer, host?.MaxThreads, b =>
        {
            uint start = (uint)b * BandRows;
            uint end = System.Math.Min(start + BandRows, rowCount);
            body(start, end);
        });
    }

    /// <summary>
    /// Convenience overload for loops indexed by absolute row over
    /// <c>[top, bottom)</c>.
    /// </summary>
    public static void Run(int top, int bottom, DngHost? host, Action<int, int> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (bottom <= top) return;
        Run((uint)(bottom - top), host, (s, e) => body(top + (int)s, top + (int)e));
    }
}
