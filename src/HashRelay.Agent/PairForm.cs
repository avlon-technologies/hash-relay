using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Net.Http.Json;
using HashRelay.Client;
using HashRelay.Contracts;

namespace HashRelay.Agent;

/// <summary>
/// Pairing window: requests a device code, opens the browser and waits for approval. Also allows changing the server.
/// </summary>
internal sealed class PairForm : Form
{
    private readonly RelayConfig _config;
    private readonly Label _title = new();
    private readonly Label _subtitle = new();
    private readonly Label _code = new();
    private readonly Label _status = new();
    private readonly LinkLabel _link = new();
    private readonly Button _primary = new();
    private readonly Button _cancel = new();
    private readonly TextBox _server = new();
    private readonly Label _serverLabel = new();
    private CancellationTokenSource? _cts;

    public event Action<RelayConfig>? Paired;

    private static readonly Color Bg = Color.FromArgb(0x12, 0x18, 0x26);
    private static readonly Color Fg = Color.FromArgb(0xE9, 0xED, 0xF5);
    private static readonly Color Muted = Color.FromArgb(0xAA, 0xB3, 0xC6);
    private static readonly Color Accent = Color.FromArgb(0x7C, 0x6C, 0xFF);
    private static readonly Color Accent2 = Color.FromArgb(0x22, 0xD3, 0xEE);

