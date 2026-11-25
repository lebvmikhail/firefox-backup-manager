@echo off
setlocal
chcp 65001 >nul

set "CSC_PATH=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC_PATH%" set "CSC_PATH=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"

if not exist "%~dp0compiler_code.cs" goto NO_CODE_BLOCK
if not exist "%CSC_PATH%" goto NO_COMPILER_BLOCK

set "MANIFEST_PATH=%TEMP%\FirefoxBackupManager-%RANDOM%-%RANDOM%.manifest"
set "OUTPUT_PATH=%~dp0Firefox_Backup_Manager.exe"

(
echo ^<?xml version="1.0" encoding="utf-8"?^>
echo ^<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1"^>
echo   ^<trustInfo xmlns="urn:schemas-microsoft-com:asm.v2"^>
echo     ^<security^>
echo       ^<requestedPrivileges xmlns="urn:schemas-microsoft-com:asm.v3"^>
echo         ^<requestedExecutionLevel level="requireAdministrator" uiAccess="false" /^>
echo       ^</requestedPrivileges^>
echo     ^</security^>
echo   ^</trustInfo^>
echo ^</assembly^>
) > "%MANIFEST_PATH%"

echo [ПРОЦЕСС] Сборка Firefox Backup Manager...
"%CSC_PATH%" /nologo /target:winexe /win32manifest:"%MANIFEST_PATH%" /out:"%OUTPUT_PATH%" "%~dp0compiler_code.cs" /reference:System.Windows.Forms.dll,System.dll,System.Drawing.dll,System.Core.dll,System.Security.dll
set "BUILD_EXIT_CODE=%ERRORLEVEL%"

del /q "%MANIFEST_PATH%" >nul 2>&1

if not "%BUILD_EXIT_CODE%"=="0" goto ERROR_BLOCK
if not exist "%OUTPUT_PATH%" goto ERROR_BLOCK

echo [УСПЕХ] Файл Firefox_Backup_Manager.exe успешно создан.
echo.
echo ============================================================
echo [ИНФО] SHA-256 исходного кода:
certutil -hashfile "%~dp0compiler_code.cs" SHA256
echo ------------------------------------------------------------
echo [ИНФО] SHA-256 готового приложения:
certutil -hashfile "%OUTPUT_PATH%" SHA256
echo ============================================================
echo.
goto END_SUCCESS

:NO_CODE_BLOCK
echo [ОШИБКА] Файл compiler_code.cs не найден.
set "BUILD_EXIT_CODE=1"
goto END_ERROR

:NO_COMPILER_BLOCK
echo [ОШИБКА] Не найден компилятор C# .NET Framework: "%CSC_PATH%".
set "BUILD_EXIT_CODE=1"
goto END_ERROR

:ERROR_BLOCK
echo [ОШИБКА] Сборка не выполнена.
if "%BUILD_EXIT_CODE%"=="0" set "BUILD_EXIT_CODE=1"
goto END_ERROR

:END_SUCCESS
if /I "%CI%"=="true" exit /b 0
pause
exit /b 0

:END_ERROR
if /I "%CI%"=="true" exit /b %BUILD_EXIT_CODE%
pause
exit /b %BUILD_EXIT_CODE%
