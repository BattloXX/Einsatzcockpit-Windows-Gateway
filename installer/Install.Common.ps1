# Gemeinsame, testbare Windows-Operationen des Installers. Keine Transkripte/Secrets.
function Test-AgentAdministrator {
    $principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}
function Resolve-AgentAccountSid {
    param([string]$Account)
    return ([Security.Principal.NTAccount]::new($Account)).Translate([Security.Principal.SecurityIdentifier]).Value
}
function Invoke-AgentSc {
    param([string[]]$Arguments)
    & sc.exe @Arguments | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Windows-Dienstkonfiguration fehlgeschlagen (sc.exe: $LASTEXITCODE)." }
}
function Invoke-AgentPair {
    param([string]$Exe, [string]$CloudUrl, [string]$Code)
    # stdin statt Prozessargument: Code steht nicht in der Prozessliste.
    $Code | & $Exe pair --url $CloudUrl --code-stdin
    if ($LASTEXITCODE -ne 0) { throw "Kopplung fehlgeschlagen. Bei ungueltigem, abgelaufenem oder verwendetem Code einen neuen Code in Einsatzcockpit erzeugen." }
}
function Set-AgentServiceBinary {
    param([string]$Name, [string]$Exe)
    # CIM erhaelt die Anfuehrungszeichen unveraendert, auch unter PowerShell 5.1.
    $service = Get-CimInstance -ClassName Win32_Service -Filter "Name='$Name'"
    $result = Invoke-CimMethod -InputObject $service -MethodName Change -Arguments @{ PathName = "`"$Exe`"" }
    if ($result.ReturnValue -ne 0) { throw "Dienstpfad konnte nicht gesetzt werden (Windows-Code $($result.ReturnValue))." }
}
function Set-AgentServiceAccount {
    param([string]$Name, [string]$Account, [Security.SecureString]$Password)
    $pointer = [IntPtr]::Zero
    try {
        $plain = ""
        if ($Password) {
            $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Password)
            $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
        }
        $service = Get-CimInstance -ClassName Win32_Service -Filter "Name='$Name'"
        $result = Invoke-CimMethod -InputObject $service -MethodName Change -Arguments @{ StartName = $Account; StartPassword = $plain }
        if ($result.ReturnValue -ne 0) { throw "Dienstkonto konnte nicht gesetzt werden (Windows-Code $($result.ReturnValue)). Passwort und Recht 'Als Dienst anmelden' pruefen." }
    } finally {
        $plain = $null
        if ($pointer -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
    }
}
function Set-AgentDataAccess {
    param([string]$Path, [string]$AccountSid)
    $grants = @('*S-1-5-18:(OI)(CI)F', '*S-1-5-32-544:(OI)(CI)F')
    if ($AccountSid -notin @('S-1-5-18', 'S-1-5-32-544')) { $grants += "*${AccountSid}:(OI)(CI)F" }
    & icacls.exe $Path /inheritance:r /grant:r @grants | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Datenverzeichnis konnte nicht geschuetzt werden (icacls: $LASTEXITCODE)." }
}
function Set-AgentTokenAccess {
    param([string]$Path, [string]$AccountSid)
    if (Test-Path $Path) {
        & icacls.exe $Path /grant:r "*${AccountSid}:F" | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Dienstkonto erhaelt keinen Zugriff auf das geschuetzte Token (icacls: $LASTEXITCODE)." }
    }
}
function Wait-AgentConnection {
    param([string]$StatusPath, [DateTimeOffset]$StartedAt, [int]$TimeoutSeconds = 60)
    $until = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        if (Test-Path $StatusPath) {
            try {
                $status = Get-Content $StatusPath -Raw | ConvertFrom-Json
                if ($status.connected -eq $true -and [DateTimeOffset]::Parse($status.started_at) -ge $StartedAt -and
                    [DateTimeOffset]::Parse($status.updated_at) -ge $StartedAt) { return $true }
            } catch { } # Die atomare Statusdatei kann waehrend des Starts noch fehlen.
        }
        Start-Sleep -Seconds 2
    } while ([DateTimeOffset]::UtcNow -lt $until)
    return $false
}
