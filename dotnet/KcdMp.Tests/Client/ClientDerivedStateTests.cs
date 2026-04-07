using KcdMp.Client;
using KcdMp.Shared.Protocol;

namespace KcdMp.Tests.Client;

public class ClientDerivedStateTests
{
    [Fact]
    public void SessionAndAdministrativeProjection_PopulatesDerivedState()
    {
        var state = new ClientDerivedState();
        state.BeginNewCycle();
        state.SetTransportClientId(7);

        state.ApplySessionCharacterProjection(
            """{"sessionId":"s1","identityId":"pid_1","characterId":"cid_1","readiness":"Ready"}""");
        state.MarkProjectionStarted(ProjectionDomain.SessionCharacter);
        state.MarkProjectionResult(ProjectionDomain.SessionCharacter, ProjectionApplyStatus.Applied);

        state.ApplyAdministrativeProjection(
            """{"accessMode":"Open","identityRole":"Player","identityStatus":"Active","characterStatus":"Active"}""");
        state.MarkProjectionStarted(ProjectionDomain.Administrative);
        state.MarkProjectionResult(ProjectionDomain.Administrative, ProjectionApplyStatus.Applied);

        var snapshot = state.Snapshot();
        Assert.Equal((byte)7, snapshot.TransportClientId);
        Assert.Equal("s1", snapshot.SessionId);
        Assert.Equal("pid_1", snapshot.IdentityId);
        Assert.Equal("cid_1", snapshot.CharacterId);
        Assert.Equal("Ready", snapshot.Readiness);
        Assert.Equal("Open", snapshot.AccessMode);
        Assert.Equal("Player", snapshot.IdentityRole);
        Assert.Equal(ClientAdaptationPhase.Complete, snapshot.AdaptationPhase);
        Assert.False(snapshot.IsInvalidated);
    }

    [Theory]
    [InlineData("""{"identityStatus":"Blocked"}""")]
    [InlineData("""{"characterStatus":"Disabled"}""")]
    [InlineData("""{"accessDenied":true,"message":"Denied"}""")]
    [InlineData("""{"isBanned":true,"message":"Banned"}""")]
    [InlineData("""{"kicked":true,"message":"Kicked"}""")]
    public void AdministrativeProjection_InvalidationSignalsAreCaptured(string json)
    {
        var state = new ClientDerivedState();
        state.BeginNewCycle();

        state.ApplyAdministrativeProjection(json);

        var snapshot = state.Snapshot();
        Assert.True(snapshot.IsInvalidated);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.InvalidationReason));
    }

    [Fact]
    public void ProjectionResults_MapToExpectedAdaptationPhase()
    {
        var state = new ClientDerivedState();
        state.BeginNewCycle();

        state.MarkProjectionStarted(ProjectionDomain.SessionCharacter);
        Assert.Equal(ClientAdaptationPhase.Pending, state.Snapshot().AdaptationPhase);

        state.MarkProjectionResult(ProjectionDomain.SessionCharacter, ProjectionApplyStatus.PartiallyApplied);
        Assert.Equal(ClientAdaptationPhase.Partial, state.Snapshot().AdaptationPhase);

        state.MarkProjectionResult(ProjectionDomain.SessionCharacter, ProjectionApplyStatus.Failed);
        Assert.Equal(ClientAdaptationPhase.Error, state.Snapshot().AdaptationPhase);
    }
}
