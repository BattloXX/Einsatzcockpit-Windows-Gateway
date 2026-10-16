<#
.SYNOPSIS
  Installiert oder aktualisiert den Einsatzcockpit-Druckagent als Windows-Dienst.

.DESCRIPTION
  - Kopiert EinsatzcockpitPrintAgent.exe nach %ProgramFiles%\Einsatzcockpit\PrintAgent
  - Legt den Dienst "EinsatzcockpitPrintAgent" an: Start automatisch (verzögert), startet
    ohne Benutzeranmeldung, Wiederherstellung: Neustart nach 10 s / 30 s / 60 s
  - Optional: koppelt den Agent direkt (-CloudUrl + -PairingCode)
  - Optional: eigenes Dienstkonto (-ServiceAccount), nötig nur für benutzergebundene
    Druckerfreigaben (\\server\drucker). Standard: LocalSystem.

  Ein laufender Dienst wird für das Update gestoppt; offene Aufträge bleiben in der lokalen
  Datenbank (%ProgramData%\Einsatzcockpit\PrintAgent) und werden nach dem Start sicher
  weiterbehandelt (kein Doppeldruck, siehe docs/print-agent-architektur.md).

.EXAMPLE
  .\install.ps1 -CloudUrl https://einsatzcockpit.example.at -PairingCode ABCD1234
#>
[CmdletBinding()]
param(
    [string]$CloudUrl,
    [string]$PairingCode,
    [string]$ServiceAccount,
    [System.Security.SecureString]$ServicePassword,
    [string]$Source = (Join-Path $PSScriptRoot "EinsatzcockpitPrintAgent.exe"),
    [switch]$Uninstall
)

$ErrorActionPreference = "Stop"
$ServiceName = "EinsatzcockpitPrintAgent"
$DisplayName = "Einsatzcockpit Druckagent"
$InstallDir = Join-Path $env:ProgramFiles "Einsatzcockpit\PrintAgent"
$Exe = Join-Path $InstallDir "EinsatzcockpitPrintAgent.exe"
$EventSource = "Einsatzcockpit Druckagent"

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Bitte PowerShell als Administrator starten."
}

function Stop-AgentService {
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($svc -and $svc.Status -ne "Stopped") {
        Write-Host "Dienst wird gestoppt ..."
        Stop-Service -Name $ServiceName -Force
        $svc.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(30))
    }
}

if ($Uninstall) {
    Stop-AgentService
    if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
        & sc.exe delete $ServiceName | Out-Null
    }
    if (Test-Path $InstallDir) { Remove-Item -Recurse -Force $InstallDir }
    Write-Host "Deinstalliert. Daten und Token bleiben in $env:ProgramData\Einsatzcockpit\PrintAgent erhalten."
    return
}

if (-not (Test-Path $Source)) {
    throw "EinsatzcockpitPrintAgent.exe nicht gefunden: $Source"
}

Stop-AgentService
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Copy-Item -Force $Source $Exe

if (-not [System.Diagnostics.EventLog]::SourceExists($EventSource)) {
    New-EventLog -LogName Application -Source $EventSource
}

if (-not (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)) {
    Write-Host "Dienst wird angelegt ..."
    New-Service -Name $ServiceName -DisplayName $DisplayName -BinaryPathName "`"$Exe`"" `
        -StartupType Automatic -Description "Druckt Aufträge aus Einsatzcockpit über den Windows-Druckspooler." | Out-Null
}

# Verzögerter Autostart: Netzwerk und Spooler sind dann sicher bereit. Läuft ohne Anmeldung.
& sc.exe config $ServiceName start= delayed-auto | Out-Null
# Spooler als Abhängigkeit, damit der Agent nach einem Neustart nicht vor ihm startet.
& sc.exe config $ServiceName depend= Spooler | Out-Null
# Wiederherstellung bei Absturz: nach 10 s, 30 s, 60 s neu starten; Zähler nach 1 Tag zurücksetzen.
& sc.exe failure $ServiceName reset= 86400 actions= restart/10000/restart/30000/restart/60000 | Out-Null
& sc.exe failureflag $ServiceName 1 | Out-Null

if ($ServiceAccount) {
    if (-not $ServicePassword) { $ServicePassword = Read-Host -AsSecureString "Passwort für $ServiceAccount" }
    $plain = [Runtime.InteropServices.Marshal]::PtrToStringAuto(
        [Runtime.InteropServices.Marshal]::SecureStringToBSTR($ServicePassword))
    & sc.exe config $ServiceName obj= $ServiceAccount password= $plain | Out-Null
    $plain = $null
    Write-Host "Dienst läuft als $ServiceAccount (Recht 'Als Dienst anmelden' muss vorhanden sein)."
}

if ($CloudUrl -and $PairingCode) {
    Write-Host "Kopplung ..."
    & $Exe pair --url $CloudUrl --code $PairingCode
    if ($LASTEXITCODE -ne 0) { throw "Kopplung fehlgeschlagen (siehe Meldung oben)." }
}

Start-Service -Name $ServiceName
Write-Host "Fertig. Status: " -NoNewline
& $Exe status
