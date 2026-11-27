using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Services;
using FluentAssertions;

namespace FormUp.Tests.Services;

/// <summary>
/// Tests the nutrition week unpublished notification templates.
/// </summary>
public class NotificationTemplatesPlanUnpublishedTests
{
    [Theory]
    [InlineData("en", "Week 3 of your nutrition plan is no longer published.")]
    [InlineData("cs", "Týden 3 vašeho jídelníčku už není zveřejněn.")]
    [InlineData("de", "Woche 3 Ihres Ernährungsplans ist nicht mehr veröffentlicht.")]
    public void Resolve_NutritionUnpublished_ReturnsLocalizedBody(string language, string expectedBody)
    {
        var (_, body) = NotificationTemplates.Resolve(
            NotificationType.PlanPublished,
            language,
            new Dictionary<string, string> { ["weekNumber"] = "3" },
            NotificationTemplates.PlanPublishedNutritionUnpublished);

        body.Should().Be(expectedBody);
    }
}
