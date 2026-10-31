using System.Diagnostics;
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

    public static void Show(NotificationMessage m)
    {
        var builder = new ToastContentBuilder()
            .AddArgument("action", "open")
            .AddArgument("deliveryId", m.DeliveryId.ToString())
            .AddText(m.Title, hintMaxLines: 2)
            .AddAttributionText($"{Tags.DisplayAll(m.Tags)} · {m.TenantName}");
        if (!string.IsNullOrWhiteSpace(m.Body)) builder.AddText(m.Body, hintMaxLines: 2);
        if (!string.IsNullOrWhiteSpace(m.Url)) builder.AddArgument("url", m.Url);
        if (File.Exists(IconPath)) builder.AddAppLogoOverride(new Uri(IconPath), ToastGenericAppLogoCrop.None);

        if (m.Priority == Priorities.High) builder.SetToastScenario(ToastScenario.Reminder);
        else if (m.Priority == Priorities.Low) builder.AddAudio(new ToastAudio { Silent = true });

        builder.Show(toast =>
        {
            toast.Tag = m.DeliveryId.ToString("n")[..16];
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
    public static void Handle(string argument)
    {
        try
        {
            var args = ToastArguments.Parse(argument);
            var url = args.TryGetValue("url", out var u) && !string.IsNullOrWhiteSpace(u) ? u : null;
            url ??= new Uri(Client.ConfigStore.Load().ServerUri, "dashboard/activity").ToString();
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Warn($"Toast activation: {ex.Message}"); }
    }
}
