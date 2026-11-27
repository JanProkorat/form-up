using FormUp.Application.Features.Billing.CreateCheckoutSession;
using FormUp.Application.Features.Client.Notifications.MarkAllRead;
using FormUp.Application.Features.Client.Notifications.MarkRead;
using FormUp.Application.Features.CoachSubscriptions.ChooseFreePlan;
using FormUp.Application.Features.Messaging.ArchiveConversation;
using FormUp.Application.Features.Messaging.GenerateChatImageUploadUrl;
using FormUp.Application.Features.Messaging.MarkRead;
using FormUp.Application.Features.Messaging.SendMessage;
using FormUp.Application.Features.Messaging.UnarchiveConversation;
using FormUp.Application.Features.Professionals.Avatar;
using FormUp.Application.Features.Users.Avatar;
using FormUp.Application.Features.Users.ChangePassword;
using FormUp.Application.Features.Users.DeleteAccount;
using FormUp.Application.Features.Users.DisableCoachAccount;
using FormUp.Application.Features.Users.RemoveCoachRole;
using FormUp.Application.Features.Users.UpdateNotificationPreferences;
using FormUp.Application.Features.Users.UpdateProfile;
using FormUp.Application.Features.Users.UpdateTimeZone;

namespace FormUp.Application.Infrastructure.PlanChoice;

/// <summary>
/// The one list of writes a coach may still make while a plan choice is required: choosing a plan or buying one, replying in
/// existing chats, marking notifications read, and account housekeeping. Every other write is refused by <see cref="PlanChoiceWriteGateMiddleware"/>.
/// Starting a new conversation, keep-clients and every coach feature stay refused. The list is pinned by an
/// architecture test; add to it only on purpose.
/// </summary>
public static class PlanChoiceExemptions
{
    /// <summary>Endpoint types that pass the write gate.</summary>
    public static IReadOnlySet<Type> Endpoints { get; } = new HashSet<Type>
    {
        typeof(ChooseFreePlanEndpoint),
        typeof(MarkAllReadEndpoint),
        typeof(MarkReadEndpoint),
        typeof(SendMessageEndpoint),
        typeof(MarkConversationReadEndpoint),
        typeof(GenerateChatImageUploadUrlEndpoint),
        typeof(ArchiveConversationEndpoint),
        typeof(UnarchiveConversationEndpoint),
        typeof(ChangePasswordEndpoint),
        typeof(DeleteAccountEndpoint),
        typeof(UpdateNotificationPreferencesEndpoint),
        typeof(UpdateTimeZoneEndpoint),
        typeof(UpdateProfileEndpoint),
        typeof(GenerateAvatarUploadUrlEndpoint),
        typeof(ConfirmAvatarEndpoint),
        typeof(DeleteAvatarEndpoint),
        typeof(GenerateProfessionalAvatarUploadUrlEndpoint),
        typeof(ConfirmProfessionalAvatarEndpoint),
        typeof(DeleteProfessionalAvatarEndpoint),
        typeof(DisableCoachAccountEndpoint),
        typeof(RemoveCoachRoleEndpoint),
        typeof(CreateCheckoutSessionEndpoint),
    };
}
