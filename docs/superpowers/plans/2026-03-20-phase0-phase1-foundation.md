# Phase 0 + Phase 1: Foundation & Polish Sandbox

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Clean up the forked repo, extract shared code, add tests, create a single unified exe with password auth and config file, and set up Mac-to-Windows dev workflow.

**Architecture:** Existing Server + Client projects get a new Shared library for deduplicated protocol code. A new App project produces a single `kcdmp.exe` that embeds both relay server and client agent, switched by config. Dev scripts bridge Mac development to Windows testing.

**Tech Stack:** .NET 8, C#, xUnit, Serilog, Lua 5.1 (game mod — unchanged this phase)

---

## Prerequisites

Before starting, install .NET 8 SDK on Mac:

```bash
brew install dotnet@8
# Verify
dotnet --version
```

## File Structure

### New files to create

```
dotnet/
  KcdMp.Shared/
    KcdMp.Shared.csproj
    Protocol/
      PacketType.cs          -- packet type enum (0x00-0x0C)
      PacketWriter.cs        -- build binary packets
      PacketReader.cs        -- parse binary packets
      StreamExtensions.cs    -- ReadExactAsync helper
    Config/
      KcdmpConfig.cs         -- JSON config model
  KcdMp.App/
    KcdMp.App.csproj         -- single exe entry point
    Program.cs               -- host/join mode switching
  KcdMp.Tests/
    KcdMp.Tests.csproj
    Protocol/
      PacketWriterTests.cs
      PacketReaderTests.cs
    Config/
      KcdmpConfigTests.cs
    Integration/
      RelayServerTests.cs

scripts/
  build.sh                   -- build + test on Mac
  deploy.sh                  -- cross-compile + SCP to Windows
  probe.sh                   -- send Lua to game API via SSH tunnel

probes/
  README.md                  -- how to use probes, results log

setup.ps1                    -- Windows player setup (mod install, firewall, portproxy)
CLAUDE.md                    -- project conventions for AI agents
```

### Files to modify

```
dotnet/KcdMp.sln                        -- add Shared, App, Tests projects
dotnet/KcdMp.Server/KcdMp.Server.csproj -- reference Shared, change to library
dotnet/KcdMp.Server/RelayServer.cs      -- use Shared types, add password auth
dotnet/KcdMp.Server/ClientSession.cs    -- use Shared types, add auth packet handling
dotnet/KcdMp.Server/Program.cs          -- add Serilog, config
dotnet/KcdMp.Client/KcdMp.Client.csproj -- reference Shared, change to library
dotnet/KcdMp.Client/Program.cs          -- extract into class, use config
dotnet/KcdMp.Client/GameBridge.cs       -- use Shared types
.gitignore                              -- consolidate ignores
```

---

## Task 1: Create KcdMp.Shared project with protocol types

**Files:**
- Create: `dotnet/KcdMp.Shared/KcdMp.Shared.csproj`
- Create: `dotnet/KcdMp.Shared/Protocol/PacketType.cs`
- Create: `dotnet/KcdMp.Shared/Protocol/PacketWriter.cs`
- Create: `dotnet/KcdMp.Shared/Protocol/PacketReader.cs`
- Create: `dotnet/KcdMp.Shared/Protocol/StreamExtensions.cs`
- Modify: `dotnet/KcdMp.sln`

- [ ] **Step 1: Create the Shared project**

```bash
cd dotnet
dotnet new classlib -n KcdMp.Shared --framework net8.0
rm KcdMp.Shared/Class1.cs
dotnet sln add KcdMp.Shared/KcdMp.Shared.csproj
```

- [ ] **Step 2: Create PacketType.cs**

```csharp
// dotnet/KcdMp.Shared/Protocol/PacketType.cs
namespace KcdMp.Shared.Protocol;

/// <summary>
/// Wire protocol packet types.
/// All packets: [type:1][payloadLen:2 LE][payload:N]
/// </summary>
public enum PacketType : byte
{
    // Existing (v1)
    Handshake    = 0x00, // C->S  payload: [name:UTF-8]
    Position     = 0x01, // C->S  payload: [x:4f][y:4f][z:4f][rotZ:4f][flags:1]
    Ghost        = 0x02, // S->C  payload: [ghostId:1][x:4f][y:4f][z:4f][rotZ:4f][flags:1]
    Name         = 0x03, // S->C  payload: [ghostId:1][name:UTF-8]
    Ping         = 0x04, // C->S  payload: [timestamp:8 LE]
    Pong         = 0x05, // S->C  payload: [timestamp:8 LE]
    Disconnect   = 0x06, // S->C  payload: [ghostId:1]

    // New (v2)
    StateUpdate  = 0x07, // C->S  payload: [stateType:1][json...]
    StateSync    = 0x08, // S->C  payload: [sourceId:1][stateType:1][json...]
    Event        = 0x09, // C->S  payload: [eventType:2 LE][json...]
    EventRelay   = 0x0A, // S->C  payload: [sourceId:1][eventType:2 LE][json...]
    Auth         = 0x0B, // C->S  payload: [password:UTF-8]
    AuthResult   = 0x0C, // S->C  payload: [ok:1][message:UTF-8]

    Ack          = 0xFF, // S->C  payload: [assignedId:1]
}
```

- [ ] **Step 3: Create PacketWriter.cs**

Extract `BuildPacket`, `WriteFloat` from `ClientSession.cs:167-180` and `GameBridge.cs:378-396`.

