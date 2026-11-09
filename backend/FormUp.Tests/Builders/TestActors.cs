using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Builders;

/// <summary>
/// An authenticated test user plus the ids a test typically needs: the caller's own
/// <see cref="ApplicationUser.Id"/>, its role-specific profile id (internal, for FK-style
/// seeding) and public id (for route parameters).
/// </summary>
public record Actor(HttpClient Http, Guid UserId, string Email, long ProfileId, Guid PublicId);

/// <summary>
/// Entry point for building authenticated test actors and the links between them, bypassing the
/// HTTP register+login round trip. Each actor is written directly via <c>UserManager</c> /
/// <see cref="ApplicationDbContext"/> and carries a JWT minted by <see cref="TestTokenFactory"/>.
/// </summary>
/// <example>
/// <code>
/// var trainer = await TestActors.Trainer(factory).CreateAsync();
/// var client = await TestActors.Client(factory).CreateAsync();
/// await TestActors.Link(factory, trainer, client).CanViewTrainingPlans(false).CreateAsync();
/// var response = await trainer.Http.GetAsync($"/trainer/clients/{client.PublicId}/plans");
/// </code>
/// </example>
public static class TestActors
{
    /// <summary>
    /// Builds a <see cref="UserRole.Trainer"/> actor with a <see cref="ProfessionalProfile"/>.
    /// </summary>
    public static ActorBuilder Trainer(FitnessApiFactory factory) => new(factory, [UserRole.Trainer]);

    /// <summary>
    /// Builds a <see cref="UserRole.Nutritionist"/> actor with a <see cref="ProfessionalProfile"/>.
    /// </summary>
    public static ActorBuilder Nutritionist(FitnessApiFactory factory) => new(factory, [UserRole.Nutritionist]);

    /// <summary>
    /// Builds a <see cref="UserRole.Client"/> actor with a <see cref="ClientProfile"/>.
    /// </summary>
    public static ActorBuilder Client(FitnessApiFactory factory) => new(factory, [UserRole.Client]);

    /// <summary>
    /// Builds an <see cref="UserRole.Admin"/> actor (no professional profile).
    /// </summary>
    public static ActorBuilder Admin(FitnessApiFactory factory) => new(factory, [UserRole.Admin]);

    /// <summary>
    /// Builds a professional actor holding an arbitrary set of global roles — needed for
    /// dual-role escalation cases (a caller holding both Trainer and Nutritionist).
    /// </summary>
    public static ActorBuilder Professional(FitnessApiFactory factory, params UserRole[] roles) =>
        new(factory, roles);

    /// <summary>
    /// Builds a <see cref="ClientProfessionalLink"/> between a professional and a client actor.
    /// </summary>
    public static LinkBuilder Link(FitnessApiFactory factory, Actor professional, Actor client) =>
        new(factory, professional.ProfileId, client.ProfileId);

    /// <summary>
    /// Creates one client per entry and links each to the professional in the given role, the collaboration of each
    /// having begun that many days ago. Returns the clients in the same order.
    /// </summary>
    public static async Task<List<Actor>> LinkClientsAsync(
        FitnessApiFactory factory, Actor professional, UserRole role, params int[] linkedDaysAgo)
    {
        var clients = new List<Actor>();

        foreach (var daysAgo in linkedDaysAgo)
        {
            var client = await Client(factory).CreateAsync(TestContext.Current.CancellationToken);
            await Link(factory, professional, client)
                .AsRole(role)
                .WithActivatedAtUtc(DateTime.UtcNow.AddDays(-daysAgo))
                .CreateAsync(TestContext.Current.CancellationToken);
            clients.Add(client);
        }

        return clients;
    }

    /// <summary>
    /// Builds a <see cref="ClientProfessionalLink"/> from raw profile ids — for fixtures that
    /// already hold ids rather than full <see cref="Actor"/> records.
    /// </summary>
    public static LinkBuilder Link(FitnessApiFactory factory, long professionalProfileId, long clientProfileId) =>
        new(factory, professionalProfileId, clientProfileId);
}

