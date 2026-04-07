param(
    [string]$GameApi = "http://localhost:1403",
    [string]$OutputPath = "probes/phase4/ft022-runtime-results.md"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Invoke-KcdApiGet {
    param([Parameter(Mandatory = $true)][string]$Url)
    return (Invoke-WebRequest -UseBasicParsing -TimeoutSec 5 -Uri $Url).Content
}

function Invoke-Lua {
    param([Parameter(Mandatory = $true)][string]$Lua)
    $encoded = [Uri]::EscapeDataString("#$Lua")
    $url = "$GameApi/api/System/Console/ExecuteString?command=$encoded"
    return Invoke-KcdApiGet -Url $url
}

function Read-Cvar {
    param([Parameter(Mandatory = $true)][string]$Name)
    $url = "$GameApi/api/System/Console/GetCvarValue?name=$Name"
    $xml = Invoke-KcdApiGet -Url $url
    $match = [regex]::Match($xml, ">([^<]*)<")
    if ($match.Success) { return $match.Groups[1].Value }
    return ""
}

function Run-ProbeCase {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Lua
    )

    try {
        [void](Invoke-Lua -Lua $Lua)
        $value = Read-Cvar -Name "sv_servername"
        return [pscustomobject]@{
            Probe = $Name
            Status = "OK"
            Value = $value
        }
    }
    catch {
        return [pscustomobject]@{
            Probe = $Name
            Status = "ERROR"
            Value = $_.Exception.Message
        }
    }
}

$cases = @(
    @{
        Name = "session_applied"
        Lua = 'System.SetCVar("sv_servername", tostring(KCD2MP_ApplySessionContext("{\"characterId\":\"cid_probe\"}")))'
    },
    @{
        Name = "session_missing_character"
        Lua = 'System.SetCVar("sv_servername", tostring(KCD2MP_ApplySessionContext("{\"sessionId\":\"s_probe\"}")))'
    },
    @{
        Name = "presence_mode_limited"
        Lua = 'System.SetCVar("sv_servername", tostring(KCD2MP_ApplyPresenceProjection("{\"mode\":\"entity_stream\"}")))'
    },
    @{
        Name = "lifecycle_unconscious_partial"
        Lua = 'System.SetCVar("sv_servername", tostring(KCD2MP_ApplyLifecycleProjection("{\"defeatState\":\"Unconscious\"}")))'
    },
    @{
        Name = "inventory_partial"
        Lua = 'System.SetCVar("sv_servername", tostring(KCD2MP_ApplyInventoryProjection("{\"reflectable\":true}")))'
    },
    @{
        Name = "currency_not_applicable"
        Lua = 'System.SetCVar("sv_servername", tostring(KCD2MP_ApplyCurrencyProjection("{\"reflectable\":true}")))'
    },
    @{
        Name = "administrative_invalidation_cleanup"
        Lua = 'System.SetCVar("sv_servername", tostring(KCD2MP_ApplyAdministrativeProjection("{\"accessDenied\":true,\"message\":\"probe access denied\"}")))'
    },
    @{
        Name = "runtime_clear_state"
        Lua = 'System.SetCVar("sv_servername", tostring(KCD2MP_ClearRuntimeProjectionState("probe runtime clear")))'
    }
)

$results = New-Object System.Collections.Generic.List[object]

$apiCheckMessage = ""
try {
    [void](Invoke-KcdApiGet -Url "$GameApi/api/rpg/Calendar?depth=1")
    $apiCheckMessage = "API reachable"
}
catch {
    $apiCheckMessage = "API unavailable: $($_.Exception.Message)"
}

foreach ($case in $cases) {
    $results.Add((Run-ProbeCase -Name $case.Name -Lua $case.Lua))
}

$timestamp = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss zzz")
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("# FT-022 Runtime Probe Results")
$lines.Add("")
$lines.Add("- Date: $timestamp")
$lines.Add("- Game API: $GameApi")
$lines.Add("- Connectivity: $apiCheckMessage")
$lines.Add("")
$lines.Add("| Probe | Status | Value |")
$lines.Add("|-------|--------|-------|")
foreach ($r in $results) {
    $value = ($r.Value -replace "\|", "\/") -replace "`r", " " -replace "`n", " "
    $lines.Add("| $($r.Probe) | $($r.Status) | $value |")
}
$lines.Add("")
$lines.Add("## Raw Results")
foreach ($r in $results) {
    $lines.Add("")
    $lines.Add("### $($r.Probe)")
    $lines.Add("- Status: $($r.Status)")
    $lines.Add("- Value: $($r.Value)")
}

$dir = Split-Path -Parent $OutputPath
if (-not [string]::IsNullOrWhiteSpace($dir)) {
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
}
Set-Content -Path $OutputPath -Value ($lines -join [Environment]::NewLine) -Encoding UTF8

Write-Host "Probe results written to: $OutputPath"