```csharp
// dotnet/KcdMp.Shared/Protocol/PacketWriter.cs
using System.Buffers.Binary;
using System.Text;

namespace KcdMp.Shared.Protocol;

public static class PacketWriter
{
    /// <summary>Builds a framed packet: [type:1][payloadLen:2 LE][payload]</summary>
    public static byte[] Build(PacketType type, byte[] payload)
    {
        var packet = new byte[3 + payload.Length];
        packet[0] = (byte)type;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(1), (ushort)payload.Length);
        payload.CopyTo(packet, 3);
        return packet;
    }

    /// <summary>Builds a Position packet (0x01): [x:4f][y:4f][z:4f][rotZ:4f][flags:1]</summary>
    public static byte[] Position(float x, float y, float z, float rotZ, bool isRiding)
    {
        var payload = new byte[17];
        WriteFloat(payload, 0, x);
        WriteFloat(payload, 4, y);
        WriteFloat(payload, 8, z);
        WriteFloat(payload, 12, rotZ);
        payload[16] = isRiding ? (byte)0x01 : (byte)0x00;
        return Build(PacketType.Position, payload);
    }

    /// <summary>Builds a Ghost packet (0x02): [ghostId:1][x:4f][y:4f][z:4f][rotZ:4f][flags:1]</summary>
    public static byte[] Ghost(byte ghostId, float x, float y, float z, float rotZ, byte flags)
    {
        var payload = new byte[18];
        payload[0] = ghostId;
        WriteFloat(payload, 1, x);
        WriteFloat(payload, 5, y);
        WriteFloat(payload, 9, z);
        WriteFloat(payload, 13, rotZ);
        payload[17] = flags;
        return Build(PacketType.Ghost, payload);
    }

    /// <summary>Builds a Handshake packet (0x00): [name:UTF-8]</summary>
    public static byte[] Handshake(string name)
        => Build(PacketType.Handshake, Encoding.UTF8.GetBytes(name));

    /// <summary>Builds an Ack packet (0xFF): [assignedId:1]</summary>
    public static byte[] Ack(byte id)
        => Build(PacketType.Ack, [id]);

    /// <summary>Builds a Name packet (0x03): [ghostId:1][name:UTF-8]</summary>
    public static byte[] NamePacket(byte ghostId, string name)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var payload = new byte[1 + nameBytes.Length];
        payload[0] = ghostId;
        nameBytes.CopyTo(payload, 1);
        return Build(PacketType.Name, payload);
    }

    /// <summary>Builds a Ping packet (0x04): [timestamp:8 LE]</summary>
    public static byte[] Ping(long timestamp)
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(payload, timestamp);
        return Build(PacketType.Ping, payload);
    }

    /// <summary>Builds a Pong packet (0x05): [timestamp:8 LE]</summary>
    public static byte[] Pong(byte[] timestampBytes)
        => Build(PacketType.Pong, timestampBytes);

    /// <summary>Builds a Disconnect packet (0x06): [ghostId:1]</summary>
    public static byte[] DisconnectPacket(byte ghostId)
        => Build(PacketType.Disconnect, [ghostId]);

    /// <summary>Builds an Auth packet (0x0B): [password:UTF-8]</summary>
    public static byte[] Auth(string password)
        => Build(PacketType.Auth, Encoding.UTF8.GetBytes(password));

    /// <summary>Builds an AuthResult packet (0x0C): [ok:1][message:UTF-8]</summary>
    public static byte[] AuthResult(bool ok, string message = "")
    {
        var msgBytes = Encoding.UTF8.GetBytes(message);
        var payload = new byte[1 + msgBytes.Length];
        payload[0] = ok ? (byte)1 : (byte)0;
        msgBytes.CopyTo(payload, 1);
        return Build(PacketType.AuthResult, payload);
    }

    public static void WriteFloat(byte[] buf, int offset, float value)
        => BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(offset), BitConverter.SingleToInt32Bits(value));
}
```

- [ ] **Step 4: Create PacketReader.cs**

```csharp
// dotnet/KcdMp.Shared/Protocol/PacketReader.cs
using System.Buffers.Binary;
using System.Text;

namespace KcdMp.Shared.Protocol;

/// <summary>Parsed packet header + payload.</summary>
public readonly record struct Packet(PacketType Type, byte[] Payload);

public static class PacketReader
{
    public static float ReadFloat(byte[] buf, int offset)
        => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(offset)));

    public static long ReadInt64(byte[] buf, int offset)
        => BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(offset));

    public static ushort ReadUInt16(byte[] buf, int offset)
        => BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(offset));

    public static string ReadUtf8(byte[] buf, int offset, int length)
        => Encoding.UTF8.GetString(buf, offset, length);

    /// <summary>Parse a Ghost packet payload: [ghostId:1][x:4f][y:4f][z:4f][rotZ:4f][flags:1]</summary>
    public static (byte ghostId, float x, float y, float z, float rotZ, byte flags) ParseGhost(byte[] payload)
    {
        byte ghostId = payload[0];
        float x = ReadFloat(payload, 1);
        float y = ReadFloat(payload, 5);
        float z = ReadFloat(payload, 9);
        float rotZ = ReadFloat(payload, 13);
        byte flags = payload.Length >= 18 ? payload[17] : (byte)0;
        return (ghostId, x, y, z, rotZ, flags);
    }

    /// <summary>Parse a Position packet payload: [x:4f][y:4f][z:4f][rotZ:4f][flags:1]</summary>
    public static (float x, float y, float z, float rotZ, byte flags) ParsePosition(byte[] payload)
    {
        float x = ReadFloat(payload, 0);
        float y = ReadFloat(payload, 4);
        float z = ReadFloat(payload, 8);
        float rotZ = ReadFloat(payload, 12);
        byte flags = payload.Length >= 17 ? payload[16] : (byte)0;
        return (x, y, z, rotZ, flags);
    }

    /// <summary>Parse a Name packet payload: [ghostId:1][name:UTF-8]</summary>
    public static (byte ghostId, string name) ParseName(byte[] payload)
        => (payload[0], ReadUtf8(payload, 1, payload.Length - 1));

    /// <summary>Parse an AuthResult payload: [ok:1][message:UTF-8]</summary>
    public static (bool ok, string message) ParseAuthResult(byte[] payload)
        => (payload[0] != 0, payload.Length > 1 ? ReadUtf8(payload, 1, payload.Length - 1) : "");
}
```

- [ ] **Step 5: Create StreamExtensions.cs**

```csharp
// dotnet/KcdMp.Shared/Protocol/StreamExtensions.cs
using System.Buffers.Binary;
using System.Net.Sockets;

namespace KcdMp.Shared.Protocol;

public static class StreamExtensions
{
    /// <summary>Read exactly <paramref name="count"/> bytes or throw EndOfStreamException.</summary>
    public static async Task ReadExactAsync(this NetworkStream stream, byte[] buffer, int count, CancellationToken ct = default)
    {
        int offset = 0;
        while (offset < count)
        {
            int n = await stream.ReadAsync(buffer, offset, count - offset, ct);
            if (n == 0) throw new EndOfStreamException();
            offset += n;
        }
    }

    /// <summary>Read exactly buffer.Length bytes.</summary>
    public static Task ReadExactAsync(this NetworkStream stream, byte[] buffer, CancellationToken ct = default)
        => ReadExactAsync(stream, buffer, buffer.Length, ct);

    /// <summary>Read one full packet (header + payload) from the stream.</summary>
    public static async Task<Packet> ReadPacketAsync(this NetworkStream stream, CancellationToken ct = default)
    {
        var header = new byte[3];
        await stream.ReadExactAsync(header, ct);

        var type = (PacketType)header[0];
        int payloadLen = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(1));

        var payload = new byte[payloadLen];
        if (payloadLen > 0)
            await stream.ReadExactAsync(payload, ct);

        return new Packet(type, payload);
    }
}
```

- [ ] **Step 6: Verify it builds**

```bash
cd dotnet && dotnet build KcdMp.Shared/KcdMp.Shared.csproj
```

Expected: Build succeeded.

- [ ] **Step 7: Commit**

```bash
git add dotnet/KcdMp.Shared/ dotnet/KcdMp.sln
git commit -m "feat: add KcdMp.Shared project with protocol types and helpers"
```

---

## Task 2: Create test project with protocol tests

**Files:**
- Create: `dotnet/KcdMp.Tests/KcdMp.Tests.csproj`
- Create: `dotnet/KcdMp.Tests/Protocol/PacketWriterTests.cs`
- Create: `dotnet/KcdMp.Tests/Protocol/PacketReaderTests.cs`

