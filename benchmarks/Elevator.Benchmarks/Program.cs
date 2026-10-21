using BenchmarkDotNet.Running;
using Elevator.Benchmarks;

if (args is ["anatomy"])
{
    ProjectionAnatomy.Run();
    return;
}
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
