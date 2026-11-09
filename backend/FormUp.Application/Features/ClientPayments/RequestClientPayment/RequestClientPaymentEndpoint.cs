using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Authorization;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
using FormUp.Application.Features.ClientPayments.Shared;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.ClientPayments.RequestClientPayment;

/// <summary>
/// Asks a client to pay: freezes the payment's amount and the coach's bank details, then tells the client by
/// email (with a QR Platba code), by app notification, or both. A failed email or notification never undoes
/// the request.
/// </summary>
/// <param name="db">Application database context.</param>
/// <param name="entitlementService">Resolves subscription entitlements.</param>
/// <param name="linkAuthorizationService">Resolves the caller's live link to the client.</param>
/// <param name="audit">Audit logging service.</param>
/// <param name="emailService">Sends the request email.</param>
/// <param name="notificationService">Creates the in-app notification.</param>
/// <param name="realtimeNotifier">Pushes the new notification to the client's open sessions.</param>
/// <param name="timeProvider">Clock.</param>
/// <param name="logger">Logger for a failed email or notification.</param>
[RemovedCoachRole(RemovedCoachRoleMode.Refuse)]
internal sealed class RequestClientPaymentEndpoint(
    IApplicationDbContext db,
    EntitlementService entitlementService,
    IClientLinkAuthorizationService linkAuthorizationService,
    IAuditService audit,
    IEmailService emailService,
    INotificationService notificationService,
    IRealtimeNotifier realtimeNotifier,
    TimeProvider timeProvider,
    ILogger<RequestClientPaymentEndpoint> logger)
    : Endpoint<RequestClientPaymentRequest, RequestClientPaymentResponse>
{
    private const int MaxRequestsPerClientPerHour = 10;
    private static readonly TimeSpan SamePaymentCooldown = TimeSpan.FromSeconds(60);

    /// <inheritdoc />
    public override void Configure()
    {
        Post("/payments/clients/{ClientId}/payments/{PaymentId}/request");
        Roles(AppRoles.Trainer, AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "Request a payment";
            s.Description = "Freezes the payment's amount and the coach's bank details and asks the client to pay, by email with a QR code, by app notification, or both. A failed email returns 200 with emailSent false.";
            s.Response<RequestClientPaymentResponse>(StatusCodes.Status200OK, "The requested payment and how the request was delivered");
            s.Response<ProblemDetails>(StatusCodes.Status400BadRequest, "Neither channel chosen (PAYMENT_REQUEST_CHANNEL_REQUIRED), an amount outside 0 to 9999999.99, or a due date in the past");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or unreadable caller claim");
            s.Response(StatusCodes.Status403Forbidden, "The subscription does not include payments (PAYMENTS_NOT_ENTITLED), or the coach role was removed");
            s.Response(StatusCodes.Status404NotFound, "No live link to the client (and no ended one with a payment assignment), or the payment is not this client's payment with this coach");
            s.Response(StatusCodes.Status409Conflict, "The payment is already paid (PAYMENT_ALREADY_PAID) or waived (PAYMENT_ALREADY_WAIVED), the coach has no bank account (BANK_ACCOUNT_NOT_SET), or no variable symbol is left (PAYMENT_VARIABLE_SYMBOL_EXHAUSTED)");
            s.Response(StatusCodes.Status429TooManyRequests, "The payment was requested a minute ago, or the client got ten requests in the last hour (PAYMENT_REQUEST_RATE_LIMITED)");
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(RequestClientPaymentRequest req, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue(AppClaims.UserId), out var userId))
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var context = await this.LoadCoachClientContextOrRespondAsync(
            db, entitlementService, linkAuthorizationService, userId, req.ClientId, ct, allowEnded: true);

        if (context is null)
        {
            return;
        }

        var settings = await db.CoachPaymentSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.ProfessionalProfileId == context.ProfessionalProfileId, ct);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var today = ClientPaymentSchedule.Today(now);

        var payment = await StoreRequestAsync(req, context, settings, userId, now, today, ct);

        if (payment is null)
        {
            return;
        }

        // The client is told only once the request is committed.
        var emailSent = req.SendEmail && await TrySendEmailAsync(payment, context, ct);
        var notificationSent = req.SendNotification && await TrySendNotificationAsync(payment, context, ct);

        await Send.OkAsync(
            new RequestClientPaymentResponse
            {
                Payment = ClientPaymentRowDto.FromFrozenEntity(payment, today),
                EmailSent = emailSent,
                NotificationSent = notificationSent
            },
            ct);
    }

    /// <summary>
    /// Checks the payment and the coach's account, freezes the payment as requested and audits it, all in one
    /// transaction under the coach lock. Returns the saved payment, or null after answering the error.
    /// </summary>
    private async Task<ClientPayment?> StoreRequestAsync(
        RequestClientPaymentRequest req,
        CoachClientContext context,
        CoachPaymentSettings? settings,
        Guid userId,
        DateTime now,
        DateOnly today,
        CancellationToken ct)
    {
        await using var transaction = await db.BeginTransactionAsync(ct);
        await db.LockProfessionalProfileAsync(context.ProfessionalProfileId, ct);

        var payment = await this.LoadPaymentOrRespondAsync(db, context, req.PaymentId, ct);

        if (payment is null || await this.RespondIfSettledAsync(payment, ct))
        {
            return null;
        }

        if (settings is null || string.IsNullOrWhiteSpace(settings.Iban) || string.IsNullOrWhiteSpace(settings.AccountHolder))
        {
            await this.SendProblemAsync(
                StatusCodes.Status409Conflict,
                ErrorCodes.BankAccountNotSet,
                "Set a bank account before requesting a payment.",
                ct);
            return null;
        }

        if (await RespondIfRateLimitedAsync(payment, now, ct))
        {
            return null;
        }

        var terms = payment.PeriodStart is null
            ? []
            : await ClientPaymentSchedule.LoadTermsAsync(db, payment.ClientPaymentAssignmentId, ct);
        var recurringStarts = await ClientPaymentSchedule.LoadRecurringStartsAsync(
            db, payment.ClientPaymentAssignmentId, ct);
        var figures = ClientPaymentSchedule.FiguresOf(payment, terms, recurringStarts);
        var amount = req.Amount ?? figures.Amount;

        if (amount is not > 0m or > ClientPaymentSchedule.MaxAmount
            || (req.Amount is not null && req.Amount > CoachPackagePricing.MaxPrice(payment.Currency)))
        {
            this.ThrowErrorWithCode(
                ErrorCodes.PaymentAmountInvalid,
                "A payment needs an amount above zero; waive it instead if nothing is owed.");
            return null;
        }

        if (payment.Status == ClientPaymentStatus.Upcoming)
        {
            var reference = payment.PeriodStart ?? payment.DueDate;
            var variableSymbol = await PickVariableSymbolAsync(payment, settings, context, reference, ct);

            if (variableSymbol is null)
            {
                await this.SendProblemAsync(
                    StatusCodes.Status409Conflict,
                    ErrorCodes.PaymentVariableSymbolExhausted,
                    "Every variable symbol of the month is taken.",
                    ct);
                return null;
            }

            ClientPaymentSchedule.Freeze(payment, terms, recurringStarts);
            payment.VariableSymbol = variableSymbol;
            payment.Message = PaymentMessageComposer.Compose(
                settings.MessageFormat,
                figures.PackageName ?? "Payment",
                context.ClientFirstName,
                context.ClientLastName,
                reference);
            payment.DueDate = req.DueDate ?? DefaultDueDate(payment, terms, settings, today);
        }
        else if (req.DueDate is { } dueDate)
        {
            payment.DueDate = dueDate;
        }

        payment.Amount = amount;
        payment.AccountIban = settings.Iban;
        payment.AccountHolder = settings.AccountHolder;
        payment.Status = ClientPaymentStatus.Requested;
        payment.RequestedAt = now;
        await db.SaveChangesAsync(ct);

        await ClientPaymentAudit.LogAsync(
            audit,
            userId,
            ClientPaymentAudit.Request,
            payment.ClientPaymentAssignment.PublicId,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            new
            {
                paymentId = payment.PublicId,
                period = payment.PeriodStart is { } start ? start.ToString("yyyy-MM") : payment.Title,
                amount = payment.Amount,
                currency = payment.Currency.ToString(),
                detail = payment.DueDate.ToString("yyyy-MM-dd"),
                channels = string.Join(',', new[] { req.SendEmail ? "email" : null, req.SendNotification ? "notification" : null }.OfType<string>())
            },
            ct);

        await transaction.CommitAsync(ct);

        return payment;
    }

    /// <summary>
    /// A coach cannot spam a client: the same payment is not requested again within a minute, and one client
    /// gets at most ten requests an hour. Answers 429 and returns true when a limit is hit.
    /// </summary>
    private async Task<bool> RespondIfRateLimitedAsync(ClientPayment payment, DateTime now, CancellationToken ct)
    {
        var tooSoon = payment.RequestedAt is { } requestedAt && now - requestedAt < SamePaymentCooldown;
        var hourAgo = now.AddHours(-1);
        var recentRequests = tooSoon
            ? 0
            : await db.AuditLogs
                .AsNoTracking()
                .CountAsync(
                    a => a.EntityId == payment.ClientPaymentAssignment.PublicId
                        && a.EntityType == ClientPaymentAudit.EntityType
                        && a.Action == ClientPaymentAudit.Request
                        && a.DateCreated > hourAgo,
                    ct);

        if (!tooSoon && recentRequests < MaxRequestsPerClientPerHour)
        {
            return false;
        }

        await this.SendProblemAsync(
            StatusCodes.Status429TooManyRequests,
            ErrorCodes.PaymentRequestRateLimited,
            "Too many payment requests for this client. Try again later.",
            ct);
        return true;
    }

    /// <summary>
    /// Picks the symbol the payment is requested under, from the coach's current format; the payment's own
    /// symbol does not count as taken.
    /// </summary>
    private async Task<string?> PickVariableSymbolAsync(
        ClientPayment payment, CoachPaymentSettings settings, CoachClientContext context, DateOnly reference, CancellationToken ct)
    {
        var used = await ClientPaymentSchedule.UsedVariableSymbolsAsync(db, context.ProfessionalProfileId, reference, ct);

        if (payment.VariableSymbol is not null)
        {
            used.Remove(payment.VariableSymbol);
        }

        var clientNumber = payment.ClientPaymentAssignment.ClientNumber;

        return VariableSymbolGenerator.Generate(settings.VariableSymbolFormat, reference, clientNumber, used);
    }

    /// <summary>
    /// The due date when the coach gives none: the period start or the coach's payment term from today,
    /// whichever is later. A one-off payment keeps the due date the coach chose for it.
    /// </summary>
    private static DateOnly DefaultDueDate(
        ClientPayment payment, IReadOnlyCollection<ClientPaymentTerms> terms, CoachPaymentSettings settings, DateOnly today)
    {
        if (payment.PeriodStart is not { } start)
        {
            return payment.DueDate;
        }

        var dueDays = ClientPaymentSchedule.TermsAt(terms, ClientPaymentSchedule.StartOfDayUtc(start))
            ?.CoachPackage.PaymentDueDaysOverride ?? settings.PaymentDueDays;
        var fromToday = today.AddDays(dueDays);

        return fromToday > payment.DueDate ? fromToday : payment.DueDate;
    }

    private async Task<bool> TrySendEmailAsync(ClientPayment payment, CoachClientContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(context.ClientEmail))
        {
            return false;
        }

        try
        {
            var qrPayload = SpaydBuilder.Build(
                payment.AccountIban!,
                payment.Amount!.Value,
                payment.VariableSymbol,
                payment.Message,
                payment.DueDate,
                payment.AccountHolder,
                payment.Currency);

            await emailService.SendPaymentRequestEmailAsync(
                new PaymentRequestEmail(
                    context.ClientEmail,
                    context.ClientLanguage ?? "en",
                    context.CoachName,
                    payment.PackageName ?? payment.Title ?? string.Empty,
                    payment.PeriodStart,
                    payment.PeriodEnd,
                    payment.Amount.Value,
                    payment.BasePrice,
                    payment.DiscountLabel,
                    payment.DueDate,
                    payment.AccountIban!,
                    payment.VariableSymbol ?? string.Empty,
                    payment.Message ?? string.Empty,
                    QrCodePngRenderer.Render(qrPayload),
                    PaymentMessageComposer.QrContentId,
                    payment.Currency,
                    payment.IsCustomPrice),
                ct);

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The log carries ids only: no IBAN, no QR payload, no address.
            logger.LogWarning(ex, "Payment request email failed for payment {PaymentId}", payment.PublicId);
            return false;
        }
    }

    private async Task<bool> TrySendNotificationAsync(ClientPayment payment, CoachClientContext context, CancellationToken ct)
    {
        try
        {
            var language = context.ClientLanguage ?? "en";
            var notification = await notificationService.CreateAsync(
                context.ClientUserId,
                NotificationType.PaymentRequested,
                new Dictionary<string, string>
                {
                    ["coachName"] = context.CoachName,
                    ["amount"] = PaymentMessageComposer.FormatAmount(payment.Amount!.Value, language, payment.Currency),
                    ["dueDate"] = PaymentMessageComposer.FormatDate(payment.DueDate, language),
                    ["paymentId"] = payment.PublicId.ToString()
                },
                ct: ct);

            await realtimeNotifier.NotifyAsync(
                context.ClientUserId,
                "newnotification",
                new
                {
                    id = notification.Id,
                    type = NotificationType.PaymentRequested.ToString(),
                    data = notification.Data
                },
                ct);

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Payment request notification failed for payment {PaymentId}", payment.PublicId);
            return false;
        }
    }
}
