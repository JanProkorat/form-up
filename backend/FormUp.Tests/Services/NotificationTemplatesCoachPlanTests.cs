using FluentAssertions;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Services;

namespace FormUp.Tests.Services;

/// <summary>
/// The coach plan notification types have their own wording in every locale, with a variant for a missing date.
/// </summary>
public class NotificationTemplatesCoachPlanTests
{
    private static readonly Dictionary<string, string> Parameters = new() { ["planName"] = "Studio", ["date"] = "27 Jan 2027" };

    [Theory]
    [InlineData(NotificationType.CoachPlanChanged, "cs")]
    [InlineData(NotificationType.CoachPlanChanged, "en")]
    [InlineData(NotificationType.CoachPlanChanged, "de")]
    [InlineData(NotificationType.CoachPlanEnded, "cs")]
    [InlineData(NotificationType.CoachPlanEnded, "en")]
    [InlineData(NotificationType.CoachPlanEnded, "de")]
    [InlineData(NotificationType.CoachPlanRenewalCancelled, "cs")]
    [InlineData(NotificationType.CoachPlanRenewalCancelled, "en")]
    [InlineData(NotificationType.CoachPlanRenewalCancelled, "de")]
    public void Resolve_CoachPlanType_HasLocalizedFilledTitleAndBody(NotificationType type, string language)
    {
        var (title, body) = NotificationTemplates.Resolve(type, language, Parameters);
        var (englishTitle, englishBody) = NotificationTemplates.Resolve(type, "en", Parameters);

        title.Should().Contain("Studio").And.NotBe("Notification").And.NotContain("{");
        body.Should().NotBeNullOrWhiteSpace().And.NotBe("You have a new notification.").And.NotContain("{");

        if (type != NotificationType.CoachPlanEnded)
        {
            body.Should().Contain("27 Jan 2027");
        }

        if (language != "en")
        {
            title.Should().NotBe(englishTitle);
            body.Should().NotBe(englishBody);
        }
    }

    [Theory]
    [InlineData(NotificationType.CoachPlanChanged, "cs")]
    [InlineData(NotificationType.CoachPlanChanged, "en")]
    [InlineData(NotificationType.CoachPlanChanged, "de")]
    [InlineData(NotificationType.CoachPlanRenewalCancelled, "cs")]
    [InlineData(NotificationType.CoachPlanRenewalCancelled, "en")]
    [InlineData(NotificationType.CoachPlanRenewalCancelled, "de")]
    public void Resolve_NoDateVariant_KeepsTheTitleAndHasAnEmptyBody(NotificationType type, string language)
    {
        var parameters = new Dictionary<string, string> { ["planName"] = "Studio" };

        var (title, body) = NotificationTemplates.Resolve(type, language, parameters, NotificationTemplates.CoachPlanNoDate);

        title.Should().Contain("Studio").And.NotBe("Notification");
        body.Should().BeEmpty();
    }

    [Fact]
    public void Resolve_EndedPlan_NamesThePlanInTheTitleOnly()
    {
        var (title, body) = NotificationTemplates.Resolve(NotificationType.CoachPlanEnded, "en", Parameters);

        title.Should().Be("Form Up ended your Studio plan");
        body.Should().Be("You're on Free now.");
    }
}
