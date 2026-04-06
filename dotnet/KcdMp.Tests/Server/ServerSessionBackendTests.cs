using KcdMp.Server.Sessions;

namespace KcdMp.Tests.Server;

public sealed class ServerSessionBackendTests
{
    [Fact]
    public void CreateSession_CreatesPendingAuthenticationSessionWithStableInternalId()
    {
        var backend = new ServerSessionBackend(TimeSpan.FromSeconds(30));
        var now = DateTimeOffset.Parse("2026-04-06T12:00:00Z");

        var session = backend.CreateSession("127.0.0.1:1000", now);

        Assert.NotEqual(Guid.Empty, session.SessionId);
        Assert.Equal(ServerSessionState.PendingAuthentication, session.State);
        Assert.Equal(ServerSessionAuthState.Pending, session.AuthState);
        Assert.Equal(ServerSessionConnectionState.Connected, session.ConnectionState);
        Assert.Equal(now, session.CreatedAtUtc);
        Assert.Equal(now, session.LastActivityAtUtc);
    }

    [Fact]
    public void MarkAuthenticationAccepted_TransitionsToAssociationPending_AndEmitsLifecycleEvents()
    {
        var backend = new ServerSessionBackend();
        var session = backend.CreateSession("client");
        var now = DateTimeOffset.Parse("2026-04-06T12:02:00Z");

        backend.MarkAuthenticationAccepted(session.SessionId, now);
        var active = backend.GetActiveSessions().Single();
        var events = backend.GetLifecycleEvents()
            .Where(x => x.SessionId == session.SessionId)
            .Select(x => x.Type)
            .ToArray();

        Assert.Equal(ServerSessionAuthState.Accepted, active.AuthState);
        Assert.Equal(ServerSessionState.AssociationPending, active.State);
        Assert.Equal(now, active.AuthenticatedAtUtc);
        Assert.Contains(ServerSessionLifecycleEventType.AuthenticationAccepted, events);
        Assert.Contains(ServerSessionLifecycleEventType.AssociationPending, events);
    }

    [Fact]
    public void AssociateIdentity_EnforcesSingleActiveSessionPerIdentity()
    {
        var backend = new ServerSessionBackend();
        var s1 = backend.CreateSession("client-1");
        var s2 = backend.CreateSession("client-2");
        backend.MarkAuthenticationAccepted(s1.SessionId);
        backend.MarkAuthenticationAccepted(s2.SessionId);

        var firstAssociation = backend.TryAssociateIdentity(s1.SessionId, "identity_henry");
        var secondAssociation = backend.TryAssociateIdentity(s2.SessionId, "identity_henry");

        Assert.True(firstAssociation);
        Assert.False(secondAssociation);
    }

    [Fact]
    public void CloseSession_MarksSessionAsClosed_WithExplicitReason_AndEmitsEvent()
    {
        var backend = new ServerSessionBackend();
        var session = backend.CreateSession("client");
        backend.MarkAuthenticationAccepted(session.SessionId);
        backend.TryAssociateIdentity(session.SessionId, "identity_robard");
        var now = DateTimeOffset.Parse("2026-04-06T12:05:00Z");

        var closed = backend.CloseSession(session.SessionId, ServerSessionCloseReason.Shutdown, now);
        var closedSession = backend.GetClosedSessions().Single(x => x.SessionId == session.SessionId);
        var closeEvent = backend.GetLifecycleEvents()
            .Last(x => x.SessionId == session.SessionId && x.Type == ServerSessionLifecycleEventType.SessionClosed);

        Assert.True(closed);
        Assert.Equal(ServerSessionState.Closed, closedSession.State);
        Assert.Equal(ServerSessionConnectionState.Disconnected, closedSession.ConnectionState);
        Assert.Equal(ServerSessionCloseReason.Shutdown, closedSession.CloseReason);
        Assert.Equal(now, closedSession.ClosedAtUtc);
        Assert.Equal(ServerSessionCloseReason.Shutdown, closeEvent.CloseReason);
    }

    [Fact]
    public void ClearIdentityAssociation_RemovesIdentityAndCharacterReferences()
    {
        var backend = new ServerSessionBackend();
        var session = backend.CreateSession("client");
        backend.MarkAuthenticationAccepted(session.SessionId);
        backend.TryAssociateIdentity(session.SessionId, "identity_henry");
        backend.SetCharacterReference(session.SessionId, "cid_henry");

        backend.ClearIdentityAssociation(session.SessionId);

        var loaded = backend.GetActiveSessions().Single(x => x.SessionId == session.SessionId);
        Assert.Null(loaded.IdentityId);
        Assert.Null(loaded.CharacterId);
        Assert.Equal(ServerSessionState.AssociationPending, loaded.State);
    }

    [Fact]
    public void GetTimedOutSessionCandidates_ReturnsOnlyIdleActiveSessions()
    {
        var backend = new ServerSessionBackend(TimeSpan.FromSeconds(10));
        var baseTime = DateTimeOffset.Parse("2026-04-06T12:10:00Z");
        var s1 = backend.CreateSession("client-1", baseTime);
        var s2 = backend.CreateSession("client-2", baseTime);
        backend.MarkAuthenticationAccepted(s1.SessionId, baseTime);
        backend.MarkAuthenticationAccepted(s2.SessionId, baseTime);
        backend.TouchSession(s2.SessionId, baseTime.AddSeconds(9));

        var timedOut = backend.GetTimedOutSessionCandidates(baseTime.AddSeconds(15));

        Assert.Contains(timedOut, x => x.SessionId == s1.SessionId);
        Assert.DoesNotContain(timedOut, x => x.SessionId == s2.SessionId);
    }
}
