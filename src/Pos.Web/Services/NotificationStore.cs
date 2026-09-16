using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;

namespace Pos.Web.Services;

/// <summary>Maintains one durable and live notification feed per Blazor circuit.</summary>
public sealed class NotificationStore(
    VaultFlowApiClient api,
    UserSession session,
    IOptions<ApiOptions> options) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<PosNotification> _items = [];
    private HubConnection? _connection;
    private bool _initialized;

    /// <summary>Raised when items, unread count, or connection state changes.</summary>
    public event Action? Changed;

    /// <summary>Gets the newest notifications currently loaded.</summary>
    public IReadOnlyList<PosNotification> Items => _items;

    /// <summary>Gets the number of visible unread notifications.</summary>
    public int UnreadCount { get; private set; }

    /// <summary>Gets whether the SignalR connection is currently active.</summary>
    public bool IsLive { get; private set; }

    /// <summary>Gets the latest user-safe loading error.</summary>
    public string? Error { get; private set; }

    /// <summary>Loads the durable feed and starts automatic real-time reconnection.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized || !session.IsAuthenticated || session.Current is null)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized || !session.IsAuthenticated || session.Current is null)
            {
                return;
            }

            _initialized = true;
            await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);

            Uri hubUri = new(options.Value.BaseAddress, "/hubs/notifications");
            _connection = new HubConnectionBuilder()
                .WithUrl(hubUri, connection => connection.AccessTokenProvider = () =>
                    Task.FromResult(session.Current?.AccessToken))
                .WithAutomaticReconnect()
                .Build();

            _connection.On<PosNotification>("notificationReceived", Receive);
            _connection.Reconnecting += _ =>
            {
                IsLive = false;
                NotifyChanged();
                return Task.CompletedTask;
            };
            _connection.Reconnected += async _ =>
            {
                IsLive = true;
                await RefreshCoreAsync(CancellationToken.None).ConfigureAwait(false);
            };
            _connection.Closed += _ =>
            {
                IsLive = false;
                NotifyChanged();
                return Task.CompletedTask;
            };

            try
            {
                await _connection.StartAsync(cancellationToken).ConfigureAwait(false);
                IsLive = true;
            }
            catch (HttpRequestException)
            {
                IsLive = false;
            }

            NotifyChanged();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Reloads the durable source of truth.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Marks one notification read and updates the local feed.</summary>
    public async Task MarkReadAsync(Guid id, CancellationToken cancellationToken = default)
    {
        PosNotification? item = _items.FirstOrDefault(notification => notification.Id == id);
        if (item is null || item.ReadAtUtc is not null)
        {
            return;
        }

        ApiResult<PosReference> result = await api
            .MarkNotificationReadAsync(id, cancellationToken)
            .ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            Error = result.Error;
            NotifyChanged();
            return;
        }

        item.ReadAtUtc = DateTimeOffset.UtcNow;
        UnreadCount = Math.Max(0, UnreadCount - 1);
        Error = null;
        NotifyChanged();
    }

    /// <summary>Marks the whole visible feed read.</summary>
    public async Task MarkAllReadAsync(CancellationToken cancellationToken = default)
    {
        ApiResult<PosMarkedReadResult> result = await api
            .MarkAllNotificationsReadAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            Error = result.Error;
            NotifyChanged();
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (PosNotification item in _items.Where(notification => notification.ReadAtUtc is null))
        {
            item.ReadAtUtc = now;
        }

        UnreadCount = 0;
        Error = null;
        NotifyChanged();
    }

    /// <summary>Stops the live connection, used before local sign-out.</summary>
    public async Task StopAsync()
    {
        if (_connection is not null)
        {
            await _connection.StopAsync().ConfigureAwait(false);
        }

        IsLive = false;
        NotifyChanged();
    }

    private async Task RefreshCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            ApiResult<PosNotificationFeed> result = await api
                .GetNotificationsAsync(unreadOnly: false, cancellationToken)
                .ConfigureAwait(false);
            if (!result.IsSuccess || result.Value is null)
            {
                Error = result.Error;
                NotifyChanged();
                return;
            }

            _items.Clear();
            _items.AddRange(result.Value.Items.OrderByDescending(item => item.CreatedAtUtc));
            UnreadCount = result.Value.UnreadCount;
            Error = null;
            NotifyChanged();
        }
        catch (HttpRequestException)
        {
            Error = "Notifications are temporarily unavailable.";
            NotifyChanged();
        }
    }

    private void Receive(PosNotification item)
    {
        if (_items.Any(existing => existing.Id == item.Id))
        {
            return;
        }

        _items.Insert(0, item);
        if (item.ReadAtUtc is null)
        {
            UnreadCount++;
        }

        NotifyChanged();
    }

    private void NotifyChanged() => Changed?.Invoke();

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }

        _gate.Dispose();
    }
}