- [ ] **Step 1: Create test project**

```bash
cd dotnet
dotnet new xunit -n KcdMp.Tests --framework net8.0
dotnet sln add KcdMp.Tests/KcdMp.Tests.csproj
cd KcdMp.Tests
dotnet add reference ../KcdMp.Shared/KcdMp.Shared.csproj
rm UnitTest1.cs
```

- [ ] **Step 2: Write PacketWriterTests.cs**

```csharp
// dotnet/KcdMp.Tests/Protocol/PacketWriterTests.cs
using KcdMp.Shared.Protocol;

namespace KcdMp.Tests.Protocol;

public class PacketWriterTests
{
    [Fact]
    public void Build_CreatesCorrectHeader()
    {
        var packet = PacketWriter.Build(PacketType.Ack, [0x42]);

        Assert.Equal(0xFF, packet[0]); // type
        Assert.Equal(1, BitConverter.ToUInt16(packet, 1)); // payload length
        Assert.Equal(0x42, packet[3]); // payload
    }

    [Fact]
    public void Build_EmptyPayload()
    {
        var packet = PacketWriter.Build(PacketType.Ping, []);

        Assert.Equal(3, packet.Length);
        Assert.Equal(0, BitConverter.ToUInt16(packet, 1));
    }

    [Fact]
    public void Position_Creates20BytePacket()
    {
        var packet = PacketWriter.Position(1.0f, 2.0f, 3.0f, 1.57f, true);

        Assert.Equal(20, packet.Length); // 3 header + 17 payload
        Assert.Equal((byte)PacketType.Position, packet[0]);
        Assert.Equal(17, BitConverter.ToUInt16(packet, 1));
        Assert.Equal(0x01, packet[19]); // isRiding flag
    }

    [Fact]
    public void Position_NotRiding_FlagIsZero()
    {
        var packet = PacketWriter.Position(0f, 0f, 0f, 0f, false);
        Assert.Equal(0x00, packet[19]);
    }

    [Fact]
    public void Ghost_Creates21BytePacket()
    {
        var packet = PacketWriter.Ghost(5, 10f, 20f, 30f, 1.0f, 0x01);

        Assert.Equal(21, packet.Length); // 3 header + 18 payload
        Assert.Equal((byte)PacketType.Ghost, packet[0]);
        Assert.Equal(5, packet[3]); // ghostId
    }

    [Fact]
    public void Handshake_EncodesNameAsUtf8()
    {
        var packet = PacketWriter.Handshake("TestPlayer");

        Assert.Equal((byte)PacketType.Handshake, packet[0]);
        Assert.Equal(10, BitConverter.ToUInt16(packet, 1)); // "TestPlayer" = 10 bytes
    }

    [Fact]
    public void Auth_EncodesPasswordAsUtf8()
    {
        var packet = PacketWriter.Auth("secret123");

        Assert.Equal((byte)PacketType.Auth, packet[0]);
        Assert.Equal(9, BitConverter.ToUInt16(packet, 1));
    }

    [Fact]
    public void AuthResult_Ok()
    {
        var packet = PacketWriter.AuthResult(true, "Welcome");

        Assert.Equal((byte)PacketType.AuthResult, packet[0]);
        Assert.Equal(1, packet[3]); // ok = 1
    }

    [Fact]
    public void AuthResult_Rejected()
    {
        var packet = PacketWriter.AuthResult(false, "Bad password");

        Assert.Equal(0, packet[3]); // ok = 0
    }
}
```

- [ ] **Step 3: Write PacketReaderTests.cs**

```csharp
// dotnet/KcdMp.Tests/Protocol/PacketReaderTests.cs
using KcdMp.Shared.Protocol;

namespace KcdMp.Tests.Protocol;

public class PacketReaderTests
{
    [Fact]
    public void ParsePosition_RoundTrips()
    {
        var packet = PacketWriter.Position(123.45f, 678.90f, 42.0f, 1.57f, true);
        // Skip 3-byte header to get payload
        var payload = packet[3..];

        var (x, y, z, rotZ, flags) = PacketReader.ParsePosition(payload);

        Assert.Equal(123.45f, x, 0.01f);
        Assert.Equal(678.90f, y, 0.01f);
        Assert.Equal(42.0f, z, 0.01f);
        Assert.Equal(1.57f, rotZ, 0.01f);
        Assert.Equal(0x01, flags);
    }

    [Fact]
    public void ParseGhost_RoundTrips()
    {
        var packet = PacketWriter.Ghost(7, 100f, 200f, 300f, 3.14f, 0x01);
        var payload = packet[3..];

        var (ghostId, x, y, z, rotZ, flags) = PacketReader.ParseGhost(payload);

        Assert.Equal(7, ghostId);
        Assert.Equal(100f, x, 0.01f);
        Assert.Equal(200f, y, 0.01f);
        Assert.Equal(300f, z, 0.01f);
        Assert.Equal(3.14f, rotZ, 0.01f);
        Assert.Equal(0x01, flags);
    }

    [Fact]
    public void ParseName_RoundTrips()
    {
        var packet = PacketWriter.NamePacket(3, "Henry");
        var payload = packet[3..];

        var (ghostId, name) = PacketReader.ParseName(payload);

        Assert.Equal(3, ghostId);
        Assert.Equal("Henry", name);
    }

    [Fact]
    public void ParseAuthResult_Ok()
    {
        var packet = PacketWriter.AuthResult(true, "Welcome");
        var payload = packet[3..];

        var (ok, message) = PacketReader.ParseAuthResult(payload);

        Assert.True(ok);
        Assert.Equal("Welcome", message);
    }

    [Fact]
    public void ParseAuthResult_Rejected()
    {
        var packet = PacketWriter.AuthResult(false, "Wrong password");
        var payload = packet[3..];

        var (ok, message) = PacketReader.ParseAuthResult(payload);

        Assert.False(ok);
        Assert.Equal("Wrong password", message);
    }

    [Fact]
    public void ParsePosition_V1_16BytePayload_FlagsDefault()
    {
        // v1 position payload was 16 bytes (no flags byte)
        var payload = new byte[16];
        PacketWriter.WriteFloat(payload, 0, 1f);
        PacketWriter.WriteFloat(payload, 4, 2f);
        PacketWriter.WriteFloat(payload, 8, 3f);
        PacketWriter.WriteFloat(payload, 12, 0.5f);

        var (x, y, z, rotZ, flags) = PacketReader.ParsePosition(payload);

        Assert.Equal(1f, x);
        Assert.Equal(0x00, flags); // default when no flags byte
    }
}
```

- [ ] **Step 4: Run tests**

```bash
cd dotnet && dotnet test KcdMp.Tests/
```

Expected: All tests pass.

- [ ] **Step 5: Commit**

```bash
git add dotnet/KcdMp.Tests/
git commit -m "test: add protocol round-trip tests for PacketWriter and PacketReader"
```

