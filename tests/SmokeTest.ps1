param(
    [Parameter(Mandatory = $true)]
    [string]$ExePath
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $ExePath -PathType Leaf)) {
    throw "Application executable not found: $ExePath"
}

$assembly = [System.Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $ExePath).Path)
$programType = $assembly.GetType("Program", $true)
$flags = [System.Reflection.BindingFlags]::Static -bor [System.Reflection.BindingFlags]::NonPublic
$createBackup = $programType.GetMethod("CreateBackup", $flags)
$retainOldBackups = $programType.GetMethod("RetainOldBackups", $flags)
$isProtectedLocation = $programType.GetMethod("IsProtectedProgramFilesLocation", $flags)
$registerScheduledTask = $programType.GetMethod("RegisterScheduledTask", $flags)
$ensurePrivateConfig = $programType.GetMethod("EnsurePrivateConfigDirectory", $flags)
$tryReadBackupConfig = $programType.GetMethod("TryReadBackupConfig", $flags)

if ($null -eq $createBackup) {
    throw "CreateBackup method was not found in the application assembly."
}
if ($null -eq $retainOldBackups) {
    throw "RetainOldBackups method was not found in the application assembly."
}
if ($null -eq $isProtectedLocation) {
    throw "IsProtectedProgramFilesLocation method was not found in the application assembly."
}
if ($null -eq $registerScheduledTask) {
    throw "RegisterScheduledTask method was not found in the application assembly."
}
if ($null -eq $ensurePrivateConfig) {
    throw "EnsurePrivateConfigDirectory method was not found in the application assembly."
}
if ($null -eq $tryReadBackupConfig) {
    throw "TryReadBackupConfig method was not found in the application assembly."
}

$testRoot = Join-Path $env:TEMP ("Firefox Backup Manager Smoke Tests " + [Guid]::NewGuid().ToString("N"))
$profilePath = Join-Path $testRoot "Test Firefox Profile"
$backupPath = Join-Path $testRoot "Backup Output"
$logPath = Join-Path $backupPath "backup_log.txt"
$password = "Smoke password with spaces; quotes " + '"' + " and backslash \ and unicode Ж"

function Invoke-BackupEngine {
    param(
        [string]$ProfileName,
        [string]$Profile,
        [string]$Destination,
        [string]$Secret,
        [string]$Log
    )

    $arguments = [object[]]@($ProfileName, $Profile, $Destination, $Secret, $Log, "", "")
    $success = [bool]$createBackup.Invoke($null, $arguments)

    return [pscustomobject]@{
        Success = $success
        Archive = [string]$arguments[5]
        Error = [string]$arguments[6]
    }
}

