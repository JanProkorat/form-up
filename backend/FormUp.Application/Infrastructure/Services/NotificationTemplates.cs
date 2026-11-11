using FormUp.Application.Domain.Enums;

namespace FormUp.Application.Infrastructure.Services;

/// <summary>
/// Per-type, per-language title/body templates for <see cref="Domain.Entities.Notification"/>.
/// Mirrors the <see cref="Domain.Documents.LocalizedNames"/> pattern already used for
/// exercise/food names — a small in-code lookup table rather than a resource file, since
/// notification copy has runtime interpolation values.
///
/// Some <see cref="NotificationType"/> values cover more than one distinct wording
/// (e.g. <see cref="NotificationType.PlanPublished"/> is raised for "plan completed" AND
/// "week published", with different copy) — those pass a non-null <c>variant</c> key.
/// Every other type uses <c>variant: null</c>.
///
/// Fallback language is "en" per #788 (approved by the orchestrator): a null/unrecognized
/// stored <see cref="Domain.Entities.ApplicationUser.Language"/> resolves to English.
/// </summary>
public static class NotificationTemplates
{
    /// <summary>
    /// A resolved, but not-yet-interpolated, title/body pair for one type+variant+language.
    /// </summary>
    private sealed record Template(string Title, string Body);

    private const string FallbackLanguage = "en";

    // Variant keys — only needed for NotificationTypes with more than one distinct wording.
    public const string PlanPublishedNutritionCompleted = "nutritionCompleted";
    public const string PlanPublishedNutritionPublished = "nutritionPublished";

    // Push/notification data keys that let a client tell a nutrition plan from a training plan.
    public const string PlanKindKey = "planKind";
    public const string PlanIdKey = "planId";
    public const string PlanKindNutrition = "nutrition";
    public const string PlanPublishedTrainingCompleted = "trainingCompleted";
    public const string PlanPublishedTrainingPublished = "trainingPublished";
    public const string InvitationCancelledByProfessional = "cancelledByProfessional";
    public const string InvitationCancelledRevokedByClient = "revokedByClient";
    public const string InvitationCancelledAutoBySibling = "autoCancelledBySibling";
    public const string QuestionnaireAssignedRevoked = "revoked";
    public const string MeasurementRequestedWithMessage = "withMessage";
    public const string CoachPlanNoDate = "noDate";

