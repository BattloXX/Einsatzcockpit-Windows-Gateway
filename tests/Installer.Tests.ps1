BeforeAll {
    $originalProgramFiles = $env:ProgramFiles
    $originalProgramData = $env:ProgramData
    $originalDataDir = $env:ECPA_DATA_DIR
    $env:ECPA_DATA_DIR = $null
    $installer = Join-Path $PSScriptRoot '../installer/install.ps1'
    . (Join-Path $PSScriptRoot '../installer/Install.Common.ps1')
    # Hauptskript laedt die echten Funktionen. Im Test nur einmal laden, danach Mock-Grenzen.
    $installBody = (Get-Content $installer -Raw) -replace '\. \(Join-Path \$PSScriptRoot "Install.Common.ps1"\)', ''
    $testInstaller = Join-Path $TestDrive 'install.ps1'
    Set-Content $testInstaller $installBody
    # Nicht vorhandene Windows-Cmdlets unter Linux nur als Mock-Grenzen bereitstellen.
    foreach ($name in @('Get-Service','Stop-Service','Start-Service','New-Service','Get-CimInstance',
        'Invoke-CimMethod','New-EventLog','Remove-ItemProperty','Set-ItemProperty')) {
        if (-not (Get-Command $name -ErrorAction SilentlyContinue)) {
            Set-Item "function:global:$name" { [CmdletBinding()]param($Name, $Filter, $ClassName, $InputObject, $MethodName, $Arguments,
                $DisplayName, $BinaryPathName, $StartupType, $Description, $LogName, $Source, $Path, $Value, [switch]$Force) }
        }
    }
}

Describe 'Parameterbindung des ausgelieferten Skripts' {
    BeforeEach {
        $env:ProgramFiles = Join-Path $TestDrive 'ProgramFiles'
        $env:ProgramData = Join-Path $TestDrive 'ProgramData'
        Mock Test-AgentAdministrator { throw 'ADMIN-GRENZE' } }
    It 'bindet PairingCode und Code benannt ohne ServicePassword' {
        { & $testInstaller -CloudUrl 'https://cloud.example' -PairingCode 'ABCD2345' } | Should -Throw '*ADMIN-GRENZE*'
        { & $testInstaller -CloudUrl 'https://cloud.example' -Code 'ABCD2345' } | Should -Throw '*ADMIN-GRENZE*'
    }
    It 'weist positionsbasierte und doppelte Bindestriche vor jeder Aenderung ab' {
        { & $testInstaller -CloudUrl 'https://cloud.example' 'ABCD2345' } | Should -Throw '*positional*'
        { & $testInstaller -CloudUrl 'https://cloud.example' --PairingCode 'ABCD2345' } | Should -Throw
        { & $testInstaller -CloudUrl 'https://cloud.example' --code 'ABCD2345' } | Should -Throw
        Should -Invoke Test-AgentAdministrator -Times 0
    }
    It 'bindet ein ausdrueckliches SecureString getrennt vom Code' {
        $password = ConvertTo-SecureString 'test-windows-password' -AsPlainText -Force
        { & $testInstaller -CloudUrl 'https://cloud.example' -PairingCode 'ABCD2345' -ServiceAccount 'DOMAIN\svc' -ServicePassword $password } | Should -Throw '*ADMIN-GRENZE*'
    }
}

