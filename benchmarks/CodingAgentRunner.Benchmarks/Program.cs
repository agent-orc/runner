using System.Reflection;
using BenchmarkDotNet.Running;

// Run with:  dotnet run -c Release --project benchmarks/CodingAgentRunner.Benchmarks
// then pick a benchmark, or pass --filter '*' to run them all. These are
// micro-benchmarks of the library's own parsing / rendering overhead — the cost
// the host pays per line of agent output — not end-to-end model benchmarks
// (those would mean actually spawning a CLI, which belongs in a separate harness).
BenchmarkSwitcher.FromAssembly(Assembly.GetExecutingAssembly()).Run(args);