/// <summary>
/// Builds one <see cref="Actor"/>: an <see cref="ApplicationUser"/> created via
/// <see cref="UserManager{TUser}"/> (no password — the returned client authenticates with a
/// minted JWT instead), assigned the requested roles, and given the matching role-specific
/// profile row.
/// </summary>
public sealed class ActorBuilder(FitnessApiFactory factory, UserRole[] roles)
{
    private string _email = $"{Guid.NewGuid():N}@actor-fixture.com";
    private string? _password;
    private string? _language;
    private string _firstName = "Test";
    private string _lastName = "Actor";
    private UserRole[] _removedRoles = [];
    private bool _emailConfirmed;
    private DateTime? _dateCreated;

    /// <summary>
    /// Overrides the generated email (default is a random, always-unique fixture address).
    /// </summary>
    public ActorBuilder WithEmail(string email) { _email = email; return this; }

    /// <summary>
    /// Gives the actor a password (the default is none, like a social-sign-in account).
    /// </summary>
    public ActorBuilder WithPassword(string password) { _password = password; return this; }

    /// <summary>
    /// Sets the actor's UI language (the default is unset).
    /// </summary>
    public ActorBuilder WithLanguage(string language) { _language = language; return this; }

    /// <summary>
    /// Marks the actor's email as verified (the default is unverified).
    /// </summary>
    public ActorBuilder Verified() { _emailConfirmed = true; return this; }

    /// <summary>
    /// Overrides the account creation timestamp (default is now).
    /// </summary>
    public ActorBuilder CreatedAt(DateTime dateCreated) { _dateCreated = dateCreated; return this; }

    /// <summary>
    /// Overrides the first/last name (defaults are fixture placeholders).
    /// </summary>
    public ActorBuilder WithName(string firstName, string lastName)
    {
        _firstName = firstName;
        _lastName = lastName;
        return this;
    }

    /// <summary>
    /// Marks the given coach roles as removed on the professional profile (the user keeps the Identity roles),
    /// which the real removal endpoint refuses to do for the last active role.
    /// </summary>
    public ActorBuilder WithRemovedCoachRoles(params UserRole[] removedRoles)
    {
        _removedRoles = removedRoles;
        return this;
    }

    /// <summary>
    /// Creates the user, assigns roles, creates the role-specific profile, and returns an
    /// <see cref="Actor"/> whose <see cref="Actor.Http"/> already carries a valid bearer token.
    /// </summary>
    public async Task<Actor> CreateAsync(CancellationToken ct = default)
    {
        using var scope = factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var user = EntityBuilder.User
            .WithEmail(_email)
            .WithFirstName(_firstName)
            .WithLastName(_lastName)
            .WithEmailConfirmed(_emailConfirmed)
            .WithDateCreated(_dateCreated ?? DateTime.UtcNow)
            .Build();

        user.Language = _language;

        var createResult = _password is null
            ? await userManager.CreateAsync(user)
            : await userManager.CreateAsync(user, _password);
        createResult.Succeeded.Should().BeTrue(
            $"actor creation failed: {string.Join(", ", createResult.Errors.Select(e => e.Description))}");

        var roleNames = roles.Select(r => r.ToString()).ToArray();
        var roleResult = await userManager.AddToRolesAsync(user, roleNames);
        roleResult.Succeeded.Should().BeTrue(
            $"role assignment failed: {string.Join(", ", roleResult.Errors.Select(e => e.Description))}");

        long profileId;
        Guid publicId;

        if (roles.Contains(UserRole.Trainer) || roles.Contains(UserRole.Nutritionist))
        {
            var removedAt = DateTime.UtcNow.AddDays(-1);
            var profile = new ProfessionalProfile
            {
                UserId = user.Id,
                TrainerRoleRemovedAt = _removedRoles.Contains(UserRole.Trainer) ? removedAt : null,
                NutritionistRoleRemovedAt = _removedRoles.Contains(UserRole.Nutritionist) ? removedAt : null,
            };
            db.ProfessionalProfiles.Add(profile);
            await db.SaveChangesAsync(ct);
            profileId = profile.Id;
            publicId = profile.PublicId;
        }
        else
        {
            var profile = new ClientProfile { UserId = user.Id };
            db.ClientProfiles.Add(profile);
            await db.SaveChangesAsync(ct);
            profileId = profile.Id;
            publicId = profile.PublicId;
        }

        var http = factory.CreateClient();
        var token = TestTokenFactory.CreateAccessToken(factory, user.Id, _email, roleNames);
        TestHelpers.SetBearerToken(http, token);

        return new Actor(http, user.Id, _email, profileId, publicId);
    }
}

