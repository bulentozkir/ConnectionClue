using ConnectionClue.Analysis;

namespace ConnectionClue.Analysis.Tests;

public sealed class BufferbloatTests
{
    [Theory]
    [InlineData(5, BufferbloatGrade.A)]
    [InlineData(5.01, BufferbloatGrade.B)]
    [InlineData(30, BufferbloatGrade.B)]
    [InlineData(30.01, BufferbloatGrade.C)]
    [InlineData(60, BufferbloatGrade.C)]
    [InlineData(60.01, BufferbloatGrade.D)]
    [InlineData(100, BufferbloatGrade.D)]
    [InlineData(100.01, BufferbloatGrade.E)]
    [InlineData(200, BufferbloatGrade.E)]
    [InlineData(200.01, BufferbloatGrade.F)]
    public void Uses_excess_loaded_latency(double increase, BufferbloatGrade expected) =>
        Assert.Equal(expected, BufferbloatEvaluator.Evaluate(20, 20 + increase, null)!.Grade);

    [Fact]
    public void Worst_measured_direction_is_graded_and_improvement_is_zero_increase()
    {
        Assert.Equal(BufferbloatGrade.C, BufferbloatEvaluator.Evaluate(20, 22, 70)!.Grade);
        var improved = BufferbloatEvaluator.Evaluate(100, 40, 60)!;
        Assert.Equal(0, improved.IncreaseMs);
        Assert.Equal(BufferbloatGrade.A, improved.Grade);
    }

    [Theory]
    [InlineData(null, 20.0, 30.0)]
    [InlineData(-1.0, 20.0, 30.0)]
    [InlineData(20.0, null, null)]
    [InlineData(20.0, -1.0, -1.0)]
    public void Missing_or_invalid_measurements_are_not_graded(double? idle, double? down, double? up) =>
        Assert.Null(BufferbloatEvaluator.Evaluate(idle, down, up));
}
