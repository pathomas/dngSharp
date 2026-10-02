using System.Diagnostics;
using System.Globalization;
using System.Text;
using DngSharp.Dng.Sdk.Codecs;
using DngSharp.Dng.Sdk.Codecs.LosslessJpeg;
using DngSharp.Dng.Sdk.Container;
using DngSharp.Dng.Sdk.Gpu;
using DngSharp.Dng.Sdk.Imaging;
using DngSharp.Dng.Sdk.IO;
using DngSharp.Dng.Sdk.Jxl;
using DngSharp.Dng.Sdk.Pipeline;
using DngSharp.Dng.Sdk.Preview;
using DngSharp.Dng.Sdk.Render;

namespace DngSharp.Dng.Sdk.Benchmarks;

/// <summary>
/// Whole-corpus conversion benchmark: every <c>images/*.dng</c> converted to
/// JPEG once on the CPU path (what <c>Validate -jpeg</c> runs) and once on the
/// GPU path (CPU Stage 1 decode → <see cref="GpuRenderPipeline.RenderToRgb8"/>
/// → JPEG). Not a BenchmarkDotNet class: one pass over 50+ files is the
/// measurement, and per-parameter BDN setup would take hours.
///
/// <para>Reports per file: wall time split into decode / render / encode,
/// managed bytes allocated, peak process working set observed during the
/// conversion, and (GPU) peak device bytes. Writes a markdown report.</para>
///
/// <para>Run: <c>dotnet run -c Release --project tests/DngSharp.Dng.Sdk.Benchmarks -- corpus [--backend cuda|opencl|cpu|auto] [--out path.md] [--cpu-only] [--gpu-only]</c></para>
/// </summary>
public static class CorpusBenchmark
{
    private sealed record PathResult(
        double DecodeMs, double RenderMs, double EncodeMs,
        long AllocatedBytes, long PeakWorkingSetBytes, long DeviceBytes,
        int Width, int Height, string? Error)
    {
        public double TotalMs => DecodeMs + RenderMs + EncodeMs;
    }

    private sealed record FileResult(string Name, long FileBytes, PathResult? Cpu, PathResult? Gpu);