try {
    New-Item -ItemType Directory -Path $profilePath -Force | Out-Null
    New-Item -ItemType Directory -Path $backupPath -Force | Out-Null

    # Config parsing must reject duplicate keys, relative paths and malformed DPAPI payloads.
    $configValidationPath = Join-Path $testRoot "config-validation.ini"
    $validConfigLines = @(
        "ProfileName=SmokeProfile",
        "ProfilePath=$profilePath",
        "BackupDir=$backupPath",
        "BackupPassword=U3ludGhldGljRG9QYVBJQmxvYg=="
    )
    Set-Content -LiteralPath $configValidationPath -Value $validConfigLines -Encoding UTF8

    $validConfigArguments = New-Object object[] 6
    $validConfigArguments[0] = [string]$configValidationPath
    $validConfigResult = [bool]$tryReadBackupConfig.Invoke([object]$null, $validConfigArguments)
    if (-not $validConfigResult) {
        throw "A valid configuration was rejected: $($validConfigArguments[5])"
    }
    if ([string]$validConfigArguments[1] -ne "SmokeProfile" -or
        [string]$validConfigArguments[2] -ne [System.IO.Path]::GetFullPath($profilePath) -or
        [string]$validConfigArguments[3] -ne [System.IO.Path]::GetFullPath($backupPath)) {
        throw "The config parser returned unexpected values for a valid configuration."
    }

    $duplicateConfigLines = @(
        "ProfileName=SmokeProfile",
        "ProfileName=UnexpectedSecondValue",
        "ProfilePath=$profilePath",
        "BackupDir=$backupPath",
        "BackupPassword=U3ludGhldGljRG9QYVBJQmxvYg=="
    )
    Set-Content -LiteralPath $configValidationPath -Value $duplicateConfigLines -Encoding UTF8
    $invalidConfigArguments = New-Object object[] 6
    $invalidConfigArguments[0] = [string]$configValidationPath
    if ([bool]$tryReadBackupConfig.Invoke([object]$null, $invalidConfigArguments)) {
        throw "The config parser accepted a duplicate ProfileName key."
    }

    $relativeConfigLines = @(
        "ProfileName=SmokeProfile",
        "ProfilePath=relative-profile",
        "BackupDir=$backupPath",
        "BackupPassword=U3ludGhldGljRG9QYVBJQmxvYg=="
    )
    Set-Content -LiteralPath $configValidationPath -Value $relativeConfigLines -Encoding UTF8
    $invalidConfigArguments = New-Object object[] 6
    $invalidConfigArguments[0] = [string]$configValidationPath
    if ([bool]$tryReadBackupConfig.Invoke([object]$null, $invalidConfigArguments)) {
        throw "The config parser accepted a relative profile path."
    }

    $badPasswordConfigLines = @(
        "ProfileName=SmokeProfile",
        "ProfilePath=$profilePath",
        "BackupDir=$backupPath",
        "BackupPassword=this-is-not-base64"
    )
    Set-Content -LiteralPath $configValidationPath -Value $badPasswordConfigLines -Encoding UTF8
    $invalidConfigArguments = New-Object object[] 6
    $invalidConfigArguments[0] = [string]$configValidationPath
    if ([bool]$tryReadBackupConfig.Invoke([object]$null, $invalidConfigArguments)) {
        throw "The config parser accepted a malformed DPAPI payload."
    }

    # Representative files only: this is an archive-engine smoke test, not a
    # real Firefox-profile compatibility or restore test.
    Set-Content -LiteralPath (Join-Path $profilePath "places.sqlite") -Value "SQLite smoke-test placeholder"
    Set-Content -LiteralPath (Join-Path $profilePath "logins.json") -Value '{"logins":[]}'
    Set-Content -LiteralPath (Join-Path $profilePath "prefs.js") -Value 'user_pref("browser.startup.homepage", "about:blank");'
    New-Item -ItemType Directory -Path (Join-Path $profilePath "storage") -Force | Out-Null
    Set-Content -LiteralPath (Join-Path (Join-Path $profilePath "storage") "example.txt") -Value "Nested file"

    $plain = Invoke-BackupEngine "SmokeProfile" $profilePath $backupPath "" $logPath
    if (-not $plain.Success) {
        throw "Unencrypted archive creation failed: $($plain.Error)"
    }
    if (-not (Test-Path -LiteralPath $plain.Archive -PathType Leaf)) {
        throw "Unencrypted archive was not published."
    }

    $encrypted = Invoke-BackupEngine "SmokeProfile" $profilePath $backupPath $password $logPath
    if (-not $encrypted.Success) {
        throw "Encrypted archive creation failed: $($encrypted.Error)"
    }
    if (-not (Test-Path -LiteralPath $encrypted.Archive -PathType Leaf)) {
        throw "Encrypted archive was not published."
    }

    $archives = @(Get-ChildItem -LiteralPath $backupPath -Filter "*.7z" -File)
    if ($archives.Count -ne 2) {
        throw "Expected exactly two published archives; found $($archives.Count)."
    }

    $partial = @(Get-ChildItem -LiteralPath $backupPath -Filter "*.partial.7z" -File)
    if ($partial.Count -ne 0) {
        throw "A temporary partial archive was left behind."
    }

    if (Test-Path -LiteralPath $logPath) {
        $logText = Get-Content -LiteralPath $logPath -Raw
        if ($logText.Contains($password)) {
            throw "The archive password was unexpectedly written to the backup log."
        }
    }

    $insideProfile = Join-Path $profilePath "Backup Must Not Be Created Here"
    $invalid = Invoke-BackupEngine "SmokeProfile" $profilePath $insideProfile "" $logPath
    if ($invalid.Success) {
        throw "A backup destination inside the Firefox profile was incorrectly accepted."
    }
    if (Test-Path -LiteralPath $insideProfile) {
        throw "The invalid destination folder was created inside the profile."
    }

    # Migrate legacy config.ini into a private directory and verify its DACL.
    $configTestRoot = Join-Path $testRoot "Configuration ACL Test"
    New-Item -ItemType Directory -Path $configTestRoot | Out-Null
    $legacyConfig = Join-Path $configTestRoot "config.ini"
    Set-Content -LiteralPath $legacyConfig -Value @("ProfileName=MigrationTest", "BackupPassword=DPAPI-TEST-BLOB") -Encoding UTF8

    $configArguments = New-Object object[] 3
    $configArguments[0] = [string]$configTestRoot
    $configArguments[1] = [string]""
    $configArguments[2] = [string]""
    $configResult = [bool]$ensurePrivateConfig.Invoke([object]$null, $configArguments)
    if (-not $configResult) {
        throw "Private config ACL/migration failed: $($configArguments[2])"
    }

    $privateConfigPath = [string]$configArguments[1]
    $privateConfigDirectory = Split-Path -Parent $privateConfigPath
    if (-not (Test-Path -LiteralPath $privateConfigPath -PathType Leaf)) {
        throw "Private config file was not created during migration."
    }
    if (Test-Path -LiteralPath $legacyConfig) {
        throw "Legacy config.ini was not removed after migration."
    }
    if (-not (Select-String -LiteralPath $privateConfigPath -SimpleMatch "DPAPI-TEST-BLOB" -Quiet)) {
        throw "The migrated private config does not contain the original settings."
    }

    $allowedConfigSids = @("S-1-5-18", "S-1-5-32-544")
    foreach ($aclPath in @($privateConfigDirectory, $privateConfigPath)) {
        $acl = Get-Acl -LiteralPath $aclPath
        $rules = $acl.GetAccessRules($true, $true, [System.Security.Principal.SecurityIdentifier])
        foreach ($rule in $rules) {
            if ($rule.AccessControlType -eq [System.Security.AccessControl.AccessControlType]::Allow -and
                $rule.IdentityReference.Value -notin $allowedConfigSids) {
                throw "Private config ACL grants access to unexpected SID $($rule.IdentityReference.Value) on $aclPath."
            }
        }
    }

    # The SYSTEM task may only execute from the standard protected Program Files roots.
    # The registration method itself must fail closed before launching PowerShell.
    $registerArguments = New-Object object[] 2
    $registerArguments[0] = [string]$ExePath
    $registerArguments[1] = [string]""
    $registrationResult = [bool]$registerScheduledTask.Invoke([object]$null, $registerArguments)
    if ($registrationResult) {
        throw "RegisterScheduledTask accepted an executable outside Program Files."
    }
    if ([string]::IsNullOrWhiteSpace([string]$registerArguments[1])) {
        throw "RegisterScheduledTask rejected the writable path without explaining the reason."
    }

    $unsafePathArguments = New-Object object[] 1
    $unsafePathArguments[0] = [string]$ExePath
    $unsafePathResult = [bool]$isProtectedLocation.Invoke([object]$null, $unsafePathArguments)
    if ($unsafePathResult) {
        throw "An executable in the writable CI workspace was incorrectly accepted for a SYSTEM startup task."
    }

    $protectedPathArguments = New-Object object[] 1
    $protectedPathArguments[0] = [string](Join-Path $env:ProgramFiles "Firefox Backup Manager\Firefox_Backup_Manager.exe")
    $protectedPathResult = [bool]$isProtectedLocation.Invoke([object]$null, $protectedPathArguments)
    if (-not $protectedPathResult) {
        throw "A path under Program Files was incorrectly rejected for a SYSTEM startup task."
    }

    # Retention must remove only files matching the application's own archive
    # naming convention, never arbitrary user-created .7z files with the same prefix.
    $oldManagedArchive = Join-Path $backupPath "SmokeProfile_2000-01-01_00-00-00_deadbeef.7z"
    $oldManualArchive = Join-Path $backupPath "SmokeProfile_manual-export.7z"
    Set-Content -LiteralPath $oldManagedArchive -Value "old managed archive placeholder"
    Set-Content -LiteralPath $oldManualArchive -Value "user archive placeholder"
    (Get-Item -LiteralPath $oldManagedArchive).LastWriteTime = (Get-Date).AddDays(-31)
    (Get-Item -LiteralPath $oldManualArchive).LastWriteTime = (Get-Date).AddDays(-31)

    $retentionArguments = New-Object object[] 3
    $retentionArguments[0] = [string]"SmokeProfile"
    $retentionArguments[1] = [string]$backupPath
    $retentionArguments[2] = [string]$logPath
    $null = $retainOldBackups.Invoke([object]$null, $retentionArguments)

    if (Test-Path -LiteralPath $oldManagedArchive) {
        throw "An old application-managed archive was not removed by retention."
    }
    if (-not (Test-Path -LiteralPath $oldManualArchive)) {
        throw "Retention deleted an unrelated user-created .7z file."
    }

    Write-Host "PASS: unencrypted archive created and passed 7-Zip integrity test."
    Write-Host "PASS: encrypted archive created and passed 7-Zip integrity test."
    Write-Host "PASS: archive password does not appear in the application log."
    Write-Host "PASS: destination inside the profile is rejected without creating a folder."
    Write-Host "PASS: retention removes expired managed archives and preserves unrelated .7z files."
}
catch {
    if (Test-Path -LiteralPath $logPath) {
        Write-Host "----- Backup engine diagnostic log -----"
        Get-Content -LiteralPath $logPath
        Write-Host "----- End diagnostic log -----"
    }
    throw
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