Describe 'Installationsablauf mit gemockten Windows-Operationen' {
    BeforeEach {
        $env:ProgramFiles = Join-Path $TestDrive 'ProgramFiles'
        $env:ProgramData = Join-Path $TestDrive 'ProgramData'
        $sourceExe = Join-Path $TestDrive 'EinsatzcockpitPrintAgent.exe'
        Set-Content $sourceExe 'fake-executable'
        $dataPath = Join-Path $env:ProgramData 'Einsatzcockpit/PrintAgent'
        New-Item -ItemType Directory -Path $dataPath -Force | Out-Null
        Remove-Item (Join-Path $dataPath '*') -Recurse -Force
        $global:EcpaTestPresent = $false
        $global:EcpaTestRunning = $false
        $global:EcpaTestService = [pscustomobject]@{ Status = 'Stopped' }
        $global:EcpaTestService | Add-Member ScriptMethod WaitForStatus { param($status, $timeout) }
        Mock Test-AgentAdministrator { $true }
        Mock Get-Service {
            if ($Name -eq 'Spooler' -or $global:EcpaTestPresent) { return $global:EcpaTestService }
        }
        Mock Get-CimInstance { }
        Mock New-Service { $global:EcpaTestPresent = $true }
        Mock Stop-Service { $global:EcpaTestRunning = $false }
        Mock Start-Service { $global:EcpaTestRunning = $true }
        Mock Stop-Process { }
        Mock Invoke-AgentSc { if ($Arguments[0] -eq 'delete') { $global:EcpaTestPresent = $false } }
        Mock Set-AgentDataAccess { }
        Mock Set-AgentTokenAccess { }
        Mock Invoke-AgentPair { Set-Content (Join-Path $dataPath 'token.bin') 'protected-test-token' }
        Mock Set-AgentServiceAccount { }
        Mock Set-AgentServiceBinary { }
        Mock Wait-AgentConnection { $true }
        Mock New-Item { } -ParameterFilter { $Path -like 'HKLM:*' }
        Mock Remove-ItemProperty { }
        Mock Set-ItemProperty { }
        Mock Start-Process { }
        # EventLog.SourceExists kann unter Linux nicht aufgerufen werden.
        $body = $installBody -replace 'if \(-not \[Diagnostics.EventLog\]::SourceExists\(\$EventSource\)\)', 'if ($false)'
        Set-Content $testInstaller $body
    }
    It 'installiert mit Code als LocalSystem ohne Passwortabfrage und bestaetigt Verbindung' {
        Mock Read-Host { throw 'Unerwartete Passwortabfrage' }
        & $testInstaller -Source $sourceExe -CloudUrl 'https://cloud.example' -PairingCode ' abcd2345 '
        Should -Invoke Invoke-AgentPair -Times 1 -ParameterFilter { $Code -ceq 'ABCD2345' }
        Should -Invoke Set-AgentServiceAccount -Times 0
        Should -Invoke Wait-AgentConnection -Times 1
        $global:EcpaTestRunning | Should -BeTrue
    }
    It 'fragt fehlenden Code verdeckt ab' {
        Mock Read-Host { ConvertTo-SecureString 'ABCD2345' -AsPlainText -Force }
        & $testInstaller -Source $sourceExe
        Should -Invoke Read-Host -Times 1 -ParameterFilter { $AsSecureString }
        Should -Invoke Invoke-AgentPair -Times 1
    }
    It 'verweigert fehlenden Code im unbeaufsichtigten Betrieb vor Dienstinstallation' {
        { & $testInstaller -Source $sourceExe -NonInteractive } | Should -Throw '*Pairing-Code fehlt*'
        Should -Invoke New-Service -Times 0
    }
    It 'verweigert einen syntaktisch ungueltigen Code vor Dienstinstallation' {
        { & $testInstaller -Source $sourceExe -PairingCode 'bad' } | Should -Throw '*8 Buchstaben*'
        Should -Invoke New-Service -Times 0
    }
    It 'behandelt einen abgelehnten Code ohne Dienststart' {
        Mock Invoke-AgentPair { throw 'Pairing-Code ungueltig, abgelaufen oder verwendet' }
        { & $testInstaller -Source $sourceExe -PairingCode 'ABCD2345' } | Should -Throw '*verwendet*'
        Should -Invoke Start-Service -Times 0
    }
    It 'weist fehlende Administratorrechte vor jeder Aenderung ab' {
        Mock Test-AgentAdministrator { $false }
        { & $testInstaller -Source $sourceExe -PairingCode 'ABCD2345' } | Should -Throw '*Administrator*'
        Should -Invoke New-Service -Times 0
        Should -Invoke Invoke-AgentPair -Times 0
    }
    It 'weist ein Passwort ohne ausdrueckliches Dienstkonto ab' {
        $password = ConvertTo-SecureString 'test-password' -AsPlainText -Force
        { & $testInstaller -Source $sourceExe -ServicePassword $password } | Should -Throw '*ServiceAccount*'
        Should -Invoke New-Service -Times 0
    }
    It 'uebergibt ein ausdrueckliches Dienstpasswort ausschliesslich an die Kontokonfiguration' {
        Mock Resolve-AgentAccountSid { 'S-1-5-21-1234-5678-9012-1001' }
        $password = ConvertTo-SecureString 'test-windows-password' -AsPlainText -Force
        & $testInstaller -Source $sourceExe -PairingCode 'ABCD2345' -ServiceAccount 'DOMAIN\svc' -ServicePassword $password
        Should -Invoke Set-AgentServiceAccount -Times 1 -ParameterFilter { $Account -eq 'DOMAIN\svc' -and $Password -is [Security.SecureString] }
        Should -Invoke Invoke-AgentPair -Times 1 -ParameterFilter { $Code -eq 'ABCD2345' }
    }
    It 'behaelt beim Update Token, Auftragsdaten und Dienstkonto' {
        $global:EcpaTestPresent = $true
        Mock Get-CimInstance { [pscustomobject]@{ StartName = 'LocalSystem' } } -ParameterFilter { $ClassName -eq 'Win32_Service' }
        Set-Content (Join-Path $dataPath 'token.bin') 'old-token'
        Set-Content (Join-Path $dataPath 'agent.db') 'existing-job-data'
        & $testInstaller -Source $sourceExe -NonInteractive
        Should -Invoke Invoke-AgentPair -Times 0
        Should -Invoke Set-AgentServiceAccount -Times 0
        Get-Content (Join-Path $dataPath 'token.bin') | Should -Be 'old-token'
        Get-Content (Join-Path $dataPath 'agent.db') | Should -Be 'existing-job-data'
    }
    It 'stellt beim fehlgeschlagenen Update die vorherige EXE wieder her' {
        $global:EcpaTestPresent = $true
        $global:EcpaTestService.Status = 'Running'
        Mock Get-CimInstance { [pscustomobject]@{ StartName = 'LocalSystem' } } -ParameterFilter { $ClassName -eq 'Win32_Service' }
        $installed = Join-Path $env:ProgramFiles 'Einsatzcockpit/PrintAgent/EinsatzcockpitPrintAgent.exe'
        New-Item -ItemType Directory -Force -Path (Split-Path $installed) | Out-Null
        Set-Content $installed 'previous-executable'
        Mock Invoke-AgentPair { throw 'Pairing fehlgeschlagen' }
        { & $testInstaller -Source $sourceExe -PairingCode 'ABCD2345' } | Should -Throw '*Pairing fehlgeschlagen*'
        Get-Content $installed | Should -Be 'previous-executable'
        $global:EcpaTestRunning | Should -BeTrue
    }
    It 'meldet fehlende Cloud-Verbindung als Fehler' {
        Mock Wait-AgentConnection { $false }
        { & $testInstaller -Source $sourceExe -PairingCode 'ABCD2345' } | Should -Throw '*Cloud-Verbindung nicht bestaetigt*'
    }
    It 'deinstalliert und installiert erneut ohne Datenverlust und ohne neuen Code' {
        $global:EcpaTestPresent = $true
        Set-Content (Join-Path $dataPath 'token.bin') 'old-token'
        Set-Content (Join-Path $dataPath 'agent.db') 'existing-job-data'
        & $testInstaller -Uninstall
        $global:EcpaTestPresent | Should -BeFalse
        & $testInstaller -Source $sourceExe -NonInteractive
        Should -Invoke Invoke-AgentPair -Times 0
        Get-Content (Join-Path $dataPath 'agent.db') | Should -Be 'existing-job-data'
        $global:EcpaTestRunning | Should -BeTrue
    }
}

