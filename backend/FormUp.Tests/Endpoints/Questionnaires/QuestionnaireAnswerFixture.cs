using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Questionnaires;

/// <summary>One question to seed: key is how the test refers to it.</summary>
internal sealed record SeedQuestion(
    string Key, string Type, string? Config = null, bool Required = false, bool Hidden = false);

/// <summary>A client, a coach, a link, a questionnaire with questions and one open response.</summary>
internal sealed class QuestionnaireAnswerScenario
{
    public required Actor Client { get; init; }
    public required Actor Coach { get; init; }
    public required Guid LinkPublicId { get; init; }
    /// <summary>The open response's public id; <see cref="Guid.Empty"/> when seeded without a response.</summary>
    public required Guid ResponsePublicId { get; init; }
    public required long LinkId { get; init; }
    public required long ResponseId { get; init; }
    public required Guid QuestionnairePublicId { get; init; }
    public required Dictionary<string, Guid> QuestionIds { get; init; }

    /// <summary>Seeds the scenario straight through the DbContext.</summary>
    public static async Task<QuestionnaireAnswerScenario> CreateAsync(
        FitnessApiFactory factory,
        IReadOnlyList<SeedQuestion> questions,
        DateTime? activatedAtUtc = null,
        bool linkActive = true,
        QuestionnaireResponseStatus status = QuestionnaireResponseStatus.Pending,
        Actor? existingClient = null,
        bool withResponse = true)
    {
        var ct = TestContext.Current.CancellationToken;
        var coach = await TestActors.Trainer(factory).CreateAsync(ct);
        var client = existingClient ?? await TestActors.Client(factory).CreateAsync(ct);

        var link = TestActors.Link(factory, coach, client);
        if (activatedAtUtc.HasValue)
        {
            link.WithActivatedAtUtc(activatedAtUtc.Value);
        }

        if (!linkActive)
        {
            link.Inactive();
        }

        var linkId = await link.CreateAsync(ct);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var questionnaire = EntityBuilder.Questionnaire.WithProfessionalUserId(coach.UserId).Build();
        var questionIds = new Dictionary<string, Guid>();
        var order = 0;
        foreach (var seed in questions)
        {
            var question = new QuestionnaireQuestion
            {
                PublicId = Guid.NewGuid(),
                OrderIndex = order++,
                Type = seed.Type,
                Label = seed.Key,
                IsRequired = seed.Required,
                IsHidden = seed.Hidden,
                Config = seed.Config,
            };
            questionnaire.Questions.Add(question);
            questionIds[seed.Key] = question.PublicId;
        }

        db.Questionnaires.Add(questionnaire);
        await db.SaveChangesAsync(ct);

        QuestionnaireResponse? response = null;
        if (withResponse)
        {
            response = EntityBuilder.QuestionnaireResponse
                .ForQuestionnaire(questionnaire.Id)
                .OnLink(linkId)
                .FromClient(client.UserId)
                .FromProfessional(coach.UserId)
                .WithStatus(status, status == QuestionnaireResponseStatus.Submitted ? DateTime.UtcNow : null)
                .Build();
            db.QuestionnaireResponses.Add(response);
            await db.SaveChangesAsync(ct);
        }

        return new QuestionnaireAnswerScenario
        {
            Client = client,
            Coach = coach,
            LinkPublicId = link.PublicId,
            ResponsePublicId = response?.PublicId ?? Guid.Empty,
            LinkId = linkId,
            ResponseId = response?.Id ?? 0,
            QuestionnairePublicId = questionnaire.PublicId,
            QuestionIds = questionIds,
        };
    }

    /// <summary>Stores a file_upload answer with the given stored URL straight through the DbContext.</summary>
    public async Task AddFileAnswerAsync(FitnessApiFactory factory, string questionKey, string storedFileUrl)
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var questionPublicId = QuestionIds[questionKey];
        var questionId = await db.QuestionnaireQuestions
            .Where(q => q.PublicId == questionPublicId)
            .Select(q => q.Id)
            .FirstAsync(ct);

        db.QuestionnaireAnswers.Add(new QuestionnaireAnswer
        {
            PublicId = Guid.NewGuid(),
            ResponseId = ResponseId,
            QuestionId = questionId,
            FileUrl = storedFileUrl,
        });
        await db.SaveChangesAsync(ct);
    }
}