---

## Task 3: Refactor Server to use Shared library

**Files:**
- Modify: `dotnet/KcdMp.Server/KcdMp.Server.csproj`
- Modify: `dotnet/KcdMp.Server/ClientSession.cs`
- Modify: `dotnet/KcdMp.Server/RelayServer.cs`

- [ ] **Step 1: Add Shared reference to Server project**

```bash
cd dotnet/KcdMp.Server
dotnet add reference ../KcdMp.Shared/KcdMp.Shared.csproj
```

- [ ] **Step 2: Refactor ClientSession.cs to use Shared types**

Replace local `BuildPacket`, `ReadFloat`, `WriteFloat`, `ReadExactAsync` methods with `PacketWriter`, `PacketReader`, `StreamExtensions` from Shared. Replace magic number packet types with `PacketType` enum. Keep the channel-based write queue and session logic unchanged.

Key changes:
- `using KcdMp.Shared.Protocol;`
- **CRITICAL: Replace the entire handshake read logic.** The current code reads `header[1]` as a single-byte `nameLen`, which is a quirk of the original implementation. Replace with `var packet = await _stream.ReadPacketAsync()` and then `Name = PacketReader.ReadUtf8(packet.Payload, 0, packet.Payload.Length)`. This aligns the handshake with the standard 3-byte-header framing used by all other packet types.
- Replace the entire position receive loop with `var packet = await _stream.ReadPacketAsync()` and switch on `packet.Type`:
  - `PacketType.Ping` → `EnqueueRaw(PacketWriter.Pong(packet.Payload))`
  - `PacketType.Position` → `var (x,y,z,rotZ,flags) = PacketReader.ParsePosition(packet.Payload)`
  - Default → skip (unknown packet)
- `EnqueueGhost(...)` body → `EnqueueRaw(PacketWriter.Ghost(...))`
- `EnqueueName(...)` body → `EnqueueRaw(PacketWriter.NamePacket(...))`
- `EnqueueDisconnect(...)` → `EnqueueRaw(PacketWriter.DisconnectPacket(...))`
- `EnqueueRaw(BuildPacket(0xFF, ...))` → `EnqueueRaw(PacketWriter.Ack(Id))`
- `ReadExactAsync` instance method → `_stream.ReadExactAsync` extension method
- Remove ALL duplicate helper methods at the bottom of the class (`BuildPacket`, `ReadFloat`, `WriteFloat`, `ReadExactAsync`)

- [ ] **Step 3: Verify Server builds**

```bash
cd dotnet && dotnet build KcdMp.Server/
```

- [ ] **Step 4: Run all tests to check no regressions**

```bash
cd dotnet && dotnet test
```

- [ ] **Step 5: Commit**

```bash
git add dotnet/KcdMp.Server/
git commit -m "refactor: server uses Shared protocol types, remove duplicated helpers"
```

---

## Task 4: Refactor Client to use Shared library

**Files:**
- Modify: `dotnet/KcdMp.Client/KcdMp.Client.csproj`
- Modify: `dotnet/KcdMp.Client/GameBridge.cs`

- [ ] **Step 1: Add Shared reference to Client project**

```bash
cd dotnet/KcdMp.Client
dotnet add reference ../KcdMp.Shared/KcdMp.Shared.csproj
```

- [ ] **Step 2: Refactor GameBridge.cs to use Shared types**

Key changes:
- `using KcdMp.Shared.Protocol;`
- Replace `SendPositionAsync` body → use `PacketWriter.Position(...)`
- Replace `ReadFloat`, `WriteFloat` → `PacketReader.ReadFloat`
- Replace `ReadExactAsync` → `stream.ReadExactAsync` (extension)
- Replace magic bytes in `ConnectAndRunAsync`:
  - Handshake build → `PacketWriter.Handshake(name)`
  - Ack read: use `stream.ReadPacketAsync()` instead of manual header read
  - Receive loop: use `stream.ReadPacketAsync()` and switch on `packet.Type`
- Replace `PingLoopAsync` ping build → `PacketWriter.Ping(ts)`

- [ ] **Step 3: Verify Client builds**

```bash
cd dotnet && dotnet build KcdMp.Client/
```

- [ ] **Step 4: Run all tests**

```bash
cd dotnet && dotnet test
```

- [ ] **Step 5: Commit**

```bash
git add dotnet/KcdMp.Client/
git commit -m "refactor: client uses Shared protocol types, remove duplicated helpers"
```

---

## Task 5: Add config model and JSON config file

**Files:**
- Create: `dotnet/KcdMp.Shared/Config/KcdmpConfig.cs`
- Create: `dotnet/KcdMp.Tests/Config/KcdmpConfigTests.cs`

- [ ] **Step 1: Write config test**

```csharp
// dotnet/KcdMp.Tests/Config/KcdmpConfigTests.cs
using KcdMp.Shared.Config;

namespace KcdMp.Tests.Config;

public class KcdmpConfigTests
{
    [Fact]
    public void Defaults_AreReasonable()
    {
        var config = new KcdmpConfig();

        Assert.Equal("host", config.Mode);
        Assert.Equal(7778, config.Port);
        Assert.Equal("", config.Password);
        Assert.Equal("", config.FriendIp);
        Assert.Equal(1404, config.GameApiPort);
        Assert.Equal("auto", config.SteamName);
    }

    [Fact]
    public void RoundTrips_ThroughJson()
    {
        var config = new KcdmpConfig
        {
            Mode = "join",
            Port = 9999,
            Password = "secret",
            FriendIp = "192.168.1.50",
        };

        var json = config.ToJson();
        var loaded = KcdmpConfig.FromJson(json);

        Assert.Equal("join", loaded.Mode);
        Assert.Equal(9999, loaded.Port);
        Assert.Equal("secret", loaded.Password);
        Assert.Equal("192.168.1.50", loaded.FriendIp);
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        var config = KcdmpConfig.LoadOrDefault("/tmp/nonexistent_kcdmp_test.json");
        Assert.Equal("host", config.Mode);
    }

    [Fact]
    public void SaveAndLoad_RoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kcdmp_test_{Guid.NewGuid()}.json");
        try
        {
            var config = new KcdmpConfig { Password = "test123", Mode = "join" };
            config.Save(path);

            var loaded = KcdmpConfig.LoadOrDefault(path);
            Assert.Equal("test123", loaded.Password);
            Assert.Equal("join", loaded.Mode);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
cd dotnet && dotnet test KcdMp.Tests/
```

Expected: Compilation error — `KcdmpConfig` doesn't exist yet.

- [ ] **Step 3: Implement KcdmpConfig.cs**

