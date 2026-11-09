namespace FormUp.Application.Domain.Constants;

/// <summary>
/// Constants for API error codes used in validation and business logic errors.
/// These codes are returned in ProblemDetails responses and mapped to translated messages on the frontend.
/// </summary>
public static class ErrorCodes
{
    // ── Auth ─────────────────────────────────────────────────────────
    /// <summary>Invalid email or password during login.</summary>
    public const string InvalidCredentials = "INVALID_CREDENTIALS";

    /// <summary>No account uses the email submitted to the password reset request.</summary>
    public const string EmailNotRegistered = "EMAIL_NOT_REGISTERED";

    /// <summary>Google-verified email matches a password-only account with no Google external login. The user should sign in with their password.</summary>
    public const string SocialEmailConflict = "social_email_conflict";

    /// <summary>Apple did not provide an email in the identity token, and no (apple, sub) link exists. Cannot provision or log in without an email address.</summary>
    public const string AppleNoEmailNoLink = "APPLE_NO_EMAIL_NO_LINK";

    /// <summary>Account is deactivated.</summary>
    public const string AccountDeactivated = "ACCOUNT_DEACTIVATED";

    /// <summary>Invalid or expired refresh token.</summary>
    public const string InvalidRefreshToken = "INVALID_REFRESH_TOKEN";

    /// <summary>Invalid password reset request.</summary>
    public const string InvalidResetRequest = "INVALID_RESET_REQUEST";

    /// <summary>Invalid invitation token.</summary>
    public const string InvalidInvitation = "INVALID_INVITATION";

    /// <summary>Invitation already used.</summary>
    public const string InvitationAlreadyUsed = "INVITATION_ALREADY_USED";

    /// <summary>Invitation has expired.</summary>
    public const string InvitationExpired = "INVITATION_EXPIRED";

    /// <summary>Invalid or expired email verification token.</summary>
    public const string InvalidVerificationToken = "INVALID_VERIFICATION_TOKEN";

    /// <summary>Email verification token has expired.</summary>
    public const string VerificationTokenExpired = "VERIFICATION_TOKEN_EXPIRED";

    /// <summary>Maximum number of verification emails has been reached.</summary>
    public const string VerificationResendLimitReached = "VERIFICATION_RESEND_LIMIT_REACHED";

    /// <summary>Email is already verified.</summary>
    public const string EmailAlreadyVerified = "EMAIL_ALREADY_VERIFIED";

    /// <summary>The new email address equals the account's current one.</summary>
    public const string EmailUnchanged = "EMAIL_UNCHANGED";

    /// <summary>The email address already belongs to another account.</summary>
    public const string EmailAlreadyInUse = "EMAIL_ALREADY_IN_USE";

    // ── Users ────────────────────────────────────────────────────────
    /// <summary>User already has the requested role.</summary>
    public const string RoleAlreadyAssigned = "ROLE_ALREADY_ASSIGNED";

    /// <summary>The caller does not hold the coach role, or it is already removed.</summary>
    public const string RoleNotAssigned = "ROLE_NOT_ASSIGNED";

    /// <summary>The role is the caller's only active coach role and cannot be removed.</summary>
    public const string OnlyCoachRole = "ONLY_COACH_ROLE";

    /// <summary>The current password supplied for a password change is wrong.</summary>
    public const string InvalidCurrentPassword = "INVALID_CURRENT_PASSWORD";

    /// <summary>The account has no password (social-only sign-in), so it cannot be changed.</summary>
    public const string PasswordNotSet = "PASSWORD_NOT_SET";

    /// <summary>Account deletion failed.</summary>
    public const string AccountDeletionFailed = "ACCOUNT_DELETION_FAILED";

    /// <summary>The supplied IANA time zone identifier is not valid.</summary>
    public const string InvalidTimeZone = "INVALID_TIME_ZONE";

    // ── Foods ────────────────────────────────────────────────────────
    /// <summary>Kcal value is inconsistent with macronutrients.</summary>
    public const string KcalInconsistent = "KCAL_INCONSISTENT";

    /// <summary>User can only edit/delete their own custom foods.</summary>
    public const string FoodNotOwned = "FOOD_NOT_OWNED";

    /// <summary>Food gallery is at its 6-entry cap; no further images can be added.</summary>
    public const string FoodGalleryFull = "FOOD_GALLERY_FULL";

    /// <summary>The image URL is neither in the food's gallery nor its current main image.</summary>
    public const string FoodGalleryImageNotFound = "FOOD_GALLERY_IMAGE_NOT_FOUND";

    /// <summary>The food's main image or gallery changed concurrently (compare-and-swap miss).</summary>
    public const string FoodImagesConflict = "FOOD_IMAGES_CONFLICT";

    /// <summary>Food tag not found, or belongs to a different owning nutritionist.</summary>
    public const string FoodTagNotFound = "FOOD_TAG_NOT_FOUND";

    /// <summary>A food tag with this Name already exists for the owning nutritionist.</summary>
    public const string FoodTagNameAlreadyExists = "FOOD_TAG_NAME_ALREADY_EXISTS";

    // ── Recipes ──────────────────────────────────────────────────────
    /// <summary>User can only edit/delete/upload images for their own recipes.</summary>
    public const string RecipeNotOwned = "RECIPE_NOT_OWNED";

