using System.Net;
using System.Text;

namespace FormUp.Tests.Services.Billing;

/// <summary>A request the <see cref="ScriptedStripeHandler"/> saw: body form fields and the raw query string.</summary>
internal sealed record CapturedRequest(
    HttpMethod Method, string Path, List<KeyValuePair<string, string>> Form, string? IdempotencyKey, string Query = "");

/// <summary>
/// Stands in for the Stripe HTTP endpoint: answers by method and path from a script and records every request. No network.
/// </summary>
internal sealed class ScriptedStripeHandler(HttpStatusCode defaultStatus, string defaultJson) : HttpMessageHandler
{
    private readonly List<(HttpMethod Method, string Path, HttpStatusCode Status, string Json)> rules = [];

    private readonly List<(HttpMethod Method, string Path, Queue<(HttpStatusCode Status, string Json)> Queue)> sequences = [];

    public List<CapturedRequest> Requests { get; } = [];

    /// <summary>Answers the next calls of a route in order, then falls back to the plain rules.</summary>
    public ScriptedStripeHandler Sequence(HttpMethod method, string path, params (HttpStatusCode Status, string Json)[] answers)
    {
        sequences.Add((method, path, new Queue<(HttpStatusCode, string)>(answers)));

        return this;
    }

    public ScriptedStripeHandler Respond(HttpMethod method, string path, string json) => Fail(method, path, HttpStatusCode.OK, json);

    public ScriptedStripeHandler Fail(HttpMethod method, string path, HttpStatusCode status, string json)
    {
        rules.Add((method, path, status, json));

        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        var path = request.RequestUri!.AbsolutePath;
        var form = body.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Select(parts => new KeyValuePair<string, string>(Decode(parts[0]), parts.Length > 1 ? Decode(parts[1]) : string.Empty))
            .ToList();
        var key = request.Headers.TryGetValues("Idempotency-Key", out var values) ? values.Single() : null;

        Requests.Add(new CapturedRequest(request.Method, path, form, key, Uri.UnescapeDataString(request.RequestUri.Query)));

        var sequence = sequences.FirstOrDefault(q => q.Method == request.Method && q.Path == path && q.Queue.Count > 0);

        if (sequence.Path is not null)
        {
            var (queuedStatus, queuedJson) = sequence.Queue.Dequeue();

            return new HttpResponseMessage(queuedStatus) { Content = new StringContent(queuedJson, Encoding.UTF8, "application/json") };
        }

        var rule = rules.FirstOrDefault(r => r.Method == request.Method && r.Path == path);
        var (status, json) = rule.Path is null ? (defaultStatus, defaultJson) : (rule.Status, rule.Json);

        return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private static string Decode(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));
}
