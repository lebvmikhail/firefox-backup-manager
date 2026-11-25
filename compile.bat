@echo off
chcp 65001 >nul
set "CSC_PATH=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC_PATH%" set "CSC_PATH=C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe"

if not exist "%~dp0compiler_code.cs" goto NO_CODE_BLOCK

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
) > "%~dp0app.MANIFEST"

echo [ПРОЦЕСС] Сборка утилиты силами Microsoft .NET...
"%CSC_PATH%" /target:winexe /win32manifest:"%~dp0app.MANIFEST" /out:"%~dp0Firefox_Backup_Manager.exe" "%~dp0compiler_code.cs" /reference:System.Windows.Forms.dll,System.dll,System.Drawing.dll,System.Core.dll,System.Security.dll

if errorlevel 1 goto ERROR_BLOCK

:SUCCESS_BLOCK
echo [УСПЕХ] Файл Firefox_Backup_Manager.exe успешно создан!
echo.
echo ============================================================
echo [ИНФО] Хэш-сумма ИСХОДНОГО КОДА (compiler_code.cs):
for /f "delims=" %%i in ('certutil -hashfile "%~dp0compiler_code.cs" SHA256 ^| more +1') do (echo %%i & goto NEXT1)
:NEXT1
echo ------------------------------------------------------------
echo [ИНФО] Хэш-сумма ГОТОВОГО ПРИЛОЖЕНИЯ (Firefox_Backup_Manager.exe):
for /f "delims=" %%i in ('certutil -hashfile "%~dp0Firefox_Backup_Manager.exe" SHA256 ^| more +1') do (echo %%i & goto NEXT2)
:NEXT2
echo ============================================================
echo.
goto END_BLOCK

:NO_CODE_BLOCK
echo [ОШИБКА] Файл с исходным кодом compiler_code.cs не найден в этой папке!
goto END_BLOCK

:ERROR_BLOCK
echo [ОШИБКА] Не удалось скомпилировать файл.
goto END_BLOCK

:END_BLOCK
del /q "%~dp0app.MANIFEST" >nul 2>&1
pause