    /// <summary>Recipe gallery is at its 6-entry cap; no further images can be added.</summary>
    public const string RecipeGalleryFull = "RECIPE_GALLERY_FULL";

    /// <summary>The recipe version is stale; another write occurred first (optimistic concurrency).</summary>
    public const string RecipeVersionConflict = "RECIPE_VERSION_CONFLICT";

    /// <summary>A recipe ingredient food does not exist, is deleted, or is another coach's private food.</summary>
    public const string RecipeFoodNotAvailable = "RECIPE_FOOD_NOT_AVAILABLE";

    /// <summary>The image URL is neither in the recipe's gallery nor its current main image.</summary>
    public const string RecipeGalleryImageNotFound = "RECIPE_GALLERY_IMAGE_NOT_FOUND";

    // ── Exercises ──────────────────────────────────────────────────
    /// <summary>User can only edit/delete their own custom exercises.</summary>
    public const string ExerciseNotOwned = "EXERCISE_NOT_OWNED";

    /// <summary>Cannot modify system exercises.</summary>
    public const string SystemExercise = "SYSTEM_EXERCISE";

    /// <summary>The exercise version is stale; another write occurred first (optimistic concurrency).</summary>
    public const string ExerciseVersionConflict = "EXERCISE_VERSION_CONFLICT";

    // ── Trainers ─────────────────────────────────────────────────────
    /// <summary>Trainer profile not found.</summary>
    public const string TrainerProfileMissing = "TRAINER_PROFILE_MISSING";

    /// <summary>Client not found.</summary>
    public const string ClientNotFound = "CLIENT_NOT_FOUND";

    /// <summary>No active relationship with client.</summary>
    public const string NoClientRelationship = "NO_CLIENT_RELATIONSHIP";

    /// <summary>Collaborator not found.</summary>
    public const string CollaboratorNotFound = "COLLABORATOR_NOT_FOUND";

    /// <summary>Collaborator already linked to client.</summary>
    /// <summary>
    /// Requested link capability scope (see <see cref="Enums.LinkCapabilityScope"/>) exceeds
    /// the identity roles actually held by the professional the link's CanView flags gate.
    /// Emitted by AcceptClientRequestEndpoint and CreatePendingInviteEndpoint.
    /// </summary>
    public const string RequestedScopeExceedsHeldRoles = "REQUESTED_SCOPE_EXCEEDS_HELD_ROLES";

    /// <summary>
    /// An unaccepted pending invite already exists for this professional and email — the
    /// professional must wait for a response or delete the existing invite before resending
    /// (claude-security F8: repeated invites to the same target are abuse, not a workflow need).
    /// </summary>
    public const string DuplicatePendingInvite = "DUPLICATE_PENDING_INVITE";

    /// <summary>
    /// The professional has reached the maximum number of outstanding (unaccepted) pending
    /// invites. Bounds the standing fan-out an abusive account can build up even when paced
    /// below the rate-limit window (claude-security F8).
    /// </summary>
    public const string TooManyPendingInvites = "TOO_MANY_PENDING_INVITES";

    /// <summary>The pending invite is past its validity window and can no longer be accepted.</summary>
    public const string InviteExpired = "INVITE_EXPIRED";

    /// <summary>Only a pending invite can be cancelled.</summary>
    public const string PendingInviteNotCancellable = "PENDING_INVITE_NOT_CANCELLABLE";

    // ── Nutrition Plans ──────────────────────────────────────────────
    /// <summary>Only draft plans can be published.</summary>
    public const string PlanNotDraft = "PLAN_NOT_DRAFT";

    /// <summary>Only active plans can be completed.</summary>
    public const string PlanNotActive = "PLAN_NOT_ACTIVE";

    /// <summary>The plan version is stale; another write occurred first (optimistic concurrency).</summary>
    public const string PlanVersionConflict = "PLAN_VERSION_CONFLICT";

    /// <summary>A meal the client ate (fully or partly), or one on a past day, was changed, moved or deleted.</summary>
    public const string MealLocked = "MEAL_LOCKED";

    /// <summary>A meal was added to, or moved onto, a day before the client's local today.</summary>
    public const string PastDayLocked = "PAST_DAY_LOCKED";

    /// <summary>The plan has a published week that has already started, so it can no longer be deleted.</summary>
    public const string PlanAlreadyStarted = "PLAN_ALREADY_STARTED";

    // ── Plan Start Date ────────────────────────────────────────────
    /// <summary>Start date is not a Monday.</summary>
    public const string StartDateNotMonday = "START_DATE_NOT_MONDAY";

    /// <summary>Start date is in the past.</summary>
    public const string StartDateInPast = "START_DATE_IN_PAST";

    /// <summary>Start date is locked because it has already arrived.</summary>
    public const string StartDateLocked = "START_DATE_LOCKED";

    /// <summary>Start date is required before publishing.</summary>
    public const string StartDateRequired = "START_DATE_REQUIRED";

    /// <summary>The target week's start Monday is in the past.</summary>
    public const string WeekStartInPast = "WEEK_START_IN_PAST";

    /// <summary>
    /// The new plan's date window ([StartDate, StartDate + WeekCount * 7)) overlaps an existing
    /// Draft or Active same-type plan for the client (#780 — multiple sequential plans per client
    /// are allowed, but their windows may not overlap).
    /// </summary>
    public const string PlanOverlap = "PLAN_OVERLAP";

