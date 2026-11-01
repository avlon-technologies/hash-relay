using System.Diagnostics;
using HashRelay.Client;
using HashRelay.Contracts;
using Microsoft.Toolkit.Uwp.Notifications;
using Microsoft.Win32;

namespace HashRelay.Agent;

/// <summary>Native Windows notifications via the Windows toast API (unpackaged desktop app registration).</summary>
internal static class Toasts
{
    private static string IconPath => Path.Combine(AppContext.BaseDirectory, "hashrelay.png");

    /// <summary>
    /// Makes sure Windows knows our display name and icon. The toolkit registers the AUMID + COM activator on first use;
    /// we then pin DisplayName/IconUri so Notification Center and Settings show "HashRelay" with our logo.
    /// </summary>
    public static void EnsureRegistered()
    {
        try
        {
            // Touching the notifier performs the toolkit's registration for unpackaged apps.
            _ = ToastNotificationManagerCompat.CreateToastNotifier();
            using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{Program.Aumid}", writable: true);
            key.SetValue("DisplayName", "HashRelay");
            if (File.Exists(IconPath)) key.SetValue("IconUri", IconPath);
            key.SetValue("IconBackgroundColor", "FF7C6CFF");
            key.SetValue("ShowInSettings", 1, RegistryValueKind.DWord);
        }
        catch (Exception ex)
        {
            Log.Warn($"Toast registration: {ex.Message}");
        }
    }

    /// <summary>Builds and shows the toast. Fetches the image first (bounded), then shows; never throws for image trouble.</summary>
    /// <param name="fromBacklog">True for an item received while the device was away that still earns its own toast (high priority, or the only one).</param>
    public static async Task ShowAsync(NotificationMessage m, bool fromBacklog = false, CancellationToken ct = default)
    {
        var image = m.ImageUrl is null ? null : await ImageCache.GetAsync(m.ImageUrl, ct);
        Show(m, image, fromBacklog);
    }

    /// <summary>The attribution line: who published it (as verified by the server), the hashtags and the workspace.</summary>
    public static string Attribution(NotificationMessage m, bool fromBacklog = false)
    {
        var parts = new List<string>(4);
        if (m.Publisher?.IsKnown == true) parts.Add(m.Publisher.Name!);
        parts.Add(Tags.DisplayAll(m.Tags));
        parts.Add(m.TenantName);
        if (fromBacklog) parts.Add("while you were away");
        return string.Join(" · ", parts);
    }

    public static void Show(NotificationMessage m, string? imagePath = null, bool fromBacklog = false)
    {
        // Click target: the web page when there is more to see than the toast can show, else the publisher's link.
        var open = m.Rich && !string.IsNullOrWhiteSpace(m.DetailUrl) ? m.DetailUrl : (m.Url ?? m.DetailUrl);
        var builder = new ToastContentBuilder()
            .AddArgument("action", "open")
            .AddArgument("deliveryId", m.DeliveryId.ToString())
            .AddText(m.Title, hintMaxLines: 2)
            .AddAttributionText(Attribution(m, fromBacklog));
        if (!string.IsNullOrWhiteSpace(m.Body)) builder.AddText(m.Body, hintMaxLines: 2);
        if (!string.IsNullOrWhiteSpace(open)) builder.AddArgument("url", open);
        if (File.Exists(IconPath)) builder.AddAppLogoOverride(new Uri(IconPath), ToastGenericAppLogoCrop.None);
        if (imagePath is not null && File.Exists(imagePath)) builder.AddInlineImage(new Uri(imagePath));
        var tag = m.DeliveryId.ToString("n")[..16];
        if (ApprovalPage(m) is { } approval)
        {
            if (OneClickEnabled() && ApprovalLink.IdOf(approval) is { } approvalId)
            {
                // One-click: the agent submits the decision with this device's grant. Arguments carry ids only, never a credential.
                foreach (var (label, decision) in new[] { ("Approve", ApprovalDecisions.Approve), ("Reject", ApprovalDecisions.Reject) })
                {
                    var button = new ToastButton().SetContent(label);
                    foreach (var (k, v) in ApprovalToastArgs.For(approvalId, decision, m.DeliveryId)) button.AddArgument(k, v);
                    button.AddArgument("tag", tag).AddArgument("subject", m.Title.Length > 120 ? m.Title[..120] : m.Title);
                    builder.AddButton(button);
                }
            }
            else
            {
                // Not enrolled: open the signed-in approval page with the choice pre-selected; the person confirms it there.
                builder.AddButton(new ToastButton().SetContent("Approve…").AddArgument("action", "url").AddArgument("url", ApprovalLink.WithDecision(approval, ApprovalDecisions.Approve).ToString()));
                builder.AddButton(new ToastButton().SetContent("Reject…").AddArgument("action", "url").AddArgument("url", ApprovalLink.WithDecision(approval, ApprovalDecisions.Reject).ToString()));
            }
        }
        else if (m.Rich && !string.IsNullOrWhiteSpace(m.Url) && open != m.Url)
            builder.AddButton(new ToastButton().SetContent("Open link").AddArgument("action", "url").AddArgument("url", m.Url));

        if (m.Priority == Priorities.High) builder.SetToastScenario(ToastScenario.Reminder);
        else if (m.Priority == Priorities.Low) builder.AddAudio(new ToastAudio { Silent = true });

        builder.Show(toast =>
        {
            toast.Tag = tag;
            toast.Group = "hashrelay";
            toast.ExpirationTime = DateTimeOffset.Now.AddDays(3);
        });
    }

