namespace Dng.Sdk.IO;

/// <summary>
/// Convenience factory for opening DNG/TIFF files. Mirrors
/// <c>dng_file_stream</c>.
/// </summary>
public static class DngFileStream
{
    public static DngStream OpenRead(string path)
    {
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.RandomAccess);
        return new DngStream(fs, bigEndian: false, offsetInOriginalFile: 0, leaveOpen: false);
    }

    public static DngStream Create(string path)
    {
        var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024,
            FileOptions.SequentialScan);
        return new DngStream(fs, bigEndian: false, offsetInOriginalFile: 0, leaveOpen: false);
    }
}

/// <summary>
/// Convenience factory for in-memory DNG/TIFF streams. Mirrors
/// <c>dng_memory_stream</c>.
/// </summary>
public static class DngMemoryStream
{
    public static DngStream Wrap(ReadOnlyMemory<byte> data, bool bigEndian = false)
    {
        var ms = new MemoryStream(data.ToArray(), writable: false);
        return new DngStream(ms, bigEndian, offsetInOriginalFile: 0, leaveOpen: false);
    }

    /// <summary>
    /// Wrap a byte array without copying. The caller must keep the array alive
    /// for the lifetime of the returned stream.
    /// </summary>
    public static DngStream WrapNoCopy(byte[] data, bool bigEndian = false)
    {
        var ms = new MemoryStream(data, writable: false);
        return new DngStream(ms, bigEndian, offsetInOriginalFile: 0, leaveOpen: false);
    }

    public static DngStream Empty()
    {
        return new DngStream(new MemoryStream(), bigEndian: false);
    }
}
