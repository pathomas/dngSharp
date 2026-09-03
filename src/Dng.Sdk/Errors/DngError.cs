namespace Dng.Sdk.Errors;

/// <summary>
/// Error codes used throughout the DNG SDK. Mirrors <c>dng_error_code</c>
/// values in <c>dng_errors.h</c>.
/// </summary>
public enum DngError
{
    None = 0,

    // Adobe's enum starts SDK errors at 100000 so they don't collide with
    // host-defined error spaces. We preserve those numeric values to make
    // diagnostics from native dng_validate runs easy to compare.
    SdkFirst = 100_000,

    Unknown = SdkFirst,
    NotYetImplemented,
    Silent,
    UserCanceled,
    HostInsufficient,
    Memory,
    BadFormat,
    MatrixMath,
    OpenFile,
    ReadFile,
    WriteFile,
    EndOfFile,
    FileIsDamaged,
    ImageTooBigDng,
    ImageTooBigTiff,
    UnsupportedDng,
    Overflow,
    JxlEncoder,
    JxlDecoder,
}
