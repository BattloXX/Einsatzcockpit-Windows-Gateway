<#
.SYNOPSIS
  Installiert, aktualisiert oder deinstalliert den Einsatzcockpit-Druckagent.
.EXAMPLE
  .\install.ps1 -CloudUrl "https://einsatzcockpit.com" -PairingCode "ABCD1234"
.DESCRIPTION
  Als Administrator im entpackten ZIP ausfuehren. Ohne Code wird dieser bei einer
  Erstinstallation verdeckt abgefragt. Updates behalten Token, Daten und Dienstkonto.
  Nur benannte PowerShell-Parameter mit einem Bindestrich verwenden.
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [string]$CloudUrl,
    [Alias("Code")][string]$PairingCode,
    [string]$ServiceAccount,
    [System.Security.SecureString]$ServicePassword,
    [string]$Source = (Join-Path $PSScriptRoot "EinsatzcockpitPrintAgent.exe"),
    [switch]$Uninstall,
    [switch]$NonInteractive
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "Install.Common.ps1")
$ServiceName = "EinsatzcockpitPrintAgent"
$DisplayName = "Einsatzcockpit Druckagent"
$InstallDir = Join-Path $env:ProgramFiles "Einsatzcockpit\PrintAgent"
$DataDir = Join-Path $env:ProgramData "Einsatzcockpit\PrintAgent"
$Exe = Join-Path $InstallDir "EinsatzcockpitPrintAgent.exe"
$EventSource = "Einsatzcockpit Druckagent"
$RunPath = "HKLM:\Software\Microsoft\Windows\CurrentVersion\Run"
$TrayValue = "EinsatzcockpitPrintAgentTray"

# Alle Eingaben pruefen, bevor ein bestehender Dienst angehalten wird.
if (-not (Test-AgentAdministrator)) { throw "Bitte PowerShell oder Installieren.cmd als Administrator starten." }
if ($ServicePassword -and -not $ServiceAccount) {
    throw "-ServicePassword erfordert ein ausdrueckliches -ServiceAccount. Der Pairing-Code gehoert zu -PairingCode."
}
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
$wasRunning = $existing -and $existing.Status -ne "Stopped"

function Stop-AgentService {
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($svc -and $svc.Status -ne "Stopped") {
        Stop-Service -Name $ServiceName -Force
        $svc.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(60))
    }
}
function Stop-TrayProcesses {
    Get-CimInstance Win32_Process -Filter "Name='EinsatzcockpitPrintAgent.exe'" |
        Where-Object { $_.CommandLine -match "tray" } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
}

if ($Uninstall) {
    Stop-AgentService
    Stop-TrayProcesses
    Remove-ItemProperty -Path $RunPath -Name $TrayValue -ErrorAction SilentlyContinue
    if ($existing) {
        Invoke-AgentSc @("delete", $ServiceName)
        $until = [DateTime]::UtcNow.AddSeconds(30)
        while (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
            if ([DateTime]::UtcNow -ge $until) { throw "Dienst noch zum Loeschen vorgemerkt. Dienste-Fenster schliessen oder Windows neu starten." }
            Start-Sleep -Seconds 1
        }
    }
    if (Test-Path $InstallDir) { Remove-Item -Recurse -Force $InstallDir }
    Write-Host "Deinstalliert. Daten und Token bleiben in $DataDir erhalten. Neuinstallation verwendet diese weiter."
    return
}

if (-not [Environment]::Is64BitOperatingSystem) { throw "Dieses ZIP benoetigt Windows x64." }
if ($env:ECPA_DATA_DIR -and [IO.Path]::GetFullPath($env:ECPA_DATA_DIR) -ne [IO.Path]::GetFullPath($DataDir)) {
    throw "ECPA_DATA_DIR verweist auf einen anderen Datenordner. Fuer die Dienstinstallation diese Umgebungsvariable entfernen."
}
if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) { throw "EinsatzcockpitPrintAgent.exe fehlt. ZIP vollstaendig entpacken." }
if (-not (Get-Service -Name Spooler -ErrorAction SilentlyContinue)) { throw "Windows-Druckwarteschlange (Spooler) fehlt." }
if ((Get-CimInstance -ClassName Win32_Service -Filter "Name='Spooler'").StartMode -eq "Disabled") {
    throw "Windows-Druckwarteschlange (Spooler) ist deaktiviert. Bitte in der Windows-Diensteverwaltung aktivieren."
}
if (-not $CloudUrl) {
    $config = Join-Path $DataDir "agent.json"
    if (Test-Path $config) { $CloudUrl = (Get-Content $config -Raw | ConvertFrom-Json).cloud_url }
    if (-not $CloudUrl) { $CloudUrl = "https://einsatzcockpit.com" }
}
$cloud = $null
if (-not [Uri]::TryCreate($CloudUrl, [UriKind]::Absolute, [ref]$cloud) -or
    $cloud.Scheme -ne "https" -or $cloud.UserInfo -or $cloud.Query -or $cloud.Fragment) {
    throw "-CloudUrl muss eine HTTPS-Adresse ohne Zugangsdaten, Query oder Fragment sein."
}
$paired = Test-Path (Join-Path $DataDir "token.bin")
if (-not $PairingCode -and (-not $paired -or $PSBoundParameters.ContainsKey("CloudUrl"))) {
    if ($NonInteractive) { throw "Pairing-Code fehlt. -PairingCode angeben oder interaktiv installieren." }
    $secret = Read-Host -AsSecureString "Pairing-Code aus Einsatzcockpit (8 Zeichen, 10 Minuten gueltig)"
    $PairingCode = [Net.NetworkCredential]::new("", $secret).Password
    $secret.Dispose()
}
if ($PairingCode) {
    $PairingCode = $PairingCode.Trim().ToUpperInvariant()
    if ($PairingCode -notmatch '^[A-Z0-9]{8}$') { throw "Pairing-Code muss aus 8 Buchstaben oder Ziffern bestehen. Bitte neuen Code in Einsatzcockpit erzeugen." }
} elseif (-not $paired) { throw "Pairing-Code fehlt. Bitte neuen Code in Einsatzcockpit erzeugen." }