    private static bool OneClickEnabled()
    {
        try { return OneClick.IsEnrolled(Client.ConfigStore.Load()); }
        catch (Exception ex) { Log.Warn($"One-click state: {ex.Message}"); return false; }
    }

    private static Uri? ApprovalPage(NotificationMessage m)
    {
        try { return ApprovalLink.TryParse(m, Client.ConfigStore.Load().ServerUri, out var page) ? page : null; }
        catch (Exception ex) { Log.Warn($"Approval link: {ex.Message}"); return null; }
    }

    /// <summary>
    /// One toast for a whole backlog: "47 notifications while you were away" with a neutral breakdown, opening the
    /// Activity page where every one of them can be read. Tagged so a later catch-up replaces it in Notification Center
    /// instead of stacking.
    /// </summary>
    public static void ShowCatchUp(CatchUpSummary summary, string activityUrl)
    {
        var b = new ToastContentBuilder()
            .AddArgument("action", "url").AddArgument("url", activityUrl)
            .AddText(summary.Title)
            .AddText(summary.Describe(), hintMaxLines: 2)
            .AddAttributionText(Tags.DisplayAll(summary.Hashtags.Take(5)) + (summary.Hashtags.Count > 5 ? " …" : ""))
            .AddAudio(new ToastAudio { Silent = true });
        if (File.Exists(IconPath)) b.AddAppLogoOverride(new Uri(IconPath), ToastGenericAppLogoCrop.None);
        b.Show(toast =>
        {
            toast.Tag = "catchup";
            toast.Group = "hashrelay";
            toast.ExpirationTime = DateTimeOffset.Now.AddDays(3);
        });
    }

    public static void ShowInfo(string title, string body, string? url = null)
    {
        var b = new ToastContentBuilder().AddArgument("action", url is null ? "open" : "url").AddText(title).AddText(body);
        if (url is not null) b.AddArgument("url", url);
        if (File.Exists(IconPath)) b.AddAppLogoOverride(new Uri(IconPath), ToastGenericAppLogoCrop.None);
        b.Show();
    }
}

internal static class ToastActivation
{
    /// <summary>
    /// Routes a toast click. One-click decisions are submitted by the agent and answered with a result toast; no
    /// browser opens for them. Everything else opens its URL as before. Completes only when the work is done, so a
    /// process Windows started just for this click can wait for it before exiting.
    /// </summary>
    public static async Task HandleAsync(string argument)
    {
        try
        {
            var args = ToastArguments.Parse(argument);
            var map = args.ToDictionary(kv => kv.Key, kv => kv.Value ?? "");
            if (map.TryGetValue("action", out var action) && action == "none") return;
            if (ApprovalToastArgs.TryParse(map, out var approvalId, out var decision, out var deliveryId))
            {
                await OneClick.DecideAsync(approvalId, decision, deliveryId, map.GetValueOrDefault("tag"), map.GetValueOrDefault("subject"));
                return;
            }
            var url = map.TryGetValue("url", out var u) && !string.IsNullOrWhiteSpace(u) ? u : null;
            url ??= new Uri(Client.ConfigStore.Load().ServerUri, "dashboard/activity").ToString();
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Warn($"Toast activation: {ex.Message}"); }
    }
}
