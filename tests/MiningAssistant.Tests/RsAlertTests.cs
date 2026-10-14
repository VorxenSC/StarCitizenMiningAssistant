using MiningAssistant.Core.Matching;

namespace MiningAssistant.Tests;

public class RsAlertTests
{
    private static readonly int[] Gold = { 3585 };

    [Theory]
    [InlineData(3585, true)]
    [InlineData(3584, true)]   // within OCR tolerance
    [InlineData(3570, false)]  // Borase, 15 away: not the same signature
    [InlineData(7170, false)]  // a multiple, but multiples are off
    public void Exact_number(int rs, bool beeps) =>
        Assert.Equal(beeps, RsAlert.Match(rs, Gold, includeMultiples: false, tolerance: 6) is not null);

    [Theory]
    [InlineData(7170, true)]
    [InlineData(10755, true)]
    [InlineData(14340, true)]
    [InlineData(7200, false)]
    public void Multiples_when_enabled(int rs, bool beeps) =>
        Assert.Equal(beeps, RsAlert.Match(rs, Gold, includeMultiples: true, tolerance: 6) is not null);

    [Fact]
    public void Several_numbers_report_which_one_matched()
    {
        Assert.Equal(3870, RsAlert.Match(11610, new[] { 3585, 3870 }, true, 6));
        Assert.Null(RsAlert.Match(4300, new[] { 3585, 3870 }, true, 6));
    }

    [Theory]
    [InlineData("3585", new[] { 3585 })]
    [InlineData("3585, 7170", new[] { 3585, 7170 })]
    [InlineData("3585,7170;3870", new[] { 3585, 7170, 3870 })]
    [InlineData("3,585  14,340", new[] { 3585, 14340 })]
    [InlineData("gold 3585 3585 12", new[] { 3585 })]
    [InlineData("", new int[0])]
    public void Parses_what_users_type(string text, int[] expected) =>
        Assert.Equal(expected, RsAlert.Parse(text));
}