    // ── Client Requests ───────────────────────────────────────────────
    /// <summary>Professional profile not found.</summary>
    public const string ProfessionalNotFound = "PROFESSIONAL_NOT_FOUND";

    /// <summary>Client and professional are already linked.</summary>
    public const string AlreadyLinked = "ALREADY_LINKED";

    /// <summary>A pending request already exists for this professional.</summary>
    public const string RequestAlreadyPending = "REQUEST_ALREADY_PENDING";

    /// <summary>The client request is not in the state the action needs (for example a package proposal that is no longer open).</summary>
    public const string ClientRequestInvalidState = "CLIENT_REQUEST_INVALID_STATE";

    /// <summary>Professional is not accepting new clients.</summary>
    public const string ProfessionalNotAccepting = "PROFESSIONAL_NOT_ACCEPTING";

    /// <summary>Client request not found.</summary>
    public const string ClientRequestNotFound = "CLIENT_REQUEST_NOT_FOUND";

    // ── Training Completion ──────────────────────────────────────────
    /// <summary>The training completion document version is stale; another write occurred first.</summary>
    public const string TrainingCompletionVersionConflict = "TRAINING_COMPLETION_VERSION_CONFLICT";

    /// <summary>The session ID was not found in the client's active training plan.</summary>
    public const string TrainingSessionNotFound = "TRAINING_SESSION_NOT_FOUND";

    /// <summary>The exercise was not found in the specified session.</summary>
    public const string TrainingExerciseNotFound = "TRAINING_EXERCISE_NOT_FOUND";

    /// <summary>The workout was not found in the specified session.</summary>
    public const string TrainingWorkoutNotFound = "TRAINING_WORKOUT_NOT_FOUND";

    /// <summary>
    /// A session's standalone exercises and workouts share one ordering sequence; two entries
    /// (in either list, or across both) claimed the same Order value.
    /// </summary>
    public const string TrainingDuplicateSessionOrder = "TRAINING_DUPLICATE_SESSION_ORDER";

    /// <summary>No active training plan found for the client.</summary>
    public const string NoActiveTrainingPlan = "NO_ACTIVE_TRAINING_PLAN";

    // ── Weekly Check-Ins ──────────────────────────────────────────────
    /// <summary>TimeOfDay must be hour-aligned (minutes, seconds, and milliseconds must all be zero).</summary>
    public const string InvalidTimeOfDay = "INVALID_TIME_OF_DAY";

    /// <summary>The requested profession is not in the trainer's specializations.</summary>
    public const string ProfessionNotSpecialized = "PROFESSION_NOT_SPECIALIZED";

    /// <summary>The trainer is not linked to the specified client.</summary>
    public const string NotLinkedToClient = "NOT_LINKED_TO_CLIENT";

    /// <summary>The weekly check-in was not found or does not belong to the caller.</summary>
    public const string CheckInNotFound = "CHECK_IN_NOT_FOUND";

    /// <summary>The trainer already reviewed this check-in; the client can no longer modify it.</summary>
    public const string CheckInAlreadyReviewed = "CHECK_IN_ALREADY_REVIEWED";

    /// <summary>The weekly check-in belongs to another professional.</summary>
    public const string CheckInNotOwned = "CHECK_IN_NOT_OWNED";

    /// <summary>The weekly check-in has expired; the client can no longer respond or dismiss it.</summary>
    public const string CheckInExpired = "CHECK_IN_EXPIRED";

    /// <summary>The client has no onboarding data (or no nutrition targets row for a fibre target) to update.</summary>
    public const string ClientNutritionProfileMissing = "CLIENT_NUTRITION_PROFILE_MISSING";

    // ── Plan Photos ───────────────────────────────────────────────────
    /// <summary>Only the uploader can delete their own plan photo.</summary>
    public const string PlanPhotoNotOwned = "PLAN_PHOTO_NOT_OWNED";

    /// <summary>Plan photo not found.</summary>
    public const string PlanPhotoNotFound = "PLAN_PHOTO_NOT_FOUND";

    /// <summary>No active plan (nutrition or training) found for the given planId.</summary>
    public const string PlanNotFound = "PLAN_NOT_FOUND";

    /// <summary>
    /// BlobUrl does not match the expected identity-scoped storage key for the resource being
    /// confirmed — e.g. <c>plan-photos/{planId}/{guid}.{ext}</c> for a plan photo, or
    /// <c>avatars/{userId}.{ext}</c> / <c>avatars/prof-{profileId}.{ext}</c> for an avatar.
    /// Prevents path traversal, cross-resource hijacking, and stored-content injection via an
    /// attacker-supplied external URL.
    /// </summary>
    public const string InvalidBlobUrl = "INVALID_BLOB_URL";

    /// <summary>A meal-log date is later than the client's local today.</summary>
    public const string MealLogDateInFuture = "MEAL_LOG_DATE_IN_FUTURE";

    // ── Image Uploads ────────────────────────────────────────────────
    /// <summary>Content type is not in the allowed image whitelist (image/jpeg, image/png, image/webp).</summary>
    public const string InvalidImageContentType = "INVALID_IMAGE_CONTENT_TYPE";

    /// <summary>Image file size exceeds the 5 MiB limit.</summary>
    public const string ImageTooLarge = "IMAGE_TOO_LARGE";