    // language -> "Type" or "Type:variant" -> Template
    private static readonly Dictionary<string, Dictionary<string, Template>> Templates = new()
    {
        ["en"] = new()
        {
            [Key(NotificationType.PersonalRecord)] =
                new("New Personal Record!", "{summary}"),

            [Key(NotificationType.PlanPublished, PlanPublishedNutritionCompleted)] =
                new("Nutrition plan completed", "Your nutrition plan \"{planName}\" has been marked as completed."),
            [Key(NotificationType.PlanPublished, PlanPublishedNutritionPublished)] =
                new("Nutrition plan updated", "Week {weekNumber} of your nutrition plan has been published."),
            [Key(NotificationType.PlanPublished, PlanPublishedTrainingCompleted)] =
                new("Training plan completed", "Your training plan \"{planName}\" has been marked as completed."),
            [Key(NotificationType.PlanPublished, PlanPublishedTrainingPublished)] =
                new("Training plan updated", "Week {weekNumber} of your training plan has been published."),

            [Key(NotificationType.General)] =
                new("Collaboration ended", "{clientName} ended the collaboration with you."),

            [Key(NotificationType.QuestionnaireSubmitted)] =
                new("Questionnaire submitted", "{clientName} filled in the intake questionnaire."),

            [Key(NotificationType.ClientRequestReceived)] =
                new("New client request", "{clientName} wants to work with you"),
            [Key(NotificationType.ClientRequestAccepted)] =
                new("Invitation accepted", "{clientName} accepted your invitation."),
            [Key(NotificationType.ClientRequestRejected)] =
                new("Request declined", "{profName} declined your request."),

            [Key(NotificationType.QuestionnaireAssigned)] =
                new("Questionnaire assigned", "You have been assigned a questionnaire: {questionnaireTitle}"),
            [Key(NotificationType.QuestionnaireAssigned, QuestionnaireAssignedRevoked)] =
                new("Questionnaire revoked", "{profName} has revoked your questionnaire."),

            [Key(NotificationType.InvitationReceived)] =
                new("New invitation", "{trainerName} invited you to join as their client."),
            [Key(NotificationType.InvitationAccepted)] =
                new("Invitation accepted", "{profName} accepted your invitation."),
            [Key(NotificationType.InvitationDeclined)] =
                new("Invitation declined", "{profName} declined your invitation."),

            [Key(NotificationType.InvitationCancelled, InvitationCancelledByProfessional)] =
                new("Invitation cancelled", "{trainerName} has cancelled their invitation."),
            [Key(NotificationType.InvitationCancelled, InvitationCancelledRevokedByClient)] =
                new("Invitation revoked", "{clientName} revoked their invitation."),
            [Key(NotificationType.InvitationCancelled, InvitationCancelledAutoBySibling)] =
                new("Invitation cancelled", "{clientName} accepted another {role}, so your invitation was cancelled."),

            // WeeklyCheckInRequested and WeeklyCheckInResponded have no sender any more; check-in reminders
            // and missed-week notices go out as ClientCheckInReminder / CoachCheckInMissed from
            // AutomationCheckInSweeper. PhotoDiaryReminderScheduler still constructs its Notification
            // entities directly instead of calling INotificationService.CreateAsync (#788 follow-up).
            [Key(NotificationType.WeeklyCheckInRequested)] =
                new("Planning next week", "{professionalName} is planning next week. Let them know if anything special is coming up."),
            [Key(NotificationType.WeeklyCheckInResponded)] =
                new("Client responded to check-in", "A client has responded to their weekly check-in reminder."),
            [Key(NotificationType.PhotoDiaryReminder)] =
                new("Don't forget your diary photo", "Today is day {dayIndex} of {durationDays}. Take a photo of what you eat — your nutritionist will see it live."),

            [Key(NotificationType.CoachRoleRemoved)] =
                new("Coach role removed", "{coachName} removed a coach role from their account. Your current plan stays available until it ends. A collaboration that only covered that role ends afterwards."),
            [Key(NotificationType.CollaborationEndedByRoleRemoval)] =
                new("Collaboration ended", "Your collaboration with {coachName} ended because they removed the coach role it covered."),
            [Key(NotificationType.CollaborationEndedByRoleRemovalCoach)] =
                new("Collaboration ended", "Your collaboration with {clientName} ended because you removed the coach role it covered."),

            [Key(NotificationType.MeasurementRequested)] =
                new("Please log your measurements", "{professionalName} asked you to log your body measurements."),
            [Key(NotificationType.MeasurementRequested, MeasurementRequestedWithMessage)] =
                new("Please log your measurements", "{professionalName} asked you to log your body measurements: {message}"),

            [Key(NotificationType.OnboardingAnswersUpdated)] =
                new("Onboarding answers updated", "{clientName} updated their onboarding answers: {fields}. You may want to review their targets."),

            [Key(NotificationType.PaymentRequested)] =
                new("Payment request", "{coachName} asks for {amount}, due {dueDate}."),

            [Key(NotificationType.OfflineSyncConflict)] =
                new("Offline changes need a look", "{clientName} synced {count} offline action(s) that no longer match the plan \"{planName}\"."),

            [Key(NotificationType.ClientCheckInReminder)] =
                new("Weekly check-in reminder", "{professionalName} is still waiting for your weekly check-in. It only takes a couple of minutes."),
            [Key(NotificationType.CoachCheckInMissed)] =
                new("Check-in missed", "{clientName} did not answer the weekly check-in in time."),
            [Key(NotificationType.ClientRequestPackageProposed)] =
                new("Package proposal", "{profName} proposed a package for your collaboration."),
            [Key(NotificationType.ClientRequestProposalAccepted)] =
                new("Proposal accepted", "{clientName} accepted your package proposal."),
            [Key(NotificationType.ClientRequestProposalDeclined)] =
                new("Proposal declined", "{clientName} declined your package proposal."),
            [Key(NotificationType.ClientRequestProposalWithdrawn)] =
                new("Proposal withdrawn", "{profName} withdrew the package proposal."),

            [Key(NotificationType.NewMessage)] =
                new("New message", "New message from {clientName}."),
            [Key(NotificationType.ProgressPhotosSubmitted)] =
                new("Progress photos submitted", "The photos from {clientName} are ready to view."),
            [Key(NotificationType.WorkoutFinished)] =
                new("Workout finished", "{clientName} · {sessionName}"),

            [Key(NotificationType.CoachPlanChanged)] =
                new("Form Up changed your plan to {planName}", "Until {date}."),
            [Key(NotificationType.CoachPlanChanged, CoachPlanNoDate)] =
                new("Form Up changed your plan to {planName}", string.Empty),
            [Key(NotificationType.CoachPlanEnded)] =
                new("Form Up ended your {planName} plan", "You're on Free now."),
            [Key(NotificationType.CoachPlanRenewalCancelled)] =
                new("Your {planName} plan won't renew", "It runs until {date}."),
            [Key(NotificationType.CoachPlanRenewalCancelled, CoachPlanNoDate)] =
                new("Your {planName} plan won't renew", string.Empty),
        },
        ["cs"] = new()
        {
            [Key(NotificationType.PersonalRecord)] =
                new("Nový osobní rekord!", "{summary}"),

            [Key(NotificationType.PlanPublished, PlanPublishedNutritionCompleted)] =
                new("Jídelníček dokončen", "Váš jídelníček \"{planName}\" byl označen jako dokončený."),
            [Key(NotificationType.PlanPublished, PlanPublishedNutritionPublished)] =
                new("Jídelníček aktualizován", "Týden {weekNumber} vašeho jídelníčku byl zveřejněn."),
            [Key(NotificationType.PlanPublished, PlanPublishedTrainingCompleted)] =
                new("Tréninkový plán dokončen", "Váš tréninkový plán \"{planName}\" byl označen jako dokončený."),
            [Key(NotificationType.PlanPublished, PlanPublishedTrainingPublished)] =
                new("Tréninkový plán aktualizován", "Týden {weekNumber} vašeho tréninkového plánu byl zveřejněn."),

            [Key(NotificationType.General)] =
                new("Spolupráce ukončena", "{clientName} s vámi ukončil(a) spolupráci."),

            [Key(NotificationType.QuestionnaireSubmitted)] =
                new("Dotazník vyplněn", "{clientName} vyplnil(a) vstupní dotazník."),

            [Key(NotificationType.ClientRequestReceived)] =
                new("Nová žádost klienta", "{clientName} s vámi chce spolupracovat"),
            [Key(NotificationType.ClientRequestAccepted)] =
                new("Pozvánka přijata", "{clientName} přijal(a) vaši pozvánku."),
            [Key(NotificationType.ClientRequestRejected)] =
                new("Žádost zamítnuta", "{profName} zamítl(a) vaši žádost."),

            [Key(NotificationType.QuestionnaireAssigned)] =
                new("Dotazník přiřazen", "Byl vám přiřazen dotazník: {questionnaireTitle}"),
            [Key(NotificationType.QuestionnaireAssigned, QuestionnaireAssignedRevoked)] =
                new("Dotazník zrušen", "{profName} zrušil(a) váš dotazník."),

            [Key(NotificationType.InvitationReceived)] =
                new("Nová pozvánka", "{trainerName} vás pozval(a), abyste se stal(a) jeho/její klient(kou)."),
            [Key(NotificationType.InvitationAccepted)] =
                new("Pozvánka přijata", "{profName} přijal(a) vaši pozvánku."),
            [Key(NotificationType.InvitationDeclined)] =
                new("Pozvánka odmítnuta", "{profName} odmítl(a) vaši pozvánku."),

            [Key(NotificationType.InvitationCancelled, InvitationCancelledByProfessional)] =
                new("Pozvánka zrušena", "{trainerName} zrušil(a) svou pozvánku."),
            [Key(NotificationType.InvitationCancelled, InvitationCancelledRevokedByClient)] =
                new("Pozvánka odvolána", "{clientName} odvolal(a) svou pozvánku."),
            [Key(NotificationType.InvitationCancelled, InvitationCancelledAutoBySibling)] =
                new("Pozvánka zrušena", "{clientName} přijal(a) jiného/jinou {role}, takže vaše pozvánka byla zrušena."),

            [Key(NotificationType.WeeklyCheckInRequested)] =
                new("Plánování dalšího týdne", "{professionalName} plánuje další týden. Dejte mu/jí vědět, pokud se něco chystá."),
            [Key(NotificationType.WeeklyCheckInResponded)] =
                new("Klient odpověděl na check-in", "Klient odpověděl na týdenní připomenutí check-inu."),
            [Key(NotificationType.PhotoDiaryReminder)] =
                new("Nezapomeňte na fotku deníku", "Dnes je den {dayIndex} z {durationDays}. Vyfoťte si, co jíte — váš výživový poradce to uvidí živě."),

            [Key(NotificationType.CoachRoleRemoved)] =
                new("Role odebrána", "{coachName} odebral(a) ze svého účtu jednu ze svých rolí (trenér, nutriční poradce). Váš aktuální plán zůstane dostupný, dokud neskončí. Spolupráce, která tuto roli pokrývala, poté skončí."),
            [Key(NotificationType.CollaborationEndedByRoleRemoval)] =
                new("Spolupráce ukončena", "Vaše spolupráce s uživatelem {coachName} skončila, protože odebral(a) roli trenéra nebo nutričního poradce, kterou pokrývala."),
            [Key(NotificationType.CollaborationEndedByRoleRemovalCoach)] =
                new("Spolupráce ukončena", "Vaše spolupráce s klientem {clientName} skončila, protože jste odebral(a) roli trenéra nebo nutričního poradce, kterou pokrývala."),

            [Key(NotificationType.MeasurementRequested)] =
                new("Zapište si prosím míry", "{professionalName} vás žádá o zapsání tělesných měr."),
            [Key(NotificationType.MeasurementRequested, MeasurementRequestedWithMessage)] =
                new("Zapište si prosím míry", "{professionalName} vás žádá o zapsání tělesných měr: {message}"),

            [Key(NotificationType.OnboardingAnswersUpdated)] =
                new("Odpovědi z dotazníku upraveny", "{clientName} upravil(a) své odpovědi ve vstupním dotazníku: {fields}. Zvažte kontrolu cílů klienta."),

            [Key(NotificationType.PaymentRequested)] =
                new("Žádost o platbu", "{coachName} vás žádá o platbu {amount} se splatností {dueDate}."),

            [Key(NotificationType.OfflineSyncConflict)] =
                new("Offline změny ke kontrole", "{clientName} nahrál(a) offline akce ({count}), které už neodpovídají jídelníčku \"{planName}\". Podívejte se na ně."),

            [Key(NotificationType.ClientCheckInReminder)] =
                new("Připomenutí týdenního check-inu", "{professionalName} stále čeká na váš týdenní check-in. Zabere jen pár minut."),
            [Key(NotificationType.CoachCheckInMissed)] =
                new("Check-in zmeškán", "{clientName} neodpověděl(a) včas na týdenní check-in."),
            [Key(NotificationType.ClientRequestPackageProposed)] =
                new("Návrh balíčku", "{profName} vám navrhl(a) balíček pro vaši spolupráci."),
            [Key(NotificationType.ClientRequestProposalAccepted)] =
                new("Návrh přijat", "{clientName} přijal(a) váš návrh balíčku."),
            [Key(NotificationType.ClientRequestProposalDeclined)] =
                new("Návrh odmítnut", "{clientName} odmítl(a) váš návrh balíčku."),
            [Key(NotificationType.ClientRequestProposalWithdrawn)] =
                new("Návrh stažen", "{profName} stáhl(a) návrh balíčku."),

            [Key(NotificationType.NewMessage)] =
                new("Nová zpráva", "Nová zpráva od {clientName}."),
            [Key(NotificationType.ProgressPhotosSubmitted)] =
                new("Odeslané fotky pokroku", "Fotky od {clientName} jsou připravené k zobrazení."),
            [Key(NotificationType.WorkoutFinished)] =
                new("Dokončený trénink", "{clientName} · {sessionName}"),

            [Key(NotificationType.CoachPlanChanged)] =
                new("Form Up změnil váš tarif na {planName}", "Do {date}."),
            [Key(NotificationType.CoachPlanChanged, CoachPlanNoDate)] =
                new("Form Up změnil váš tarif na {planName}", string.Empty),
            [Key(NotificationType.CoachPlanEnded)] =
                new("Form Up ukončil váš tarif {planName}", "Nyní jste na tarifu Free."),
            [Key(NotificationType.CoachPlanRenewalCancelled)] =
                new("Váš tarif {planName} se neprodlouží", "Platí do {date}."),
            [Key(NotificationType.CoachPlanRenewalCancelled, CoachPlanNoDate)] =
                new("Váš tarif {planName} se neprodlouží", string.Empty),
        },
        ["de"] = new()
        {
            [Key(NotificationType.PersonalRecord)] =
                new("Neuer persönlicher Rekord!", "{summary}"),

            [Key(NotificationType.PlanPublished, PlanPublishedNutritionCompleted)] =
                new("Ernährungsplan abgeschlossen", "Ihr Ernährungsplan \"{planName}\" wurde als abgeschlossen markiert."),
            [Key(NotificationType.PlanPublished, PlanPublishedNutritionPublished)] =
                new("Ernährungsplan aktualisiert", "Woche {weekNumber} Ihres Ernährungsplans wurde veröffentlicht."),
            [Key(NotificationType.PlanPublished, PlanPublishedTrainingCompleted)] =
                new("Trainingsplan abgeschlossen", "Ihr Trainingsplan \"{planName}\" wurde als abgeschlossen markiert."),
            [Key(NotificationType.PlanPublished, PlanPublishedTrainingPublished)] =
                new("Trainingsplan aktualisiert", "Woche {weekNumber} Ihres Trainingsplans wurde veröffentlicht."),

            [Key(NotificationType.General)] =
                new("Zusammenarbeit beendet", "{clientName} hat die Zusammenarbeit mit Ihnen beendet."),

            [Key(NotificationType.QuestionnaireSubmitted)] =
                new("Fragebogen ausgefüllt", "{clientName} hat den Aufnahmefragebogen ausgefüllt."),

            [Key(NotificationType.ClientRequestReceived)] =
                new("Neue Klientenanfrage", "{clientName} möchte mit Ihnen zusammenarbeiten"),
            [Key(NotificationType.ClientRequestAccepted)] =
                new("Einladung angenommen", "{clientName} hat Ihre Einladung angenommen."),
            [Key(NotificationType.ClientRequestRejected)] =
                new("Anfrage abgelehnt", "{profName} hat Ihre Anfrage abgelehnt."),

            [Key(NotificationType.QuestionnaireAssigned)] =
                new("Fragebogen zugewiesen", "Ihnen wurde ein Fragebogen zugewiesen: {questionnaireTitle}"),
            [Key(NotificationType.QuestionnaireAssigned, QuestionnaireAssignedRevoked)] =
                new("Fragebogen storniert", "{profName} hat Ihren Fragebogen storniert."),

            [Key(NotificationType.InvitationReceived)] =
                new("Neue Einladung", "{trainerName} hat Sie eingeladen, sein/ihr Klient zu werden."),
            [Key(NotificationType.InvitationAccepted)] =
                new("Einladung angenommen", "{profName} hat Ihre Einladung angenommen."),
            [Key(NotificationType.InvitationDeclined)] =
                new("Einladung abgelehnt", "{profName} hat Ihre Einladung abgelehnt."),

            [Key(NotificationType.InvitationCancelled, InvitationCancelledByProfessional)] =
                new("Einladung storniert", "{trainerName} hat seine/ihre Einladung storniert."),
            [Key(NotificationType.InvitationCancelled, InvitationCancelledRevokedByClient)] =
                new("Einladung zurückgezogen", "{clientName} hat die Einladung zurückgezogen."),
            [Key(NotificationType.InvitationCancelled, InvitationCancelledAutoBySibling)] =
                new("Einladung storniert", "{clientName} hat eine andere {role} angenommen, daher wurde Ihre Einladung storniert."),

            [Key(NotificationType.WeeklyCheckInRequested)] =
                new("Planung der nächsten Woche", "{professionalName} plant die nächste Woche. Sagen Sie ihm/ihr Bescheid, falls etwas Besonderes ansteht."),
            [Key(NotificationType.WeeklyCheckInResponded)] =
                new("Klient hat auf Check-in geantwortet", "Ein Klient hat auf die wöchentliche Check-in-Erinnerung geantwortet."),
            [Key(NotificationType.PhotoDiaryReminder)] =
                new("Vergessen Sie Ihr Tagebuchfoto nicht", "Heute ist Tag {dayIndex} von {durationDays}. Machen Sie ein Foto von dem, was Sie essen — Ihr Ernährungsberater sieht es live."),

            [Key(NotificationType.CoachRoleRemoved)] =
                new("Rolle entfernt", "{coachName} hat eine Coach-Rolle aus dem eigenen Konto entfernt. Ihr aktueller Plan bleibt verfügbar, bis er endet. Eine Zusammenarbeit, die nur diese Rolle abdeckte, endet danach."),
            [Key(NotificationType.CollaborationEndedByRoleRemoval)] =
                new("Zusammenarbeit beendet", "Ihre Zusammenarbeit mit {coachName} wurde beendet, weil die abgedeckte Coach-Rolle entfernt wurde."),
            [Key(NotificationType.CollaborationEndedByRoleRemovalCoach)] =
                new("Zusammenarbeit beendet", "Ihre Zusammenarbeit mit {clientName} wurde beendet, weil Sie die abgedeckte Coach-Rolle entfernt haben."),

            [Key(NotificationType.MeasurementRequested)] =
                new("Bitte Maße eintragen", "{professionalName} bittet Sie, Ihre Körpermaße einzutragen."),
            [Key(NotificationType.MeasurementRequested, MeasurementRequestedWithMessage)] =
                new("Bitte Maße eintragen", "{professionalName} bittet Sie, Ihre Körpermaße einzutragen: {message}"),

            [Key(NotificationType.OnboardingAnswersUpdated)] =
                new("Fragebogenantworten aktualisiert", "{clientName} hat die Antworten im Aufnahmefragebogen geändert: {fields}. Prüfen Sie ggf. die Ziele des Klienten."),

            [Key(NotificationType.PaymentRequested)] =
                new("Zahlungsaufforderung", "{coachName} bittet um {amount}, fällig am {dueDate}."),

            [Key(NotificationType.OfflineSyncConflict)] =
                new("Offline-Änderungen prüfen", "{clientName} hat {count} Offline-Aktion(en) synchronisiert, die nicht mehr zum Plan \"{planName}\" passen. Bitte prüfen Sie diese."),

            [Key(NotificationType.ClientCheckInReminder)] =
                new("Erinnerung an Ihren wöchentlichen Check-in", "{professionalName} wartet noch auf Ihren wöchentlichen Check-in. Er dauert nur wenige Minuten."),
            [Key(NotificationType.CoachCheckInMissed)] =
                new("Check-in verpasst", "{clientName} hat den wöchentlichen Check-in nicht rechtzeitig beantwortet."),
            [Key(NotificationType.ClientRequestPackageProposed)] =
                new("Paketvorschlag", "{profName} hat Ihnen ein Paket für Ihre Zusammenarbeit vorgeschlagen."),
            [Key(NotificationType.ClientRequestProposalAccepted)] =
                new("Vorschlag angenommen", "{clientName} hat Ihren Paketvorschlag angenommen."),
            [Key(NotificationType.ClientRequestProposalDeclined)] =
                new("Vorschlag abgelehnt", "{clientName} hat Ihren Paketvorschlag abgelehnt."),
            [Key(NotificationType.ClientRequestProposalWithdrawn)] =
                new("Vorschlag zurückgezogen", "{profName} hat den Paketvorschlag zurückgezogen."),

            [Key(NotificationType.NewMessage)] =
                new("Neue Nachricht", "Neue Nachricht von {clientName}."),
            [Key(NotificationType.ProgressPhotosSubmitted)] =
                new("Fortschrittsfotos eingereicht", "Die Fotos von {clientName} liegen zur Ansicht bereit."),
            [Key(NotificationType.WorkoutFinished)] =
                new("Training beendet", "{clientName} · {sessionName}"),

            [Key(NotificationType.CoachPlanChanged)] =
                new("Form Up hat Ihren Tarif auf {planName} umgestellt", "Bis {date}."),
            [Key(NotificationType.CoachPlanChanged, CoachPlanNoDate)] =
                new("Form Up hat Ihren Tarif auf {planName} umgestellt", string.Empty),
            [Key(NotificationType.CoachPlanEnded)] =
                new("Form Up hat Ihren Tarif {planName} beendet", "Sie nutzen jetzt Free."),
            [Key(NotificationType.CoachPlanRenewalCancelled)] =
                new("Ihr Tarif {planName} wird nicht verlängert", "Er läuft bis {date}."),
            [Key(NotificationType.CoachPlanRenewalCancelled, CoachPlanNoDate)] =
                new("Ihr Tarif {planName} wird nicht verlängert", string.Empty),
        },
    };

