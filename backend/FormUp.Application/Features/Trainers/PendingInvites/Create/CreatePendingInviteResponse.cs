namespace FormUp.Application.Features.Trainers.PendingInvites.Create;

/// <summary>
/// Response model returned after creating a pending invitation.
/// </summary>
public class CreatePendingInviteResponse
{
    /// <summary>
    /// Public identifier of the created pending invite.
    /// </summary>
    public Guid PublicId { get; set; }

    /// <summary>
    /// Email address of the invited client.
    /// </summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>
    /// Timestamp when the invitation was sent.
    /// </summary>
    public DateTime SentAt { get; set; }

    /// <summary>
    /// Public ID of the assigned questionnaire, if any.
    /// </summary>
    public Guid? QuestionnairePublicId { get; set; }
}
