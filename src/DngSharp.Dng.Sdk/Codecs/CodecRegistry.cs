using DngSharp.Dng.Sdk.Codecs.LosslessJpeg;
using DngSharp.Dng.Sdk.Errors;
using DngSharp.Dng.Sdk.Tiff;

namespace DngSharp.Dng.Sdk.Codecs;

/// <summary>
/// Maps DNG <see cref="Compression"/> codes to <see cref="IRawDecoder"/>
/// implementations. Hosts replace built-in decoders via <see cref="Register"/>
/// (e.g. to plug in <c>DngSharp.Dng.Sdk.Jxl</c> for <see cref="Compression.Jxl"/>).
/// </summary>
public sealed class CodecRegistry
{
    /// <summary>Default registry — uncompressed + deflate + lossless JPEG built in.</summary>
    public static CodecRegistry Default { get; } = CreateDefault();

    private readonly Dictionary<Compression, IRawDecoder> _decoders = [];

    public void Register(IRawDecoder decoder)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        _decoders[decoder.Compression] = decoder;
    }

    public IRawDecoder GetDecoder(Compression compression)
    {
        if (_decoders.TryGetValue(compression, out var dec)) return dec;
        throw new DngException(DngError.NotYetImplemented,
            $"No decoder registered for compression {compression} (={(uint)compression})");
    }

    public bool HasDecoder(Compression compression) => _decoders.ContainsKey(compression);

    private static CodecRegistry CreateDefault()
    {
        var r = new CodecRegistry();
        r.Register(new UncompressedDecoder());
        r.Register(new DeflateDecoder());
        r.Register(new LosslessJpegDecoder());
        return r;
    }
}