```csharp
// dotnet/KcdMp.Shared/Config/KcdmpConfig.cs
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KcdMp.Shared.Config;

public class KcdmpConfig
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Mode { get; set; } = "host";
    public int Port { get; set; } = 7778;
    public string Password { get; set; } = "";
    public string FriendIp { get; set; } = "";
    public int GameApiPort { get; set; } = 1404;
    public string SteamName { get; set; } = "auto";

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);

    public static KcdmpConfig FromJson(string json)
        => JsonSerializer.Deserialize<KcdmpConfig>(json, JsonOpts) ?? new();

    public void Save(string path)
        => File.WriteAllText(path, ToJson());

    public static KcdmpConfig LoadOrDefault(string path)
    {
        if (!File.Exists(path)) return new();
        try { return FromJson(File.ReadAllText(path)); }
        catch { return new(); }
    }
}
```

- [ ] **Step 4: Run tests**

```bash
cd dotnet && dotnet test
```

Expected: All tests pass.

- [ ] **Step 5: Commit**

```bash
git add dotnet/KcdMp.Shared/Config/ dotnet/KcdMp.Tests/Config/
git commit -m "feat: add KcdmpConfig with JSON serialization and defaults"
```

---

## Task 6: Add password authentication to relay server

**Files:**
- Modify: `dotnet/KcdMp.Server/RelayServer.cs`
- Modify: `dotnet/KcdMp.Server/ClientSession.cs`
- Create: `dotnet/KcdMp.Tests/Integration/RelayServerTests.cs`

- [ ] **Step 1: Write integration test for auth**

```csharp
// dotnet/KcdMp.Tests/Integration/RelayServerTests.cs
using System.Net.Sockets;
using KcdMp.Shared.Protocol;

namespace KcdMp.Tests.Integration;

public class RelayServerTests : IAsyncLifetime
{
    private KcdMp.Server.RelayServer _server = null!;
    private Task _serverTask = null!;
    private CancellationTokenSource _cts = null!;
    private const int TestPort = 17778;
    private const string TestPassword = "testpass";

    public async Task InitializeAsync()
    {
        _cts = new CancellationTokenSource();
        _server = new KcdMp.Server.RelayServer(TestPort, password: TestPassword);
        _serverTask = _server.RunAsync(_cts.Token);
        await Task.Delay(100); // let server start
    }

    public async Task DisposeAsync()
    {
        _cts.Cancel();
        try { await _serverTask; } catch { }
    }

    [Fact]
    public async Task Client_WithCorrectPassword_GetsAck()
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", TestPort);
        var stream = tcp.GetStream();

        // Send Auth
        await stream.WriteAsync(PacketWriter.Auth(TestPassword));
        var authResponse = await stream.ReadPacketAsync();
        Assert.Equal(PacketType.AuthResult, authResponse.Type);
        var (ok, _) = PacketReader.ParseAuthResult(authResponse.Payload);
        Assert.True(ok);

        // Send Handshake
        await stream.WriteAsync(PacketWriter.Handshake("TestPlayer"));
        var ackPacket = await stream.ReadPacketAsync();
        Assert.Equal(PacketType.Ack, ackPacket.Type);
    }

    [Fact]
    public async Task Client_WithWrongPassword_GetsRejected()
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", TestPort);
        var stream = tcp.GetStream();

        await stream.WriteAsync(PacketWriter.Auth("wrongpassword"));
        var authResponse = await stream.ReadPacketAsync();
        Assert.Equal(PacketType.AuthResult, authResponse.Type);
        var (ok, _) = PacketReader.ParseAuthResult(authResponse.Payload);
        Assert.False(ok);
    }

    [Fact]
    public async Task Client_NoPassword_ServerWithNoPassword_Connects()
    {
        // Server with no password
        using var cts2 = new CancellationTokenSource();
        var openServer = new KcdMp.Server.RelayServer(TestPort + 1, password: "");
        var openTask = openServer.RunAsync(cts2.Token);
        await Task.Delay(100);

        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync("127.0.0.1", TestPort + 1);
            var stream = tcp.GetStream();

            // No auth needed, go straight to handshake
            await stream.WriteAsync(PacketWriter.Handshake("OpenPlayer"));
            var ack = await stream.ReadPacketAsync();
            Assert.Equal(PacketType.Ack, ack.Type);
        }
        finally
        {
            cts2.Cancel();
            try { await openTask; } catch { }
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
cd dotnet && dotnet test KcdMp.Tests/
```

Expected: Fails — `RelayServer` constructor doesn't accept `password` parameter, doesn't accept `CancellationToken`.

- [ ] **Step 3: Add Server project reference to Tests**

```bash
cd dotnet/KcdMp.Tests
dotnet add reference ../KcdMp.Server/KcdMp.Server.csproj
```

- [ ] **Step 4: Add `[Collection("Integration")]` to test class to prevent parallel port conflicts**

```csharp
[Collection("Integration")]
public class RelayServerTests : IAsyncLifetime
```

- [ ] **Step 5: Modify RelayServer to accept password and CancellationToken**

Update constructor: `public RelayServer(int port, bool echo = false, string password = "")`

Store `password` as a public property: `public string Password { get; } = password;`

Update `RunAsync` signature to `public async Task RunAsync(CancellationToken ct = default)`. Use `listener.AcceptTcpClientAsync(ct)` (.NET 8 supports this overload). Wrap the accept loop in `try { ... } catch (OperationCanceledException) { listener.Stop(); }`.

`ClientSession` already holds a `_server` reference — it reads the password via `_server.Password`.

- [ ] **Step 6: Modify ClientSession to handle Auth packet before Handshake**

In `ClientSession.RunAsync()`, replace the handshake section. The new flow:

```csharp
// --- Auth (if server has password) ---
if (!string.IsNullOrEmpty(_server.Password))
{
    var authPacket = await _stream.ReadPacketAsync();
    if (authPacket.Type != PacketType.Auth)
    {
        EnqueueRaw(PacketWriter.AuthResult(false, "Expected Auth packet"));
        return;
    }
    string clientPassword = PacketReader.ReadUtf8(authPacket.Payload, 0, authPacket.Payload.Length);
    if (clientPassword != _server.Password)
    {
        EnqueueRaw(PacketWriter.AuthResult(false, "Wrong password"));
        await Task.Delay(100); // let packet flush
        return;
    }
    EnqueueRaw(PacketWriter.AuthResult(true, "OK"));
}

// --- Handshake ---
var hsPacket = await _stream.ReadPacketAsync();
if (hsPacket.Type != PacketType.Handshake)
{
    Console.WriteLine($"[!] Bad handshake type 0x{(byte)hsPacket.Type:X2}");
    return;
}
Name = PacketReader.ReadUtf8(hsPacket.Payload, 0, hsPacket.Payload.Length);
```

- [ ] **Step 6: Run tests**

```bash
cd dotnet && dotnet test
```

Expected: All tests pass including new integration tests.

- [ ] **Step 7: Commit**

```bash
git add dotnet/KcdMp.Server/ dotnet/KcdMp.Tests/Integration/
git commit -m "feat: add password authentication to relay server"
```

---

## Task 7: Convert Server and Client to libraries, create unified App

