using System.Diagnostics.CodeAnalysis;

namespace Dng.Sdk.Errors;

/// <summary>
/// Base exception for all DNG SDK errors. Mirrors <c>dng_exception</c>.
/// </summary>
public class DngException : Exception
{
    public DngError ErrorCode { get; }

    public DngException(DngError errorCode)
        : base(errorCode.ToString())
    {
        ErrorCode = errorCode;
    }

    public DngException(DngError errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    public DngException(DngError errorCode, string message, Exception inner)
        : base(message, inner)
    {
        ErrorCode = errorCode;
    }
}

/// <summary>
/// Static throw helpers mirroring the <c>ThrowXxx</c> family in
/// <c>dng_exceptions.h</c>. Marked <see cref="DoesNotReturnAttribute"/> so the
/// compiler can drop dead code after the call site.
/// </summary>
public static class DngThrow
{
    [DoesNotReturn]
    public static void ProgramError(string message) =>
        throw new DngException(DngError.Unknown, message);

    [DoesNotReturn]
    public static void Memory(string? message = null) =>
        throw new DngException(DngError.Memory, message ?? "Out of memory");

    [DoesNotReturn]
    public static void Overflow(string what) =>
        throw new DngException(DngError.Overflow, $"Arithmetic overflow ({what})");

    [DoesNotReturn]
    public static void BadFormat(string? message = null) =>
        throw new DngException(DngError.BadFormat, message ?? "Bad file format");

    [DoesNotReturn]
    public static void UnsupportedDng(string? message = null) =>
        throw new DngException(DngError.UnsupportedDng, message ?? "Unsupported DNG version");

    [DoesNotReturn]
    public static void NotYetImplemented(string? message = null) =>
        throw new DngException(DngError.NotYetImplemented, message ?? "Not yet implemented");

    [DoesNotReturn]
    public static void MatrixMath(string? message = null) =>
        throw new DngException(DngError.MatrixMath, message ?? "Matrix math error");
}
