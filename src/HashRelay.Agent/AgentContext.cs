using System.Diagnostics;
using HashRelay.Client;
using HashRelay.Contracts;
using Microsoft.Toolkit.Uwp.Notifications;

namespace HashRelay.Agent;

/// <summary>
/// Tray application: owns the realtime listener, shows toasts, and offers pairing when the device has no credential.
/// Reconnects forever; re-reads config when the CLI changes it (e.g. after <c>hashrelay login</c>).
/// </summary>
internal sealed class AgentContext : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly Control _ui;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _startupItem;
    private readonly ToolStripMenuItem _pairItem;
    private readonly ToolStripMenuItem _oneClickItem;
    private bool _oneClickBusy;
    private readonly FileSystemWatcher? _configWatcher;
    private readonly System.Windows.Forms.Timer _debounce;
    /// <summary>Live deliveries toast at once; the backlog replayed on reconnect becomes one catch-up summary. Also de-duplicates by notification id.</summary>
    private readonly NotificationPresenter _presenter;
    private RelayListener? _listener;
    private RelayConfig _config;
    private CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private PairForm? _pairForm;
    private readonly bool _silent;
    private bool _announced;
    private int _unauthorizedStreak;
    private Icon? _iconIdle, _iconOn;

    /// <summary>Consecutive 401s on the same token before the agent stops hammering the server and asks to be paired again.</summary>
    private const int UnauthorizedGiveUp = 6;
    /// <summary>How long to wait for the CLI to write the replacement token after the server says ours was replaced.</summary>
    private static readonly TimeSpan ReplacedTokenWait = TimeSpan.FromSeconds(20);

    public AgentContext(bool silent)
    {
        _silent = silent;
        _config = LoadConfig();
        LoadIcons();

        // Everything that touches the tray or the config is marshalled onto the UI thread through this hidden
        // control. The context menu's handle only exists once the menu has been opened, so it is not a reliable
        // target; a WinForms timer started from a thread-pool thread never ticks, which is how a config change
        // written by the CLI used to go unnoticed.
        _ui = new Control();
        _ = _ui.Handle;

        _statusItem = new ToolStripMenuItem("Starting…") { Enabled = false };
        _pairItem = new ToolStripMenuItem("Pair this device…", null, (_, _) => ShowPairing());
        _oneClickItem = new ToolStripMenuItem("Enable one-click approvals…", null, async (_, _) => await ToggleOneClickAsync());
        _startupItem = new ToolStripMenuItem("Start with Windows", null, (_, _) => { Startup.Toggle(); RefreshMenu(); }) { CheckOnClick = false };
        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Open dashboard", null, (_, _) => OpenUrl("dashboard")));
        menu.Items.Add(new ToolStripMenuItem("Manage subscriptions…", null, (_, _) => OpenUrl("dashboard/devices")));
        menu.Items.Add(new ToolStripMenuItem("Send test notification", null, async (_, _) => await SendTestAsync()));
        menu.Items.Add(_pairItem);
        menu.Items.Add(_oneClickItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_startupItem);
        menu.Items.Add(new ToolStripMenuItem("Open log folder", null, (_, _) => OpenFolder(ConfigStore.LogDirectory)));
        menu.Items.Add(new ToolStripMenuItem($"About HashRelay {AgentInfo.Version}", null, (_, _) => OpenUrl("")));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Quit", null, (_, _) => Quit()));

        _tray = new NotifyIcon { Icon = _iconIdle, Text = "HashRelay", Visible = true, ContextMenuStrip = menu };
        _tray.DoubleClick += (_, _) => OpenUrl("dashboard");

        Toasts.EnsureRegistered();
        ImageCache.Prune();
        ToastNotificationManagerCompat.OnActivated += e => _ = ToastActivation.HandleAsync(e.Argument);
        _presenter = new NotificationPresenter(ShowToast, ShowCatchUp);

        _debounce = new System.Windows.Forms.Timer { Interval = 600 };
        _debounce.Tick += (_, _) => { _debounce.Stop(); OnConfigChanged(); };
        try
        {
            Directory.CreateDirectory(ConfigStore.Directory);
            _configWatcher = new FileSystemWatcher(ConfigStore.Directory, "config.json") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size, EnableRaisingEvents = true };
            _configWatcher.Changed += (_, _) => Invoke(() => { _debounce.Stop(); _debounce.Start(); });
            _configWatcher.Created += (_, _) => Invoke(() => { _debounce.Stop(); _debounce.Start(); });
            _configWatcher.Renamed += (_, _) => Invoke(() => { _debounce.Stop(); _debounce.Start(); });
        }
        catch (Exception ex) { Log.Warn($"Config watcher unavailable: {ex.Message}"); }

        RefreshMenu();
        _ = StartAsync();
    }

    private static RelayConfig LoadConfig()
    {
        var cfg = ConfigStore.Load();
        if (!ConfigStore.Exists)
        {
            cfg.Server = AgentInfo.DefaultServer;
            ConfigStore.Save(cfg); // persist the generated endpoint id so it stays stable across restarts/upgrades
        }
        return cfg;
    }

    private void LoadIcons()
    {
        try
        {
            var ico = Path.Combine(AppContext.BaseDirectory, "hashrelay.ico");
            _iconOn = File.Exists(ico) ? new Icon(ico, 32, 32) : SystemIcons.Application;
            _iconIdle = _iconOn; // same mark; status is conveyed in the tooltip/menu
        }
        catch { _iconOn = _iconIdle = SystemIcons.Application; }
    }

    private void Invoke(Action a)
    {
        if (_ui is { IsDisposed: false, IsHandleCreated: true }) _ui.BeginInvoke(a);
        else a();
    }

    // ------------------------------------------------------------------ connection

    /// <summary>
    /// (Re)creates the listener for the current <see cref="_config"/>. Callers may overlap (config watcher, revoke
    /// handler, pairing form); the set-up is serialised so two listeners are never alive at once, and a later call
    /// simply cancels the one before it.
    /// </summary>
    private async Task StartAsync()
    {
        RelayListener listener;
        CancellationToken ct;
        await _startGate.WaitAsync();
        try
        {
            await StopListenerAsync();
            _cts = new CancellationTokenSource();
            ct = _cts.Token;
            _unauthorizedStreak = 0;
            if (!_config.IsLoggedIn)
            {
                SetStatus("Not paired", "HashRelay — not paired. Right-click to pair this device.");
                if (!_silent) ShowPairing();
                return;
            }

            try
            {
                listener = new RelayListener(_config, "windows", AgentInfo.Version);
            }
            catch (Exception ex)
            {
                Log.Error("Listener init failed", ex);
                SetStatus("Configuration error", $"HashRelay — {ex.Message}");
                return;
            }
            _listener = listener;
            Wire(listener, _config.ApiKey!);
        }
        finally { _startGate.Release(); }

        try { await listener.StartAsync(ct); }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { Log.Error("Start failed", ex); }
    }

    private void Wire(RelayListener listener, string tokenInUse)
    {
        listener.StateChanged += (s, ex) => Invoke(() =>
        {
            if (!ReferenceEquals(listener, _listener)) return; // a newer listener has replaced this one
            switch (s)
            {
                case ListenerState.Connected: SetStatus($"Connected · {_config.TenantName ?? _config.Server}", $"HashRelay — connected to {_config.TenantName ?? "workspace"}"); break;
                case ListenerState.Connecting: SetStatus("Connecting…", "HashRelay — connecting"); break;
                case ListenerState.Reconnecting: SetStatus("Reconnecting…", "HashRelay — reconnecting"); break;
                default: SetStatus("Disconnected", "HashRelay — disconnected"); break;
            }
            if (ex is null) { if (s == ListenerState.Connected) _unauthorizedStreak = 0; return; }
            Log.Warn($"{s}: {ex.GetType().Name}: {ex.Message}");
            if (ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized }) OnUnauthorized(tokenInUse);
        });
        listener.Registered += hello => Invoke(() =>
        {
            if (!ReferenceEquals(listener, _listener)) return;
            _unauthorizedStreak = 0;
            _config.TenantName = hello.Tenant.Name;
            _config.TenantSlug = hello.Tenant.Slug;
            SetStatus($"Connected · {hello.Tenant.Name}", $"HashRelay — {hello.Tenant.Name} · {(hello.Subscriptions.Count == 0 ? "no subscriptions" : Tags.DisplayAll(hello.Subscriptions))}");
            Log.Info($"Registered as {hello.Name} in {hello.Tenant.Name}; subscriptions: {Tags.DisplayAll(hello.Subscriptions)}; replayed {hello.PendingReplay}");
            // The server sends the backlog before it answers Hello, so the batch is complete now; present it as one catch-up.
            _presenter.Flush();
            if (!_silent && !_announced)
            {
                // Launched by hand or by the installer while already paired: say so, otherwise a reinstall looks like it did nothing.
                _announced = true;
                Toasts.ShowInfo("HashRelay is running", $"Connected to {hello.Tenant.Name} as {hello.Name}. Right-click the tray icon to pair with a different workspace.");
            }
        });
        listener.NotificationReceived += OnNotification;
        listener.SubscriptionsChanged += tags => Invoke(() =>
        {
            if (!ReferenceEquals(listener, _listener)) return;
            _tray.Text = Truncate($"HashRelay — {_config.TenantName} · {(tags.Count == 0 ? "no subscriptions" : Tags.DisplayAll(tags))}");
            Log.Info($"Subscriptions changed: {Tags.DisplayAll(tags)}");
        });
        listener.CredentialRevoked += reason => Invoke(async () =>
        {
            if (!ReferenceEquals(listener, _listener)) return;
            Log.Warn($"Credential revoked: {reason}");
            await StopListenerAsync();
            if (reason == "replaced" && await AdoptReplacementTokenAsync(tokenInUse)) return;
            MarkRevoked();
        });
    }

    /// <summary>
    /// "replaced" means the CLI paired this same device again. The server tells us the moment it issues the new
    /// token, which is before the CLI has finished polling for it and writing it to config.json, so reading the
    /// file straight away hands back the token that was just revoked. Wait for the file to actually change.
    /// </summary>
    private async Task<bool> AdoptReplacementTokenAsync(string revokedToken)
    {
        var deadline = DateTime.UtcNow + ReplacedTokenWait;
        while (DateTime.UtcNow < deadline)
        {
            var fresh = ConfigStore.Load();
            if (fresh.IsLoggedIn && fresh.ApiKey != revokedToken)
            {
                if (_config.ApiKey == fresh.ApiKey && _listener is not null) return true; // the config watcher got there first
                Log.Info("Replacement token found in config; reconnecting");
                _config = fresh;
                RefreshMenu();
                _ = StartAsync();
                return true;
            }
            await Task.Delay(250);
        }
        Log.Warn($"No replacement token appeared within {ReplacedTokenWait.TotalSeconds:0}s");
        return false;
    }

    /// <summary>
    /// A 401 while (re)connecting means the server no longer accepts our token. If the CLI has meanwhile written a
    /// different one, switch to it; otherwise stop after a few attempts instead of retrying a dead token forever.
    /// </summary>
    private void OnUnauthorized(string tokenInUse)
    {
        var fresh = ConfigStore.Load();
        if (fresh.IsLoggedIn && fresh.ApiKey != tokenInUse)
        {
            Log.Info("Token on disk differs from the one being rejected; reconnecting with it");
            _config = fresh;
            RefreshMenu();
            _ = StartAsync();
            return;
        }
        if (++_unauthorizedStreak < UnauthorizedGiveUp) return;
        Log.Warn($"Token rejected {_unauthorizedStreak} times in a row; giving up until this device is paired again");
        _ = Task.Run(async () => { await StopListenerAsync(); Invoke(MarkRevoked); });
    }

    private void MarkRevoked()
    {
        _unauthorizedStreak = 0;
        _config.ApiKey = null;
        ConfigStore.Save(_config);
        SetStatus("Access revoked", "HashRelay — access revoked. Pair again to continue.");
        Toasts.ShowInfo("HashRelay access was revoked", "This device's token was revoked in the dashboard. Pair again to keep receiving notifications.");
        RefreshMenu();
    }

    private async Task StopListenerAsync()
    {
        _cts.Cancel();
        if (_listener is { } l)
        {
            _listener = null;
            try { await l.DisposeAsync(); } catch { }
        }
    }

    private void OnNotification(NotificationMessage m)
    {
        // Every delivery is acknowledged as received, whether it becomes a toast, part of a summary or a duplicate:
        // the ack says "this device has it", not "a toast was shown".
        var fresh = _presenter.Receive(m);
        if (!fresh) Log.Info($"Duplicate suppressed: {m.NotificationId}");
        _ = _listener?.AckAsync(m.DeliveryId);
    }

    private void ShowToast(NotificationMessage m, bool fromBacklog)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Toasts.ShowAsync(m, fromBacklog);
                Log.Info($"Shown: {Tags.DisplayAll(m.Tags)} \"{m.Title}\"{(m.Publisher?.IsKnown == true ? $" from {m.Publisher.Name}" : "")}{(fromBacklog ? " (while away)" : "")}{(m.ImageUrl is null ? "" : " +image")}");
            }
            catch (Exception ex) { Log.Error("Toast failed", ex); }
        });
    }

    private void ShowCatchUp(CatchUpSummary summary)
    {
        try
        {
            Toasts.ShowCatchUp(summary, new Uri(_config.ServerUri, "dashboard/activity").ToString());
            Log.Info($"Catch-up: {summary.Describe()} ({Tags.DisplayAll(summary.Hashtags)})");
        }
        catch (Exception ex) { Log.Error("Catch-up toast failed", ex); }
    }

    private void OnConfigChanged()
    {
        var fresh = ConfigStore.Load();
        if (fresh.ApiKey == _config.ApiKey && fresh.Server == _config.Server && fresh.EndpointId == _config.EndpointId && fresh.DeviceName == _config.DeviceName)
        {
            _config = fresh; RefreshMenu(); return;
        }
        Log.Info("Configuration changed; reconnecting");
        _config = fresh;
        RefreshMenu();
        _ = StartAsync();
    }

    // ------------------------------------------------------------------ UI

    private void SetStatus(string menuText, string tooltip)
    {
        _statusItem.Text = menuText;
        _tray.Text = Truncate(tooltip);
        _tray.Icon = _iconOn;
    }

    private static string Truncate(string s) => s.Length > 120 ? s[..117] + "…" : s;

    private void RefreshMenu()
    {
        _startupItem.Checked = Startup.IsEnabled();
        _pairItem.Text = _config.IsLoggedIn ? "Pair with a different workspace…" : "Pair this device…";
        _oneClickItem.Enabled = _config.IsLoggedIn && !_oneClickBusy;
        _oneClickItem.Text = OneClick.IsEnrolled(_config) ? "Disable one-click approvals" : "Enable one-click approvals…";
    }

    private void ShowPairing()
    {
        if (_pairForm is { IsDisposed: false }) { _pairForm.Activate(); return; }
        _pairForm = new PairForm(_config);
        _pairForm.Paired += cfg => Invoke(() => { _config = cfg; RefreshMenu(); _ = StartAsync(); });
        _pairForm.FormClosed += (_, _) => _pairForm = null;
        _pairForm.Show();
        _pairForm.Activate();
    }

    /// <summary>
    /// Enabling opens the website, where the person who paired this PC signs in, reads the unlocked-PC warning and grants
    /// it. Disabling revokes on the server and removes the sealed grant from this PC.
    /// </summary>
    private async Task ToggleOneClickAsync()
    {
        if (!_config.IsLoggedIn || _oneClickBusy) return;
        _oneClickBusy = true;
        RefreshMenu();
        try
        {
            if (OneClick.IsEnrolled(_config)) await OneClick.DisableAsync(_config, _cts.Token);
            else await OneClick.EnableAsync(_config, _cts.Token);
        }
        catch (Exception ex)
        {
            Log.Error("One-click approvals", ex);
            Toasts.ShowInfo("One-click approvals", ex is HashRelayApiException api ? api.Message : "Couldn't reach HashRelay. Try again in a moment.");
        }
        finally
        {
            _oneClickBusy = false;
            Invoke(RefreshMenu);
        }
    }

    private async Task SendTestAsync()
    {
        if (!_config.IsLoggedIn) { ShowPairing(); return; }
        try
        {
            using var api = new HashRelayApi(_config.ServerUri, _config.ApiKey, userAgent: $"hashrelay-agent/{AgentInfo.Version}");
            var me = await api.MeAsync();
            if (me.Credential.Scopes.Contains(Scopes.Subscribe))
            {
                var subs = await api.GetSubscriptionsAsync(_config.EndpointId);
                if (!subs.Tags.Contains("test")) await api.AddSubscriptionsAsync(_config.EndpointId, ["test"]);
            }
            if (!me.Credential.Scopes.Contains(Scopes.Publish))
            {
                Toasts.ShowInfo("This device can't publish", "Pair again and allow publishing, or run `hashrelay test --key <api key>`.");
                return;
            }
            await api.PublishAsync(new PublishRequest(["test"], "HashRelay test", $"Notifications are working on {_config.DeviceName}.", Priority: Priorities.High));
        }
        catch (Exception ex)
        {
            Log.Error("Test failed", ex);
            Toasts.ShowInfo("Test failed", ex.Message);
        }
    }

    private void OpenUrl(string path)
    {
        try { Process.Start(new ProcessStartInfo(new Uri(_config.ServerUri, path).ToString()) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn($"Open URL: {ex.Message}"); }
    }

    private static void OpenFolder(string path)
    {
        try { Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
    }

    private void Quit()
    {
        _tray.Visible = false;
        _ = StopListenerAsync();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _configWatcher?.Dispose();
            _tray.Dispose();
            _debounce.Dispose();
            _presenter.Dispose();
            _ui.Dispose();
            _startGate.Dispose();
        }
        base.Dispose(disposing);
    }
}
