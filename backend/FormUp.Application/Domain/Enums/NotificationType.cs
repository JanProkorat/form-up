namespace FormUp.Application.Domain.Enums;

/// <summary>
/// Type of notification sent to a user.
/// </summary>
public enum NotificationType
{
    /// <summary>Client achieved a personal record.</summary>
    PersonalRecord,

    /// <summary>A new training plan was published.</summary>
    PlanPublished,

    /// <summary>General system notification.</summary>
    General,

    /// <summary>A client submitted a questionnaire response.</summary>
    QuestionnaireSubmitted,

    /// <summary>A client request was received by a professional.</summary>
    ClientRequestReceived,

    /// <summary>A client request was accepted by a professional.</summary>
    ClientRequestAccepted,

    /// <summary>A client request was rejected by a professional.</summary>
    ClientRequestRejected,

    /// <summary>A questionnaire was assigned to a client.</summary>
    QuestionnaireAssigned,

    /// <summary>A professional sent an invitation to a client.</summary>
    InvitationReceived,

    /// <summary>A client's invite was accepted by a professional.</summary>
    InvitationAccepted,

    /// <summary>A client's invite was declined by a professional.</summary>
    InvitationDeclined,

    /// <summary>A pending invite was auto-cancelled because another professional of the same role was accepted.</summary>
    InvitationCancelled,

    /// <summary>A weekly check-in reminder was sent to a client by the scheduler.</summary>
    WeeklyCheckInRequested,

    /// <summary>A client responded to a weekly check-in reminder; delivered to the professional.</summary>
    WeeklyCheckInResponded,

    /// <summary>
    /// A daily reminder sent to a client in an active photo diary workflow
    /// when no photo has been uploaded for the current day yet.
    /// </summary>
    PhotoDiaryReminder,

    /// <summary>A professional removed a coach role that covers the client's plans; delivered to the client.</summary>
    CoachRoleRemoved,

    /// <summary>A collaboration was ended because the professional removed the coach role it covered; delivered to the client.</summary>
    CollaborationEndedByRoleRemoval,

    /// <summary>A collaboration was ended because the professional removed the coach role it covered; delivered to the professional.</summary>
    CollaborationEndedByRoleRemovalCoach,

    /// <summary>A coach's automation asked the client to log their body measurements; delivered to the client.</summary>
    MeasurementRequested,

    /// <summary>A client edited their onboarding answers; delivered to each linked professional.</summary>
    OnboardingAnswersUpdated,

    /// <summary>A coach asked the client to pay; delivered to the client.</summary>
    PaymentRequested,

    /// <summary>A client's offline-synced action collided with a plan change; delivered to the plan's author.</summary>
    OfflineSyncConflict,

    /// <summary>A weekly check-in is still unanswered after the rule's reminder delay; delivered to the client.</summary>
    ClientCheckInReminder,

    /// <summary>A client did not answer a weekly check-in within its answer window; delivered to the coach.</summary>
    CoachCheckInMissed,

    /// <summary>A coach proposed a package on the client's request; delivered to the client.</summary>
    ClientRequestPackageProposed,

    /// <summary>A client accepted the coach's package proposal; delivered to the coach.</summary>
    ClientRequestProposalAccepted,

    /// <summary>A client declined the coach's package proposal; delivered to the coach.</summary>
    ClientRequestProposalDeclined,

    /// <summary>A coach withdrew their package proposal; delivered to the client.</summary>
    ClientRequestProposalWithdrawn,

    /// <summary>A client wrote to a coach in chat; delivered to the coach.</summary>
    NewMessage,

    /// <summary>A client submitted the photos a coach asked for; delivered to the coach.</summary>
    ProgressPhotosSubmitted,

    /// <summary>A client finished a plan workout; delivered to the plan's author.</summary>
    WorkoutFinished,

    /// <summary>An admin changed the coach's plan; delivered to the coach.</summary>
    CoachPlanChanged,

    /// <summary>An admin ended the coach's plan now; delivered to the coach.</summary>
    CoachPlanEnded,

    /// <summary>An admin cancelled the renewal of the coach's plan; delivered to the coach.</summary>
    CoachPlanRenewalCancelled
}
