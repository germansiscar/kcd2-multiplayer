namespace KcdMp.Server.Sessions;

public enum ServerSessionState
{
    PendingAuthentication = 0,
    AssociationPending = 1,
    Active = 2,
    Closed = 3,
}

public enum ServerSessionConnectionState
{
    Connected = 0,
    Disconnected = 1,
}

public enum ServerSessionAuthState
{
    Pending = 0,
    Accepted = 1,
    Rejected = 2,
}

public enum ServerSessionPresenceState
{
    Connected = 0,
    Offline = 1,
}

public enum ServerSessionCloseReason
{
    NetworkDisconnect = 0,
    Timeout = 1,
    Shutdown = 2,
    AuthenticationRejected = 3,
}

public enum ServerSessionLifecycleEventType
{
    SessionCreated = 0,
    AuthenticationAccepted = 1,
    AuthenticationRejected = 2,
    AssociationPending = 3,
    SessionClosed = 4,
}

public sealed record ServerSessionLifecycleEvent(
    ServerSessionLifecycleEventType Type,
    Guid SessionId,
    DateTimeOffset OccurredAtUtc,
    ServerSessionCloseReason? CloseReason = null);

public sealed class ServerSessionRecord
{
    public Guid SessionId { get; init; }
    public byte? TransportClientId { get; set; }
    public string? RemoteEndpoint { get; init; }
    public ServerSessionState State { get; set; }
    public ServerSessionConnectionState ConnectionState { get; set; }
    public ServerSessionAuthState AuthState { get; set; }
    public ServerSessionPresenceState Presence { get; set; }
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset LastActivityAtUtc { get; set; }
    public DateTimeOffset? AuthenticatedAtUtc { get; set; }
    public DateTimeOffset? ClosedAtUtc { get; set; }
    public ServerSessionCloseReason? CloseReason { get; set; }
    public string? IdentityId { get; set; }
    public string? CharacterId { get; set; }

    public ServerSessionRecord Clone()
    {
        return new ServerSessionRecord
        {
            SessionId = SessionId,
            TransportClientId = TransportClientId,
            RemoteEndpoint = RemoteEndpoint,
            State = State,
            ConnectionState = ConnectionState,
            AuthState = AuthState,
            Presence = Presence,
            CreatedAtUtc = CreatedAtUtc,
            LastActivityAtUtc = LastActivityAtUtc,
            AuthenticatedAtUtc = AuthenticatedAtUtc,
            ClosedAtUtc = ClosedAtUtc,
            CloseReason = CloseReason,
            IdentityId = IdentityId,
            CharacterId = CharacterId,
        };
    }
}

public sealed class ServerSessionBackend
{
    private readonly Dictionary<Guid, ServerSessionRecord> _sessions = [];
    private readonly Dictionary<string, Guid> _identityToSession = new(StringComparer.Ordinal);
    private readonly List<ServerSessionLifecycleEvent> _events = [];
    private readonly object _lock = new();

    public ServerSessionBackend(TimeSpan? idleTimeout = null)
    {
        IdleTimeout = idleTimeout ?? TimeSpan.FromSeconds(45);
    }

    public TimeSpan IdleTimeout { get; }

    public ServerSessionRecord CreateSession(string? remoteEndpoint, DateTimeOffset? now = null)
    {
        var utcNow = now ?? DateTimeOffset.UtcNow;
        var record = new ServerSessionRecord
        {
            SessionId = Guid.NewGuid(),
            RemoteEndpoint = remoteEndpoint,
            State = ServerSessionState.PendingAuthentication,
            ConnectionState = ServerSessionConnectionState.Connected,
            AuthState = ServerSessionAuthState.Pending,
            Presence = ServerSessionPresenceState.Connected,
            CreatedAtUtc = utcNow,
            LastActivityAtUtc = utcNow,
        };

        lock (_lock)
        {
            _sessions[record.SessionId] = record;
            AddEventUnsafe(ServerSessionLifecycleEventType.SessionCreated, record.SessionId, utcNow);
            return record.Clone();
        }
    }

    public void AttachTransportClientId(Guid sessionId, byte transportClientId)
    {
        lock (_lock)
        {
            if (_sessions.TryGetValue(sessionId, out var session))
                session.TransportClientId = transportClientId;
        }
    }

    public void TouchSession(Guid sessionId, DateTimeOffset? now = null)
    {
        var utcNow = now ?? DateTimeOffset.UtcNow;
        lock (_lock)
        {
            if (_sessions.TryGetValue(sessionId, out var session) && session.State != ServerSessionState.Closed)
                session.LastActivityAtUtc = utcNow;
        }
    }

    public void MarkAuthenticationAccepted(Guid sessionId, DateTimeOffset? now = null)
    {
        var utcNow = now ?? DateTimeOffset.UtcNow;
        lock (_lock)
        {
            if (!_sessions.TryGetValue(sessionId, out var session) || session.State == ServerSessionState.Closed)
                return;

            session.AuthState = ServerSessionAuthState.Accepted;
            session.AuthenticatedAtUtc = utcNow;
            session.State = ServerSessionState.AssociationPending;
            session.LastActivityAtUtc = utcNow;
            AddEventUnsafe(ServerSessionLifecycleEventType.AuthenticationAccepted, session.SessionId, utcNow);
            AddEventUnsafe(ServerSessionLifecycleEventType.AssociationPending, session.SessionId, utcNow);
        }
    }

