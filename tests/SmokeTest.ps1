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
$createBackup = $programType.GetMethod("CreateBackupCore", $flags)
$createBackupWithGuard = $programType.GetMethod("CreateBackup", $flags)
$retainOldBackups = $programType.GetMethod("RetainOldBackups", $flags)
$isProtectedLocation = $programType.GetMethod("IsProtectedProgramFilesLocation", $flags)
$registerScheduledTask = $programType.GetMethod("RegisterScheduledTask", $flags)
$ensurePrivateConfig = $programType.GetMethod("EnsurePrivateConfigDirectory", $flags)
$tryReadBackupConfig = $programType.GetMethod("TryReadBackupConfig", $flags)
$restoreBackup = $programType.GetMethod("RestoreBackup", $flags)
$verifyBackupArchive = $programType.GetMethod("VerifyBackupArchive", $flags)
$isSafeArchiveEntryPath = $programType.GetMethod("IsSafeArchiveEntryPath", $flags)
$ensureFirefoxClosed = $programType.GetMethod("EnsureFirefoxClosed", $flags)
$removeScheduledTask = $programType.GetMethod("RemoveScheduledTask", $flags)
$deleteDirectoryNoFollow = $programType.GetMethod("DeleteDirectoryNoFollow", $flags)
$ensurePathHasNoReparsePoints = $programType.GetMethod("EnsurePathHasNoReparsePoints", $flags)
$ensurePrivateConfigDirectory = $programType.GetMethod("EnsurePrivateConfigDirectory", $flags)

if ($null -eq $createBackup) {
    throw "CreateBackupCore method was not found in the application assembly."
}
if ($null -eq $createBackupWithGuard) {
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
if ($null -eq $restoreBackup) {
    throw "RestoreBackup method was not found in the application assembly."
}
if ($null -eq $verifyBackupArchive) {
    throw "VerifyBackupArchive method was not found in the application assembly."
}
if ($null -eq $isSafeArchiveEntryPath) {
    throw "IsSafeArchiveEntryPath method was not found in the application assembly."
}
if ($null -eq $ensureFirefoxClosed) {
    throw "EnsureFirefoxClosed method was not found in the application assembly."
}
if ($null -eq $deleteDirectoryNoFollow) {
    throw "DeleteDirectoryNoFollow method was not found in the application assembly."
}
if ($null -eq $ensurePathHasNoReparsePoints) {
    throw "EnsurePathHasNoReparsePoints method was not found in the application assembly."
}
if ($null -eq $removeScheduledTask) {
    throw "RemoveScheduledTask method was not found in the application assembly."
}
if ($null -eq $ensurePrivateConfigDirectory) {
    throw "EnsurePrivateConfigDirectory method was not found in the application assembly."
}

$testRoot = Join-Path $env:TEMP ("Firefox Backup Manager Smoke Ж [paths] & " + [Guid]::NewGuid().ToString("N"))
$profilePath = Join-Path $testRoot "Test Firefox Profile Ж"
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

    $arguments = [object[]]@($ProfileName, $Profile, $Destination, $Secret, $Log, [bool]$false, "", "")
    $success = [bool]$createBackup.Invoke($null, $arguments)

    return [pscustomobject]@{
        Success = $success
        Archive = [string]$arguments[6]
        Error = [string]$arguments[7]
    }
}

function Invoke-RestoreEngine {
    param(
        [string]$Archive,
        [string]$DestinationParent,
        [string]$Secret,
        [string]$Log
    )

    $arguments = [object[]]@($Archive, $DestinationParent, $Secret, $Log, "", "")
    $success = [bool]$restoreBackup.Invoke($null, $arguments)

    return [pscustomobject]@{
        Success = $success
        RestorePath = [string]$arguments[4]
        Error = [string]$arguments[5]
    }
}