AfterAll {
    $env:ProgramFiles = $originalProgramFiles
    $env:ProgramData = $originalProgramData
    $env:ECPA_DATA_DIR = $originalDataDir
    Remove-Variable EcpaTestPresent, EcpaTestRunning, EcpaTestService -Scope Global -ErrorAction SilentlyContinue
}

Describe 'Frische Verbindungsbestaetigung' {
    It 'akzeptiert keine Statusdatei von einem frueheren Dienststart' {
        $status = Join-Path $TestDrive 'status.json'
        @{ connected = $true; started_at = '2020-01-01T00:00:00Z'; updated_at = [DateTimeOffset]::UtcNow.ToString('o') } |
            ConvertTo-Json | Set-Content $status
        Mock Start-Sleep { }
        Wait-AgentConnection $status ([DateTimeOffset]::UtcNow.AddSeconds(-5)) -TimeoutSeconds 0 | Should -BeFalse
    }
    It 'akzeptiert eine frische verbundene Statusdatei' {
        $status = Join-Path $TestDrive 'status.json'
        $now = [DateTimeOffset]::UtcNow
        @{ connected = $true; started_at = $now.ToString('o'); updated_at = $now.ToString('o') } |
            ConvertTo-Json | Set-Content $status
        Wait-AgentConnection $status $now.AddSeconds(-1) -TimeoutSeconds 0 | Should -BeTrue
    }
}