    /// <summary>
    /// subPath tried to escape the scope prefix: it contains <c>..</c>, a backslash (<c>\</c>),
    /// starts with a leading <c>/</c>, or is null/empty/whitespace.
    /// </summary>
    public const string InvalidImageSubPath = "INVALID_IMAGE_SUB_PATH";

    /// <summary>
    /// A chat message's <c>ImageUploadId</c> has no staged object at send time — never PUT to the
    /// presigned upload URL, already consumed by an earlier send, or staged by another user or
    /// conversation.
    /// </summary>
    public const string ChatImageUploadNotFound = "CHAT_IMAGE_UPLOAD_NOT_FOUND";

    /// <summary>
    /// A professional tried to send into a thread whose client has never been linked to them
    /// (an invite-only thread the client has not accepted).
    /// </summary>
    public const string ConversationLocked = "CONVERSATION_LOCKED";

    // ── Photo Diary Requests ─────────────────────────────────────────
    /// <summary>Photo diary request not found or does not belong to the caller.</summary>
    public const string PhotoDiaryRequestNotFound = "PHOTO_DIARY_REQUEST_NOT_FOUND";

    /// <summary>The photo diary request is not in the expected status for this operation.</summary>
    public const string PhotoDiaryRequestInvalidStatus = "PHOTO_DIARY_REQUEST_INVALID_STATUS";

    /// <summary>Exactly one of linkId or pendingInviteId must be set.</summary>
    public const string PhotoDiaryRequestLinkXorInvite = "PHOTO_DIARY_REQUEST_LINK_XOR_INVITE";

    /// <summary>The referenced client-professional link does not belong to the calling professional.</summary>
    public const string PhotoDiaryRequestLinkNotOwned = "PHOTO_DIARY_REQUEST_LINK_NOT_OWNED";

    /// <summary>The referenced pending invite does not belong to the calling professional.</summary>
    public const string PhotoDiaryRequestInviteNotOwned = "PHOTO_DIARY_REQUEST_INVITE_NOT_OWNED";

    /// <summary>The referenced plan does not belong to the client associated with this link/invite.</summary>
    public const string PhotoDiaryRequestPlanNotOwned = "PHOTO_DIARY_REQUEST_PLAN_NOT_OWNED";

    /// <summary>The caller is not the owning professional for this photo diary request (role-gate rejection).</summary>
    public const string PhotoDiaryRequestForbidden = "PHOTO_DIARY_REQUEST_FORBIDDEN";

    /// <summary>The coach already has an open photo diary request for this client.</summary>
    public const string PhotoDiaryRequestAlreadyOpen = "PHOTO_DIARY_REQUEST_ALREADY_OPEN";

    /// <summary>The photo diary request's window has ended, so no more photos can be added to it.</summary>
    public const string PhotoDiaryRequestExpired = "PHOTO_DIARY_REQUEST_EXPIRED";

    // ── Workout Templates ────────────────────────────────────────────
    /// <summary>Workout template not found, or not owned by the calling trainer (indistinguishable from missing).</summary>
    public const string WorkoutTemplateNotFound = "WORKOUT_TEMPLATE_NOT_FOUND";

    /// <summary>Workout template version mismatch (optimistic concurrency).</summary>
    public const string WorkoutTemplateVersionConflict = "WORKOUT_TEMPLATE_VERSION_CONFLICT";

    // ── Training Workouts ────────────────────────────────────────────
    /// <summary>Session workouts list is empty.</summary>
    public const string WorkoutsRequired = "WORKOUTS_REQUIRED";

    /// <summary>Duplicate Order values across workouts in the same session.</summary>
    public const string WorkoutOrderDuplicate = "WORKOUT_ORDER_DUPLICATE";

    // ── Trainer Finish Session ───────────────────────────────────────
    /// <summary>The session is currently locked by another party (live or editing lock conflict).</summary>
    public const string SessionLocked = "session_locked";

    /// <summary>The session already has a completed workout log; cannot finish again.</summary>
    public const string SessionAlreadyCompleted = "SESSION_ALREADY_COMPLETED";

    /// <summary>
    /// Attempt to edit a training plan section whose content has already been completed
    /// by the client (via a finished WorkoutLog or a TrainingCompletion record).
    /// </summary>
    public const string WorkoutAlreadyCompleted = "WORKOUT_ALREADY_COMPLETED";

    /// <summary>completedAt is in the future; backdating to the future is not allowed.</summary>
    public const string CompletedAtInFuture = "COMPLETED_AT_IN_FUTURE";

    /// <summary>completedAt is before the plan's start date; history cannot be written before the plan began.</summary>
    public const string CompletedAtBeforePlanStart = "COMPLETED_AT_BEFORE_PLAN_START";

    // ── Trainer Notes ─────────────────────────────────────────────────
    /// <summary>Trainer note not found or belongs to a different trainer/client.</summary>
    public const string TrainerNoteNotFound = "TRAINER_NOTE_NOT_FOUND";

    // ── Validation (generic) ─────────────────────────────────────────
    /// <summary>Required field is missing.</summary>
    public const string Required = "REQUIRED";

    /// <summary>Value is out of allowed range.</summary>
    public const string OutOfRange = "OUT_OF_RANGE";

    /// <summary>Two or more fields were supplied together when exactly one (or none) is allowed.</summary>
    public const string MutuallyExclusiveFields = "MUTUALLY_EXCLUSIVE_FIELDS";

