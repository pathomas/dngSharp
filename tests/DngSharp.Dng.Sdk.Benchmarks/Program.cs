using BenchmarkDotNet.Running;

namespace DngSharp.Dng.Sdk.Benchmarks;

/// <summary>
/// Phase 10 microbenchmark entry point.
///
/// Run all benchmarks:
///   dotnet run -c Release --project tests/DngSharp.Dng.Sdk.Benchmarks -- --filter *
///
/// Run one class:
///   dotnet run -c Release --project tests/DngSharp.Dng.Sdk.Benchmarks -- --filter *ContainerParseBenchmarks*
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
        return 0;
    }
}