    // Machine keys of the editable onboarding answers (also the changedFields values).
    public const string OnboardingFieldPrimaryGoal = "primaryGoal";
    public const string OnboardingFieldTargetWeightKg = "targetWeightKg";
    public const string OnboardingFieldDesiredTrainingFrequency = "desiredTrainingFrequency";
    public const string OnboardingFieldGymAccess = "gymAccess";
    public const string OnboardingFieldInjuries = "injuries";
    public const string OnboardingFieldDietaryStyle = "dietaryStyle";
    public const string OnboardingFieldAllergies = "allergies";
    public const string OnboardingFieldDislikes = "dislikes";
    public const string OnboardingFieldDateOfBirth = "dateOfBirth";
    public const string OnboardingFieldSex = "sex";
    public const string OnboardingFieldHeightCm = "heightCm";
    public const string OnboardingFieldJobType = "jobType";
    public const string OnboardingFieldSleepHours = "sleepHours";
    public const string OnboardingFieldStressLevel = "stressLevel";
    public const string OnboardingFieldCurrentTrainingFrequency = "currentTrainingFrequency";
    public const string OnboardingFieldPreferredActivities = "preferredActivities";
    public const string OnboardingFieldMealsPerDay = "mealsPerDay";
    public const string OnboardingFieldHabits = "habits";

