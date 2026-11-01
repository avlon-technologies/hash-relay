using System.Security.Claims;
using HashRelay.Contracts;
using HashRelay.Server.Data;

namespace HashRelay.Server.Auth;

/// <summary>
/// The publisher behind an authenticated request, resolved server-side from the credential and carried in claims so
/// the API, the MCP server and the hub all stamp notifications the same way. Nothing here comes from the request body.
/// <list type="bullet">
/// <item>Service key → the service account (stable across key rotation and OAuth token refresh); its declared type.</item>
/// <item>API key → the key itself; the type declared when it was created.</item>
/// <item>Device token → the paired endpoint; always <see cref="PublisherTypes.Human"/>: pairing is approved interactively for a person's own device.</item>
/// </list>
/// A type that was never declared is <see cref="PublisherTypes.Unknown"/>; the transport is never used to guess it.
/// </summary>
public sealed record PublisherIdentity(Guid Id, string Name, string Type)
{
    public const string ClaimId = "hr:pub";
    public const string ClaimName = "hr:pub_name";
    public const string ClaimType = "hr:pub_type";

    public static PublisherIdentity ForServiceAccount(ServiceAccount account)
        => new(account.Id, account.Name, PublisherTypes.Normalize(account.PublisherType));

    public static PublisherIdentity ForApiKey(Guid credentialId, string label, string? declaredType)
        => new(credentialId, label, PublisherTypes.Normalize(declaredType));

    public static PublisherIdentity ForDevice(Guid credentialId, Guid? endpointRef, string label)
        => new(endpointRef ?? credentialId, label, PublisherTypes.Human);

    public IEnumerable<Claim> ToClaims()
    {
        yield return new Claim(ClaimId, Id.ToString());
        yield return new Claim(ClaimName, Name);
        yield return new Claim(ClaimType, Type);
    }

    public static PublisherIdentity? Of(ClaimsPrincipal? user)
    {
        if (user is null || !Guid.TryParse(user.FindFirstValue(ClaimId), out var id)) return null;
        return new PublisherIdentity(id, user.FindFirstValue(ClaimName) ?? "", PublisherTypes.Normalize(user.FindFirstValue(ClaimType)));
    }

    public PublisherInfo ToInfo(string? channel) => new(Id, Name, Type, channel);
}

/// <summary>
/// The entry point a publication arrived through. The path decides MCP versus REST; among REST callers the first-party
/// clients identify themselves by User-Agent (cli, agent, mobile, sdk). The header is client-supplied, so this is a
/// display and audit hint only and is kept apart from <see cref="PublisherIdentity"/>.
/// </summary>
public static class PublishChannel
{
    public static string Of(HttpContext? ctx)
    {
        if (ctx is null) return Channels.Rest;
        if (ctx.Request.Path.StartsWithSegments("/mcp")) return Channels.Mcp;
        var ua = ctx.Request.Headers.UserAgent.ToString();
        if (ua.StartsWith("hashrelay-cli/", StringComparison.OrdinalIgnoreCase)) return Channels.Cli;
        if (ua.StartsWith("hashrelay-agent/", StringComparison.OrdinalIgnoreCase)) return Channels.Agent;
        if (ua.StartsWith("hashrelay-mobile/", StringComparison.OrdinalIgnoreCase)) return Channels.Mobile;
        if (ua.StartsWith("hashrelay-client/", StringComparison.OrdinalIgnoreCase)) return Channels.Sdk;
        return Channels.Rest;
    }
}
