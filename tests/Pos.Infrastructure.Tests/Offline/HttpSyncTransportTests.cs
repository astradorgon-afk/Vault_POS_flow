using System.Net;
using System.Text;
using FluentAssertions;
using Pos.Domain.Common;
using Pos.Infrastructure.Offline;
using Pos.Shared.Sync;

namespace Pos.Infrastructure.Tests.Offline;

/// <summary>
/// The wire. One property matters here above all: a failure must never come
/// back looking like an answer, because the uploader keeps the events it was
/// not answered about and discards the ones it was.
/// </summary>
public sealed class HttpSyncTransportTests
{
    [Fact]
    public async Task AnAnsweredBatch_ComesBackWithItsVerdicts()
    {
        Guid eventId = Guid.CreateVersion7();

        HttpSyncTransport transport = Transport(HttpStatusCode.OK,
            $$"""
            {
              "serverReceivedAtUtc": "2026-09-17T02:00:00+00:00",
              "clockSkewSeconds": 1.5,
              "results": [
                { "eventId": "{{eventId}}", "outcome": "Accepted", "serverDocumentNumber": "SAL-2026-D03-000001" }
              ]
            }
            """, out _);

        Result<SyncPushResponse> answered = await transport.PushAsync(Batch(eventId), CancellationToken.None);

        answered.IsSuccess.Should().BeTrue(answered.IsFailure ? answered.Error.ToString() : string.Empty);
        answered.Value.Results.Should().ContainSingle();
        answered.Value.Results[0].Outcome.Should().Be(
            SyncOutcome.Accepted, "the verdict travels by name, as OFFLINE_SYNC.md §3 documents it");
        answered.Value.Results[0].ServerDocumentNumber.Should().Be("SAL-2026-D03-000001");
        answered.Value.ClockSkewSeconds.Should().Be(1.5d);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task AnyAnswerThatIsNotSuccess_IsAFailure_SoTheEventsAreKept(HttpStatusCode status)
    {
        HttpSyncTransport transport = Transport(status, "{}", out _);

        Result<SyncPushResponse> answered = await transport.PushAsync(Batch(Guid.CreateVersion7()), CancellationToken.None);

        answered.IsFailure.Should().BeTrue();
        answered.Error.Code.Should().Be(
            "sync.http_" + ((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task ARevokedDevice_DoesNotLoseItsQueue()
    {
        HttpSyncTransport transport = Transport(HttpStatusCode.Forbidden, "{}", out _);

        Result<SyncPushResponse> answered = await transport.PushAsync(Batch(Guid.CreateVersion7()), CancellationToken.None);

        // Deliberately the same shape as an unreachable server: the uploader
        // schedules a retry and keeps the events. A credential problem is fixed
        // by re-enrolling the register, and the trading it did before that must
        // still be there when somebody does.
        answered.IsFailure.Should().BeTrue();
        answered.Error.Type.Should().Be(ErrorType.Unavailable);
    }

    [Fact]
    public async Task AServerThatCannotBeReached_IsAFailureAndNotAnException()
    {
        using HttpClient client = new(new ThrowingHandler(new HttpRequestException("no route to host")))
        {
            BaseAddress = new Uri("https://head-office.invalid/"),
        };

        HttpSyncTransport transport = new(client);

        Result<SyncPushResponse> answered = await transport.PushAsync(Batch(Guid.CreateVersion7()), CancellationToken.None);

        answered.IsFailure.Should().BeTrue();
        answered.Error.Code.Should().Be("sync.unreachable");
    }

    [Fact]
    public async Task AClientTimeout_IsNotMistakenForTheCallerGivingUp()
    {
        using HttpClient client = new(new ThrowingHandler(new TaskCanceledException("timed out")))
        {
            BaseAddress = new Uri("https://head-office.invalid/"),
        };

        HttpSyncTransport transport = new(client);

        // A timeout surfaces as a cancellation, and treating it as one would
        // abandon the batch without scheduling anything.
        Result<SyncPushResponse> answered = await transport.PushAsync(Batch(Guid.CreateVersion7()), CancellationToken.None);

        answered.IsFailure.Should().BeTrue();
        answered.Error.Code.Should().Be("sync.timeout");
    }

    [Fact]
    public async Task AnUnreadableAnswer_IsAFailureRatherThanAnEmptyBatchOfVerdicts()
    {
        HttpSyncTransport transport = Transport(HttpStatusCode.OK, "not json at all", out _);

        Result<SyncPushResponse> answered = await transport.PushAsync(Batch(Guid.CreateVersion7()), CancellationToken.None);

        answered.IsFailure.Should().BeTrue(
            "an empty result list reads as 'no verdict for any event', which is a different and much worse thing");
        answered.Error.Code.Should().Be("sync.response_unreadable");
    }

    [Fact]
    public async Task TheBatchGoesToThePushRoute()
    {
        HttpSyncTransport transport = Transport(
            HttpStatusCode.OK,
            """{"serverReceivedAtUtc":"2026-09-17T02:00:00+00:00","clockSkewSeconds":0,"results":[]}""",
            out RecordingHandler handler);

        await transport.PushAsync(Batch(Guid.CreateVersion7()), CancellationToken.None);

        handler.LastRequestUri!.AbsolutePath.Should().Be("/api/v1/sync/push");
        handler.LastBody.Should().Contain("deviceSequence");
    }

    private static HttpSyncTransport Transport(HttpStatusCode status, string body, out RecordingHandler handler)
    {
        handler = new RecordingHandler(status, body);

        HttpClient client = new(handler) { BaseAddress = new Uri("https://head-office.invalid/") };

        return new HttpSyncTransport(client);
    }

    private static SyncPushRequest Batch(Guid eventId)
        => new(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            new DateTimeOffset(2026, 9, 17, 2, 0, 0, TimeSpan.Zero),
            1_000L,
            [new SyncPushEvent(eventId, 1, "ShiftOpened", default, "{}", "hash")]);

    private sealed class RecordingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public Uri? LastRequestUri { get; private set; }

        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class ThrowingHandler(Exception thrown) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => throw thrown;
    }
}