    // language -> onboarding field key -> label
    private static readonly Dictionary<string, Dictionary<string, string>> OnboardingFieldLabels = new()
    {
        ["en"] = new()
        {
            [OnboardingFieldPrimaryGoal] = "goal",
            [OnboardingFieldTargetWeightKg] = "target weight",
            [OnboardingFieldDesiredTrainingFrequency] = "desired training frequency",
            [OnboardingFieldGymAccess] = "gym access",
            [OnboardingFieldInjuries] = "injuries",
            [OnboardingFieldDietaryStyle] = "dietary style",
            [OnboardingFieldAllergies] = "allergies",
            [OnboardingFieldDislikes] = "disliked foods",
            [OnboardingFieldDateOfBirth] = "date of birth",
            [OnboardingFieldSex] = "sex",
            [OnboardingFieldHeightCm] = "height",
            [OnboardingFieldJobType] = "job type",
            [OnboardingFieldSleepHours] = "sleep hours",
            [OnboardingFieldStressLevel] = "stress level",
            [OnboardingFieldCurrentTrainingFrequency] = "current training frequency",
            [OnboardingFieldPreferredActivities] = "preferred activities",
            [OnboardingFieldMealsPerDay] = "meals per day",
            [OnboardingFieldHabits] = "eating habits",
        },
        ["cs"] = new()
        {
            [OnboardingFieldPrimaryGoal] = "cíl",
            [OnboardingFieldTargetWeightKg] = "cílová váha",
            [OnboardingFieldDesiredTrainingFrequency] = "požadovaná frekvence tréninku",
            [OnboardingFieldGymAccess] = "přístup do posilovny",
            [OnboardingFieldInjuries] = "zranění",
            [OnboardingFieldDietaryStyle] = "styl stravování",
            [OnboardingFieldAllergies] = "alergie",
            [OnboardingFieldDislikes] = "nechuti k jídlu",
            [OnboardingFieldDateOfBirth] = "datum narození",
            [OnboardingFieldSex] = "pohlaví",
            [OnboardingFieldHeightCm] = "výška",
            [OnboardingFieldJobType] = "typ zaměstnání",
            [OnboardingFieldSleepHours] = "hodiny spánku",
            [OnboardingFieldStressLevel] = "úroveň stresu",
            [OnboardingFieldCurrentTrainingFrequency] = "současná frekvence tréninku",
            [OnboardingFieldPreferredActivities] = "oblíbené aktivity",
            [OnboardingFieldMealsPerDay] = "počet jídel denně",
            [OnboardingFieldHabits] = "stravovací návyky",
        },
        ["de"] = new()
        {
            [OnboardingFieldPrimaryGoal] = "Ziel",
            [OnboardingFieldTargetWeightKg] = "Zielgewicht",
            [OnboardingFieldDesiredTrainingFrequency] = "gewünschte Trainingshäufigkeit",
            [OnboardingFieldGymAccess] = "Zugang zum Fitnessstudio",
            [OnboardingFieldInjuries] = "Verletzungen",
            [OnboardingFieldDietaryStyle] = "Ernährungsstil",
            [OnboardingFieldAllergies] = "Allergien",
            [OnboardingFieldDislikes] = "Lebensmittelabneigungen",
            [OnboardingFieldDateOfBirth] = "Geburtsdatum",
            [OnboardingFieldSex] = "Geschlecht",
            [OnboardingFieldHeightCm] = "Körpergröße",
            [OnboardingFieldJobType] = "Berufsart",
            [OnboardingFieldSleepHours] = "Schlafstunden",
            [OnboardingFieldStressLevel] = "Stresslevel",
            [OnboardingFieldCurrentTrainingFrequency] = "aktuelle Trainingshäufigkeit",
            [OnboardingFieldPreferredActivities] = "bevorzugte Aktivitäten",
            [OnboardingFieldMealsPerDay] = "Mahlzeiten pro Tag",
            [OnboardingFieldHabits] = "Essgewohnheiten",
        },
    };

