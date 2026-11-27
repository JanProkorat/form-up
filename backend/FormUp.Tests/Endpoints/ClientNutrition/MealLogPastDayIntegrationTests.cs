using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace FormUp.Tests.Endpoints.ClientNutrition;

/// <summary>
/// Testcontainers integration tests (real MongoDB and PostgreSQL) for reading and adding meal
/// photos on past plan days. The mocked Mongo context ignores filters, so day keying, the
/// "never marks eaten" guarantee and cross-client isolation can only be proven against real databases.
/// </summary>
[Collection(TestCollection.Name)]
public class MealLogPastDayIntegrationTests(FitnessApiFactory factory)
{
    private const string PhotoA = "https://minio.local/bucket/past-a.jpg";
    private const string PhotoB = "https://minio.local/bucket/past-b.jpg";

    /// <summary>A photo URL under the meal's <c>diary/{mealId}/</c> folder, as the upload-url endpoint issues it.</summary>
    private static string DiaryPhoto(Guid mealId, string name) => $"{MealPhotoTestHost.BlobRoot}diary/{mealId}/{name}.jpg";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static DateTime TodayUtc => DateTime.UtcNow.Date;

    private static DateOnly Today => DateOnly.FromDateTime(TodayUtc);

    /// <summary>A past day that is not a Monday, so it differs from the Monday-only meal's day.</summary>
    private static DateOnly PastNonMonday
    {
        get
        {
            var date = Today.AddDays(-1);
            return date.DayOfWeek == DayOfWeek.Monday ? date.AddDays(-1) : date;
        }
    }

    private static DateTime Utc(DateOnly date) => date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

    private static DateOnly PlanStart => DateOnly.FromDateTime(ClientNutritionFixtures.CurrentWeekMondayUtc().AddDays(-14));

    /// <summary>
    /// Seeds a plan that started two weeks before this week's Monday (three published weeks).
    /// The all-days meal sits on every day; the Monday-only meal sits on Mondays alone.
    /// </summary>
    private async Task<(NutritionPlan Plan, Guid AllDaysMealId, Guid MondayMealId)> SeedPlanAsync(
        Guid clientUserId, NutritionPlanStatus status = NutritionPlanStatus.Active)
    {
        var allDaysMealId = Guid.NewGuid();
        var mondayMealId = Guid.NewGuid();

        PlanMeal Meal(Guid id) => new()
        {
            MealId = id,
            Kind = MealKind.Lunch,
            Order = 1,
            Foods =
            [
                new MealFood
                {
                    FoodExternalId = Guid.NewGuid(),
                    FoodName = "Rice",
                    NutrientValuePer100Grams = new NutrientValue { Kcal = 100, Protein = 10, Carbs = 10, Fat = 5 },
                    AmountGrams = 150,
                },
            ],
        };

        PlanWeek Week(int number) => new()
        {
            WeekNumber = number,
            Status = WeekStatus.Published,
            DatePublished = DateTime.UtcNow,
            Days = Enumerable.Range(1, 7)
                .Select(day => new PlanDay
                {
                    DayOfWeek = day,
                    Meals = day == 1 ? [Meal(allDaysMealId), Meal(mondayMealId)] : [Meal(allDaysMealId)],
                })
                .ToList(),
        };

        // A real author: the photo dual-write adds a viewer row that has a FK to the users table.
        var author = await TestActors.Nutritionist(factory).CreateAsync(TestContext.Current.CancellationToken);
        var plan = await ClientNutritionFixtures.InsertPlanAsync(
            factory, clientUserId, status, Utc(PlanStart), author.UserId, null, Week(1), Week(2), Week(3));
        return (plan, allDaysMealId, mondayMealId);
    }

    private async Task<MealLog> SeedLogAsync(
        Guid clientUserId, Guid planId, Guid mealId, DateOnly date, bool eaten, params string[] photoUrls)
    {
        var log = new MealLog
        {
            ClientId = clientUserId,
            PlanId = planId,
            MealId = mealId,
            LogDate = Utc(date),
            EatenAt = eaten ? Utc(date).AddHours(13) : null,
            Photos = photoUrls.Select(url => new MealPhoto { BlobUrl = url, UploadedAt = DateTime.UtcNow }).ToList(),
        };

        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
        await mongo.MealLogs.InsertOneAsync(log, cancellationToken: TestContext.Current.CancellationToken);
        return log;
    }