**Files:**
- Modify: `dotnet/KcdMp.Server/KcdMp.Server.csproj` (change OutputType to Library)
- Modify: `dotnet/KcdMp.Client/KcdMp.Client.csproj` (change OutputType to Library)
- Modify: `dotnet/KcdMp.Client/Program.cs` (extract into a class)
- Create: `dotnet/KcdMp.App/KcdMp.App.csproj`
- Create: `dotnet/KcdMp.App/Program.cs`
- Modify: `dotnet/KcdMp.sln`

- [ ] **Step 1: Convert Server csproj to Library**

Change `<OutputType>Exe</OutputType>` to `<OutputType>Library</OutputType>` in `KcdMp.Server.csproj`. Remove `<AssemblyName>` (defaults to project name).

Keep `Program.cs` as-is for now — it will be dead code in the library but serves as reference. Can be removed later.

- [ ] **Step 2: Convert Client csproj to Library**

Same change in `KcdMp.Client.csproj`. Remove `<AssemblyName>`.

- [ ] **Step 3: Extract Client Program.cs logic into a launchable class**

The current `Program.cs` has top-level statements with static helper methods for Steam name detection. Wrap these into a class:

```csharp
// dotnet/KcdMp.Client/ClientLauncher.cs
namespace KcdMp.Client;

public static class ClientLauncher
{
    public static async Task RunAsync(string serverHost, int serverPort,
        string password, string name, string gameApiBase, CancellationToken ct = default)
    {
        var bridge = new GameBridge(serverHost, serverPort, password, name, gameApiBase);
        await bridge.RunAsync(ct);
    }
}
```

`GameBridge` constructor also needs the `password` parameter added. In `ConnectAndRunAsync`, after TCP connect and before Handshake, send the Auth packet if password is non-empty:

```csharp
// --- Auth (if password set) ---
if (!string.IsNullOrEmpty(password))
{
    await stream.WriteAsync(PacketWriter.Auth(password));
    var authResult = await stream.ReadPacketAsync();
    var (ok, message) = PacketReader.ParseAuthResult(authResult.Payload);
    if (!ok)
    {
        Console.WriteLine($"[!] Auth failed: {message}");
        return;
    }
    Console.WriteLine("Authenticated.");
}

// --- Handshake (existing code) ---
await stream.WriteAsync(PacketWriter.Handshake(name));
```

Steam name detection helpers (`GetSteamNameFromKcdLog`, `GetSteamPersonaName`) move to a new `SteamNameResolver` class in the Client project. Extract the two static methods verbatim from the existing `Program.cs` into:

```csharp
// dotnet/KcdMp.Client/SteamNameResolver.cs
namespace KcdMp.Client;

public static class SteamNameResolver
{
    /// <summary>Returns Steam persona name, or null if undetectable.</summary>
    public static string? Resolve()
    {
        if (!OperatingSystem.IsWindows()) return null;
        return GetSteamNameFromKcdLog() ?? GetSteamPersonaName();
    }

    // Copy GetSteamNameFromKcdLog() verbatim from existing Program.cs (lines 19-79)
    // Copy GetSteamPersonaName() verbatim from existing Program.cs (lines 84-138)
    // Both methods stay as private static, called by Resolve()
}
```

These use Windows registry so they're guarded with `OperatingSystem.IsWindows()`. The existing `Program.cs` top-level statements become dead code after the App project replaces it.

- [ ] **Step 4: Create KcdMp.App project**

```bash
cd dotnet
dotnet new console -n KcdMp.App --framework net8.0
dotnet sln add KcdMp.App/KcdMp.App.csproj
cd KcdMp.App
dotnet add reference ../KcdMp.Shared/KcdMp.Shared.csproj
dotnet add reference ../KcdMp.Server/KcdMp.Server.csproj
dotnet add reference ../KcdMp.Client/KcdMp.Client.csproj
```

Set `<AssemblyName>kcdmp</AssemblyName>` in the csproj.

- [ ] **Step 5: Write App Program.cs**

```csharp
// dotnet/KcdMp.App/Program.cs
using KcdMp.Shared.Config;
using KcdMp.Client;
using KcdMp.Server;

var configPath = Path.Combine(AppContext.BaseDirectory, "kcdmp.json");
var config = KcdmpConfig.LoadOrDefault(configPath);

// CLI overrides: kcdmp.exe [host|join] [friendIp]
if (args.Length > 0) config.Mode = args[0];
if (args.Length > 1) config.FriendIp = args[1];

string gameApi = $"http://localhost:{config.GameApiPort}";
string name = config.SteamName == "auto"
    ? SteamNameResolver.Resolve() ?? Environment.MachineName
    : config.SteamName;

Console.WriteLine("=== KCD2 Multiplayer ===");
Console.WriteLine($"Mode    : {config.Mode}");
Console.WriteLine($"Name    : {name}");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

if (config.Mode == "host")
{
    Console.WriteLine($"Port    : {config.Port}");
    Console.WriteLine($"Password: {(string.IsNullOrEmpty(config.Password) ? "(none)" : "****")}");
    Console.WriteLine();

    // Start relay server in background
    var server = new RelayServer(config.Port, password: config.Password);
    var serverTask = server.RunAsync(cts.Token);

    // Start client agent connecting to localhost (no password needed for local connection)
    await ClientLauncher.RunAsync("localhost", config.Port, "", name, gameApi, cts.Token);
}
else
{
    if (string.IsNullOrEmpty(config.FriendIp))
    {
        Console.Write("Enter host IP: ");
        config.FriendIp = Console.ReadLine()?.Trim() ?? "";
        config.Mode = "join";
        config.Save(configPath);
    }

    Console.WriteLine($"Host    : {config.FriendIp}:{config.Port}");
    Console.WriteLine();

    await ClientLauncher.RunAsync(config.FriendIp, config.Port, config.Password, name, gameApi, cts.Token);
}
```

- [ ] **Step 6: Verify it builds**

```bash
cd dotnet && dotnet build KcdMp.App/
```

- [ ] **Step 7: Run all tests**

```bash
cd dotnet && dotnet test
```

- [ ] **Step 8: Commit**

```bash
git add dotnet/
git commit -m "feat: unified kcdmp.exe with host/join modes, config file support"
```

---

## Task 8: Add Serilog structured logging

**Files:**
- Modify: `dotnet/KcdMp.App/KcdMp.App.csproj` (add Serilog packages)
- Modify: `dotnet/KcdMp.App/Program.cs` (configure Serilog)
- Modify: `dotnet/KcdMp.Server/RelayServer.cs` (accept ILogger)
- Modify: `dotnet/KcdMp.Server/ClientSession.cs` (accept ILogger)
- Modify: `dotnet/KcdMp.Client/GameBridge.cs` (accept ILogger)

- [ ] **Step 1: Add Serilog packages**

```bash
cd dotnet/KcdMp.App
dotnet add package Serilog
dotnet add package Serilog.Sinks.Console
dotnet add package Serilog.Sinks.File
cd ../KcdMp.Server
dotnet add package Serilog
cd ../KcdMp.Client
dotnet add package Serilog
```