/// <summary>
/// Builds one <see cref="ClientProfessionalLink"/> row directly via
/// <see cref="ApplicationDbContext"/>.
/// </summary>
public sealed class LinkBuilder(FitnessApiFactory factory, long professionalProfileId, long clientProfileId)
{
    private UserRole _professionalRole = UserRole.Trainer;
    private bool _isActive = true;
    private ConnectionState? _state;
    private bool _canViewNutritionPlans = true;
    private bool _canViewTrainingPlans = true;
    private DateTime _dateCreated = DateTime.UtcNow;
    private DateTime? _activatedAtUtc;
    private bool _skipOnboardingForm;
    private long? _questionnaireId;

    /// <summary>
    /// The link's public id, pre-generated so callers can read it without a second DB round
    /// trip after <see cref="CreateAsync"/> completes.
    /// </summary>
    public Guid PublicId { get; } = Guid.NewGuid();

    /// <summary>
    /// Sets the professional role recorded on the link (defaults to <see cref="UserRole.Trainer"/>).
    /// </summary>
    public LinkBuilder AsRole(UserRole role) { _professionalRole = role; return this; }

    /// <summary>
    /// Sets whether the link grants nutrition-plan visibility (defaults to <c>true</c>).
    /// </summary>
    public LinkBuilder CanViewNutritionPlans(bool value = true) { _canViewNutritionPlans = value; return this; }

    /// <summary>
    /// Sets whether the link grants training-plan visibility (defaults to <c>true</c>).
    /// </summary>
    public LinkBuilder CanViewTrainingPlans(bool value = true) { _canViewTrainingPlans = value; return this; }

    /// <summary>
    /// Marks the link inactive (defaults to active).
    /// </summary>
    public LinkBuilder Inactive() { _isActive = false; return this; }

    /// <summary>
    /// Sets the link's exact connection state (defaults to <see cref="ConnectionState.Connected"/>).
    /// </summary>
    public LinkBuilder WithState(ConnectionState state) { _state = state; return this; }

    /// <summary>
    /// Overrides the link's creation timestamp (defaults to now).
    /// </summary>
    public LinkBuilder WithDateCreated(DateTime dateCreated) { _dateCreated = dateCreated; return this; }

    /// <summary>
    /// Sets when the current collaboration began (defaults to null, a legacy link).
    /// </summary>
    public LinkBuilder WithActivatedAtUtc(DateTime activatedAtUtc) { _activatedAtUtc = activatedAtUtc; return this; }

    /// <summary>
    /// Marks the link as having no onboarding form (defaults to false).
    /// </summary>
    public LinkBuilder SkippingOnboardingForm() { _skipOnboardingForm = true; return this; }

    /// <summary>
    /// Sets the link's explicit questionnaire (internal id).
    /// </summary>
    public LinkBuilder WithQuestionnaireId(long questionnaireId) { _questionnaireId = questionnaireId; return this; }

    /// <summary>
    /// Persists the link and returns its internal id.
    /// </summary>
    public async Task<long> CreateAsync(CancellationToken ct = default)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var link = new ClientProfessionalLink
        {
            PublicId = PublicId,
            ProfessionalProfileId = professionalProfileId,
            ClientProfileId = clientProfileId,
            ProfessionalRole = _professionalRole,
            ConnectionState = _state ?? (_isActive ? ConnectionState.Connected : ConnectionState.Ended),
            CanViewNutritionPlans = _canViewNutritionPlans,
            CanViewTrainingPlans = _canViewTrainingPlans,
            DateCreated = _dateCreated,
            ActivatedAtUtc = _activatedAtUtc,
            SkipOnboardingForm = _skipOnboardingForm,
            QuestionnaireId = _questionnaireId,
        };
        db.ClientProfessionalLinks.Add(link);

        await db.SaveChangesAsync(ct);
        return link.Id;
    }
}