    private async Task<List<MealLog>> LoadLogsAsync(Guid clientUserId, Guid mealId)
    {
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
        return await mongo.MealLogs
            .Find(l => l.ClientId == clientUserId && l.MealId == mealId)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    private async Task<List<PlanPhoto>> LoadPlanPhotosAsync(string mealLogId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        return await db.PlanPhotos
            .AsNoTracking()
            .Where(p => p.MealLogId == mealLogId)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Creates a client whose time zone is explicitly UTC, so the UTC-based "today" used by these
    /// tests equals the client-local today the endpoints resolve, at any hour of the day.
    /// </summary>
    private async Task<Actor> CreateClientAsync()
    {
        var client = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Users
            .Where(u => u.Id == client.UserId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.TimeZone, "UTC"), TestContext.Current.CancellationToken);
        return client;
    }

    private async Task<TimeZoneInfo> LoadTimeZoneAsync(Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        return await db.ResolveClientTimeZoneAsync(userId, TestContext.Current.CancellationToken);
    }

    private Task<HttpResponseMessage> SavePhotosAsync(
        Actor client, Guid mealId, DateOnly? date, params string[] photoUrls) =>
        MealPhotoTestHost.CreateClientFor(factory, client).PostAsJsonAsync(
            $"/client/nutrition/log/meals/{mealId}/photos",
            new { Date = date?.ToString("yyyy-MM-dd"), Photos = photoUrls.Select(url => new { BlobUrl = url }).ToArray() },
            TestContext.Current.CancellationToken);

    private static Task<HttpResponseMessage> UploadUrlAsync(Actor client, Guid mealId, string? date) =>
        client.Http.PostAsJsonAsync(
            $"/client/nutrition/log/meals/{mealId}/photo-upload-url",
            new { Date = date, ContentType = "image/jpeg", SizeBytes = 1024 },
            TestContext.Current.CancellationToken);

    private static async Task<WeekDto> GetWeekAsync(Actor client, DateOnly date)
    {
        var response = await client.Http.GetAsync(
            $"/client/nutrition/log/week?date={date:yyyy-MM-dd}", TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<WeekDto>(JsonOptions, TestContext.Current.CancellationToken))!;
    }

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return document.RootElement.TryGetProperty("errorCode", out var code) ? code.GetString() : null;
    }

    [Fact]
    public async Task SavePhotos_PastDate_KeysRowToThatDay_NeverMarksEaten_AndShowsInWeek()
    {
        var client = await CreateClientAsync();
        var (plan, mealId, _) = await SeedPlanAsync(client.UserId);
        var pastDate = PastNonMonday;

        var response = await SavePhotosAsync(client, mealId, pastDate, DiaryPhoto(mealId, "past-a"));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var row = (await LoadLogsAsync(client.UserId, mealId)).Should().ContainSingle().Subject;
        row.LogDate.Should().Be(Utc(pastDate));
        row.EatenAt.Should().BeNull("a photo on a past day must never mark the meal eaten");

        var planPhoto = (await LoadPlanPhotosAsync(row.Id.ToString())).Should().ContainSingle().Subject;
        var timeZone = await LoadTimeZoneAsync(client.UserId);
        var expectedNoon = TimeZoneInfo.ConvertTimeToUtc(
            pastDate.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Unspecified), timeZone);
        DateTime.SpecifyKind(planPhoto.TakenAt, DateTimeKind.Utc).Should().Be(expectedNoon, "local noon keeps the photo on its day");