All logging uses `Serilog.ILogger` (not `Microsoft.Extensions.Logging.ILogger`). Server and Client libraries need the base `Serilog` package for the interface. Only the App project needs the Sinks.

- [ ] **Step 2: Configure Serilog in App Program.cs**

Add at the top of Program.cs:

```csharp
using Serilog;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File("kcdmp.log", rollingInterval: RollingInterval.Day)
    .CreateLogger();
```

- [ ] **Step 3: Thread `Serilog.ILogger` through Server and Client classes**

Add `Serilog.ILogger` parameter to constructors of `RelayServer`, `ClientSession`, and `GameBridge`, with default `Serilog.Log.Logger`. Replace `Console.WriteLine` calls with `_logger.Information(...)`, `_logger.Warning(...)`, `_logger.Error(...)` etc.

This is a mechanical change — replace `Console.WriteLine($"[+] ...")` with `_logger.Information(...)` etc.

- [ ] **Step 4: Verify build and tests**

```bash
cd dotnet && dotnet build && dotnet test
```

- [ ] **Step 5: Commit**

```bash
git add dotnet/
git commit -m "feat: add Serilog structured logging, replace Console.WriteLine"
```

---

## Task 9: Create dev scripts

**Files:**
- Create: `scripts/build.sh`
- Create: `scripts/deploy.sh`
- Create: `scripts/probe.sh`

- [ ] **Step 1: Create build.sh**

```bash
#!/usr/bin/env bash
# scripts/build.sh — Build and test on Mac
set -euo pipefail
cd "$(dirname "$0")/../dotnet"

echo "=== Building ==="
dotnet build

echo ""
echo "=== Running Tests ==="
dotnet test

echo ""
echo "=== Build + Test OK ==="
```

- [ ] **Step 2: Create deploy.sh**

```bash
#!/usr/bin/env bash
# scripts/deploy.sh — Cross-compile for Windows and deploy via SCP
# Usage: ./scripts/deploy.sh [user@host] [remote_dir]
set -euo pipefail

REMOTE="${1:-}"
REMOTE_DIR="${2:-C:/kcdmp}"

if [ -z "$REMOTE" ]; then
    echo "Usage: deploy.sh user@windowspc [remote_dir]"
    echo "  Builds win-x64 single-file exe and copies to Windows PC via SCP"
    exit 1
fi

cd "$(dirname "$0")/../dotnet"

echo "=== Cross-compiling for win-x64 ==="
dotnet publish KcdMp.App -c Release -r win-x64 --self-contained \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -o publish/app

echo ""
echo "=== Deploying to $REMOTE:$REMOTE_DIR ==="
ssh "$REMOTE" "mkdir -p '$REMOTE_DIR'" 2>/dev/null || true
scp publish/app/kcdmp.exe "$REMOTE:$REMOTE_DIR/"

# Also deploy the mod files
echo "=== Deploying mod files ==="
scp -r ../kdcmp "$REMOTE:$REMOTE_DIR/"

echo ""
echo "=== Deploy complete ==="
echo "On Windows: cd $REMOTE_DIR && kcdmp.exe"
```

- [ ] **Step 3: Create probe.sh**

```bash
#!/usr/bin/env bash
# scripts/probe.sh — Send a Lua snippet to the game's debug API
# Requires SSH tunnel: ssh -L 1404:localhost:1404 windowspc
# Usage: ./scripts/probe.sh 'System.LogAlways("hello")'
#        ./scripts/probe.sh --read sv_servername
set -euo pipefail

GAME_API="${GAME_API:-http://localhost:1404}"

if [ "${1:-}" = "--read" ]; then
    # Read a CVar value
    CVAR="${2:?Usage: probe.sh --read <cvar_name>}"
    curl -s "$GAME_API/api/System/Console/GetCvarValue?name=$CVAR"
    echo ""
    exit 0
fi

LUA="${1:?Usage: probe.sh '<lua code>' or probe.sh --read <cvar>}"

# Prefix with # for Lua execution, URL-encode
ENCODED=$(python3 -c "import urllib.parse; print(urllib.parse.quote('#${LUA}'))")
RESULT=$(curl -s "$GAME_API/api/System/Console/ExecuteString?command=$ENCODED")
echo "$RESULT"
```

- [ ] **Step 4: Make scripts executable**

```bash
chmod +x scripts/build.sh scripts/deploy.sh scripts/probe.sh
```

- [ ] **Step 5: Commit**

```bash
git add scripts/
git commit -m "feat: add build, deploy, and probe dev scripts"
```

---

## Task 10: Create Windows setup script

**Files:**
- Create: `setup.ps1`

- [ ] **Step 1: Write setup.ps1**

```powershell
# setup.ps1 — One-time setup for KCD2 Multiplayer on Windows
# Run as Administrator

param(
    [string]$ModToolsPath = ""
)

$ErrorActionPreference = "Stop"

Write-Host "=== KCD2 Multiplayer Setup ===" -ForegroundColor Cyan
Write-Host ""

# --- Find Modding Tools path ---
if (-not $ModToolsPath) {
    # Try Steam registry
    $steamPath = (Get-ItemProperty -Path "HKLM:\SOFTWARE\WOW6432Node\Valve\Steam" -Name InstallPath -ErrorAction SilentlyContinue).InstallPath
    if (-not $steamPath) {
        $steamPath = (Get-ItemProperty -Path "HKLM:\SOFTWARE\Valve\Steam" -Name InstallPath -ErrorAction SilentlyContinue).InstallPath
    }

    if ($steamPath) {
        # Check default and library folders
        $candidates = @(
            "$steamPath\steamapps\common\KCD2ModMods"
            "$steamPath\steamapps\common\KCD2ModdingTools"
        )

        # Parse libraryfolders.vdf for additional paths
        $vdf = "$steamPath\config\libraryfolders.vdf"
        if (Test-Path $vdf) {
            $content = Get-Content $vdf -Raw
            $matches = [regex]::Matches($content, '"path"\s+"([^"]+)"')
            foreach ($m in $matches) {
                $libPath = $m.Groups[1].Value -replace '\\\\', '\'
                $candidates += "$libPath\steamapps\common\KCD2ModMods"
                $candidates += "$libPath\steamapps\common\KCD2ModdingTools"
            }
        }

        foreach ($c in $candidates) {
            if (Test-Path "$c\Mods") {
                $ModToolsPath = $c
                Write-Host "Found Modding Tools: $ModToolsPath" -ForegroundColor Green
                break
            }
        }
    }

    if (-not $ModToolsPath) {
        $ModToolsPath = Read-Host "Enter KCD2 Modding Tools path (e.g. D:\Steam\steamapps\common\KCD2ModMods)"
    }
}

$modsDir = "$ModToolsPath\Mods"
if (-not (Test-Path $modsDir)) {
    Write-Host "ERROR: Mods directory not found at $modsDir" -ForegroundColor Red
    exit 1
}

# --- Install mod ---
$modDest = "$modsDir\kdcmp"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$modSrc = "$scriptDir\kdcmp"

if (Test-Path $modSrc) {
    Write-Host "Installing mod to $modDest..."
    Copy-Item -Path $modSrc -Destination $modDest -Recurse -Force
    Write-Host "Mod installed." -ForegroundColor Green
} else {
    Write-Host "WARNING: kdcmp folder not found next to setup.ps1. Install mod manually." -ForegroundColor Yellow
}

# --- Port proxy (game API: 1403 -> 1404) ---
Write-Host ""
Write-Host "Setting up port proxy (1403 -> 1404)..."
netsh interface portproxy add v4tov4 listenaddress=0.0.0.0 listenport=1404 connectaddress=127.0.0.1 connectport=1403

# --- Firewall rules ---
Write-Host "Adding firewall rules..."
netsh advfirewall firewall add rule name="KCD2 API 1404" dir=in action=allow protocol=TCP localport=1404 2>$null
netsh advfirewall firewall add rule name="KCD2MP Relay 7778" dir=in action=allow protocol=TCP localport=7778 2>$null

Write-Host ""
Write-Host "=== Setup Complete ===" -ForegroundColor Cyan
Write-Host "1. Launch KCD2 through Modding Tools"
Write-Host "2. Load a save"
Write-Host "3. Run kcdmp.exe"
```

