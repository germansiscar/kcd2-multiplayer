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
    $steamPath = (Get-ItemProperty -Path "HKLM:\SOFTWARE\WOW6432Node\Valve\Steam" -Name InstallPath -ErrorAction SilentlyContinue).InstallPath
    if (-not $steamPath) {
        $steamPath = (Get-ItemProperty -Path "HKLM:\SOFTWARE\Valve\Steam" -Name InstallPath -ErrorAction SilentlyContinue).InstallPath
    }

    if ($steamPath) {
        $candidates = @()

        # Parse libraryfolders.vdf for all library paths
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

        # Also check default Steam path
        $candidates += "$steamPath\steamapps\common\KCD2ModMods"
        $candidates += "$steamPath\steamapps\common\KCD2ModdingTools"

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