        var week = await GetWeekAsync(client, pastDate);
        week.PlanId.Should().Be(plan.ExternalId);
        var meal = week.Days.Single(d => d.Date == pastDate).Meals.Should().ContainSingle().Subject;
        meal.Eaten.Should().BeFalse();
        var photo = meal.Photos.Should().ContainSingle().Subject;
        photo.BlobUrl.Should().Be(DiaryPhoto(mealId, "past-a"));
        photo.DisplayUrl.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task SavePhotos_DateOmitted_UsesToday_AndKeepsTakenAtNow()
    {
        var client = await CreateClientAsync();
        var (_, mealId, _) = await SeedPlanAsync(client.UserId);

        var response = await SavePhotosAsync(client, mealId, null, DiaryPhoto(mealId, "past-a"));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var row = (await LoadLogsAsync(client.UserId, mealId)).Should().ContainSingle().Subject;
        row.LogDate.Should().Be(TodayUtc);
        var planPhoto = (await LoadPlanPhotosAsync(row.Id.ToString())).Should().ContainSingle().Subject;
        DateTime.SpecifyKind(planPhoto.TakenAt, DateTimeKind.Utc).Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task SavePhotos_PastDateOnEatenMeal_KeepsEatenAtAndReplacesPhotosWithoutDuplicateRow()
    {
        var client = await CreateClientAsync();
        var (plan, mealId, _) = await SeedPlanAsync(client.UserId);
        var pastDate = PastNonMonday;
        var seeded = await SeedLogAsync(client.UserId, plan.ExternalId, mealId, pastDate, eaten: true, PhotoA);

        var response = await SavePhotosAsync(client, mealId, pastDate, DiaryPhoto(mealId, "past-b"));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var row = (await LoadLogsAsync(client.UserId, mealId)).Should().ContainSingle().Subject;
        row.Id.Should().Be(seeded.Id);
        row.EatenAt.Should().Be(seeded.EatenAt);
        row.Photos.Should().ContainSingle().Which.BlobUrl.Should().Be(DiaryPhoto(mealId, "past-b"));
    }

    [Fact]
    public async Task SavePhotos_FutureDate_Returns400WithErrorCode_AndWritesNothing()
    {
        var client = await CreateClientAsync();
        var (_, mealId, _) = await SeedPlanAsync(client.UserId);

        var response = await SavePhotosAsync(client, mealId, Today.AddDays(1), PhotoA);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadErrorCodeAsync(response)).Should().Be("MEAL_LOG_DATE_IN_FUTURE");
        (await LoadLogsAsync(client.UserId, mealId)).Should().BeEmpty();
    }