function Invoke-VerifyEngine {
    param(
        [string]$Archive,
        [string]$Secret
    )

    $arguments = New-Object object[] 3
    $arguments[0] = [string]$Archive
    $arguments[1] = [string]$Secret
    $arguments[2] = [string]""
    $success = [bool]$verifyBackupArchive.Invoke([object]$null, $arguments)

    return [pscustomobject]@{
        Success = $success
        Error = [string]$arguments[2]
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

    # An intentionally empty password is valid; the key must still exist.
    $emptyPasswordLines = @(
        "ProfileName=SmokeProfile",
        "ProfilePath=$profilePath",
        "BackupDir=$backupPath",
        "BackupPassword="
    )
    Set-Content -LiteralPath $configValidationPath -Value $emptyPasswordLines -Encoding UTF8
    $emptyPasswordArguments = New-Object object[] 6
    $emptyPasswordArguments[0] = [string]$configValidationPath
    if (-not [bool]$tryReadBackupConfig.Invoke([object]$null, $emptyPasswordArguments)) {
        throw "The config parser rejected an intentionally empty archive password: $($emptyPasswordArguments[5])"
    }
    if (-not [string]::IsNullOrEmpty([string]$emptyPasswordArguments[4])) {
        throw "The config parser changed an intentionally empty archive password."
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
    Set-Content -LiteralPath (Join-Path $profilePath "Unicode Ж [1] & data.txt") -Value "Unicode path and file name."

    $plain = Invoke-BackupEngine "Smoke Profile Ж" $profilePath $backupPath "" $logPath
    if (-not $plain.Success) {
        throw "Unencrypted archive creation failed: $($plain.Error)"
    }
    if (-not (Test-Path -LiteralPath $plain.Archive -PathType Leaf)) {
        throw "Unencrypted archive was not published."
    }

    $encrypted = Invoke-BackupEngine "Smoke Profile Ж" $profilePath $backupPath $password $logPath
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

    # Reject a backup destination that is a junction into the source profile.
    $profileBackupTarget = Join-Path $profilePath "backup-target"
    New-Item -ItemType Directory -Path $profileBackupTarget -Force | Out-Null
    $backupJunction = Join-Path $testRoot "Backup Destination Junction"
    $backupJunctionCommand = 'mklink /J "' + $backupJunction + '" "' + $profileBackupTarget + '"'
    & $env:ComSpec /c $backupJunctionCommand | Out-Null
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $backupJunction)) {
        throw "Could not create the backup destination junction."
    }
    try {
        $aliasedBackup = Invoke-BackupEngine "Smoke Profile Ж" $profilePath $backupJunction "" $logPath
        if ($aliasedBackup.Success) {
            throw "Backup unexpectedly accepted a destination junction into the source profile."
        }
        if (@(Get-ChildItem -LiteralPath $profileBackupTarget -Filter "*.7z" -File).Count -ne 0) {
            throw "Backup wrote an archive through a destination junction into the source profile."
        }
    }
    finally {
        & $env:ComSpec /c ('rmdir "' + $backupJunction + '"') | Out-Null
    }

    # A directory without Firefox root markers must be rejected before creating output.
    $nonProfilePath = Join-Path $testRoot "Not A Firefox Profile"
    $nonProfileBackup = Join-Path $testRoot "Non Profile Backup Output"
    New-Item -ItemType Directory -Path $nonProfilePath -Force | Out-Null
    $nonProfile = Invoke-BackupEngine "Not Firefox" $nonProfilePath $nonProfileBackup "" $logPath
    if ($nonProfile.Success -or (Test-Path -LiteralPath $nonProfileBackup)) {
        throw "Backup unexpectedly accepted a directory without Firefox profile marker files."
    }

    # Check archives separately without extracting; wrong passwords must fail.
    $plainVerify = Invoke-VerifyEngine $plain.Archive ""
    if (-not $plainVerify.Success) {
        throw "Unencrypted archive verification failed: $($plainVerify.Error)"
    }
    $encryptedVerify = Invoke-VerifyEngine $encrypted.Archive $password
    if (-not $encryptedVerify.Success) {
        throw "Encrypted archive verification failed: $($encryptedVerify.Error)"
    }
    $wrongVerify = Invoke-VerifyEngine $encrypted.Archive "incorrect password"
    if ($wrongVerify.Success) {
        throw "Standalone archive verification unexpectedly accepted an incorrect password."
    }
    $verifyTempLogs = @(Get-ChildItem -LiteralPath $env:TEMP -Filter "ffbm-verify-*.log" -File -ErrorAction SilentlyContinue)
    if ($verifyTempLogs.Count -ne 0) {
        throw "Standalone archive verification left temporary log files behind."
    }

    # Restore both archive types into new directories; reject a wrong password and traversal paths.
    $restoreParent = Join-Path $testRoot "Restore Output"
    New-Item -ItemType Directory -Path $restoreParent -Force | Out-Null
    $plainRestore = Invoke-RestoreEngine $plain.Archive $restoreParent "" (Join-Path $restoreParent "restore_log.txt")
    if (-not $plainRestore.Success -or
        -not (Test-Path -LiteralPath (Join-Path $plainRestore.RestorePath "places.sqlite"))) {
        throw "Unencrypted archive restore failed: $($plainRestore.Error)"
    }

    $encryptedRestore = Invoke-RestoreEngine $encrypted.Archive $restoreParent $password (Join-Path $restoreParent "restore_log.txt")
    if (-not $encryptedRestore.Success -or
        -not (Test-Path -LiteralPath (Join-Path $encryptedRestore.RestorePath "places.sqlite")) -or
        -not (Test-Path -LiteralPath (Join-Path $encryptedRestore.RestorePath "storage/example.txt"))) {
        throw "Encrypted archive restore failed: $($encryptedRestore.Error)"
    }

    $wrongPasswordRestore = Invoke-RestoreEngine $encrypted.Archive $restoreParent "incorrect password" (Join-Path $restoreParent "restore_log.txt")
    if ($wrongPasswordRestore.Success) {
        throw "Restore unexpectedly succeeded with an incorrect password."
    }

    $corruptArchive = Join-Path $testRoot "Corrupted archive.7z"
    Copy-Item -LiteralPath $plain.Archive -Destination $corruptArchive
    $corruptStream = [System.IO.File]::Open($corruptArchive, [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
    try {
        if ($corruptStream.Length -le 80) {
            throw "The smoke archive is too small for a deterministic corruption test."
        }
        $corruptStream.Position = 50
        $originalByte = $corruptStream.ReadByte()
        $corruptStream.Position = 50
        $corruptStream.WriteByte([byte]($originalByte -bxor 255))
        $corruptStream.Flush()
    }
    finally {
        $corruptStream.Dispose()
    }
    $corruptRestore = Invoke-RestoreEngine $corruptArchive $restoreParent "" (Join-Path $restoreParent "restore_log.txt")
    if ($corruptRestore.Success) {
        throw "Restore unexpectedly succeeded with a corrupted archive."
    }
    $corruptVerify = Invoke-VerifyEngine $corruptArchive ""
    if ($corruptVerify.Success) {
        throw "Standalone archive verification unexpectedly accepted a corrupted archive."
    }

    $leftoverStaging = @(Get-ChildItem -LiteralPath $restoreParent -Directory -Filter ".ffbm-restore-*")
    if ($leftoverStaging.Count -ne 0) {
        throw "A failed restore left temporary staging directories behind."
    }

    $unsafeArchivePathArguments = New-Object object[] 3
    $unsafeArchivePathArguments[0] = [string]"..\outside.txt"
    $unsafeArchivePathArguments[1] = [string]$restoreParent
    $unsafeArchivePathArguments[2] = [string]""
    if ([bool]$isSafeArchiveEntryPath.Invoke([object]$null, $unsafeArchivePathArguments)) {
        throw "Archive path validation accepted a parent-directory traversal."
    }
    $absoluteArchivePathArguments = New-Object object[] 3
    $absoluteArchivePathArguments[0] = [string]"C:\outside.txt"
    $absoluteArchivePathArguments[1] = [string]$restoreParent
    $absoluteArchivePathArguments[2] = [string]""
    if ([bool]$isSafeArchiveEntryPath.Invoke([object]$null, $absoluteArchivePathArguments)) {
        throw "Archive path validation accepted an absolute Windows path."
    }
    $deviceArchivePathArguments = New-Object object[] 3
    $deviceArchivePathArguments[0] = [string]"CONIN$"
    $deviceArchivePathArguments[1] = [string]$restoreParent
    $deviceArchivePathArguments[2] = [string]""
    if ([bool]$isSafeArchiveEntryPath.Invoke([object]$null, $deviceArchivePathArguments)) {
        throw "Archive path validation accepted a reserved Windows console device name."
    }

    # Staging cleanup must remove a junction without deleting its external target.
    $cleanupRoot = Join-Path $testRoot "Staging Cleanup Junction"
    $cleanupTarget = Join-Path $testRoot "Staging Cleanup Target"
    New-Item -ItemType Directory -Path $cleanupRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $cleanupTarget -Force | Out-Null
    $cleanupTargetFile = Join-Path $cleanupTarget "outside.txt"
    Set-Content -LiteralPath $cleanupTargetFile -Value "Must survive cleanup."
    $cleanupLink = Join-Path $cleanupRoot "external-link"
    $cleanupCommand = 'mklink /J "' + $cleanupLink + '" "' + $cleanupTarget + '"'
    & $env:ComSpec /c $cleanupCommand | Out-Null
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $cleanupLink)) {
        throw "Could not create the staging cleanup test junction."
    }
    $deleteArguments = New-Object object[] 1
    $deleteArguments[0] = [string]$cleanupRoot
    $null = $deleteDirectoryNoFollow.Invoke([object]$null, $deleteArguments)
    if (Test-Path -LiteralPath $cleanupRoot) {
        throw "No-follow staging cleanup did not remove the temporary directory."
    }
    if (-not (Test-Path -LiteralPath $cleanupTargetFile)) {
        throw "No-follow staging cleanup deleted content outside the staging directory."
    }

    # Verify the production wrapper refuses a backup while Firefox is running.
    $firefoxPresentBeforeGuardTest = @(Get-Process -Name "firefox" -ErrorAction SilentlyContinue).Count -gt 0
    if (-not $firefoxPresentBeforeGuardTest) {
        $fakeFirefoxPath = Join-Path $testRoot "firefox.exe"
        Copy-Item -LiteralPath (Join-Path $env:WINDIR "System32\cmd.exe") -Destination $fakeFirefoxPath
        $fakeFirefox = Start-Process -FilePath $fakeFirefoxPath -ArgumentList @("/c", "ping -n 30 127.0.0.1 > nul") -PassThru -WindowStyle Hidden
        try {
            Start-Sleep -Milliseconds 500
            $firefoxGuardArguments = New-Object object[] 1
            $firefoxGuardArguments[0] = [string]""
            if ([bool]$ensureFirefoxClosed.Invoke([object]$null, $firefoxGuardArguments)) {
                throw "Backup was not blocked while a firefox.exe process was present."
            }
            if ([string]::IsNullOrWhiteSpace([string]$firefoxGuardArguments[0])) {
                throw "Firefox process guard returned no diagnostic message."
            }

            $guardBackupPath = Join-Path $testRoot "Guarded Backup Output"
            $guardArguments = [object[]]@("Smoke Profile Ж", $profilePath, $guardBackupPath, "", $logPath, "", "")
            if ([bool]$createBackupWithGuard.Invoke([object]$null, $guardArguments)) {
                throw "Production CreateBackup wrapper did not block while firefox.exe was running."
            }
            if (Test-Path -LiteralPath $guardBackupPath) {
                throw "Production CreateBackup wrapper created output despite a running Firefox process."
            }
        }
        finally {
            if (-not $fakeFirefox.HasExited) {
                Stop-Process -Id $fakeFirefox.Id -Force -ErrorAction SilentlyContinue
            }
        }
    }
    else {
        $firefoxGuardArguments = New-Object object[] 1
        $firefoxGuardArguments[0] = [string]""
        if ([bool]$ensureFirefoxClosed.Invoke([object]$null, $firefoxGuardArguments)) {
            throw "Firefox process guard accepted a host that already has firefox.exe running."
        }
        Write-Host "SKIP: host Firefox was already running; the process guard rejected it without terminating it."
    }

    # A profile containing a directory junction must fail closed before 7-Zip starts.
    $junctionTarget = Join-Path $testRoot "External Junction Target"
    $junctionPath = Join-Path $profilePath "linked-external-data"
    New-Item -ItemType Directory -Path $junctionTarget -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $junctionTarget "outside.txt") -Value "This file is outside the profile."
    $junctionCommand = 'mklink /J "' + $junctionPath + '" "' + $junctionTarget + '"'
    & $env:ComSpec /c $junctionCommand | Out-Null
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $junctionPath)) {
        throw "Could not create the test directory junction."
    }
    try {
        $junctionBackup = Invoke-BackupEngine "Smoke Profile Ж" $profilePath $backupPath "" $logPath
        if ($junctionBackup.Success) {
            throw "Backup unexpectedly accepted a profile containing a directory junction."
        }
        $archiveCountAfterJunction = @(Get-ChildItem -LiteralPath $backupPath -Filter "*.7z" -File).Count
        if ($archiveCountAfterJunction -ne 2) {
            throw "A failed junction check published an unexpected archive."
        }
    }
    finally {
        & $env:ComSpec /c ('rmdir "' + $junctionPath + '"') | Out-Null
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
        if (-not $acl.AreAccessRulesProtected) {
            throw "Private config ACL still inherits permissions from its parent on $aclPath."
        }
        $rules = $acl.GetAccessRules($true, $true, [System.Security.Principal.SecurityIdentifier])
        $observedAllowedSids = @()
        foreach ($rule in $rules) {
            if ($rule.AccessControlType -eq [System.Security.AccessControl.AccessControlType]::Allow) {
                $sid = $rule.IdentityReference.Value
                if ($sid -notin $allowedConfigSids) {
                    throw "Private config ACL grants access to unexpected SID $sid on $aclPath."
                }
                if ($rule.IsInherited) {
                    throw "Private config ACL contains an inherited allow rule on $aclPath."
                }
                $fullControl = [System.Security.AccessControl.FileSystemRights]::FullControl
                if (($rule.FileSystemRights -band $fullControl) -ne $fullControl) {
                    throw "Private config ACL does not grant full control to $sid on $aclPath."
                }
                $observedAllowedSids += $sid
            }
        }
        foreach ($requiredSid in $allowedConfigSids) {
            if ($observedAllowedSids -notcontains $requiredSid) {
                throw "Private config ACL is missing required SID $requiredSid on $aclPath."
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

    # Verify real task registration under the protected installation root and clean it up.
    $taskName = "Firefox_Local_AutoBackup"
    $preExistingTask = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    if ($null -ne $preExistingTask) {
        throw "The CI runner already contains $taskName; refusing to replace an unrelated task."
    }
    $ciInstallDirectory = Join-Path $env:ProgramFiles "Firefox Backup Manager CI"
    $ciInstallExecutable = Join-Path $ciInstallDirectory "Firefox_Backup_Manager.exe"
    $cleanupTaskAfterTest = $true
    try {
        New-Item -ItemType Directory -Path $ciInstallDirectory -Force | Out-Null
        Copy-Item -LiteralPath $ExePath -Destination $ciInstallExecutable -Force

        $taskRegistrationArguments = New-Object object[] 2
        $taskRegistrationArguments[0] = [string]$ciInstallExecutable
        $taskRegistrationArguments[1] = [string]""
        if (-not [bool]$registerScheduledTask.Invoke([object]$null, $taskRegistrationArguments)) {
            throw "RegisterScheduledTask failed under Program Files: $($taskRegistrationArguments[1])"
        }

        $createdTask = Get-ScheduledTask -TaskName $taskName -ErrorAction Stop
        $taskUser = [string]$createdTask.Principal.UserId
        if ($taskUser -notin @("SYSTEM", "NT AUTHORITY\SYSTEM", "S-1-5-18")) {
            throw "Scheduled task principal is not SYSTEM: $taskUser"
        }
        if ($createdTask.Actions.Count -ne 1 -or
            [System.IO.Path]::GetFullPath([string]$createdTask.Actions[0].Execute) -ne
            [System.IO.Path]::GetFullPath($ciInstallExecutable) -or
            [string]$createdTask.Actions[0].Arguments -notmatch "--silent-backup") {
            throw "Scheduled task action does not match the expected executable and argument."
        }
        if ($createdTask.Settings.DisallowStartIfOnBatteries -or $createdTask.Settings.StopIfGoingOnBatteries) {
            throw "Scheduled task does not preserve the configured battery-friendly settings."
        }
        if (-not [bool]$createdTask.Triggers[0].Enabled) {
            throw "Scheduled task startup trigger is disabled."
        }

        # Write a safe synthetic config and run the scheduled action as SYSTEM.
        $systemBackupPath = Join-Path $testRoot "SYSTEM Backup Output"
        New-Item -ItemType Directory -Path $systemBackupPath -Force | Out-Null
        $privateConfigArguments = New-Object object[] 3
        $privateConfigArguments[0] = [string]$ciInstallDirectory
        $privateConfigArguments[1] = [string]""
        $privateConfigArguments[2] = [string]""
        if (-not [bool]$ensurePrivateConfigDirectory.Invoke([object]$null, $privateConfigArguments)) {
            throw "Could not prepare the SYSTEM task's private config: $($privateConfigArguments[2])"
        }
        $systemConfigPath = [string]$privateConfigArguments[1]
        $systemConfigLines = [string[]]@(
            "ProfileName=SystemTaskSmoke",
            "ProfilePath=$profilePath",
            "BackupDir=$systemBackupPath",
            "BackupPassword="
        )
        [System.IO.File]::WriteAllLines($systemConfigPath, $systemConfigLines, [System.Text.Encoding]::UTF8)

        $hostFirefoxProcesses = @(Get-Process -Name "firefox" -ErrorAction SilentlyContinue)
        if ($hostFirefoxProcesses.Count -gt 0) {
            Write-Host "SKIP: SYSTEM task execution was not triggered because the hosted runner already has firefox.exe running; production guard correctly refuses backup."
        }
        else {
        $previousTaskInfo = Get-ScheduledTaskInfo -TaskName $taskName -ErrorAction Stop
        $previousRunTime = $previousTaskInfo.LastRunTime
        & (Join-Path $env:WINDIR "System32\schtasks.exe") /Run /TN $taskName | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "schtasks /Run failed with exit code $LASTEXITCODE."
        }

        $taskDeadline = (Get-Date).AddSeconds(35)
        $taskCompleted = $false
        do {
            Start-Sleep -Milliseconds 500
            $currentTaskInfo = Get-ScheduledTaskInfo -TaskName $taskName -ErrorAction Stop
            $currentTask = Get-ScheduledTask -TaskName $taskName -ErrorAction Stop
            if ($currentTaskInfo.LastRunTime -gt $previousRunTime -and $currentTask.State -ne "Running") {
                $taskCompleted = $true
                break
            }
        } while ((Get-Date) -lt $taskDeadline)

        if (-not $taskCompleted) {
            throw "The SYSTEM backup task did not finish within 35 seconds."
        }
        if ([int]$currentTaskInfo.LastTaskResult -ne 0) {
            throw "The SYSTEM backup task failed with result $($currentTaskInfo.LastTaskResult). Check backup_log.txt."
        }
        $systemArchives = @(Get-ChildItem -LiteralPath $systemBackupPath -Filter "SystemTaskSmoke_*.7z" -File)
        if ($systemArchives.Count -ne 1) {
            throw "The SYSTEM task did not publish exactly one verified archive."
        }
        if (-not (Test-Path -LiteralPath (Join-Path $systemBackupPath "backup_log.txt"))) {
            throw "The SYSTEM task did not create its backup log."
        }
        }

        $removeArguments = New-Object object[] 1
        $removeArguments[0] = [string]""
        if (-not [bool]$removeScheduledTask.Invoke([object]$null, $removeArguments)) {
            throw "RemoveScheduledTask failed after successful registration: $($removeArguments[0])"
        }
        $remainingTask = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
        if ($null -ne $remainingTask) {
            throw "Scheduled task remained registered after removal."
        }
        $cleanupTaskAfterTest = $false
    }
    finally {
        if ($cleanupTaskAfterTest) {
            $removeArguments = New-Object object[] 1
            $removeArguments[0] = [string]""
            $null = $removeScheduledTask.Invoke([object]$null, $removeArguments)
        }
        if (Test-Path -LiteralPath $ciInstallDirectory) {
            Remove-Item -LiteralPath $ciInstallDirectory -Recurse -Force
        }
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

    Write-Host "PASS: unencrypted archive created, verified independently, and restored to a separate directory."
    Write-Host "PASS: encrypted archive verified independently with a Unicode password; wrong password rejected."
    Write-Host "PASS: encrypted archive with Unicode paths restored; wrong password rejected."
    Write-Host "PASS: archive path traversal, absolute paths, and reserved device names rejected."
    Write-Host "PASS: corrupted archive and wrong password fail without leaving staging folders."
    Write-Host "PASS: staging cleanup deletes a junction itself without touching its external target."
    Write-Host "PASS: production backup entry point blocks when Firefox is running without terminating it."
    Write-Host "PASS: SYSTEM startup task registers, runs the real background backup path, and removes cleanly."
    Write-Host "PASS: archive password does not appear in the application log."
    Write-Host "PASS: private config migration applies explicit SYSTEM/Administrators-only ACL."
    Write-Host "PASS: backup destination junctions into the source profile are rejected."
    Write-Host "PASS: non-profile directories are rejected before output creation."
    Write-Host "PASS: profile junctions are rejected before archiving."
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