- [ ] **Step 2: Commit**

```bash
git add setup.ps1
git commit -m "feat: add Windows setup script (mod install, portproxy, firewall)"
```

---

## Task 11: Create probe infrastructure

**Files:**
- Create: `probes/README.md`
- Create: `probes/phase2/`

- [ ] **Step 1: Create probes README**

```markdown
# KCD2 API Probes

Probe scripts test whether specific Lua APIs exist and work in KCD2.

## How to use

1. SSH tunnel to your Windows gaming PC: `ssh -L 1404:localhost:1404 windowspc`
2. Make sure the game is running with a save loaded
3. Run a probe: `./scripts/probe.sh '<lua code>'`
4. Document results below

## Quick reference

```bash
# Execute Lua and log result
./scripts/probe.sh 'System.LogAlways("probe: " .. tostring(os.execute ~= nil))'

# Write result to CVar, then read it
./scripts/probe.sh 'System.SetCVar("sv_servername", tostring(os.execute ~= nil))'
./scripts/probe.sh --read sv_servername
```

## Probe Results

| Probe | API | Expected | Actual | Date | Status |
|-------|-----|----------|--------|------|--------|
| (run during Phase 2) | | | | | |
```

- [ ] **Step 2: Create phase2 directory**

```bash
mkdir -p probes/phase2
touch probes/phase2/.gitkeep
```

- [ ] **Step 3: Commit**

```bash
git add probes/
git commit -m "docs: add probe infrastructure for Phase 2 API discovery"
```

---

## Task 12: Write CLAUDE.md and update documentation

**Files:**
- Create: `CLAUDE.md`
- Modify: `.gitignore`

- [ ] **Step 1: Write CLAUDE.md**

```markdown
# KCD2 Multiplayer Campaign Co-op

## Project overview

Multiplayer campaign co-op mod for Kingdom Come: Deliverance 2. Fork of marczukmichal/kcd2-multiplayer.
Two players play through the campaign together — each runs their own game, connected via a relay server.
Ghost NPCs represent the other player in each game instance.

## Architecture

```
Game <--HTTP--> Client Agent <--TCP--> Relay Server <--TCP--> Client Agent <--HTTP--> Game
(Lua mod)        (C# .NET 8)           (C# .NET 8)          (C# .NET 8)         (Lua mod)
```

The game's Lua environment has NO socket library. All communication goes through the debug REST API
on localhost:1403 (proxied to 1404). This is the fundamental constraint.

## Project structure

- `dotnet/KcdMp.Shared/` — protocol types, packet builder/reader, config model
- `dotnet/KcdMp.Server/` — TCP relay server (library)
- `dotnet/KcdMp.Client/` — game bridge client agent (library)
- `dotnet/KcdMp.App/` — unified entry point (single exe, host/join modes)
- `dotnet/KcdMp.Tests/` — xUnit tests
- `kdcmp/` — Lua game mod (installed into KCD2 Modding Tools)
- `scripts/` — dev scripts (build, deploy, probe)
- `probes/` — Lua API probe scripts and results
- `docs/` — design specs, API docs, plans

## Build and test

```bash
# Build everything
cd dotnet && dotnet build

# Run tests
cd dotnet && dotnet test

# Cross-compile for Windows
cd dotnet && dotnet publish KcdMp.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish/app
```

## Key rules

1. **No API assumptions.** Every game API call must be VERIFIED before building on it.
   See `docs/superpowers/specs/2026-03-20-kcd2-multiplayer-campaign-design.md` for the verification matrix.
2. **Tests required.** C# changes need xUnit tests. Lua changes need probe verification.
3. **Flag unknowns.** If suggesting a game API that hasn't been tested, mark it as PLAUSIBLE or UNKNOWN.

## Wire protocol

All packets: [type:1][payloadLen:2 LE][payload:N]
See `KcdMp.Shared/Protocol/PacketType.cs` for the full enum.

## Dev workflow (Mac to Windows)

1. Tailscale connects machines
2. SSH tunnel: `ssh -L 1404:localhost:1404 windowspc`
3. Build + deploy: `./scripts/deploy.sh user@windowspc`
4. Probe Lua API: `./scripts/probe.sh '<lua>'`
```

- [ ] **Step 2: Update .gitignore**

Append missing entries:

```
# Build outputs
dotnet/publish/
dotnet/**/bin/
dotnet/**/obj/

# IDE
.vs/
.idea/
*.user
*.suo

# OS
.DS_Store
Thumbs.db

# Project
.superpowers/
```

- [ ] **Step 3: Commit**

```bash
git add CLAUDE.md .gitignore
git commit -m "docs: add CLAUDE.md project conventions and consolidate .gitignore"
```

---

## Task 13: Save project memory

**Files:**
- Create/update: `/Users/mbehr/.claude/projects/-Users-mbehr-Documents-kcd2-multiplayer/memory/MEMORY.md`

- [ ] **Step 1: Write memory file**

Save key project facts to the auto-memory file so future sessions have context:
- Project is a fork of marczukmichal/kcd2-multiplayer
- Architecture: Game <-> HTTP <-> Client Agent <-> TCP <-> Relay Server
- .NET 8, C#, Lua 5.1, xUnit
- Dev on Mac, test on Windows PC via Tailscale + SSH
- Hard rule: no unverified API assumptions
- Phase structure and current progress
- Key file paths

- [ ] **Step 2: Verify everything builds clean**

```bash
cd dotnet && dotnet build && dotnet test
```

- [ ] **Step 3: Final commit with any remaining cleanup**

```bash
git status
# Stage anything missed
git status
# Stage specific remaining files (do NOT use git add -A)
git commit -m "chore: Phase 0 + Phase 1 foundation complete"
```
