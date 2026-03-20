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
export PATH="$HOME/.dotnet:$PATH"
cd dotnet && dotnet build

# Run tests
dotnet test

# Cross-compile for Windows
dotnet publish KcdMp.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish/app
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
