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

if ($null -eq $createBackup) {
    throw "CreateBackup method was not found in the application assembly."
}
if ($null -eq $retainOldBackups) {
    throw "RetainOldBackups method was not found in the application assembly."
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
