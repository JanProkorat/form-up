namespace FormUp.Application.Domain.Enums;

/// <summary>
/// Identifies the logical bucket scope for an image upload request.
/// Each scope maps to a specific blob-path convention under the storage root.
/// </summary>
public enum ImageUploadScope
{
    /// <summary>
    /// User or professional avatar.
    /// Blob path: <c>avatars/{userId}.{ext}</c>
    /// </summary>
    Avatar,

    /// <summary>
    /// Food item image.
    /// Blob path: <c>foods/{foodId}.{ext}</c>
    /// </summary>
    Food,

    /// <summary>
    /// Recipe image (primary or gallery slot).
    /// Blob path: <c>recipes/{recipeId}/{slot}.{ext}</c>
    /// </summary>
    Recipe,

    /// <summary>
    /// Nutrition or training plan photo.
    /// Blob path: <c>plan-photos/{planId}/{photoId}.{ext}</c>
    /// </summary>
    PlanPhoto,

    /// <summary>
    /// Client diary photo (progress photo, etc.).
    /// Blob path: <c>diary/{diaryId}/{photoId}.{ext}</c>
    /// </summary>
    Diary,

    /// <summary>
    /// A staged chat-image upload awaiting promotion to a permanent object at send time. The
    /// staging object carries no extension — its real content type is sniffed from its bytes when
    /// the message is sent, never trusted from the client-declared content type used only to mint
    /// this upload URL.
    /// Blob path: <c>chat-uploads/{conversationId}/{callerUserId}/{uploadId}</c>
    /// </summary>
    ChatUpload,

    /// <summary>
    /// A staged questionnaire file_upload answer awaiting promotion at answer-save time. Carries no
    /// extension — the real content type is sniffed from its bytes when the answer is saved.
    /// Blob path: <c>questionnaire-uploads/{responsePublicId}/{callerUserId}/{uploadId}</c>
    /// </summary>
    QuestionnaireUpload,
}
