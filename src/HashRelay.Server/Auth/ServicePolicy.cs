using System.Security.Claims;
using System.Text.RegularExpressions;
using HashRelay.Server.Data;

namespace HashRelay.Server.Auth;

/// <summary>
/// The publish policy a service account imposes on every request made with one of its keys: which hashtags it may
/// address, which link hosts it may use and how many publishes per hour. Carried in claims so the API, the MCP server
/// and the rate limiter all see the same policy without another database read.
/// </summary>
public sealed record ServicePolicy(Guid ServiceAccountId, string Name, IReadOnlyList<string> AllowedTags, IReadOnlyList<string> AllowedUrlHosts, int HourlyPublishLimit)
{
    public const string ClaimId = "hr:svc";
    public const string ClaimName = "hr:svc_name";
    public const string ClaimTags = "hr:svc_tags";
    public const string ClaimHosts = "hr:svc_hosts";
    public const string ClaimLimit = "hr:svc_limit";

    public static ServicePolicy From(ServiceAccount account)
        => new(account.Id, account.Name, account.AllowedTagList, account.AllowedUrlHostList, account.HourlyPublishLimit);

    public IEnumerable<Claim> ToClaims()
    {
        yield return new Claim(ClaimId, ServiceAccountId.ToString());
        yield return new Claim(ClaimName, Name);
        yield return new Claim(ClaimTags, string.Join(',', AllowedTags));
        yield return new Claim(ClaimHosts, string.Join(',', AllowedUrlHosts));
        yield return new Claim(ClaimLimit, HourlyPublishLimit.ToString());
    }

    public static ServicePolicy? Of(ClaimsPrincipal? user)
    {
        if (user is null || !Guid.TryParse(user.FindFirstValue(ClaimId), out var id)) return null;
        static IReadOnlyList<string> Split(string? csv) => (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new ServicePolicy(id, user.FindFirstValue(ClaimName) ?? "", Split(user.FindFirstValue(ClaimTags)), Split(user.FindFirstValue(ClaimHosts)),
            int.TryParse(user.FindFirstValue(ClaimLimit), out var limit) ? limit : 60);
    }

    /// <summary>True when the (normalised, '#'-less) tag matches one of the allowed glob patterns. '*' matches any run of characters.</summary>
    public bool AllowsTag(string tag) => AllowedTags.Any(p => GlobMatches(p, tag));

    public IReadOnlyList<string> DisallowedTags(IEnumerable<string> tags) => tags.Where(t => !AllowsTag(t)).ToList();

    /// <summary>Checks a link against the URL policy: https only, and on one of the allowed hosts (or any host when the list is empty).</summary>
    public string? UrlProblem(string? url)
    {
        if (url is null) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return "must be an absolute URL";
        if (u.Scheme != Uri.UriSchemeHttps) return "must use https";
        if (AllowedUrlHosts.Count == 0) return null;
        var host = u.Host.ToLowerInvariant();
        return AllowedUrlHosts.Any(h => host == h || host.EndsWith("." + h, StringComparison.Ordinal)) ? null : $"host {host} is not in this service account's allowed hosts";
    }

    public static bool GlobMatches(string pattern, string value)
    {
        if (pattern == "*") return true;
        var rx = "^" + string.Join(".*", pattern.Split('*').Select(Regex.Escape)) + "$";
        return Regex.IsMatch(value, rx, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    }
}
