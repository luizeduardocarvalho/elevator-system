namespace Elevator.Metrics.Tests;

public class StatsTests
{
    [Theory]
    [InlineData(1, 12.706)]
    [InlineData(4, 2.776)]
    [InlineData(9, 2.262)]
    [InlineData(19, 2.093)]
    [InlineData(29, 2.045)]
    [InlineData(45, 2.014)]
    [InlineData(1000, 1.962)]
    public void Student_t_two_sided_95_matches_tables(int df, double expected) =>
        Assert.Equal(expected, Stats.StudentT975(df), 2);

    [Fact]
    public void Mean_ci_of_one_to_five()
    {
        var e = Stats.MeanCi([1, 2, 3, 4, 5]);
        Assert.Equal((3.0, 5), (e.Mean, e.N));
        Assert.Equal(1.963, e.HalfWidth, 3);
    }

    [Fact]
    public void Single_sample_has_no_interval() =>
        Assert.True(double.IsNaN(Stats.MeanCi([4]).HalfWidth));

    [Fact]
    public void Paired_difference_uses_matching_seeds_only()
    {
        var baseline = new Dictionary<int, double> { [1] = 10, [2] = 50, [3] = 90 };
        var strategy = new Dictionary<int, double> { [1] = 8, [2] = 48, [3] = 88, [4] = 1 };
        var d = Stats.PairedDifference(strategy, baseline);
        Assert.Equal((-2.0, 0.0, 3), (d.Mean, d.HalfWidth, d.N));
    }
}