    [Fact]
    public async Task SavePhotos_InvalidDate_Returns400()
    {
        var client = await CreateClientAsync();
        var (_, mealId, _) = await SeedPlanAsync(client.UserId);

        var response = await client.Http.PostAsJsonAsync(
            $"/client/nutrition/log/meals/{mealId}/photos",
            new { Date = "0001-01-01", Photos = Array.Empty<object>() },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SavePhotos_DateBeforePlanStart_Returns404()
    {
        var client = await CreateClientAsync();
        var (_, mealId, _) = await SeedPlanAsync(client.UserId);

        var response = await SavePhotosAsync(client, mealId, PlanStart.AddDays(-1), PhotoA);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await LoadLogsAsync(client.UserId, mealId)).Should().BeEmpty();
    }

    [Fact]
    public async Task SavePhotos_CompletedPlan_Returns404()
    {
        var client = await CreateClientAsync();
        var (_, mealId, _) = await SeedPlanAsync(client.UserId, NutritionPlanStatus.Completed);

        var response = await SavePhotosAsync(client, mealId, PastNonMonday, PhotoA);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SavePhotos_MealNotOnThatDaysPlanDay_Returns404()
    {
        var client = await CreateClientAsync();
        var (_, _, mondayMealId) = await SeedPlanAsync(client.UserId);

        var response = await SavePhotosAsync(client, mondayMealId, PastNonMonday, PhotoA);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await LoadLogsAsync(client.UserId, mondayMealId)).Should().BeEmpty();
    }

    [Fact]
    public async Task SavePhotos_OtherClientsMeal_Returns404_AndLeavesOwnerRowUntouched()
    {
        var owner = await CreateClientAsync();
        var intruder = await CreateClientAsync();
        var (plan, mealId, _) = await SeedPlanAsync(owner.UserId);
        var pastDate = PastNonMonday;
        var seeded = await SeedLogAsync(owner.UserId, plan.ExternalId, mealId, pastDate, eaten: false, PhotoA);

        var response = await SavePhotosAsync(intruder, mealId, pastDate, PhotoB);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var row = (await LoadLogsAsync(owner.UserId, mealId)).Should().ContainSingle().Subject;
        row.Id.Should().Be(seeded.Id);
        row.Photos.Should().ContainSingle().Which.BlobUrl.Should().Be(PhotoA);
        (await LoadLogsAsync(intruder.UserId, mealId)).Should().BeEmpty();
    }

    [Fact]
    public async Task UploadUrl_PastDateInPlan_Returns200WithUrls()
    {
        var client = await CreateClientAsync();
        var (_, mealId, _) = await SeedPlanAsync(client.UserId);

        var withDate = await UploadUrlAsync(client, mealId, PastNonMonday.ToString("yyyy-MM-dd"));
        var withoutDate = await UploadUrlAsync(client, mealId, null);

        withDate.StatusCode.Should().Be(HttpStatusCode.OK);
        withoutDate.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await withDate.Content.ReadFromJsonAsync<UploadUrlDto>(JsonOptions, TestContext.Current.CancellationToken);
        body!.UploadUrl.Should().NotBeNullOrEmpty();
        body.BlobUrl.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task UploadUrl_FutureDate_Returns400WithErrorCode()
    {
        var client = await CreateClientAsync();
        var (_, mealId, _) = await SeedPlanAsync(client.UserId);

        var response = await UploadUrlAsync(client, mealId, Today.AddDays(1).ToString("yyyy-MM-dd"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadErrorCodeAsync(response)).Should().Be("MEAL_LOG_DATE_IN_FUTURE");
    }

    [Fact]
    public async Task UploadUrl_InvalidDate_Returns400()
    {
        var client = await CreateClientAsync();
        var (_, mealId, _) = await SeedPlanAsync(client.UserId);

        var response = await UploadUrlAsync(client, mealId, "0001-01-01");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UploadUrl_DateOutsidePlanOrMealNotOnDay_Returns404()
    {
        var client = await CreateClientAsync();
        var (_, allDaysMealId, mondayMealId) = await SeedPlanAsync(client.UserId);

        var beforePlan = await UploadUrlAsync(client, allDaysMealId, PlanStart.AddDays(-1).ToString("yyyy-MM-dd"));
        var wrongDay = await UploadUrlAsync(client, mondayMealId, PastNonMonday.ToString("yyyy-MM-dd"));

        beforePlan.StatusCode.Should().Be(HttpStatusCode.NotFound);
        wrongDay.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UploadUrl_OtherClientsMeal_Returns404()
    {
        var owner = await CreateClientAsync();
        var intruder = await CreateClientAsync();
        var (_, mealId, _) = await SeedPlanAsync(owner.UserId);

        var response = await UploadUrlAsync(intruder, mealId, PastNonMonday.ToString("yyyy-MM-dd"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetWeek_SplitRowsOfOneMealAndDay_AreMergedIntoOneEntry()
    {
        var client = await CreateClientAsync();
        var (plan, mealId, _) = await SeedPlanAsync(client.UserId);
        var pastDate = PastNonMonday;
        var legacyRow = await SeedLogAsync(client.UserId, plan.ExternalId, mealId, pastDate, eaten: true, PhotoA);
        await SeedLogAsync(client.UserId, plan.ExternalId, mealId, pastDate, eaten: false, PhotoA, PhotoB);

        // The unique (client, plan, meal, day) index rejects two rows with the same LogDate, so the older row
        // takes the legacy shape: no LogDate, keyed to the day only by its EatenAt.
        using (var scope = factory.Services.CreateScope())
        {
            var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
            await mongo.MealLogs.UpdateOneAsync(
                l => l.Id == legacyRow.Id,
                Builders<MealLog>.Update.Set(l => l.LogDate, DateTime.MinValue),
                cancellationToken: TestContext.Current.CancellationToken);
        }

        var week = await GetWeekAsync(client, pastDate);

        var meal = week.Days.Single(d => d.Date == pastDate).Meals.Should().ContainSingle().Subject;
        meal.MealId.Should().Be(mealId);
        meal.Eaten.Should().BeTrue();
        meal.EatenAt.Should().NotBeNull();
        meal.Photos.Select(p => p.BlobUrl).Should().BeEquivalentTo(PhotoA, PhotoB);
    }

    [Fact]
    public async Task GetWeek_ReturnsSevenDays_WeekBounds_AndNoMealsOrPhotoRightsForFutureDays()
    {
        var client = await CreateClientAsync();
        var (plan, mealId, _) = await SeedPlanAsync(client.UserId);
        await SeedLogAsync(client.UserId, plan.ExternalId, mealId, Today, eaten: true);

        var week = await GetWeekAsync(client, Today);

        week.WeekNumber.Should().Be(3);
        week.WeekStartDate.Should().Be(PlanStart.AddDays(14));
        week.WeekEndDate.Should().Be(PlanStart.AddDays(20));
        week.Today.Should().Be(Today);
        week.Days.Should().HaveCount(7);
        week.Days.Single(d => d.Date == Today).CanAddPhotos.Should().BeTrue();
        week.Days.Single(d => d.Date == Today).Meals.Should().ContainSingle().Which.Eaten.Should().BeTrue();
        week.Days.Where(d => d.Date > Today).Should().OnlyContain(d => !d.CanAddPhotos && d.Meals.Count == 0);
        week.Days.Where(d => d.Date < Today).Should().OnlyContain(d => d.CanAddPhotos);
    }

    [Fact]
    public async Task GetWeek_DateOutsidePlanOrCompletedPlan_Returns404()
    {
        var client = await CreateClientAsync();
        await SeedPlanAsync(client.UserId);
        var completedClient = await CreateClientAsync();
        await SeedPlanAsync(completedClient.UserId, NutritionPlanStatus.Completed);

        var beforePlan = await client.Http.GetAsync(
            $"/client/nutrition/log/week?date={PlanStart.AddDays(-1):yyyy-MM-dd}", TestContext.Current.CancellationToken);
        var afterPlan = await client.Http.GetAsync(
            $"/client/nutrition/log/week?date={PlanStart.AddDays(21):yyyy-MM-dd}", TestContext.Current.CancellationToken);
        var completed = await completedClient.Http.GetAsync(
            $"/client/nutrition/log/week?date={PastNonMonday:yyyy-MM-dd}", TestContext.Current.CancellationToken);

        beforePlan.StatusCode.Should().Be(HttpStatusCode.NotFound);
        afterPlan.StatusCode.Should().Be(HttpStatusCode.NotFound);
        completed.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetWeek_OtherClient_Returns404_AndNeverSeesOwnerRows()
    {
        var owner = await CreateClientAsync();
        var other = await CreateClientAsync();
        var (plan, mealId, _) = await SeedPlanAsync(owner.UserId);
        await SeedLogAsync(owner.UserId, plan.ExternalId, mealId, PastNonMonday, eaten: true, PhotoA);

        var response = await other.Http.GetAsync(
            $"/client/nutrition/log/week?date={PastNonMonday:yyyy-MM-dd}", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetWeek_MissingDate_Returns400()
    {
        var client = await CreateClientAsync();

        var response = await client.Http.GetAsync("/client/nutrition/log/week", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private record WeekDto(
        Guid PlanId, int WeekNumber, DateOnly WeekStartDate, DateOnly WeekEndDate, DateOnly Today, List<DayDto> Days);

    private record DayDto(DateOnly Date, bool CanAddPhotos, List<MealDto> Meals);

    private record MealDto(Guid MealId, bool Eaten, DateTime? EatenAt, string? Note, List<PhotoDto> Photos);

    private record PhotoDto(string BlobUrl, string DisplayUrl, DateTime UploadedAt, string? Note);

    private record UploadUrlDto(string UploadUrl, string BlobUrl);
}