    /// <summary>DeadlineOffsetHours is not one of the allowed values (24, 48, 72, 120, 168).</summary>
    public const string InvalidDeadlineOffsetHours = "INVALID_DEADLINE_OFFSET_HOURS";

    // ── Sharing Libraries (#858) ──────────────────────────────────────
    // Meal templates, session templates, nutrition-plan templates, and training-plan
    // templates — the four reusable-content libraries sharing LibraryVisibility /
    // LibraryAccessGuard / LibrarySearchHelper. Exactly 12 codes (4 libraries x
    // NOT_FOUND/NOT_OWNED/VERSION_CONFLICT) — never a WORKOUT_TEMPLATE_* code; that name is
    // minted by #857's rename of the pre-existing SECTION_TEMPLATE_* codes above.

    /// <summary>Meal template not found, or belongs to another owner and is Private.</summary>
    public const string MealTemplateNotFound = "MEAL_TEMPLATE_NOT_FOUND";

    /// <summary>Meal template is readable but belongs to another owner.</summary>
    public const string MealTemplateNotOwned = "MEAL_TEMPLATE_NOT_OWNED";

    /// <summary>Meal template version mismatch (optimistic concurrency).</summary>
    public const string MealTemplateVersionConflict = "MEAL_TEMPLATE_VERSION_CONFLICT";

    /// <summary>Session template not found, or belongs to another owner and is Private.</summary>
    public const string SessionTemplateNotFound = "SESSION_TEMPLATE_NOT_FOUND";

    /// <summary>Session template is readable but belongs to another owner.</summary>
    public const string SessionTemplateNotOwned = "SESSION_TEMPLATE_NOT_OWNED";

    /// <summary>Session template version mismatch (optimistic concurrency).</summary>
    public const string SessionTemplateVersionConflict = "SESSION_TEMPLATE_VERSION_CONFLICT";

    /// <summary>Nutrition plan template not found, or belongs to another owner and is Private.</summary>
    public const string NutritionPlanTemplateNotFound = "NUTRITION_PLAN_TEMPLATE_NOT_FOUND";

    /// <summary>Nutrition plan template is readable but belongs to another owner.</summary>
    public const string NutritionPlanTemplateNotOwned = "NUTRITION_PLAN_TEMPLATE_NOT_OWNED";

    /// <summary>Nutrition plan template version mismatch (optimistic concurrency).</summary>
    public const string NutritionPlanTemplateVersionConflict = "NUTRITION_PLAN_TEMPLATE_VERSION_CONFLICT";

    /// <summary>Training plan template not found, or belongs to another owner and is Private.</summary>
    public const string TrainingPlanTemplateNotFound = "TRAINING_PLAN_TEMPLATE_NOT_FOUND";

    /// <summary>Training plan template is readable but belongs to another owner.</summary>
    public const string TrainingPlanTemplateNotOwned = "TRAINING_PLAN_TEMPLATE_NOT_OWNED";

    /// <summary>Training plan template version mismatch (optimistic concurrency).</summary>
    public const string TrainingPlanTemplateVersionConflict = "TRAINING_PLAN_TEMPLATE_VERSION_CONFLICT";

    // ── Subscription Plans (#595) ───────────────────────────────────
    /// <summary>A subscription plan with this Code already exists.</summary>
    public const string SubscriptionPlanCodeAlreadyExists = "SUBSCRIPTION_PLAN_CODE_ALREADY_EXISTS";

    /// <summary>Subscription plan not found for the given Code.</summary>
    public const string SubscriptionPlanNotFound = "SUBSCRIPTION_PLAN_NOT_FOUND";

    /// <summary>A plan must either include both roles or carry a positive second-role add-on price, never both or neither.</summary>
    public const string SecondRoleOptionInvalid = "SECOND_ROLE_OPTION_INVALID";

    /// <summary>The Free plan is fixed at price 0 and must stay offered; it cannot be repriced, hidden or deactivated.</summary>
    public const string FreePlanLocked = "FREE_PLAN_LOCKED";

    /// <summary>Currency is not one of the platform's supported billing currencies.</summary>
    public const string UnsupportedCurrency = "UNSUPPORTED_CURRENCY";

    // ── Client-Professional Links (#980) ─────────────────────────────
    /// <summary>
    /// Accepting/creating this link would give the client a second ACTIVE link holding
    /// a profession slot (CanViewNutritionPlans or CanViewTrainingPlans) that a
    /// different active professional already occupies. A client may hold at most one
    /// active coach per profession — ending the existing link first (see
    /// EndCollaborationEndpoint) frees the slot. A single dual-role professional
    /// legitimately occupies both slots through one link; this fires only when a
    /// different professional would claim a slot already held. See
    /// <see cref="FormUp.Application.Domain.Services.ProfessionSlotGuard"/>.
    /// </summary>
    public const string ProfessionAlreadyOccupied = "PROFESSION_ALREADY_OCCUPIED";

    // ── Client Tags (#1063) ────────────────────────────────────────────
    /// <summary>Client tag not found, or belongs to a different owning professional.</summary>
    public const string ClientTagNotFound = "CLIENT_TAG_NOT_FOUND";

    /// <summary>A tag with this Name already exists for the owning professional.</summary>
    public const string ClientTagNameAlreadyExists = "CLIENT_TAG_NAME_ALREADY_EXISTS";

