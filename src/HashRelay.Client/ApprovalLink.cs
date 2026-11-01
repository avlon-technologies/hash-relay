using HashRelay.Contracts;

namespace HashRelay.Client;

/// <summary>
/// Recognises a notification that links to an approval on the agent's own HashRelay server (<c>{server}/approvals/{id}</c>)
/// so the toast can offer Approve and Reject. The buttons only open that first-party page with the choice pre-selected;
/// the decision is recorded when the signed-in person confirms it there. A device token never decides, so a forged link
/// in an ordinary notification gains nothing a click on the toast would not.
/// </summary>
public static class ApprovalLink
{
    public static bool TryParse(NotificationMessage m, Uri server, out Uri page)
    {
        page = null!;
        if (!Uri.TryCreate(m.Url, UriKind.Absolute, out var url)) return false;
        if (url.Scheme != server.Scheme || !string.Equals(url.Host, server.Host, StringComparison.OrdinalIgnoreCase) || url.Port != server.Port) return false;
        var segments = url.AbsolutePath.Trim('/').Split('/');
        if (segments is not ["approvals", var id] || !Guid.TryParse(id, out var guid)) return false;
        page = new Uri(server, $"approvals/{guid}");
        return true;
    }

    /// <summary>The page with <paramref name="decision"/> (approve or reject) pre-selected. Opening it records nothing.</summary>
    public static Uri WithDecision(Uri page, string decision) => new($"{page.GetLeftPart(UriPartial.Path)}?decision={Uri.EscapeDataString(decision)}");
}
