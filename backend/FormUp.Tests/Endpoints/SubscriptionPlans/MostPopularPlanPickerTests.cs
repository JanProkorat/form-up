using FluentAssertions;
using FormUp.Application.Features.SubscriptionPlans.GetPublicSubscriptionPlans;

namespace FormUp.Tests.Endpoints.SubscriptionPlans;

/// <summary>
/// Unit tests for <see cref="MostPopularPlanPicker"/>.
/// </summary>
public class MostPopularPlanPickerTests
{
    [Fact]
    public void Pick_NoCoachOnAnyPaidPlan_ReturnsNull()
    {
        var result = MostPopularPlanPicker.Pick([("free", 0, 0), ("pro", 29900, 0), ("max", 59900, 0)]);

        result.Should().BeNull();
    }

    [Fact]
    public void Pick_TwoPaidPlansTieOnTopCount_ReturnsNull()
    {
        var result = MostPopularPlanPicker.Pick([("basic", 9900, 3), ("pro", 29900, 3), ("max", 59900, 1)]);

        result.Should().BeNull();
    }

    [Fact]
    public void Pick_FreePlanHasMostCoaches_IsNeverChosen()
    {
        var result = MostPopularPlanPicker.Pick([("free", 0, 50), ("pro", 29900, 2), ("max", 59900, 1)]);

        result.Should().Be("pro");
    }

    [Fact]
    public void Pick_SingleLeader_ReturnsItsCode()
    {
        var result = MostPopularPlanPicker.Pick([("basic", 9900, 1), ("pro", 29900, 4), ("max", 59900, 2)]);

        result.Should().Be("pro");
    }

    [Fact]
    public void Pick_NoPlans_ReturnsNull()
    {
        MostPopularPlanPicker.Pick([]).Should().BeNull();
    }
}