    // ── Messaging Broadcast (#1065) ────────────────────────────────────
    /// <summary>The recipient list exceeds the maximum allowed size for a single broadcast.</summary>
    public const string BroadcastRecipientLimitExceeded = "BROADCAST_RECIPIENT_LIMIT_EXCEEDED";

    /// <summary>A recipient id does not resolve to a client with a live link to the caller.</summary>
    public const string BroadcastRecipientNotLinked = "BROADCAST_RECIPIENT_NOT_LINKED";

    /// <summary>
    /// A recipient's personalized text (after {{firstName}}/{{fullName}} substitution) exceeds
    /// the message column's storage limit, even though the pre-substitution template passed
    /// validation. Rejected before any write — checked for every recipient up front, alongside
    /// the authorization gates.
    /// </summary>
    public const string BroadcastMessageTooLongAfterSubstitution = "BROADCAST_MESSAGE_TOO_LONG_AFTER_SUBSTITUTION";

    // ── Messaging Inbox Filter (#1095) ──────────────────────────────────
    /// <summary>A non-All conversation filter chip was requested by a caller who is not a professional.</summary>
    public const string FilterRequiresProfessionalCaller = "FILTER_REQUIRES_PROFESSIONAL_CALLER";

    // ── Client Message Stats (#1098) ────────────────────────────────────
    /// <summary>The requested number of weeks for the message stats is outside 1-26.</summary>
    public const string MessageStatsWeeksOutOfRange = "MESSAGE_STATS_WEEKS_OUT_OF_RANGE";

    // ── Remove coach role (#1265) ───────────────────────────────────────
    /// <summary>The write belongs to a coach role the caller has removed (read-only).</summary>
    public const string CoachRoleRemoved = "COACH_ROLE_REMOVED";

    // ── Disable coach account (#1266) ───────────────────────────────────
    /// <summary>Every coach role the caller holds is already removed, so there is nothing to disable.</summary>
    public const string NoActiveCoachRole = "NO_ACTIVE_COACH_ROLE";

    /// <summary>Keep was requested but the coach account has no pending disable.</summary>
    public const string CoachAccountNotDisabling = "COACH_ACCOUNT_NOT_DISABLING";

    /// <summary>Keep was requested after the disable already took effect; add the role back instead.</summary>
    public const string CoachAccountDisableEnded = "COACH_ACCOUNT_DISABLE_ENDED";

    // ── Form types and question types (#1340) ───────────────────────────
    /// <summary>A question type outside the fixed set.</summary>
    public const string QuestionTypeInvalid = "QUESTION_TYPE_INVALID";

    /// <summary>A question's config is not valid JSON for its type.</summary>
    public const string QuestionConfigInvalid = "QUESTION_CONFIG_INVALID";

    /// <summary>A section divider cannot be required or mapped to a profile field.</summary>
    public const string QuestionSectionInvalid = "QUESTION_SECTION_INVALID";

    /// <summary>Only Onboarding forms can be the default form.</summary>
    public const string CheckInFormCannotBeDefault = "CHECKIN_FORM_CANNOT_BE_DEFAULT";

    /// <summary>A Check-in form cannot be assigned, replaced, or attached to an invite or accepted request.</summary>
    public const string CheckInFormNotSendable = "CHECKIN_FORM_NOT_SENDABLE";

    // ── Automations (#1504) ─────────────────────────────────────────────
    /// <summary>The automation rule does not exist or is not owned by the caller.</summary>
    public const string AutomationRuleNotFound = "AUTOMATION_RULE_NOT_FOUND";

    /// <summary>The audience tag does not exist or is owned by another coach.</summary>
    public const string AutomationTagNotFound = "AUTOMATION_TAG_NOT_FOUND";

    /// <summary>A chosen client has no live link to the caller.</summary>
    public const string AutomationAudienceClientNotFound = "AUTOMATION_AUDIENCE_CLIENT_NOT_FOUND";

    /// <summary>The questionnaire to send does not exist or is not owned by the caller.</summary>
    public const string AutomationQuestionnaireNotFound = "AUTOMATION_QUESTIONNAIRE_NOT_FOUND";

    /// <summary>The questionnaire to send is the caller's but inactive or a check-in form, so no run could send it.</summary>
    public const string AutomationQuestionnaireNotUsable = "AUTOMATION_QUESTIONNAIRE_NOT_USABLE";

    /// <summary>An update tried to change the action type of an existing rule.</summary>
    public const string AutomationActionTypeLocked = "AUTOMATION_ACTION_TYPE_LOCKED";

    /// <summary>The pause end date is before the client's current local date.</summary>
    public const string AutomationPausedUntilInPast = "AUTOMATION_PAUSED_UNTIL_IN_PAST";

    /// <summary>A pause end date or note was sent without pausing.</summary>
    public const string AutomationPauseDetailsWithoutPause = "AUTOMATION_PAUSE_DETAILS_WITHOUT_PAUSE";

    /// <summary>Reset was requested but the rule has no override for this client.</summary>
    public const string AutomationNoOverrideToReset = "AUTOMATION_NO_OVERRIDE_TO_RESET";

    /// <summary>Override or pause was requested on a client-only rule; update the rule itself.</summary>
    public const string AutomationClientOnlyRuleNotOverridable = "AUTOMATION_CLIENT_ONLY_RULE_NOT_OVERRIDABLE";

    /// <summary>An audience was sent for a client-only rule.</summary>
    public const string AutomationAudienceNotAllowedOnClientOnlyRule = "AUTOMATION_AUDIENCE_NOT_ALLOWED_ON_CLIENT_ONLY_RULE";