    public static int Run(string[] args)
    {
        var backend = GpuBackend.Auto;
        string? outPath = null;
        bool runCpu = true, runGpu = true;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--backend": backend = ParseBackend(args[++i]); break;
                case "--out": outPath = args[++i]; break;
                case "--cpu-only": runGpu = false; break;
                case "--gpu-only": runCpu = false; break;
            }
        }

        var imagesDir = FindImagesDir();
        var files = Directory.EnumerateFiles(imagesDir, "*.dng").Order(StringComparer.Ordinal).ToList();

        var registry = new CodecRegistry();
        registry.Register(new UncompressedDecoder());
        registry.Register(new DeflateDecoder());
        registry.Register(new LosslessJpegDecoder());
        if (JxlDecoder.IsAvailable) registry.Register(new JxlDecoder());
        var host = new DngHost { MaxThreads = Environment.ProcessorCount };

        GpuDevice? device = null;
        GpuRenderPipeline? gpu = null;
        if (runGpu)
        {
            device = GpuDevice.Create(backend);
            gpu = new GpuRenderPipeline(device);
        }

        Console.WriteLine($"// corpus: {files.Count} files in {imagesDir}");
        Console.WriteLine($"// cpu: {Environment.ProcessorCount} threads; gpu: {(device is null ? "off" : $"{device.Name} ({device.MemorySize >> 20} MiB)")}");

        // Warm-up: JIT (CLR and, for the GPU, PTX) on the smallest file, untimed.
        var warm = files.OrderBy(f => new FileInfo(f).Length).First();
        Console.WriteLine($"// warm-up on {Path.GetFileName(warm)}");
        var warmBytes = File.ReadAllBytes(warm);
        if (runCpu) ConvertCpu(warmBytes, registry, host);
        if (gpu is not null) ConvertGpu(warmBytes, registry, host, gpu);

        var results = new List<FileResult>(files.Count);
        var sw = Stopwatch.StartNew();
        foreach (var path in files)
        {
            var name = Path.GetFileName(path);
            var bytes = File.ReadAllBytes(path);
            PathResult? cpu = null, gpuRes = null;

            if (runCpu)
            {
                cpu = Measure(() => ConvertCpu(bytes, registry, host));
                Console.WriteLine($"{name,-32} CPU {Fmt(cpu)}");
            }
            if (gpu is not null)
            {
                gpuRes = Measure(() => ConvertGpu(bytes, registry, host, gpu), gpu);
                Console.WriteLine($"{name,-32} GPU {Fmt(gpuRes)}");
            }
            results.Add(new FileResult(name, bytes.LongLength, cpu, gpuRes));
        }
        sw.Stop();

        var report = BuildReport(results, device, sw.Elapsed);
        Console.WriteLine();
        Console.WriteLine(report);
        if (outPath is not null)
        {
            File.WriteAllText(outPath, report);
            Console.WriteLine($"// wrote {outPath}");
        }

        gpu?.Dispose();
        device?.Dispose();
        return 0;
    }

    // ── Conversion paths ──────────────────────────────────────────────────────

    private readonly record struct Phases(double DecodeMs, double RenderMs, double EncodeMs, int W, int H, long DeviceBytes);

    private static Phases ConvertCpu(byte[] file, CodecRegistry registry, DngHost host)
    {
        var sw = Stopwatch.StartNew();
        using var stream = DngMemoryStream.WrapNoCopy(file);
        var container = DngContainer.Parse(stream);
        var r = StripReader.ReadStage1(stream, container, registry, host);
        double decode = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        var stage1 = OpcodeList1Applier.Apply(r.Stage1, r.OpcodeList1, host);
        var stage2 = Stage2Builder.Build(stage1, r.Linearization, host);
        stage2 = OpcodeList2Applier.Apply(stage2, r.OpcodeList2, host);
        var stage3 = (SimpleImage)Stage3Builder.Build(stage2, r.Photometric, r.Mosaic, host);
        stage3 = OpcodeList3Applier.Apply(stage3, r.OpcodeList3, host);
        if ((r.DefaultCropArea ?? r.ActiveArea) is { } crop)
            stage3 = ImageCrop.Crop(stage3, crop);

        var camToXyz = Stage3Renderer.ResolveCameraToXyzD50(r.CameraProfile, r.Shared);
        Func<double, double>? toneCurve = r.CameraProfile?.ToneCurve is { } tc
            ? x => HdrToneMapper.EvaluateCurve(x, tc) : null;
        HueSatMap? hueSat = null;
        if (r.CameraProfile is { Illuminants.Count: > 0 } profile)
            hueSat = CameraColorMatrix.ResolveHueSatMap(profile, Stage3Renderer.EstimateAsShotKelvin(r.Shared));

        var rgb = Stage3Renderer.Render(stage3, camToXyz, r.Shared.BaselineExposure,
            toneCurve: toneCurve, host: host, hueSatMap: hueSat);
        HdrToneMapper.Apply(rgb, r.CameraProfile?.ToneCurve, host);
        int w = (int)rgb.Bounds.W, h = (int)rgb.Bounds.H;
        var rgb8 = new byte[w * h * 3];
        Stage3Renderer.GammaAndQuantize(rgb, rgb8, host);
        double render = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        _ = JpegEncoder.Encode(rgb8, w, h);
        double encode = sw.Elapsed.TotalMilliseconds;

        return new Phases(decode, render, encode, w, h, 0);
    }

    private static Phases ConvertGpu(byte[] file, CodecRegistry registry, DngHost host, GpuRenderPipeline gpu)
    {
        var sw = Stopwatch.StartNew();
        using var stream = DngMemoryStream.WrapNoCopy(file);
        var container = DngContainer.Parse(stream);
        var r = StripReader.ReadStage1(stream, container, registry, host);
        double decode = sw.Elapsed.TotalMilliseconds;

        if ((r.OpcodeList1?.Count ?? 0) + (r.OpcodeList2?.Count ?? 0) + (r.OpcodeList3?.Count ?? 0) > 0)
            throw new NotSupportedException("opcodes present; GPU path does not apply them");

        sw.Restart();
        var crop = r.DefaultCropArea ?? r.ActiveArea;
        var camToXyz = Stage3Renderer.ResolveCameraToXyzD50(r.CameraProfile, r.Shared);
        HueSatMap? hueSat = null;
        if (r.CameraProfile is { Illuminants.Count: > 0 } profile)
            hueSat = CameraColorMatrix.ResolveHueSatMap(profile, Stage3Renderer.EstimateAsShotKelvin(r.Shared));
        var p = new GpuRenderParams(camToXyz, r.Shared.BaselineExposure, HueSatMap: hueSat, ToneCurve: r.CameraProfile?.ToneCurve);

        var rgb8 = gpu.RenderToRgb8((SimpleImage)r.Stage1, r.Linearization, r.Photometric, r.Mosaic, p, crop);
        var bounds = crop ?? r.Stage1.Bounds;
        int w = (int)bounds.W, h = (int)bounds.H;
        double render = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        _ = JpegEncoder.Encode(rgb8, w, h);
        double encode = sw.Elapsed.TotalMilliseconds;

        return new Phases(decode, render, encode, w, h, gpu.LastPeakDeviceBytes);
    }

    // ── Measurement ───────────────────────────────────────────────────────────

    private static PathResult Measure(Func<Phases> convert, GpuRenderPipeline? gpu = null)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var proc = Process.GetCurrentProcess();
        long peakWs = proc.WorkingSet64;
        using var stop = new CancellationTokenSource();
        var sampler = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                proc.Refresh();
                peakWs = System.Math.Max(peakWs, proc.WorkingSet64);
                Thread.Sleep(10);
            }
        });

        long alloc0 = GC.GetTotalAllocatedBytes(precise: true);
        try
        {
            var ph = convert();
            long alloc = GC.GetTotalAllocatedBytes(precise: true) - alloc0;
            stop.Cancel(); sampler.Wait();
            proc.Refresh();
            peakWs = System.Math.Max(peakWs, proc.WorkingSet64);
            return new PathResult(ph.DecodeMs, ph.RenderMs, ph.EncodeMs, alloc, peakWs, ph.DeviceBytes, ph.W, ph.H, null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            stop.Cancel(); sampler.Wait();
            return new PathResult(0, 0, 0, 0, peakWs, 0, 0, 0, ex.GetType().Name + ": " + ex.Message);
        }
    }

    // ── Reporting ─────────────────────────────────────────────────────────────

    private static string Fmt(PathResult r) => r.Error is not null
        ? $"ERROR {r.Error}"
        : $"{r.TotalMs / 1000,6:F2}s (dec {r.DecodeMs / 1000:F2} ren {r.RenderMs / 1000:F2} enc {r.EncodeMs / 1000:F2})  alloc {Mb(r.AllocatedBytes),7} MB  peakWS {Mb(r.PeakWorkingSetBytes),6} MB" +
          (r.DeviceBytes > 0 ? $"  dev {Mb(r.DeviceBytes)} MB" : "");

    private static string Mb(long b) => (b / (1024.0 * 1024.0)).ToString("F0", CultureInfo.InvariantCulture);

    private static string BuildReport(List<FileResult> results, GpuDevice? device, TimeSpan wall)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"# Corpus conversion benchmark — {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Files: {results.Count} DNG → JPEG (q90), {results.Sum(r => r.FileBytes) / (1024.0 * 1024 * 1024):F2} GiB input");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- CPU: {Environment.ProcessorCount} logical threads, {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- GPU: {(device is null ? "not run" : $"{device.Name}, {device.MemorySize >> 20} MiB")}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Total wall time for the run: {wall.TotalSeconds:F0} s");
        sb.AppendLine();
        sb.AppendLine("Time = file already in memory; decode = parse + Stage 1 (CPU in both paths); render = Stage 2 → RGB8; encode = JPEG (CPU, serial). Alloc = managed bytes allocated; peak WS = max process working set sampled at 10 ms during the conversion; dev = peak GPU buffer bytes resident at once.");
        sb.AppendLine();

        var ok = results.Where(r => r.Cpu?.Error is null && r.Gpu?.Error is null && r.Cpu is not null && r.Gpu is not null).ToList();
        if (ok.Count > 0)
        {
            double cpuTot = ok.Sum(r => r.Cpu!.TotalMs), gpuTot = ok.Sum(r => r.Gpu!.TotalMs);
            double cpuRen = ok.Sum(r => r.Cpu!.RenderMs), gpuRen = ok.Sum(r => r.Gpu!.RenderMs);
            sb.AppendLine("## Summary (files that ran on both paths)");
            sb.AppendLine();
            sb.AppendLine("| | CPU | GPU | Speed-up |");
            sb.AppendLine("|---|---|---|---|");
            sb.AppendLine(CultureInfo.InvariantCulture, $"| Files | {ok.Count} | {ok.Count} | |");
            sb.AppendLine(CultureInfo.InvariantCulture, $"| Total conversion time | {cpuTot / 1000:F1} s | {gpuTot / 1000:F1} s | **{cpuTot / gpuTot:F2}×** |");
            sb.AppendLine(CultureInfo.InvariantCulture, $"| Mean per file | {cpuTot / ok.Count / 1000:F2} s | {gpuTot / ok.Count / 1000:F2} s | {cpuTot / gpuTot:F2}× |");
            sb.AppendLine(CultureInfo.InvariantCulture, $"| Render stage only (Stage 2 → RGB8) | {cpuRen / 1000:F1} s | {gpuRen / 1000:F1} s | **{cpuRen / gpuRen:F1}×** |");
            sb.AppendLine(CultureInfo.InvariantCulture, $"| Decode (shared, CPU) | {ok.Sum(r => r.Cpu!.DecodeMs) / 1000:F1} s | {ok.Sum(r => r.Gpu!.DecodeMs) / 1000:F1} s | |");
            sb.AppendLine(CultureInfo.InvariantCulture, $"| JPEG encode (shared, CPU) | {ok.Sum(r => r.Cpu!.EncodeMs) / 1000:F1} s | {ok.Sum(r => r.Gpu!.EncodeMs) / 1000:F1} s | |");
            sb.AppendLine(CultureInfo.InvariantCulture, $"| Mean managed alloc per file | {Mb((long)ok.Average(r => r.Cpu!.AllocatedBytes))} MB | {Mb((long)ok.Average(r => r.Gpu!.AllocatedBytes))} MB | |");
            sb.AppendLine(CultureInfo.InvariantCulture, $"| Max peak working set | {Mb(ok.Max(r => r.Cpu!.PeakWorkingSetBytes))} MB | {Mb(ok.Max(r => r.Gpu!.PeakWorkingSetBytes))} MB | |");
            sb.AppendLine(CultureInfo.InvariantCulture, $"| Max GPU device memory | — | {Mb(ok.Max(r => r.Gpu!.DeviceBytes))} MB | |");
            sb.AppendLine();
        }

        var failed = results.Where(r => r.Cpu?.Error is not null || r.Gpu?.Error is not null).ToList();
        if (failed.Count > 0)
        {
            sb.AppendLine("## Files with errors");
            sb.AppendLine();
            foreach (var f in failed)
            {
                if (f.Cpu?.Error is { } ce) sb.AppendLine(CultureInfo.InvariantCulture, $"- `{f.Name}` CPU: {ce}");
                if (f.Gpu?.Error is { } ge) sb.AppendLine(CultureInfo.InvariantCulture, $"- `{f.Name}` GPU: {ge}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("## Per file");
        sb.AppendLine();
        sb.AppendLine("| File | Size | Pixels | CPU total | CPU dec/ren/enc | CPU alloc | CPU peak WS | GPU total | GPU dec/ren/enc | GPU alloc | GPU peak WS | GPU dev | Speed-up |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var r in results)
        {
            var c = r.Cpu; var g = r.Gpu;
            var px = (c?.Error is null && c is not null) ? $"{c.Width}×{c.Height}" : (g?.Error is null && g is not null ? $"{g.Width}×{g.Height}" : "");
            string cell(PathResult? p, Func<PathResult, string> f) => p is null ? "—" : p.Error is not null ? "ERR" : f(p);
            var speed = c is { Error: null } && g is { Error: null } ? $"{c.TotalMs / g.TotalMs:F2}×" : "";
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"| {r.Name} | {r.FileBytes / (1024.0 * 1024):F0} MB | {px} " +
                $"| {cell(c, p => $"{p.TotalMs / 1000:F2} s")} | {cell(c, p => $"{p.DecodeMs / 1000:F2} / {p.RenderMs / 1000:F2} / {p.EncodeMs / 1000:F2}")} | {cell(c, p => Mb(p.AllocatedBytes) + " MB")} | {cell(c, p => Mb(p.PeakWorkingSetBytes) + " MB")} " +
                $"| {cell(g, p => $"{p.TotalMs / 1000:F2} s")} | {cell(g, p => $"{p.DecodeMs / 1000:F2} / {p.RenderMs / 1000:F2} / {p.EncodeMs / 1000:F2}")} | {cell(g, p => Mb(p.AllocatedBytes) + " MB")} | {cell(g, p => Mb(p.PeakWorkingSetBytes) + " MB")} | {cell(g, p => Mb(p.DeviceBytes) + " MB")} | {speed} |");
        }
        return sb.ToString();
    }

    private static GpuBackend ParseBackend(string s) => s.ToLowerInvariant() switch
    {
        "cuda" => GpuBackend.Cuda,
        "opencl" => GpuBackend.OpenCL,
        "cpu" => GpuBackend.Cpu,
        _ => GpuBackend.Auto,
    };

    private static string FindImagesDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "images");
            if (Directory.Exists(candidate) && Directory.EnumerateFiles(candidate, "*.dng").Any())
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("No images/*.dng found above " + AppContext.BaseDirectory);
    }
}
