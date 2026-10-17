<#
.SYNOPSIS
  Installiert ein bereits geprüftes Agent-Update mit Rückfall auf die vorherige EXE.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PackageDir,
    [Parameter(Mandatory = $true)][string]$InstallDir,
    [Parameter(Mandatory = $true)][string]$Version
)

$ErrorActionPreference = "Stop"
$ServiceName = "EinsatzcockpitPrintAgent"
$DataDir = Join-Path $env:ProgramData "Einsatzcockpit\PrintAgent"
$Pending = Join-Path $DataDir "update-pending"
$StatusPath = Join-Path $DataDir "status.json"
$LogDir = Join-Path $DataDir "logs"
$LogPath = Join-Path $LogDir ("update-" + (Get-Date -Format "yyyyMMdd-HHmmss") + ".log")
$Exe = Join-Path $InstallDir "EinsatzcockpitPrintAgent.exe"
$Backup = "$Exe.bak"
$FailedPath = Join-Path $DataDir "update-failed.json"

New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
Start-Transcript -Path $LogPath -Append | Out-Null

function Wait-ForStop {
    param([int]$Seconds)
    $until = (Get-Date).AddSeconds($Seconds)
    do {
        $trays = Get-CimInstance Win32_Process -Filter "Name='EinsatzcockpitPrintAgent.exe'" |
            Where-Object { $_.CommandLine -match "tray" }
        if (-not $trays) { return }
        Start-Sleep -Seconds 2
    } while ((Get-Date) -lt $until)

    $trays | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
}

function Add-FailedVersion {
    $versions = @()
    if (Test-Path $FailedPath) {
        try { $versions = @(Get-Content $FailedPath -Raw | ConvertFrom-Json) } catch { $versions = @() }
    }
    if ($versions -notcontains $Version) { $versions += $Version }
    $versions | ConvertTo-Json | Set-Content -Path $FailedPath
}

function Test-AgentHealth {
    param([DateTimeOffset]$StartedAt)
    $until = (Get-Date).AddSeconds(60)
    do {
        $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        if ($service -and $service.Status -eq "Running" -and (Test-Path $StatusPath)) {
            try {
                $status = Get-Content $StatusPath -Raw | ConvertFrom-Json
                $updatedAt = [DateTimeOffset]::Parse($status.updated_at)
                if ($status.version -eq $Version -and $updatedAt -ge $StartedAt) { return $true }
            }
            catch { }
        }
        Start-Sleep -Seconds 2
    } while ((Get-Date) -lt $until)
    return $false
}

try {
    Wait-ForStop -Seconds 60
    $service = Get-Service -Name $ServiceName -ErrorAction Stop
    if ($service.Status -ne "Stopped") {
        Stop-Service -Name $ServiceName -Force
        $service.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(60))
    }

    if (Test-Path $Exe) { Copy-Item -Force $Exe $Backup }
    Copy-Item -Force (Join-Path $PackageDir "EinsatzcockpitPrintAgent.exe") $Exe
    foreach ($name in "install.ps1", "update.ps1", "README.md") {
        $source = Join-Path $PackageDir $name
        if (Test-Path $source) { Copy-Item -Force $source (Join-Path $InstallDir $name) }
    }

    $startTime = [DateTimeOffset]::UtcNow
    Start-Service -Name $ServiceName
    if (-not (Test-AgentHealth -StartedAt $startTime)) {
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
        if (Test-Path $Backup) { Copy-Item -Force $Backup $Exe }
        Start-Service -Name $ServiceName
        Add-FailedVersion
        throw "Gesundheitsprüfung der neuen Agentversion ist fehlgeschlagen."
    }
    Write-Host "Update erfolgreich installiert."
}
catch {
    Write-Error "Update fehlgeschlagen: $($_.Exception.Message)"
    if (Test-Path $Backup) { Copy-Item -Force $Backup $Exe }
    Add-FailedVersion
    Start-Service -Name $ServiceName -ErrorAction SilentlyContinue
    throw
}
finally {
    Remove-Item -Force $Pending -ErrorAction SilentlyContinue
    Stop-Transcript | Out-Null
}