# Bestehendes Konto beibehalten; nur ausdrueckliche Angabe aendert es.
$account = "LocalSystem"
if ($existing) { $account = (Get-CimInstance -ClassName Win32_Service -Filter "Name='$ServiceName'").StartName }
if ($ServiceAccount) { $account = $ServiceAccount }
$accountSid = "S-1-5-18"
if ($account -notin @("LocalSystem", "NT AUTHORITY\SYSTEM")) {
    $accountSid = Resolve-AgentAccountSid $account
}
$builtin = $accountSid -in @("S-1-5-18", "S-1-5-19", "S-1-5-20") -or $account.StartsWith("NT SERVICE\") -or $account.EndsWith('$')
if ($ServiceAccount -and -not $builtin -and -not $ServicePassword) {
    if ($NonInteractive) { throw "Dieses Dienstkonto benoetigt -ServicePassword als SecureString." }
    $ServicePassword = Read-Host -AsSecureString "Windows-Passwort fuer $ServiceAccount (kein Pairing-Code)"
}
if ($ServicePassword -and $builtin) { throw "Dieses Dienstkonto benoetigt kein Passwort. -ServicePassword weglassen." }

Write-Host "[1/4] Voraussetzungen geprueft. Dienst wird installiert/aktualisiert ..."
$backup = "$Exe.install-backup"
$copied = $false
try {
    Stop-AgentService
    Stop-TrayProcesses
    New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
    if (([IO.Path]::GetFullPath($Source)) -ne ([IO.Path]::GetFullPath($Exe))) {
        if (Test-Path $Exe) { Copy-Item -Force $Exe $backup }
        Copy-Item -Force $Source $Exe
        $copied = $true
    }
    # Auch die lokale Deinstallation und automatische Updates brauchen diese Dateien.
    foreach ($name in @("install.ps1", "Install.Common.ps1", "Installieren.cmd", "update.ps1", "README.md")) {
        $file = Join-Path $PSScriptRoot $name
        $target = Join-Path $InstallDir $name
        if ((Test-Path $file) -and ([IO.Path]::GetFullPath($file) -ne [IO.Path]::GetFullPath($target))) { Copy-Item -Force $file $target }
    }
    if (-not [Diagnostics.EventLog]::SourceExists($EventSource)) { [Diagnostics.EventLog]::CreateEventSource($EventSource, "Application") }
    if (-not $existing) {
        New-Service -Name $ServiceName -DisplayName $DisplayName -BinaryPathName "`"$Exe`"" -StartupType Automatic `
            -Description "Druckt Auftraege aus Einsatzcockpit ueber den Windows-Druckspooler." | Out-Null
    }
    Set-AgentServiceBinary $ServiceName $Exe
    Invoke-AgentSc @("config", $ServiceName, "start=", "delayed-auto", "depend=", "Spooler")
    Invoke-AgentSc @("failure", $ServiceName, "reset=", "86400", "actions=", "restart/10000/restart/30000/restart/60000")
    Invoke-AgentSc @("failureflag", $ServiceName, "1")
    if ($ServiceAccount) { Set-AgentServiceAccount $ServiceName $account $ServicePassword }

    Write-Host "[2/4] Daten schuetzen und Kopplung pruefen ..."
    New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
    Set-AgentDataAccess $DataDir $accountSid
    if ($PairingCode) {
        Invoke-AgentPair $Exe $CloudUrl $PairingCode
        $PairingCode = $null
    }
    # DPAPI-Token ist zusaetzlich auf SYSTEM/Administratoren beschraenkt.
    Set-AgentTokenAccess (Join-Path $DataDir "token.bin") $accountSid
    Write-Host "[3/4] Windows-Dienst starten ..."
    $startedAt = [DateTimeOffset]::UtcNow
    Start-Service -Name $ServiceName
    (Get-Service $ServiceName).WaitForStatus("Running", [TimeSpan]::FromSeconds(30))
    New-Item -Path $RunPath -Force | Out-Null
    Set-ItemProperty -Path $RunPath -Name $TrayValue -Value ("`"$Exe`" tray")
    Start-Process -FilePath $Exe -ArgumentList "tray" -ErrorAction SilentlyContinue
    Write-Host "[4/4] Cloud-Verbindung pruefen (bis zu 60 Sekunden) ..."
    if (-not (Wait-AgentConnection (Join-Path $DataDir "status.json") $startedAt)) {
        throw "Dienst installiert, aber Cloud-Verbindung nicht bestaetigt. HTTPS/WSS (Port 443), Proxy und Tray-Status pruefen. Daten bleiben erhalten; Installation erneut ausfuehren."
    }
    if (Test-Path $backup) { Remove-Item -Force $backup }
    Write-Host "Installation erfolgreich: Dienst laeuft, mit Einsatzcockpit verbunden. Jetzt im Einsatzcockpit die Warteschlange einem Druckziel zuordnen und Testseite drucken."
} catch {
    # Vorhandene EXE und vorher laufenden Dienst bei einem fehlgeschlagenen Update retten.
    if ($existing -and $copied -and (Test-Path $backup)) {
        Stop-AgentService
        Copy-Item -Force $backup $Exe
        if ($wasRunning) { Start-Service -Name $ServiceName }
    }
    throw
} finally {
    $PairingCode = $null
}
