using Xunit;
using KcdMp.Shared.Protocol;

namespace KcdMp.Tests.Protocol;

public class StateEventPacketTests
{
    [Fact]
    public void StateUpdate_RoundTrips()
    {
        // Arrange
        byte stateType = 1; // CombatState
        byte[] payload = [0b00000101]; // flags: weapon drawn + sneaking

        // Act
        var packet = PacketWriter.StateUpdate(stateType, payload);

        // Assert
        Assert.Equal((byte)PacketType.StateUpdate, packet[0]);
        var (parsedType, parsedPayload) = PacketReader.ParseStateUpdate(packet[3..]);
        Assert.Equal(stateType, parsedType);
        Assert.Equal(payload, parsedPayload);
    }

    [Fact]
    public void StateSync_RoundTrips()
    {
        // Arrange
        byte sourceId = 2;
        byte stateType = 2; // Equipment
        byte[] payload = "{\"clothing\":\"abc\"}"u8.ToArray();

        // Act
        var packet = PacketWriter.StateSync(sourceId, stateType, payload);

        // Assert
        Assert.Equal((byte)PacketType.StateSync, packet[0]);
        var (parsedSrc, parsedType, parsedPayload) = PacketReader.ParseStateSync(packet[3..]);
        Assert.Equal(sourceId, parsedSrc);
        Assert.Equal(stateType, parsedType);
        Assert.Equal(payload, parsedPayload);
    }

    [Fact]
    public void StateUpdate_WithAnimationName_RoundTrips()
    {
        // Arrange
        byte stateType = 1; // CombatState
        var animName = "combat_sword_idle"u8.ToArray();
        byte[] payload = new byte[1 + 1 + animName.Length];
        payload[0] = 0b00000011; // flags
        payload[1] = (byte)animName.Length;
        Buffer.BlockCopy(animName, 0, payload, 2, animName.Length);

        // Act
        var packet = PacketWriter.StateUpdate(stateType, payload);
        var (parsedType, parsedPayload) = PacketReader.ParseStateUpdate(packet[3..]);

        // Assert
        Assert.Equal(stateType, parsedType);
        Assert.Equal(payload, parsedPayload);
    }

    [Fact]
    public void Event_RoundTrips()
    {
        ushort eventType = (ushort)EventType.DamageDealt;
        byte[] json = "{\"amount\":50}"u8.ToArray();

        var packet = PacketWriter.Event(eventType, json);

        Assert.Equal((byte)PacketType.Event, packet[0]);
        var (parsedType, parsedJson) = PacketReader.ParseEvent(packet[3..]);
        Assert.Equal(eventType, parsedType);
        Assert.Equal(json, parsedJson);
    }

    [Fact]
    public void EventRelay_RoundTrips()
    {
        byte sourceId = 1;
        ushort eventType = (ushort)EventType.NpcDamage;
        byte[] json = "{\"entity\":\"krab_man_4\",\"amount\":50}"u8.ToArray();

        var packet = PacketWriter.EventRelay(sourceId, eventType, json);

        Assert.Equal((byte)PacketType.EventRelay, packet[0]);
        var (parsedSrc, parsedType, parsedJson) = PacketReader.ParseEventRelay(packet[3..]);
        Assert.Equal(sourceId, parsedSrc);
        Assert.Equal(eventType, parsedType);
        Assert.Equal(json, parsedJson);
    }

    [Fact]
    public void Event_EmptyPayload_RoundTrips()
    {
        ushort eventType = (ushort)EventType.PlayerDied;
        byte[] json = "{}"u8.ToArray();

        var packet = PacketWriter.Event(eventType, json);
        var (parsedType, parsedJson) = PacketReader.ParseEvent(packet[3..]);

        Assert.Equal(eventType, parsedType);
        Assert.Equal(json, parsedJson);
    }

    [Fact]
    public void ChatEventRelay_RoundTrips()
    {
        byte sourceId = 4;
        ushort eventType = (ushort)EventType.ChatMessage;
        byte[] json = """{"channel":"global_ooc","formatted":"[OOC] Henry: Hola"}"""u8.ToArray();

        var packet = PacketWriter.EventRelay(sourceId, eventType, json);

        Assert.Equal((byte)PacketType.EventRelay, packet[0]);
        var (parsedSrc, parsedType, parsedJson) = PacketReader.ParseEventRelay(packet[3..]);
        Assert.Equal(sourceId, parsedSrc);
        Assert.Equal(eventType, parsedType);
        Assert.Equal(json, parsedJson);
    }

    [Fact]
    public void StateProjection_RoundTrips()
    {
        const uint projectionId = 42;
        const byte domain = (byte)ProjectionDomain.Inventory;
        const byte applicability = (byte)ProjectionApplicability.Partial;
        byte[] json = """{"items":[{"id":"itm_1","qty":2}]}"""u8.ToArray();

        var packet = PacketWriter.StateProjection(projectionId, domain, applicability, json);

        Assert.Equal((byte)PacketType.StateProjection, packet[0]);
        var (parsedId, parsedDomain, parsedApplicability, parsedJson) = PacketReader.ParseStateProjection(packet[3..]);
        Assert.Equal(projectionId, parsedId);
        Assert.Equal(domain, parsedDomain);
        Assert.Equal(applicability, parsedApplicability);
        Assert.Equal(json, parsedJson);
    }

    [Fact]
    public void StateProjectionResult_RoundTrips()
    {
        const uint projectionId = 99;
        const byte domain = (byte)ProjectionDomain.Currency;
        const byte status = (byte)ProjectionApplyStatus.PartiallyApplied;
        byte[] details = """{"message":"applied as reflection"}"""u8.ToArray();

        var packet = PacketWriter.StateProjectionResult(projectionId, domain, status, details);

        Assert.Equal((byte)PacketType.StateProjectionResult, packet[0]);
        var (parsedId, parsedDomain, parsedStatus, parsedDetails) = PacketReader.ParseStateProjectionResult(packet[3..]);
        Assert.Equal(projectionId, parsedId);
        Assert.Equal(domain, parsedDomain);
        Assert.Equal(status, parsedStatus);
        Assert.Equal(details, parsedDetails);
    }
}