Describe 'Windows-Schnittstellen des Installers' {
    BeforeEach {
        Mock Get-CimInstance { [pscustomobject]@{ Name = 'EinsatzcockpitPrintAgent' } }
        Mock Invoke-CimMethod { [pscustomobject]@{ ReturnValue = 0 } }
    }
    It 'setzt das Passwort per CIM und den Programmpfad mit Anfuehrungszeichen' {
        $password = ConvertTo-SecureString 'test-password' -AsPlainText -Force
        Set-AgentServiceAccount 'EinsatzcockpitPrintAgent' 'DOMAIN\svc' $password
        Should -Invoke Invoke-CimMethod -Times 1 -ParameterFilter {
            $MethodName -eq 'Change' -and $Arguments.StartPassword -eq 'test-password' -and $Arguments.StartName -eq 'DOMAIN\svc'
        }
        Set-AgentServiceBinary 'EinsatzcockpitPrintAgent' 'C:\Program Files\Einsatzcockpit\PrintAgent.exe'
        Should -Invoke Invoke-CimMethod -Times 1 -ParameterFilter {
            $Arguments.PathName -ceq '"C:\Program Files\Einsatzcockpit\PrintAgent.exe"'
        }
    }
    It 'verweigert eine fehlgeschlagene Windows-Kontokonfiguration' {
        Mock Invoke-CimMethod { [pscustomobject]@{ ReturnValue = 22 } }
        { Set-AgentServiceAccount 'EinsatzcockpitPrintAgent' 'DOMAIN\svc' $null } | Should -Throw '*Windows-Code 22*'
    }
    It 'uebergibt den Code per stdin und nicht als Argument' {
        $fake = Join-Path $TestDrive 'agent.ps1'
        $capture = Join-Path $TestDrive 'capture.json'
        @'
$argsForCapture = $args
$stdinForCapture = @($input)
@{ arguments = $argsForCapture; stdin = $stdinForCapture } | ConvertTo-Json | Set-Content $env:ECPA_TEST_CAPTURE
$global:LASTEXITCODE = 0
'@ | Set-Content $fake
        $env:ECPA_TEST_CAPTURE = $capture
        try {
            Invoke-AgentPair $fake 'https://cloud.example' 'ABCD2345'
            $result = Get-Content $capture -Raw | ConvertFrom-Json
            $result.arguments | Should -Contain '--code-stdin'
            $result.arguments | Should -Not -Contain 'ABCD2345'
            $result.stdin | Should -Contain 'ABCD2345'
        } finally { $env:ECPA_TEST_CAPTURE = $null }
    }
}
