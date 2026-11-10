using FastEndpoints;
using FormUp.Application.Infrastructure.PlanChoice;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Architecture;

/// <summary>
/// Pins the one list of writes a coach may still make while a plan choice is required. Adding to it must be a
/// reviewed change to this file.
/// </summary>
[Collection(TestCollection.Name)]
public class PlanChoiceExemptionsArchitectureTests(FitnessApiFactory factory)
{
    private static readonly string[] ExpectedExemptions =
    [
        "ArchiveConversationEndpoint",
        "ChangePasswordEndpoint",
        "ChooseFreePlanEndpoint",
        "ConfirmAvatarEndpoint",
        "ConfirmProfessionalAvatarEndpoint",
        "DeleteAccountEndpoint",
        "DeleteAvatarEndpoint",
        "DeleteProfessionalAvatarEndpoint",
        "DisableCoachAccountEndpoint",
        "GenerateAvatarUploadUrlEndpoint",
        "GenerateChatImageUploadUrlEndpoint",
        "GenerateProfessionalAvatarUploadUrlEndpoint",
        "MarkAllReadEndpoint",
        "MarkConversationReadEndpoint",
        "MarkReadEndpoint",
        "RemoveCoachRoleEndpoint",
        "SendMessageEndpoint",
        "UnarchiveConversationEndpoint",
        "UpdateNotificationPreferencesEndpoint",
        "UpdateProfileEndpoint",
        "UpdateTimeZoneEndpoint",
    ];

    [Fact]
    public void ExemptionList_IsTheReviewedSet()
    {
        PlanChoiceExemptions.Endpoints.Select(type => type.Name).Order(StringComparer.Ordinal)
            .Should().Equal(ExpectedExemptions);
    }

    [Fact]
    public void EveryExemptType_IsARegisteredWriteEndpoint()
    {
        var registered = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.Metadata.GetMetadata<EndpointDefinition>()?.EndpointType)
            .Where(type => type is not null)
            .ToHashSet();

        PlanChoiceExemptions.Endpoints.Where(type => !registered.Contains(type))
            .Should().BeEmpty("a stale entry would exempt nothing and hide a renamed endpoint");
    }

    [Fact]
    public void StartingAConversation_AndEveryKeepClientsWrite_AreNotExempt()
    {
        PlanChoiceExemptions.Endpoints.Select(type => type.Name).Should()
            .NotContain(["StartConversationEndpoint", "SetKeepClientsEndpoint", "BroadcastMessageEndpoint"]);
    }
}
