using System.Diagnostics;
using HashRelay.Client;
using HashRelay.Contracts;
using Microsoft.Toolkit.Uwp.Notifications;

namespace HashRelay.Agent;

/// <summary>
/// Tray application: owns the realtime listener, shows toasts, and offers pairing when the device has no credential.
/// Reconnects forever; re-reads config when the CLI changes it (e.g. after <c>hash-relay login</c>).
/// </summary>
internal sealed class AgentContext : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _startupItem;
    private readonly ToolStripMenuItem _pairItem;
    private readonly FileSystemWatcher? _configWatcher;
    private readonly System.Windows.Forms.Timer _debounce;
    private readonly HashSet<Guid> _recent = [];
    private readonly Queue<Guid> _recentOrder = new();
    private RelayListener? _listener;
    private RelayConfig _config;
    private CancellationTokenSource _cts = new();
    private PairForm? _pairForm;
    private readonly bool _silent;
    private bool _announced;
    private Icon? _iconIdle, _iconOn;

    public AgentContext(bool silent)
    {
        _silent = silent;
        _config = LoadConfig();
        LoadIcons();

        _statusItem = new ToolStripMenuItem("Starting…") { Enabled = false };
        _pairItem = new ToolStripMenuItem("Pair this device…", null, (_, _) => ShowPairing());
        _startupItem = new ToolStripMenuItem("Start with Windows", null, (_, _) => { Startup.Toggle(); RefreshMenu(); }) { CheckOnClick = false };
        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Open dashboard", null, (_, _) => OpenUrl("dashboard")));
        menu.Items.Add(new ToolStripMenuItem("Manage subscriptions…", null, (_, _) => OpenUrl("dashboard/devices")));
        menu.Items.Add(new ToolStripMenuItem("Send test notification", null, async (_, _) => await SendTestAsync()));
        menu.Items.Add(_pairItem);
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
        ToastNotificationManagerCompat.OnActivated += e => ToastActivation.Handle(e.Argument);

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
        if (_tray.ContextMenuStrip is { IsHandleCreated: true } m) m.BeginInvoke(a);
        else a();
    }

    // ------------------------------------------------------------------ connection

    private async Task StartAsync()
    {
        await StopListenerAsync();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        if (!_config.IsLoggedIn)
        {
            SetStatus("Not paired", "HashRelay — not paired. Right-click to pair this device.");
            if (!_silent) ShowPairing();
            return;
        }

        try
        {
            _listener = new RelayListener(_config, "windows", AgentInfo.Version);
        }
        catch (Exception ex)
        {
            Log.Error("Listener init failed", ex);
            SetStatus("Configuration error", $"HashRelay — {ex.Message}");
            return;
        }
        _listener.StateChanged += (s, ex) => Invoke(() =>
        {
            switch (s)
            {
                case ListenerState.Connected: SetStatus($"Connected · {_config.TenantName ?? _config.Server}", $"HashRelay — connected to {_config.TenantName ?? "workspace"}"); break;
                case ListenerState.Connecting: SetStatus("Connecting…", "HashRelay — connecting"); break;
                case ListenerState.Reconnecting: SetStatus("Reconnecting…", "HashRelay — reconnecting"); break;
                default: SetStatus("Disconnected", "HashRelay — disconnected"); break;
            }
            if (ex is not null) Log.Warn($"{s}: {ex.GetType().Name}: {ex.Message}");
        });
        _listener.Registered += hello => Invoke(() =>
        {
            _config.TenantName = hello.Tenant.Name;
            _config.TenantSlug = hello.Tenant.Slug;
            SetStatus($"Connected · {hello.Tenant.Name}", $"HashRelay — {hello.Tenant.Name} · {(hello.Subscriptions.Count == 0 ? "no subscriptions" : Tags.DisplayAll(hello.Subscriptions))}");
            Log.Info($"Registered as {hello.Name} in {hello.Tenant.Name}; subscriptions: {Tags.DisplayAll(hello.Subscriptions)}; replayed {hello.PendingReplay}");
            if (!_silent && !_announced)
            {
                // Launched by hand or by the installer while already paired: say so, otherwise a reinstall looks like it did nothing.
                _announced = true;
                Toasts.ShowInfo("HashRelay is running", $"Connected to {hello.Tenant.Name} as {hello.Name}. Right-click the tray icon to pair with a different workspace.");
            }
        });
        _listener.NotificationReceived += OnNotification;
        _listener.SubscriptionsChanged += tags => Invoke(() =>
        {
            _tray.Text = Truncate($"HashRelay — {_config.TenantName} · {(tags.Count == 0 ? "no subscriptions" : Tags.DisplayAll(tags))}");
            Log.Info($"Subscriptions changed: {Tags.DisplayAll(tags)}");
        });
        _listener.CredentialRevoked += reason => Invoke(async () =>
        {
            Log.Warn($"Credential revoked: {reason}");
            await StopListenerAsync();
            if (reason == "replaced")
            {
                // The CLI re-paired this device; pick up the new token.
                _config = ConfigStore.Load();
                if (_config.IsLoggedIn) { _ = StartAsync(); return; }
            }
            _config.ApiKey = null;
            ConfigStore.Save(_config);
            SetStatus("Access revoked", "HashRelay — access revoked. Pair again to continue.");
            Toasts.ShowInfo("HashRelay access was revoked", "This device's token was revoked in the dashboard. Pair again to keep receiving notifications.");
            RefreshMenu();
        });

        try { await _listener.StartAsync(ct); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Error("Start failed", ex); }
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
        // Suppress duplicates (e.g. a replay racing a live push) by notification id, remembering the last 200.
        lock (_recent)
        {
            if (!_recent.Add(m.NotificationId)) { _ = _listener?.AckAsync(m.DeliveryId); return; }
            _recentOrder.Enqueue(m.NotificationId);
            while (_recentOrder.Count > 200) _recent.Remove(_recentOrder.Dequeue());
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await Toasts.ShowAsync(m);
                Log.Info($"Shown: {Tags.DisplayAll(m.Tags)} \"{m.Title}\"{(m.Replayed ? " (replayed)" : "")}{(m.ImageUrl is null ? "" : " +image")}");
            }
            catch (Exception ex) { Log.Error("Toast failed", ex); }
        });
        _ = _listener?.AckAsync(m.DeliveryId);
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

    private async Task SendTestAsync()
    {
        if (!_config.IsLoggedIn) { ShowPairing(); return; }
        try
        {
            using var api = new HashRelayApi(_config.ServerUri, _config.ApiKey, userAgent: $"hash-relay-agent/{AgentInfo.Version}");
            var me = await api.MeAsync();
            if (me.Credential.Scopes.Contains(Scopes.Subscribe))
            {
                var subs = await api.GetSubscriptionsAsync(_config.EndpointId);
                if (!subs.Tags.Contains("test")) await api.AddSubscriptionsAsync(_config.EndpointId, ["test"]);
            }
            if (!me.Credential.Scopes.Contains(Scopes.Publish))
            {
                Toasts.ShowInfo("This device can't publish", "Pair again and allow publishing, or run `hash-relay test --key <api key>`.");
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
        }
        base.Dispose(disposing);
    }
}
