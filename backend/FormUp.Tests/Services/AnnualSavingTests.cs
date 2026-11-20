using FluentAssertions;
using FormUp.Application.Domain.Services;

namespace FormUp.Tests.Services;

/// <summary>
/// Unit tests for <see cref="AnnualSaving"/>: the display-only yearly saving percent of the plan line.
/// </summary>
public class AnnualSavingTests
{
    [Theory]
    [InlineData(29900, 299000, 17)]
    [InlineData(200, 2376, 1)]
    [InlineData(200, 2388, 1)]
    [InlineData(200, 1200, 50)]
    public void Percent_AnnualCheaperThanTwelveMonths_ReturnsTheWholePercent_RoundingHalfAwayFromZero(
        long monthly, long annual, int expected)
    {
        AnnualSaving.Percent(monthly, annual).Should().Be(expected);
    }

    [Theory]
    [InlineData(200, 2400)]
    [InlineData(200, 2500)]
    [InlineData(0, 1000)]
    public void Percent_NoSaving_ReturnsNull(long monthly, long annual)
    {
        AnnualSaving.Percent(monthly, annual).Should().BeNull();
    }

    [Fact]
    public void Percent_NoAnnualPrice_ReturnsNull()
    {
        AnnualSaving.Percent(29900, null).Should().BeNull();
    }
}
