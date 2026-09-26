using ConnectionClue.Presentation.ViewModels;

namespace ConnectionClue.Presentation.Tests;

/// <summary>Chart scale: the top covers the value and each of its 4 grid steps is a round whole number.</summary>
public class ChartScaleTests
{
    [Theory]
    [InlineData(10, 12)]
    [InlineData(115, 120)]
    [InlineData(230, 240)]
    [InlineData(250, 320)]
    [InlineData(520, 600)]
    [InlineData(1150, 1200)]
    public void Top_is_nice(double value, double top) => Assert.Equal(top, LaneViewModel.NiceCeiling(value));

    [Fact]
    public void Every_step_is_whole_and_headroom_is_bounded()
    {
        for (int i = 100; i <= 50_000; i += 7)
        {
            double v = i / 10.0, top = LaneViewModel.NiceCeiling(v), step = top / 4;
            Assert.True(top >= v - 1e-9 && top <= v * 1.5 + 1e-9, $"{v} → {top}");
            Assert.Equal(Math.Round(step), step);
        }
    }
}
