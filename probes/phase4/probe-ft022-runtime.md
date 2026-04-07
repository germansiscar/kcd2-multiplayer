# FT-022 Runtime Probes

Purpose: validate that runtime projection handlers return explicit apply results:

- `applied`
- `partial`
- `not_applicable`
- `failed`

and validate runtime cleanup on administrative/session invalidation.

## Automated Run

Use:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/probe-ft022.ps1 -GameApi http://localhost:1403
```

Result file:

- `probes/phase4/ft022-runtime-results.md`

## Manual Quick Probes

```lua
System.SetCVar("sv_servername", tostring(KCD2MP_ApplySessionContext("{\"characterId\":\"cid_probe\"}")))
System.SetCVar("sv_servername", tostring(KCD2MP_ApplyAdministrativeProjection("{\"accessDenied\":true,\"message\":\"probe denied\"}")))
System.SetCVar("sv_servername", tostring(KCD2MP_ClearRuntimeProjectionState("manual clear")))
```