    /// <summary>The audience tag id is missing for a Tag audience.</summary>
    public const string AutomationAudienceTagRequired = "AUTOMATION_AUDIENCE_TAG_REQUIRED";

    /// <summary>The chosen client list is empty for a Chosen audience.</summary>
    public const string AutomationAudienceClientsRequired = "AUTOMATION_AUDIENCE_CLIENTS_REQUIRED";

    /// <summary>The schedule trigger is incomplete or out of range.</summary>
    public const string AutomationScheduleInvalid = "AUTOMATION_SCHEDULE_INVALID";

    /// <summary>The event trigger is incomplete or out of range.</summary>
    public const string AutomationEventInvalid = "AUTOMATION_EVENT_INVALID";

    /// <summary>Once-per-plan was set on an event that is not a plan event.</summary>
    public const string AutomationOncePerPlanNeedsPlanEvent = "AUTOMATION_ONCE_PER_PLAN_NEEDS_PLAN_EVENT";

    /// <summary>A send-form rule has no questionnaire.</summary>
    public const string AutomationQuestionnaireRequired = "AUTOMATION_QUESTIONNAIRE_REQUIRED";

    /// <summary>The send limit is below 1.</summary>
    public const string AutomationStopAfterInvalid = "AUTOMATION_STOP_AFTER_INVALID";

    /// <summary>A progress-photos rule's message is over the photo request's 500-character limit.</summary>
    public const string AutomationPhotoMessageTooLong = "AUTOMATION_PHOTO_MESSAGE_TOO_LONG";

    /// <summary>A check-in rule is missing a check-in field, carries an invalid one, or is not a weekly schedule.</summary>
    public const string AutomationCheckInInvalid = "AUTOMATION_CHECK_IN_INVALID";

    /// <summary>Check-in fields were sent for a rule whose form is not a check-in form.</summary>
    public const string AutomationCheckInFieldsNotAllowed = "AUTOMATION_CHECK_IN_FIELDS_NOT_ALLOWED";

    /// <summary>The coach already has an enabled check-in rule.</summary>
    public const string CheckInRuleExists = "CHECK_IN_RULE_EXISTS";

    /// <summary>The client has not completed onboarding, so there are no answers to read or edit.</summary>
    public const string OnboardingNotCompleted = "ONBOARDING_NOT_COMPLETED";

    // ── Coach payment settings (#1577) ──────────────────────────────────
    /// <summary>The coach's subscription does not include the payments feature, or is not in good standing.</summary>
    public const string PaymentsNotEntitled = "PAYMENTS_NOT_ENTITLED";

    /// <summary>The check-in can no longer be answered: its answer window has passed.</summary>
    public const string CheckInAnswerWindowClosed = "CHECK_IN_ANSWER_WINDOW_CLOSED";

    /// <summary>The coach's current subscription does not include the feature being used.</summary>
    public const string SubscriptionRequired = "SUBSCRIPTION_REQUIRED";

    /// <summary>The coach is at the active-client limit of the current subscription.</summary>
    public const string ClientLimitReached = "CLIENT_LIMIT_REACHED";

    /// <summary>The client is over the Free plan's client cap after the coach's paid plan lapsed, so the action is refused.</summary>
    public const string ClientOverPlanLimit = "CLIENT_OVER_PLAN_LIMIT";

    /// <summary>The kept-clients choice has a duplicate id or the wrong number of clients.</summary>
    public const string KeepClientsCountInvalid = "KEEP_CLIENTS_COUNT_INVALID";

    /// <summary>The coach's paid plan has not lapsed, so there is no kept-clients choice to make.</summary>
    public const string SubscriptionNotLapsed = "SUBSCRIPTION_NOT_LAPSED";

    /// <summary>The coach has no paying subscription whose renewal could be cancelled (no row, Free plan, a gift, or a plan that no longer applies).</summary>
    public const string NoRenewingSubscription = "NO_RENEWING_SUBSCRIPTION";

    /// <summary>The IBAN is malformed or fails the mod-97 check.</summary>
    public const string InvalidIban = "INVALID_IBAN";

    /// <summary>The caller has a password but sent none; a bank account change needs it.</summary>
    public const string ReauthPasswordRequired = "REAUTH_PASSWORD_REQUIRED";

    /// <summary>The caller has no password and sent no emailed code; a bank account change needs it.</summary>
    public const string ReauthCodeRequired = "REAUTH_CODE_REQUIRED";

    /// <summary>The emailed code is wrong, expired, used up or locked after too many wrong attempts.</summary>
    public const string ReauthCodeInvalid = "REAUTH_CODE_INVALID";

    /// <summary>A code was requested by a caller who has a password and so does not use codes.</summary>
    public const string ReauthCodeNotAvailable = "REAUTH_CODE_NOT_AVAILABLE";

    /// <summary>The bank account undo token is unknown, expired or already used.</summary>
    public const string BankAccountUndoInvalid = "BANK_ACCOUNT_UNDO_INVALID";

    /// <summary>The bank account was changed again after the change being undone.</summary>
    public const string BankAccountUndoSuperseded = "BANK_ACCOUNT_UNDO_SUPERSEDED";

    /// <summary>The notification email link is tampered, expired, from another key, or its account is gone. One code for every case.</summary>
    public const string EmailLinkInvalid = "EMAIL_LINK_INVALID";

