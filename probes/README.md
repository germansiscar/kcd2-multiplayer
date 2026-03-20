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
