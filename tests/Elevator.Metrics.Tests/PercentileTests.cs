namespace Elevator.Metrics.Tests;

public class PercentileTests
{
    [Fact]
    public void Empty_sample_has_no_percentile() => Assert.Null(Percentile.NearestRank([], 95));

    [Fact]
    public void Single_sample_is_every_percentile()
    {
        Assert.Equal(7, Percentile.NearestRank([7], 1));
        Assert.Equal(7, Percentile.NearestRank([7], 100));
    }

    [Theory]
    [InlineData(50, 5)]
    [InlineData(95, 10)]
    [InlineData(90, 9)]
    [InlineData(100, 10)]
    public void Nearest_rank_on_one_to_ten(double p, double expected) =>
        Assert.Equal(expected, Percentile.NearestRank([10, 3, 1, 2, 4, 5, 6, 7, 9, 8], p));

    [Fact]
    public void Ties_return_an_observed_value() =>
        Assert.Equal(4, Percentile.NearestRank([4, 4, 4, 1], 95));

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void Percentile_must_be_in_range(double p) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Percentile.NearestRank([1], p));
}
