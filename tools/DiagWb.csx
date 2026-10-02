using DngSharp.Dng.Sdk.Imaging.Profile;
using DngSharp.Dng.Sdk.IO;
using DngSharp.Dng.Sdk.Container;
using DngSharp.Dng.Sdk.Render;

var dng = @"D:\git\dng\images\IMG_3353-HDR.dng";
using var stream = DngFileStream.OpenRead(dng);
var container = DngContainer.Parse(stream);

var sharedIfd = container.TopLevelIfds[0];
bool be = container.Header.BigEndian;
var shared = new DngSharp.Dng.Sdk.Metadata.DngShared();
var profile = CameraProfileReader.Read(stream, sharedIfd, be, shared);

Console.WriteLine($"AsShotNeutral: {shared.AsShotNeutral?[0]:F4} {shared.AsShotNeutral?[1]:F4} {shared.AsShotNeutral?[2]:F4}");
Console.WriteLine($"BaselineExposure: {shared.BaselineExposure:F3}");
Console.WriteLine($"Profile null? {profile is null}");
if (profile?.Illuminants.Count > 0) {
    var i0 = profile.Illuminants[0];
    Console.WriteLine($"  Illum[0] Kelvin={i0.Kelvin:F0} HasForward={i0.ForwardMatrix!=null} HasCC={i0.CameraCalibration!=null}");
    Console.WriteLine($"  CC[0,0]={i0.CameraCalibration?[0,0]:F4}");
}

var m = Stage3Renderer.ResolveCameraToXyzD50(profile, shared);
Console.WriteLine($"\ncameraToXyzD50:");
Console.WriteLine($"  {m[0,0]:F4} {m[0,1]:F4} {m[0,2]:F4}");
Console.WriteLine($"  {m[1,0]:F4} {m[1,1]:F4} {m[1,2]:F4}");
Console.WriteLine($"  {m[2,0]:F4} {m[2,1]:F4} {m[2,2]:F4}");

var n = shared.AsShotNeutral!;
double x = m[0,0]*n[0] + m[0,1]*n[1] + m[0,2]*n[2];
double y = m[1,0]*n[0] + m[1,1]*n[1] + m[1,2]*n[2];
double z = m[2,0]*n[0] + m[2,1]*n[1] + m[2,2]*n[2];
Console.WriteLine($"\nNeutral→XYZ_D50: X={x:F4} Y={y:F4} Z={z:F4}");
Console.WriteLine($"(D50 white should be ≈ X=0.964 Y=1.000 Z=0.825)");