    public PairForm(RelayConfig config)
    {
        _config = config;
        Text = "Pair with HashRelay";
        // The app runs PerMonitorV2, so WinForms treats coordinates set here as being in the *current* monitor DPI
        // (it resets AutoScaleDimensions to DeviceDpi on handle creation; auto-scaling only kicks in for later DPI
        // changes). The layout below is designed at 96 dpi, so scale every measure by hand from DeviceDpi.
        AutoScaleMode = AutoScaleMode.Dpi;
        var scale = DeviceDpi / 96f;
        int S(int v) => (int)Math.Round(v * scale);
        Point P(int x, int y) => new(S(x), S(y));
        Size Z(int w, int h) => new(S(w), S(h));

        SuspendLayout();
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = Z(460, 364);
        BackColor = Bg; ForeColor = Fg;
        Font = new Font("Segoe UI", 10f); // point sizes already follow the DPI
        try { Icon = new Icon(Path.Combine(AppContext.BaseDirectory, "hashrelay.ico")); } catch { }

        var header = new Panel { Dock = DockStyle.Top, Height = S(92) };
        header.Paint += (_, e) =>
        {
            using var brush = new LinearGradientBrush(new Rectangle(0, 0, Math.Max(1, header.Width), Math.Max(1, header.Height)), Color.FromArgb(40, Accent), Color.FromArgb(0, Bg), 0f);
            e.Graphics.FillRectangle(brush, header.ClientRectangle);
            var logoRect = new Rectangle(S(24), S(24), S(40), S(40));
            using var logo = new LinearGradientBrush(logoRect, Accent, Accent2, 45f);
            using var path = Rounded(logoRect, S(10));
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.FillPath(logo, path);
            using var f = new Font("Consolas", 20f, FontStyle.Bold);
            e.Graphics.DrawString("#", f, Brushes.White, new RectangleF(logoRect.X, logoRect.Y - S(2), logoRect.Width, logoRect.Height + S(4)),
                new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
        };
        _title.Text = "Pair this device"; _title.Font = new Font("Segoe UI Semibold", 15f); _title.AutoSize = true; _title.Location = P(76, 20); _title.BackColor = Color.Transparent;
        _subtitle.Text = _config.DeviceName; _subtitle.ForeColor = Muted; _subtitle.AutoSize = true; _subtitle.Location = P(78, 56); _subtitle.BackColor = Color.Transparent;
        header.Controls.Add(_title); header.Controls.Add(_subtitle);

        _serverLabel.Text = "Server"; _serverLabel.ForeColor = Muted; _serverLabel.Location = P(24, 104); _serverLabel.AutoSize = true;
        _server.Text = _config.Server; _server.Location = P(24, 128); _server.Width = S(412); _server.BackColor = Color.FromArgb(0x0D, 0x11, 0x1B); _server.ForeColor = Fg; _server.BorderStyle = BorderStyle.FixedSingle;

        _code.Text = "— — — —"; _code.Font = new Font("Consolas", 26f, FontStyle.Bold); _code.TextAlign = ContentAlignment.MiddleCenter;
        _code.Location = P(24, 168); _code.Size = Z(412, 56); _code.BackColor = Color.FromArgb(0x0D, 0x11, 0x1B); _code.ForeColor = Fg;

        _status.Text = "Click Pair to get a code, then approve it in your browser."; _status.ForeColor = Muted; _status.Location = P(24, 234); _status.Size = Z(412, 44);
        _link.Text = ""; _link.Location = P(24, 278); _link.Size = Z(412, 22); _link.LinkColor = Accent2; _link.ActiveLinkColor = Accent; _link.LinkBehavior = LinkBehavior.HoverUnderline;
        _link.AutoEllipsis = true;
        _link.LinkClicked += (_, _) => { if (_link.Tag is string url) Open(url); };

        _primary.Text = "Pair in browser"; _primary.Location = P(292, 312); _primary.Size = Z(144, 36); _primary.FlatStyle = FlatStyle.Flat;
        _primary.BackColor = Accent; _primary.ForeColor = Color.White; _primary.FlatAppearance.BorderSize = 0; _primary.Font = new Font("Segoe UI Semibold", 10f);
        _primary.Click += async (_, _) => await PairAsync();
        _cancel.Text = "Close"; _cancel.Location = P(188, 312); _cancel.Size = Z(96, 36); _cancel.FlatStyle = FlatStyle.Flat; _cancel.ForeColor = Muted;
        _cancel.FlatAppearance.BorderColor = Color.FromArgb(0x2A, 0x33, 0x4A); _cancel.Click += (_, _) => Close();

        Controls.AddRange([header, _serverLabel, _server, _code, _status, _link, _cancel, _primary]);
        AcceptButton = _primary;
        FormClosed += (_, _) => _cts?.Cancel();
        ResumeLayout(false);
        PerformLayout();
    }

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var p = new GraphicsPath(); var d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90); p.CloseFigure();
        return p;
    }

    private async Task PairAsync()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _primary.Enabled = false; _server.Enabled = false;
        try
        {
            var server = _server.Text.Trim();
            if (!server.Contains("://")) server = "https://" + server;
            if (!Uri.TryCreate(server, UriKind.Absolute, out var uri)) { _status.Text = "That server address is not valid."; return; }
            _config.Server = uri.GetLeftPart(UriPartial.Authority);

            using var http = new HttpClient { BaseAddress = _config.ServerUri, Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"hash-relay-agent/{AgentInfo.Version}");
            _status.Text = "Requesting a pairing code…";
            var res = await http.PostAsJsonAsync("api/v1/device/code", new DeviceCodeRequest(_config.EndpointId, _config.DeviceName, "windows", AgentInfo.Version, "agent"), ct);
            if (!res.IsSuccessStatusCode) { _status.Text = $"The server refused the request ({(int)res.StatusCode})."; return; }
            var code = (await res.Content.ReadFromJsonAsync<DeviceCodeResponse>(ct))!;
            _code.Text = code.UserCode;
            _link.Text = code.VerificationUrlComplete; _link.Tag = code.VerificationUrlComplete;
            _status.Text = "Approve this code in your browser. Waiting…";
            Open(code.VerificationUrlComplete);

            var deadline = DateTime.UtcNow.AddSeconds(code.ExpiresInSeconds);
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(2, code.IntervalSeconds)), ct);
                using var poll = await http.PostAsJsonAsync("api/v1/device/token", new DeviceTokenRequest(code.DeviceCode), ct);
                if (!poll.IsSuccessStatusCode) continue;
                var r = (await poll.Content.ReadFromJsonAsync<DeviceTokenResponse>(ct))!;
                if (r.Status == "pending") continue;
                if (r.Status == "denied") { _status.Text = "The request was denied in the dashboard."; return; }
                if (r.Status == "expired") { _status.Text = "The code expired. Click Pair to get a new one."; return; }
                _config.ApiKey = r.Token; _config.TenantName = r.Tenant!.Name; _config.TenantSlug = r.Tenant.Slug; _config.KeyLabel = r.Credential!.Label; _config.LoggedInAt = DateTimeOffset.UtcNow;
                ConfigStore.Save(_config);
                _status.Text = $"Paired with {r.Tenant.Name}. You're all set!";
                _code.ForeColor = Color.FromArgb(0x34, 0xD3, 0x99);
                Paired?.Invoke(_config);
                Toasts.ShowInfo("HashRelay is connected", $"This device is paired with {r.Tenant.Name}. Subscribe to hashtags from the dashboard or with hash-relay subscribe.");
                await Task.Delay(1200, ct);
                Close();
                return;
            }
            _status.Text = "The code expired. Click Pair to get a new one.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error("Pairing failed", ex);
            _status.Text = $"Could not reach the server: {ex.Message}";
        }
        finally { _primary.Enabled = true; _server.Enabled = true; }
    }

    private static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }
}
