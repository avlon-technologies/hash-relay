using HashRelay.Contracts;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HashRelay.Client;

public enum ListenerState { Disconnected, Connecting, Connected, Reconnecting }

/// <summary>
/// Realtime subscriber connection. Wraps a SignalR <see cref="HubConnection"/> with infinite reconnection and
/// re-registers the endpoint (Hello) after every (re)connect so the server can rebuild its transport routing.
/// </summary>
public sealed class RelayListener : IAsyncDisposable
{
    private readonly HubConnection _hub;
    private readonly HelloRequest _hello;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _disposed;

    public event Action<NotificationMessage>? NotificationReceived;
    public event Action<IReadOnlyList<string>>? SubscriptionsChanged;
    public event Action<ListenerState, Exception?>? StateChanged;
    public event Action<HelloResponse>? Registered;
    public event Action<string>? CredentialRevoked;

    public ListenerState State { get; private set; } = ListenerState.Disconnected;
    public HelloResponse? LastHello { get; private set; }
    public Exception? LastError { get; private set; }

    public RelayListener(RelayConfig config, string platform, string? agentVersion, ILoggerFactory? loggerFactory = null,
        Func<HttpMessageHandler, HttpMessageHandler>? configureHandler = null)
    {
        if (!config.IsLoggedIn) throw new InvalidOperationException("No API key configured.");
        _hello = new HelloRequest(config.EndpointId, config.DeviceName, platform, agentVersion);
        var url = new Uri(config.ServerUri, HubMethods.Path.TrimStart('/'));
        var builder = new HubConnectionBuilder()
            .WithUrl(url, o =>
            {
                o.AccessTokenProvider = () => Task.FromResult<string?>(config.ApiKey);
                if (configureHandler is not null) o.HttpMessageHandlerFactory = configureHandler;
            })
            .WithAutomaticReconnect(new InfiniteRetryPolicy())
            .WithServerTimeout(TimeSpan.FromSeconds(60))
            .WithKeepAliveInterval(TimeSpan.FromSeconds(15));
        if (loggerFactory is not null) builder.ConfigureLogging(l => l.Services.AddSingleton(loggerFactory));
        _hub = builder.Build();

        _hub.On<NotificationMessage>(HubMethods.NotificationReceived, m => NotificationReceived?.Invoke(m));
        _hub.On<string>(HubMethods.CredentialRevoked, r => CredentialRevoked?.Invoke(r));
        _hub.On<IReadOnlyList<string>>(HubMethods.SubscriptionsChanged, t => SubscriptionsChanged?.Invoke(t));
        _hub.Reconnecting += ex => { Set(ListenerState.Reconnecting, ex); return Task.CompletedTask; };
        _hub.Reconnected += async _ => { Set(ListenerState.Connected, null); await SayHelloAsync(CancellationToken.None); };
        _hub.Closed += async ex =>
        {
            Set(ListenerState.Disconnected, ex);
            // Automatic reconnect only gives up when the server deliberately closes the connection
            // (for example after a key is revoked). Keep trying with a pause unless we were disposed.
            if (!_disposed)
            {
                await Task.Delay(TimeSpan.FromSeconds(10));
                if (!_disposed) await StartAsync(CancellationToken.None);
            }
        };
    }

    public async Task StartAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_hub.State != HubConnectionState.Disconnected) return;
            Set(ListenerState.Connecting, null);
            while (!ct.IsCancellationRequested && !_disposed)
            {
                try
                {
                    await _hub.StartAsync(ct);
                    Set(ListenerState.Connected, null);
                    await SayHelloAsync(ct);
                    return;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    Set(ListenerState.Reconnecting, ex);
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                }
            }
        }
        finally { _gate.Release(); }
    }

    private async Task SayHelloAsync(CancellationToken ct)
    {
        try
        {
            var res = await _hub.InvokeAsync<HelloResponse>(HubMethods.Hello, _hello, ct);
            LastHello = res;
            Registered?.Invoke(res);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Set(State, ex);
        }
    }

    public Task<IReadOnlyList<string>> SubscribeAsync(IEnumerable<string> tags, CancellationToken ct = default)
        => _hub.InvokeAsync<IReadOnlyList<string>>(HubMethods.Subscribe, tags.ToArray(), ct);

    public Task<IReadOnlyList<string>> UnsubscribeAsync(IEnumerable<string> tags, CancellationToken ct = default)
        => _hub.InvokeAsync<IReadOnlyList<string>>(HubMethods.Unsubscribe, tags.ToArray(), ct);

    public async Task AckAsync(Guid deliveryId)
    {
        try { if (_hub.State == HubConnectionState.Connected) await _hub.SendAsync(HubMethods.Ack, deliveryId); }
        catch { /* best effort */ }
    }

    private void Set(ListenerState s, Exception? ex)
    {
        State = s;
        if (ex is not null) LastError = ex;
        StateChanged?.Invoke(s, ex);
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        await _hub.DisposeAsync();
        _gate.Dispose();
    }

    private sealed class InfiniteRetryPolicy : IRetryPolicy
    {
        private static readonly TimeSpan[] Steps = [TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];
        public TimeSpan? NextRetryDelay(RetryContext ctx)
            => Steps[Math.Min((int)ctx.PreviousRetryCount, Steps.Length - 1)];
    }
}
