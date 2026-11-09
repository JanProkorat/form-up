using System.Net;
using System.Net.Sockets;
using System.Text;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
using FormUp.Application.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using NSubstitute;
using Resend;

namespace FormUp.Tests.Endpoints.ClientPayments;

/// <summary>
/// The payment request email as each provider builds it: the QR code goes out as an inline image the HTML
/// refers to by content id, in the recipient's language. Encoding of user text is
/// <c>PaymentMessageComposerTests</c>' job, since both providers render through the composer.
/// </summary>
public class PaymentRequestEmailServiceTests
{
    private static readonly byte[] FakePng = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

    private static PaymentRequestEmail Email(string language) => new(
        "eva@example.com",
        language,
        "Martin Král",
        "Online coaching",
        new DateOnly(2026, 11, 1),
        new DateOnly(2026, 11, 30),
        1750m,
        2500m,
        "Friends & family −30 %",
        new DateOnly(2026, 11, 1),
        "CZ6508000000192000145399",
        "2026110142",
        "Online coaching 11/2026",
        FakePng,
        PaymentMessageComposer.QrContentId);

    private static IConfiguration Configuration(int smtpPort = 1025) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Email:SmtpHost"] = "127.0.0.1",
            ["Email:SmtpPort"] = smtpPort.ToString(),
            ["Email:FromAddress"] = "noreply@example.com",
            ["Email:FromName"] = "Form Up",
            ["App:BaseUrl"] = "https://app.example.com"
        }).Build();

    [Theory]
    [InlineData("Smtp", "cs", "Částka", "ŽÁDOST O PLATBU", " za Online coaching")]
    [InlineData("Resend", "en", "Amount", "PAYMENT REQUEST", " for Online coaching")]
    [InlineData("Resend", "de", "Betrag", "ZAHLUNGSAUFFORDERUNG", " f&#252;r Online coaching")]
    public async Task PaymentRequest_EachProvider_SendsTheQrInlineUnderTheContentIdTheHtmlUsesInTheRecipientsLanguage(
        string provider, string language, string amountLabel, string label, string headingTail)
    {
        var sent = provider == "Smtp" ? await SendViaSmtpAsync(Email(language)) : await SendViaResendAsync(Email(language));

        sent.To.Should().Equal("eva@example.com");
        sent.Subject.Should().StartWith("Martin Král: 1");
        sent.Html.Should().Contain($"cid:{PaymentMessageComposer.QrContentId}").And.Contain(amountLabel);
        System.Text.RegularExpressions.Regex.Match(sent.Html, "<h1[^>]*>(.*?)</h1>", System.Text.RegularExpressions.RegexOptions.Singleline)
            .Groups[1].Value.Should().Contain(headingTail);
        sent.Html.Should().Contain($">{label}</p>", "the label sits above the heading");
        sent.Html.Should().Contain("prefers-color-scheme").And.NotContain("{{");
        sent.ImageContentId.Should().Be(PaymentMessageComposer.QrContentId);
        sent.ImageBytes.Should().Equal(FakePng);
    }

    private sealed record SentEmail(IReadOnlyList<string> To, string Subject, string Html, string? ImageContentId, byte[] ImageBytes);

    private static async Task<SentEmail> SendViaResendAsync(PaymentRequestEmail email)
    {
        var resend = Substitute.For<IResend>();
        var service = new ResendEmailService(resend, Configuration(), NullLogger<ResendEmailService>.Instance);

        await service.SendPaymentRequestEmailAsync(email, TestContext.Current.CancellationToken);

        var message = (EmailMessage)resend.ReceivedCalls().Single().GetArguments()[0]!;
        var attachment = message.Attachments!.Should().ContainSingle().Subject;
        attachment.ContentType.Should().Be("image/png");

        return new SentEmail(
            message.To.Select(t => t.Email).ToList(),
            message.Subject,
            message.HtmlBody!,
            attachment.ContentId,
            attachment.Content!.Value.AsByteArray());
    }

    private static async Task<SentEmail> SendViaSmtpAsync(PaymentRequestEmail email)
    {
        await using var server = FakeSmtpServer.Start();
        var service = new SmtpEmailService(Configuration(server.Port), NullLogger<SmtpEmailService>.Instance);

        await service.SendPaymentRequestEmailAsync(email, TestContext.Current.CancellationToken);

        var message = await server.ReceivedAsync();
        var related = message.Body.Should().BeAssignableTo<Multipart>().Subject;
        related.ContentType.MimeType.Should().Be("multipart/related");
        var image = related.OfType<MimePart>().Single(part => part.ContentType.MimeType == "image/png");
        using var imageBytes = new MemoryStream();
        await image.Content!.DecodeToAsync(imageBytes, TestContext.Current.CancellationToken);

        return new SentEmail(
            message.To.Mailboxes.Select(m => m.Address).ToList(),
            message.Subject ?? string.Empty,
            message.HtmlBody ?? string.Empty,
            image.ContentId,
            imageBytes.ToArray());
    }

    /// <summary>
    /// A one-connection SMTP server that accepts one message and keeps it for inspection.
    /// </summary>
    private sealed class FakeSmtpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly TaskCompletionSource<byte[]> _received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task _serving = Task.CompletedTask;

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public static FakeSmtpServer Start()
        {
            var server = new FakeSmtpServer();
            server._listener.Start();
            server._serving = Task.Run(server.ServeAsync);

            return server;
        }

        public async Task<MimeMessage> ReceivedAsync()
        {
            var raw = await _received.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
            using var stream = new MemoryStream(raw);

            return await MimeMessage.LoadAsync(stream, TestContext.Current.CancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();

            try
            {
                await _serving;
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or IOException)
            {
                // The listener was stopped while a connection was pending.
            }
        }

        private async Task ServeAsync()
        {
            using var client = await _listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\r\n", AutoFlush = true };

            await writer.WriteLineAsync("220 fake ESMTP");

            while (await reader.ReadLineAsync() is { } line)
            {
                if (line.StartsWith("EHLO", StringComparison.OrdinalIgnoreCase) || line.StartsWith("HELO", StringComparison.OrdinalIgnoreCase))
                {
                    await writer.WriteLineAsync("250 fake");
                }
                else if (line.StartsWith("DATA", StringComparison.OrdinalIgnoreCase))
                {
                    await writer.WriteLineAsync("354 go ahead");
                    _received.TrySetResult(await ReadDataAsync(reader));
                    await writer.WriteLineAsync("250 queued");
                }
                else if (line.StartsWith("QUIT", StringComparison.OrdinalIgnoreCase))
                {
                    await writer.WriteLineAsync("221 bye");
                    return;
                }
                else
                {
                    await writer.WriteLineAsync("250 OK");
                }
            }
        }

        private static async Task<byte[]> ReadDataAsync(StreamReader reader)
        {
            var data = new StringBuilder();

            while (await reader.ReadLineAsync() is { } line && line != ".")
            {
                data.Append(line.StartsWith("..", StringComparison.Ordinal) ? line[1..] : line).Append("\r\n");
            }

            return Encoding.UTF8.GetBytes(data.ToString());
        }
    }
}
