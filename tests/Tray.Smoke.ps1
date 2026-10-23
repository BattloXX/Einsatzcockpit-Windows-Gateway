[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$Exe)
$ErrorActionPreference = "Stop"
$Exe = (Resolve-Path $Exe).Path
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("ecpa-tray-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$originalAcl = Get-Acl $testRoot
$originalData = $env:ECPA_DATA_DIR
$process = $null
try {
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $acl = Get-Acl $testRoot
    $deny = [Security.AccessControl.FileSystemAccessRule]::new($sid,
        [Security.AccessControl.FileSystemRights]::Write,
        [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [Security.AccessControl.InheritanceFlags]::ObjectInherit,
        [Security.AccessControl.PropagationFlags]::None, [Security.AccessControl.AccessControlType]::Deny)
    $acl.AddAccessRule($deny)
    Set-Acl -Path $testRoot -AclObject $acl
    $env:ECPA_DATA_DIR = $testRoot
    $process = Start-Process -FilePath $Exe -ArgumentList "tray" -PassThru
    if ($process.WaitForExit(5000)) { throw "Tray beendet sich vorzeitig (Exitcode $($process.ExitCode))." }
    if (@(Get-ChildItem -LiteralPath $testRoot -Force).Count -ne 0) { throw "Tray hat Dienstdateien angelegt." }
    Write-Host "Tray startet trotz verweigertem Schreibzugriff auf Dienstordner."
} finally {
    if ($process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force }
    $env:ECPA_DATA_DIR = $originalData
    Set-Acl -Path $testRoot -AclObject $originalAcl
    Remove-Item -LiteralPath $testRoot -Recurse -Force
}