    /// <summary>
    /// Joins the localized labels of the given onboarding field keys for one recipient language.
    /// </summary>
    /// <param name="fieldKeys">Keys from the <c>OnboardingField*</c> constants.</param>
    /// <param name="language">Recipient language; null or unknown falls back to English.</param>
    public static string LocalizeOnboardingFields(IEnumerable<string> fieldKeys, string? language)
    {
        var labels = OnboardingFieldLabels[Normalize(language)];

        return string.Join(", ", fieldKeys.Select(key => labels.TryGetValue(key, out var label) ? label : key));
    }

    /// <summary>
    /// Safe last-resort pair returned when a (type, variant) combination is missing from
    /// BOTH the requested language and the English fallback table — a coding bug (a new
    /// call site added a variant without a matching template entry), not a runtime
    /// condition callers can prevent. Degrades to a generic string instead of throwing
    /// <see cref="KeyNotFoundException"/> / 500ing the caller (pass-1 review finding,
    /// #788 — with more call sites now resolving through here, a missing key must not
    /// take down the request/tick that triggered it).
    /// </summary>
    private static readonly Template SafeDefault = new("Notification", "You have a new notification.");

    /// <summary>
    /// Resolves the localized title/body for a notification, interpolating
    /// <paramref name="parameters"/> values into <c>{key}</c> placeholders.
    /// </summary>
    /// <param name="type">The notification type.</param>
    /// <param name="language">
    /// The recipient's stored <see cref="Domain.Entities.ApplicationUser.Language"/>.
    /// Null or unrecognized falls back to <see cref="FallbackLanguage"/> ("en", #788).
    /// </param>
    /// <param name="parameters">Interpolation values, keyed by placeholder name (no braces).</param>
    /// <param name="variant">
    /// Distinguishes multiple wordings under the same <paramref name="type"/> — see the
    /// <c>*Variant</c>/named constants on this class. Null for types with a single wording.
    /// </param>
    public static (string Title, string Body) Resolve(
        NotificationType type,
        string? language,
        IReadOnlyDictionary<string, string>? parameters = null,
        string? variant = null)
    {
        var normalizedLanguage = Normalize(language);
        var key = Key(type, variant);

        if (!Templates.TryGetValue(normalizedLanguage, out var languageTemplates))
            languageTemplates = Templates[FallbackLanguage];

        if (!languageTemplates.TryGetValue(key, out var template)
            && !Templates[FallbackLanguage].TryGetValue(key, out template))
        {
            template = SafeDefault;
        }

        return (Interpolate(template.Title, parameters), Interpolate(template.Body, parameters));
    }

    private static string Key(NotificationType type, string? variant = null) =>
        variant is null ? type.ToString() : $"{type}:{variant}";

    private static string Normalize(string? language) =>
        language?.ToLowerInvariant() switch
        {
            "cs" => "cs",
            "de" => "de",
            "en" => "en",
            _ => FallbackLanguage,
        };

    private static string Interpolate(string template, IReadOnlyDictionary<string, string>? parameters)
    {
        if (parameters is null || parameters.Count == 0) return template;

        var result = template;
        foreach (var (paramKey, value) in parameters)
        {
            result = result.Replace("{" + paramKey + "}", value, StringComparison.Ordinal);
        }
        return result;
    }
}
