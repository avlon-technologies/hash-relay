using System.Collections.Concurrent;

namespace HashRelay.Server.Services;

/// <summary>
/// In-memory map of live hub connections to endpoints. This is transport state only: subscriptions and
/// deliveries live in the database, and this registry is rebuilt by each agent's Hello after reconnect.
/// </summary>
public sealed class ConnectionRegistry
{
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, byte>> _byEndpoint = new();
    private readonly ConcurrentDictionary<string, (Guid EndpointRef, Guid CredentialId)> _byConnection = new();

    public void Attach(string connectionId, Guid endpointRef, Guid credentialId)
    {
        Detach(connectionId);
        _byConnection[connectionId] = (endpointRef, credentialId);
        _byEndpoint.GetOrAdd(endpointRef, _ => new()).TryAdd(connectionId, 0);
    }

    /// <summary>Removes the connection; returns the endpoint it belonged to and whether that endpoint is now offline.</summary>
    public (Guid? EndpointRef, bool NowOffline) Detach(string connectionId)
    {
        if (!_byConnection.TryRemove(connectionId, out var info)) return (null, false);
        if (_byEndpoint.TryGetValue(info.EndpointRef, out var set))
        {
            set.TryRemove(connectionId, out _);
            if (set.IsEmpty)
            {
                _byEndpoint.TryRemove(info.EndpointRef, out _);
                return (info.EndpointRef, true);
            }
        }
        return (info.EndpointRef, false);
    }

    public bool IsOnline(Guid endpointRef) => _byEndpoint.TryGetValue(endpointRef, out var set) && !set.IsEmpty;
    public int ConnectionCount(Guid endpointRef) => _byEndpoint.TryGetValue(endpointRef, out var set) ? set.Count : 0;
    public Guid? EndpointFor(string connectionId) => _byConnection.TryGetValue(connectionId, out var i) ? i.EndpointRef : null;
    public IReadOnlyCollection<string> ConnectionsForCredential(Guid credentialId)
        => _byConnection.Where(kv => kv.Value.CredentialId == credentialId).Select(kv => kv.Key).ToList();
    public int OnlineEndpoints => _byEndpoint.Count;
}