    // ── Invite package terms (#1676) ────────────────────────────────────
    /// <summary>An invite names a discount but no package.</summary>
    public const string InviteDiscountNeedsPackage = "INVITE_DISCOUNT_NEEDS_PACKAGE";

    /// <summary>The package a client attached to a request is unknown, not the coach's, archived or not shown on the profile.</summary>
    public const string RequestPackageUnavailable = "REQUEST_PACKAGE_UNAVAILABLE";

    // ── Coach packages and discounts (#1578) ────────────────────────────
    /// <summary>The package is archived and can no longer be edited.</summary>
    public const string PackageArchived = "PACKAGE_ARCHIVED";

    /// <summary>The discount is archived and can no longer be edited.</summary>
    public const string DiscountArchived = "DISCOUNT_ARCHIVED";

    /// <summary>A package the discount applies to is unknown, foreign or archived.</summary>
    public const string DiscountPackageInvalid = "DISCOUNT_PACKAGE_INVALID";

    /// <summary>A fixed discount and a package it is combined with are in different currencies.</summary>
    public const string CurrencyMismatch = "CURRENCY_MISMATCH";

    /// <summary>The currency cannot change while client terms use the package or fixed discount.</summary>
    public const string CurrencyLocked = "CURRENCY_LOCKED";

    // ── Client payments (#1579) ─────────────────────────────────────────
    /// <summary>The coach has no IBAN or account holder set, so a payment cannot be requested.</summary>
    public const string BankAccountNotSet = "BANK_ACCOUNT_NOT_SET";

    /// <summary>The payment is already paid.</summary>
    public const string PaymentAlreadyPaid = "PAYMENT_ALREADY_PAID";

    /// <summary>The payment is already waived.</summary>
    public const string PaymentAlreadyWaived = "PAYMENT_ALREADY_WAIVED";

    /// <summary>Too many payment requests were sent for the client in a short time.</summary>
    public const string PaymentRequestRateLimited = "PAYMENT_REQUEST_RATE_LIMITED";

    /// <summary>A payment request must be sent by email, as a notification, or both.</summary>
    public const string PaymentRequestChannelRequired = "PAYMENT_REQUEST_CHANNEL_REQUIRED";

    /// <summary>The paid-on date is in the future.</summary>
    public const string PaymentPaidOnInFuture = "PAYMENT_PAID_ON_IN_FUTURE";

    /// <summary>Terms carry both a discount and a custom price.</summary>
    public const string PaymentTermsDiscountAndCustomPrice = "PAYMENT_TERMS_DISCOUNT_AND_CUSTOM_PRICE";

    /// <summary>A payment amount is not above zero or exceeds the payable maximum.</summary>
    public const string PaymentAmountInvalid = "PAYMENT_AMOUNT_INVALID";

    /// <summary>The coach has used all 9999 client numbers.</summary>
    public const string PaymentClientNumberExhausted = "PAYMENT_CLIENT_NUMBER_EXHAUSTED";

    /// <summary>No variable symbol is left for the month.</summary>
    public const string PaymentVariableSymbolExhausted = "PAYMENT_VARIABLE_SYMBOL_EXHAUSTED";

    /// <summary>The first billing period starts in the past or too far ahead.</summary>
    public const string PaymentStartDateInvalid = "PAYMENT_START_DATE_INVALID";

    // ── Client nutrition day log ────────────────────────────────────────
    /// <summary>The day log changed between the read and the write; retry.</summary>
    public const string DayLogVersionConflict = "DAY_LOG_VERSION_CONFLICT";

    // ── Questionnaire answers ───────────────────────────────────────────
    /// <summary>An answer does not fit its question's type or config.</summary>
    public const string QuestionnaireAnswerInvalid = "QUESTIONNAIRE_ANSWER_INVALID";

    /// <summary>The coach tried to mark a response as seen before the client submitted it.</summary>
    public const string QuestionnaireResponseNotSubmitted = "QUESTIONNAIRE_RESPONSE_NOT_SUBMITTED";

    /// <summary>An answered question id is not a visible, answerable question of the response's questionnaire.</summary>
    public const string QuestionnaireQuestionNotInForm = "QUESTIONNAIRE_QUESTION_NOT_IN_FORM";

    /// <summary>A required, visible question has no answer at submit time.</summary>
    public const string QuestionnaireRequiredAnswerMissing = "QUESTIONNAIRE_REQUIRED_ANSWER_MISSING";

    /// <summary>The item is not in the plan meal, or the meal is not in the caller's plan.</summary>
    public const string MealItemNotFound = "MEAL_ITEM_NOT_FOUND";

    // ── Offline sync ────────────────────────────────────────────────────
    /// <summary>The client-reported occurrence time is more than 5 minutes ahead of the server.</summary>
    public const string OccurredAtInFuture = "OCCURRED_AT_IN_FUTURE";

    /// <summary>The client-reported occurrence time is older than the 7-day offline window.</summary>
    public const string OccurredAtTooOld = "OCCURRED_AT_TOO_OLD";

    // ── Onboarding form choice ──────────────────────────────────────────
    /// <summary>The request skips the onboarding form and also names a questionnaire.</summary>
    public const string SkipOnboardingFormWithQuestionnaire = "SKIP_ONBOARDING_FORM_WITH_QUESTIONNAIRE";
}
