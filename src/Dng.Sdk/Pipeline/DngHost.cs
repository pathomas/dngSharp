using Dng.Sdk.Memory;
using Dng.Sdk.Metadata;
using Dng.Sdk.Metadata.Xmp;
using Dng.Sdk.Tasks;

namespace Dng.Sdk.Pipeline;

/// <summary>
/// Application-facing control point for the SDK. Mirrors <c>dng_host</c>.
///
/// <para>Threaded through every parse/render operation. Holds the allocator,
/// abort sniffer, XMP SDK, and high-level user policies (preview/save
/// behavior, JXL encode settings, reader-version capability). Tests use the
/// default-constructed instance — no host wiring required.</para>
/// </summary>
public sealed class DngHost
{
    public IMemoryAllocator Allocator { get; set; } = PooledMemoryAllocator.Shared;

    public AbortSniffer Sniffer { get; set; } = AbortSniffer.None;

    public IXmpSdk XmpSdk { get; set; } = new NullXmpSdk();

    /// <summary>
    /// Maximum DNG spec version this reader claims to understand. Files with
    /// <c>DNGBackwardVersion</c> greater than this are rejected by
    /// <see cref="DngShared.ValidateReadable"/>.
    /// </summary>
    public DngVersion ReaderVersion { get; set; } = DngVersion.V1_7_1;

    /// <summary>
    /// When true, the SDK is operating in "preview" mode — opcodes marked
    /// <c>OptionalForPreview</c> may be skipped, and renderers can substitute
    /// faster but lower-quality kernels (e.g. nearest-neighbor demosaic).
    /// </summary>
    public bool PreviewMode { get; set; }

    /// <summary>
    /// If non-null, preview rendering targets this maximum long-edge size in
    /// pixels (mirrors the C++ <c>kPreview_</c> sizes).
    /// </summary>
    public int? PreviewLongEdgePixels { get; set; }

    /// <summary>
    /// Maximum tile size for parallel work dispatch. Smaller tiles improve
    /// load balancing on irregular cancellation; larger tiles amortize
    /// per-tile overhead.
    /// </summary>
    public int MaxTileEdgePixels { get; set; } = 256;
}
