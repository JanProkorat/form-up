using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using FastEndpoints;
using FormUp.Application.Domain.Authorization;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
using FormUp.Application.Features.PaymentSettings.Shared;
using FormUp.Application.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.PaymentSettings.ChangeBankAccount;

/// <summary>
/// Sets or changes the coach's bank account after re-authenticating them, records the change and,
/// when it replaced an existing account, emails an undo link.
/// </summary>
/// <param name="db">Application database context.</param>
/// <param name="userManager">ASP.NET Identity user manager, used to check the password.</param>
/// <param name="entitlementService">Resolves subscription entitlements.</param>
/// <param name="audit">Audit logging service.</param>
/// <param name="emailService">Sends the bank account changed notice.</param>
/// <param name="timeProvider">Clock.</param>
/// <param name="logger">Logger for a failed notice email.</param>
[RemovedCoachRole(RemovedCoachRoleMode.Exempt, Reason = "The coach's own bank account stays editable while no coach role is active.")]
internal sealed class ChangeBankAccountEndpoint(
    IApplicationDbContext db,
    UserManager<ApplicationUser> userManager,
    EntitlementService entitlementService,
    IAuditService audit,
    IEmailService emailService,
    TimeProvider timeProvider,
    ILogger<ChangeBankAccountEndpoint> logger)
    : Endpoint<ChangeBankAccountRequest, PaymentSettingsDto>
{
    private const int MaxCodeAttempts = 5;
    private static readonly TimeSpan UndoLinkLifetime = TimeSpan.FromDays(7);

    /// <inheritdoc />
    public override void Configure()
    {
        Put("/payments/bank-account");
        Roles(AppRoles.Trainer, AppRoles.Nutritionist);
        Options(x => x.RequireRateLimiting(AppPolicies.AuthRateLimit));
        Summary(s =>
        {
            s.Summary = "Set or change the bank account";
            s.Description = "Sets the coach's IBAN and account holder. Needs the caller's password, or the emailed code when the account has no password. Replacing an existing account emails a 7-day undo link.";
            s.Response<PaymentSettingsDto>(StatusCodes.Status200OK, "Updated payment settings");
            s.Response<ProblemDetails>(StatusCodes.Status400BadRequest, "Invalid IBAN or holder (INVALID_IBAN), or failed re-authentication (REAUTH_PASSWORD_REQUIRED, REAUTH_CODE_REQUIRED, INVALID_CURRENT_PASSWORD, REAUTH_CODE_INVALID)");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or unreadable caller claim");
            s.Response(StatusCodes.Status403Forbidden, "The subscription does not include payments (PAYMENTS_NOT_ENTITLED)");
            s.Response(StatusCodes.Status404NotFound, "The caller has no professional profile or account");
            s.Response(StatusCodes.Status429TooManyRequests, "Rate limit exceeded");
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(ChangeBankAccountRequest req, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue(AppClaims.UserId), out var userId))
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var profileId = await this.LoadEntitledProfileIdOrRespondAsync(db, entitlementService, userId, ct);

        if (profileId is null)
        {
            return;
        }

        var user = await userManager.FindByIdAsync(userId.ToString());

        if (user is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var reauthCodeId = await VerifyReauthenticationAsync(req, user, now, ct);

        var iban = IbanValidator.Normalize(req.Iban);
        var accountHolder = req.AccountHolder.Trim();

        var settings = await db.CoachPaymentSettings
            .FirstOrDefaultAsync(s => s.ProfessionalProfileId == profileId, ct);

        if (settings is not null && settings.Iban == iban && settings.AccountHolder == accountHolder)
        {
            await Send.OkAsync(PaymentSettingsDto.FromEntity(settings), ct);
            return;
        }

        var previousIban = settings?.Iban;
        var (saved, undoToken) = await StoreChangeAsync(
            settings, profileId.Value, userId, reauthCodeId, iban, accountHolder, now, ct);

        if (previousIban is not null && undoToken is not null)
        {
            await SendChangedEmailAsync(user, iban, previousIban, undoToken, ct);
        }

        await Send.OkAsync(PaymentSettingsDto.FromEntity(saved), ct);
    }

    /// <summary>
    /// Stores the change, its history row and its audit entry in one transaction, consuming the
    /// re-authentication code when one was used. Returns the saved settings and the raw undo token
    /// (null when there was no previous account to go back to).
    /// </summary>
    private async Task<(CoachPaymentSettings Settings, string? UndoToken)> StoreChangeAsync(
        CoachPaymentSettings? settings,
        long profileId,
        Guid userId,
        long? reauthCodeId,
        string iban,
        string accountHolder,
        DateTime now,
        CancellationToken ct)
    {
        await using var transaction = await db.BeginTransactionAsync(ct);

        if (reauthCodeId is not null)
        {
            var claimed = await db.CoachReauthCodes
                .Where(c => c.Id == reauthCodeId && c.UsedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.UsedAt, now), ct);

            if (claimed == 0)
            {
                this.ThrowErrorWithCode(ErrorCodes.ReauthCodeInvalid, "The code is invalid or has already been used.");
            }
        }

        if (settings is null)
        {
            settings = new CoachPaymentSettings { ProfessionalProfileId = profileId };
            db.CoachPaymentSettings.Add(settings);
        }

        var previousIban = settings.Iban;
        var undoToken = previousIban is null ? null : GenerateUndoToken();

        db.CoachBankAccountChanges.Add(new CoachBankAccountChange
        {
            ProfessionalProfileId = profileId,
            PreviousIban = previousIban,
            PreviousAccountHolder = settings.AccountHolder,
            NewIban = iban,
            NewAccountHolder = accountHolder,
            UndoTokenHash = undoToken is null ? null : PaymentSecrets.HashToken(undoToken),
            UndoExpiresAt = undoToken is null ? null : now.Add(UndoLinkLifetime)
        });

        settings.Iban = iban;
        settings.AccountHolder = accountHolder;
        settings.BankAccountChangedAt = now;

        await db.SaveChangesAsync(ct);

        // Audit rows carry the masked IBAN only; the account holder is personal data and stays out.
        await audit.LogAsync(
            userId,
            "ChangeBankAccount",
            nameof(CoachPaymentSettings),
            settings.PublicId,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            previousIban is null ? null : JsonSerializer.Serialize(new { iban = IbanValidator.Mask(previousIban) }),
            JsonSerializer.Serialize(new { iban = IbanValidator.Mask(iban) }),
            ct);

        await transaction.CommitAsync(ct);

        return (settings, undoToken);
    }

    /// <summary>
    /// Checks the password, or the emailed code for an account with no password. Throws a coded 400
    /// on any mismatch and returns the id of the code to consume, or null for a password check.
    /// </summary>
    private async Task<long?> VerifyReauthenticationAsync(
        ChangeBankAccountRequest req, ApplicationUser user, DateTime now, CancellationToken ct)
    {
        if (user.PasswordHash is not null)
        {
            if (string.IsNullOrEmpty(req.Password))
            {
                this.ThrowErrorWithCode(ErrorCodes.ReauthPasswordRequired, "The current password is required.");
                return null;
            }

            if (!await userManager.CheckPasswordAsync(user, req.Password))
            {
                this.ThrowErrorWithCode(ErrorCodes.InvalidCurrentPassword, "Current password is incorrect.");
                return null;
            }

            return null;
        }

        if (string.IsNullOrWhiteSpace(req.Code))
        {
            this.ThrowErrorWithCode(ErrorCodes.ReauthCodeRequired, "The emailed code is required.");
            return null;
        }

        var activeCodes = await db.CoachReauthCodes
            .Where(c => c.UserId == user.Id && c.UsedAt == null && c.ExpiresAt > now && c.FailedAttempts < MaxCodeAttempts)
            .ToListAsync(ct);
        var codeHash = PaymentSecrets.HashCode(user.Id, req.Code.Trim());
        var matching = activeCodes.FirstOrDefault(c => c.CodeHash == codeHash);

        if (matching is null)
        {
            foreach (var code in activeCodes)
            {
                code.FailedAttempts++;
            }

            await db.SaveChangesAsync(ct);

            this.ThrowErrorWithCode(ErrorCodes.ReauthCodeInvalid, "The code is invalid, expired or already used.");
            return null;
        }

        return matching.Id;
    }

    /// <summary>
    /// Sends the changed notice after the commit. A mail failure must not undo or fail a change that
    /// is already stored, so it is logged and swallowed; the log carries no IBAN or token.
    /// </summary>
    private async Task SendChangedEmailAsync(
        ApplicationUser user, string newIban, string previousIban, string undoToken, CancellationToken ct)
    {
        try
        {
            await emailService.SendBankAccountChangedEmailAsync(
                user.Email!,
                IbanValidator.Mask(newIban),
                IbanValidator.Mask(previousIban),
                undoToken,
                user.Language ?? "en",
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Bank account changed email failed for {UserId}", user.Id);
        }
    }

    private static string GenerateUndoToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
}
