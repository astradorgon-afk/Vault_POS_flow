using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Pos.Api.Notifications;
using Pos.Application.Identity;
using Pos.Application.Notifications;
using Pos.Domain.Common;
using Pos.Domain.Notifications;
using Pos.Infrastructure.Persistence;

namespace Pos.Api.IntegrationTests;

[Collection("api")]
public sealed class NotificationEndpointTests(PosApiFactory factory)
{
    [Fact]
    public async Task Feed_IsLocationScoped_AndPersistsPerUserReadState()
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];
        LocationId assigned = await factory.CreateLocationAsync($"NA{suffix[..4]}", "Assigned notification store");
        LocationId outside = await factory.CreateLocationAsync($"NB{suffix[..4]}", "Outside notification store");
        UserId userId = await factory.CreateUserAsync($"notify-{suffix}", Roles.StoreManager, [assigned]);

        Notification assignedAlert = Create($"notify:{suffix}:assigned", assigned);
        Notification outsideAlert = Create($"notify:{suffix}:outside", outside);
        Notification globalAlert = Create($"notify:{suffix}:global", locationId: null);
        await factory.WithServiceAsync<PosDbContext>(async context =>
        {
            context.Notifications.AddRange(assignedAlert, outsideAlert, globalAlert);
            await context.SaveChangesAsync();
        });

        using HttpClient client = factory.CreateClient();
        string token = await SignInAsync(client, $"notify-{suffix}");

        using HttpResponseMessage anonymous = await client.GetAsync("/api/v1/notifications");
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using HttpResponseMessage listed = await SendAsync(client, HttpMethod.Get, "/api/v1/notifications", token);
        listed.StatusCode.Should().Be(HttpStatusCode.OK);
        using JsonDocument feed = JsonDocument.Parse(await listed.Content.ReadAsStringAsync());
        Guid[] ids = [.. feed.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("id").GetGuid())];
        ids.Should().Contain(assignedAlert.Id.Value).And.Contain(globalAlert.Id.Value);
        ids.Should().NotContain(outsideAlert.Id.Value);

        await factory.CreateUserAsync($"notify-owner-{suffix}", Roles.Owner);
        string ownerToken = await SignInAsync(client, $"notify-owner-{suffix}");
        using HttpResponseMessage ownerList = await SendAsync(
            client,
            HttpMethod.Get,
            "/api/v1/notifications?limit=100",
            ownerToken);
        using JsonDocument ownerFeed = JsonDocument.Parse(await ownerList.Content.ReadAsStringAsync());
        ownerFeed.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("id").GetGuid())
            .Should().Contain(outsideAlert.Id.Value);

        using HttpResponseMessage marked = await SendAsync(
            client,
            HttpMethod.Post,
            $"/api/v1/notifications/{assignedAlert.Id.Value:D}/read",
            token);
        marked.StatusCode.Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage unread = await SendAsync(
            client,
            HttpMethod.Get,
            "/api/v1/notifications?unreadOnly=true",
            token);
        using JsonDocument unreadFeed = JsonDocument.Parse(await unread.Content.ReadAsStringAsync());
        unreadFeed.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("id").GetGuid())
            .Should().NotContain(assignedAlert.Id.Value);

        using HttpResponseMessage hidden = await SendAsync(
            client,
            HttpMethod.Post,
            $"/api/v1/notifications/{outsideAlert.Id.Value:D}/read",
            token);
        hidden.StatusCode.Should().Be(HttpStatusCode.NotFound);

        using HttpResponseMessage allRead = await SendAsync(
            client,
            HttpMethod.Post,
            "/api/v1/notifications/read-all",
            token);
        allRead.StatusCode.Should().Be(HttpStatusCode.OK);

        await factory.WithServiceAsync<PosDbContext>(async context =>
        {
            List<NotificationReceipt> receipts = await context.NotificationReceipts.AsNoTracking()
                .Where(receipt => receipt.UserId == userId)
                .ToListAsync();
            receipts.Should().Contain(receipt => receipt.NotificationId == assignedAlert.Id && receipt.ReadAtUtc != null);
            receipts.Should().Contain(receipt => receipt.NotificationId == globalAlert.Id && receipt.ReadAtUtc != null);
            receipts.Should().NotContain(receipt => receipt.NotificationId == outsideAlert.Id);
        });
    }

    [Fact]
    public async Task Hub_DeliversANewlyPersistedNotification_ToTheAssignedLocationGroup()
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];
        LocationId location = await factory.CreateLocationAsync($"NH{suffix[..4]}", "Live notification store");
        await factory.CreateUserAsync($"notify-live-{suffix}", Roles.StoreManager, [location]);
        using HttpClient client = factory.CreateClient();
        string token = await SignInAsync(client, $"notify-live-{suffix}");
        TaskCompletionSource<NotificationItem> received = new(TaskCreationOptions.RunContinuationsAsynchronously);

        await using HubConnection connection = new HubConnectionBuilder()
            .WithUrl(new Uri(client.BaseAddress!, NotificationHub.Route), options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
            })
            .Build();
        connection.On<NotificationItem>(NotificationHub.ClientEvent, item => received.TrySetResult(item));
        await connection.StartAsync();

        Notification alert = Create($"notify:{suffix}:live", location);
        await factory.WithServiceAsync<INotificationWriter>(async writer =>
        {
            (await writer.WriteOnceAsync(alert, CancellationToken.None)).Should().BeTrue();
        });

        NotificationItem delivered = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        delivered.Id.Should().Be(alert.Id.Value);
        delivered.LocationId.Should().Be(location.Value);
        delivered.ReadAtUtc.Should().BeNull();
    }

    private static Notification Create(string key, LocationId? locationId) => Notification.Create(
        NotificationKind.BatchExpiringSoon,
        NotificationSeverity.Warning,
        "Batch nearing expiry",
        "Review this batch before its expiry date.",
        key,
        DateTimeOffset.UtcNow,
        locationId);

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string token)
    {
        using HttpRequestMessage request = new(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (method == HttpMethod.Post)
        {
            request.Content = JsonContent.Create(new { });
        }

        return await client.SendAsync(request);
    }

    private static async Task<string> SignInAsync(HttpClient client, string userName)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { userName, password = PosApiFactory.TestPassword });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("accessToken").GetString()!;
    }
}
