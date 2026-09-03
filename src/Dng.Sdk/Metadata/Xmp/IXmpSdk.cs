using Dng.Sdk.Errors;

namespace Dng.Sdk.Metadata.Xmp;

/// <summary>
/// Raw XMP packet (UTF-8 XML wrapped in RDF). Mirrors the byte-level state
/// of an XMP serialization before/after parsing.
///
/// <para>This holds the on-disk bytes; structured access to properties is
/// the responsibility of an <see cref="IXmpSdk"/> implementation.</para>
/// </summary>
public sealed class DngXmpPacket
{
    public ReadOnlyMemory<byte> Bytes { get; }

    public DngXmpPacket(ReadOnlyMemory<byte> bytes)
    {
        Bytes = bytes;
    }

    public bool IsEmpty => Bytes.IsEmpty;

    public string AsUtf8String() =>
        Bytes.IsEmpty ? string.Empty : System.Text.Encoding.UTF8.GetString(Bytes.Span);
}

/// <summary>
/// Pluggable XMP toolkit. Mirrors <c>dng_xmp_sdk</c>.
///
/// <para>The DNG SDK uses Adobe's XMP Toolkit (a C++ library) for all XMP
/// parsing/serialization. The .NET port keeps that boundary as an interface
/// so:
///   <list type="bullet">
///     <item><c>Dng.Sdk.Xmp</c> can wrap libxmp via <c>LibraryImport</c>
///       (default plan for production hosts),</item>
///     <item>tests and CLI smoke runs can use <see cref="NullXmpSdk"/> to
///       avoid a native dependency,</item>
///     <item>a future pure-managed implementation can drop in without
///       touching the rest of the SDK.</item>
///   </list>
/// </para>
///
/// <para><b>Lifecycle:</b> mirrors the C++ <c>Initialize</c>/<c>Terminate</c>
/// requirement. Disposing the instance terminates the toolkit and releases
/// any process-global resources.</para>
/// </summary>
public interface IXmpSdk : IDisposable
{
    /// <summary>Has the SDK been initialized via <see cref="Initialize"/>?</summary>
    bool IsInitialized { get; }

    /// <summary>
    /// Initialize the underlying XMP toolkit. Safe to call multiple times.
    /// Mirrors <c>dng_xmp_sdk::InitializeSDK</c>.
    /// </summary>
    void Initialize();

    /// <summary>
    /// Parse a UTF-8 RDF/XMP packet into the SDK's internal representation.
    /// Returns a handle the caller can use with <see cref="GetProperty"/>
    /// / <see cref="SerializePacket"/>.
    /// </summary>
    IXmpMeta Parse(ReadOnlySpan<byte> packet);

    /// <summary>Create a new, empty XMP meta object.</summary>
    IXmpMeta CreateEmpty();
}

/// <summary>
/// Opaque handle to a parsed XMP packet. Mirrors <c>SXMPMeta</c> in the
/// Adobe XMP toolkit. Subset interface — covers reads/writes typical of
/// DNG metadata sync.
/// </summary>
public interface IXmpMeta : IDisposable
{
    /// <summary>
    /// Get a simple property value. Returns null when the property is
    /// absent. <paramref name="schemaNs"/> is the property's namespace URI
    /// (e.g. <c>http://ns.adobe.com/exif/1.0/</c>); <paramref name="propName"/>
    /// is the local QName.
    /// </summary>
    string? GetProperty(string schemaNs, string propName);

    /// <summary>Set a simple property value, creating it if absent.</summary>
    void SetProperty(string schemaNs, string propName, string value);

    /// <summary>Delete a property if present.</summary>
    void DeleteProperty(string schemaNs, string propName);

    /// <summary>Serialize back to a UTF-8 RDF/XMP packet.</summary>
    byte[] SerializePacket();
}

/// <summary>
/// No-op XMP SDK. Returns empty packets and silently discards writes.
/// Useful for hosts that don't need XMP (and for tests) without taking a
/// dependency on the native libxmp adapter.
/// </summary>
public sealed class NullXmpSdk : IXmpSdk
{
    public bool IsInitialized { get; private set; }

    public void Initialize() => IsInitialized = true;

    public IXmpMeta Parse(ReadOnlySpan<byte> packet) => new NullMeta();
    public IXmpMeta CreateEmpty() => new NullMeta();

    public void Dispose() => IsInitialized = false;

    private sealed class NullMeta : IXmpMeta
    {
        public string? GetProperty(string schemaNs, string propName) => null;
        public void SetProperty(string schemaNs, string propName, string value) { }
        public void DeleteProperty(string schemaNs, string propName) { }
        public byte[] SerializePacket() => [];
        public void Dispose() { }
    }
}

/// <summary>
/// Sentinel SDK whose <see cref="Parse"/>/<see cref="CreateEmpty"/> throws
/// <see cref="DngError.NotYetImplemented"/>. Use this when a host explicitly
/// requires real XMP but hasn't wired the libxmp adapter; tests then surface
/// the missing-dependency clearly instead of silently dropping metadata.
/// </summary>
public sealed class ThrowingXmpSdk : IXmpSdk
{
    public bool IsInitialized { get; private set; }
    public void Initialize() => IsInitialized = true;
    public IXmpMeta Parse(ReadOnlySpan<byte> packet) { DngThrow.NotYetImplemented("Wire Dng.Sdk.Xmp via IXmpSdk"); return null!; }
    public IXmpMeta CreateEmpty() { DngThrow.NotYetImplemented("Wire Dng.Sdk.Xmp via IXmpSdk"); return null!; }
    public void Dispose() => IsInitialized = false;
}