    public void MarkAuthenticationRejected(Guid sessionId, DateTimeOffset? now = null)
    {
        var utcNow = now ?? DateTimeOffset.UtcNow;
        lock (_lock)
        {
            if (!_sessions.TryGetValue(sessionId, out var session) || session.State == ServerSessionState.Closed)
                return;

            session.AuthState = ServerSessionAuthState.Rejected;
            session.LastActivityAtUtc = utcNow;
            AddEventUnsafe(ServerSessionLifecycleEventType.AuthenticationRejected, session.SessionId, utcNow);
        }
    }

    public bool TryAssociateIdentity(Guid sessionId, string identityId, DateTimeOffset? now = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identityId);
        var utcNow = now ?? DateTimeOffset.UtcNow;

        lock (_lock)
        {
            if (!_sessions.TryGetValue(sessionId, out var session) || session.State == ServerSessionState.Closed)
                return false;

            if (_identityToSession.TryGetValue(identityId, out var existingSessionId) &&
                existingSessionId != sessionId &&
                _sessions.TryGetValue(existingSessionId, out var existingSession) &&
                existingSession.State != ServerSessionState.Closed)
            {
                return false;
            }

            if (session.IdentityId is not null)
                _identityToSession.Remove(session.IdentityId);

            session.IdentityId = identityId;
            session.State = ServerSessionState.Active;
            session.LastActivityAtUtc = utcNow;
            _identityToSession[identityId] = sessionId;
            return true;
        }
    }

    public void SetCharacterReference(Guid sessionId, string? characterId, DateTimeOffset? now = null)
    {
        var utcNow = now ?? DateTimeOffset.UtcNow;
        lock (_lock)
        {
            if (!_sessions.TryGetValue(sessionId, out var session) || session.State == ServerSessionState.Closed)
                return;

            session.CharacterId = characterId;
            session.LastActivityAtUtc = utcNow;
        }
    }

    public bool CloseSession(Guid sessionId, ServerSessionCloseReason reason, DateTimeOffset? now = null)
    {
        var utcNow = now ?? DateTimeOffset.UtcNow;
        lock (_lock)
        {
            if (!_sessions.TryGetValue(sessionId, out var session) || session.State == ServerSessionState.Closed)
                return false;

            session.State = ServerSessionState.Closed;
            session.ConnectionState = ServerSessionConnectionState.Disconnected;
            session.Presence = ServerSessionPresenceState.Offline;
            session.ClosedAtUtc = utcNow;
            session.CloseReason = reason;
            session.LastActivityAtUtc = utcNow;

            if (session.IdentityId is not null &&
                _identityToSession.TryGetValue(session.IdentityId, out var mappedSessionId) &&
                mappedSessionId == sessionId)
            {
                _identityToSession.Remove(session.IdentityId);
            }

            AddEventUnsafe(ServerSessionLifecycleEventType.SessionClosed, session.SessionId, utcNow, reason);
            return true;
        }
    }

    public IReadOnlyList<ServerSessionRecord> GetActiveSessions()
    {
        lock (_lock)
        {
            return _sessions.Values
                .Where(x => x.State != ServerSessionState.Closed)
                .Select(x => x.Clone())
                .ToArray();
        }
    }

    public IReadOnlyList<ServerSessionRecord> GetClosedSessions()
    {
        lock (_lock)
        {
            return _sessions.Values
                .Where(x => x.State == ServerSessionState.Closed)
                .Select(x => x.Clone())
                .ToArray();
        }
    }

    public IReadOnlyList<ServerSessionRecord> GetPendingAuthenticationSessions()
    {
        lock (_lock)
        {
            return _sessions.Values
                .Where(x => x.State != ServerSessionState.Closed && x.AuthState == ServerSessionAuthState.Pending)
                .Select(x => x.Clone())
                .ToArray();
        }
    }

    public IReadOnlyList<ServerSessionRecord> GetTimedOutSessionCandidates(DateTimeOffset? now = null)
    {
        var utcNow = now ?? DateTimeOffset.UtcNow;
        var threshold = utcNow - IdleTimeout;

        lock (_lock)
        {
            return _sessions.Values
                .Where(x => x.State != ServerSessionState.Closed && x.LastActivityAtUtc <= threshold)
                .Select(x => x.Clone())
                .ToArray();
        }
    }

    public IReadOnlyList<ServerSessionLifecycleEvent> GetLifecycleEvents()
    {
        lock (_lock)
            return _events.ToArray();
    }

    private void AddEventUnsafe(
        ServerSessionLifecycleEventType type,
        Guid sessionId,
        DateTimeOffset occurredAtUtc,
        ServerSessionCloseReason? closeReason = null)
    {
        _events.Add(new ServerSessionLifecycleEvent(type, sessionId, occurredAtUtc, closeReason));
        const int maxEvents = 2048;
        if (_events.Count > maxEvents)
            _events.RemoveRange(0, _events.Count - maxEvents);
    }
}

