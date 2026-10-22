namespace Elevator.Lab.Tests;

/// <summary>
/// A wall-clock measurement, not a logic test: it only means something on a quiet machine. Plain <c>dotnet test</c>
/// runs the test projects in parallel processes, which starves these tests' threads, so they report as skipped unless
/// <c>ELEVATOR_PERF=1</c>. <c>scripts/perf-tests.sh</c> sets it and runs them alone.
/// </summary>
public sealed class PerfFactAttribute : FactAttribute
{
    public PerfFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("ELEVATOR_PERF") != "1")
            Skip = "wall-clock perf test: run scripts/perf-tests.sh on a quiet machine (ELEVATOR_PERF=1)";
    }
}
