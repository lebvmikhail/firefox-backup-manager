/*
 * Firefox Backup Manager
 * Copyright (C) 2026 lebvmikhail
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program. If not, see <https://gnu.org>.
 */

using System;
using System.IO;
using System.Text;
using System.Diagnostics;
using System.Windows.Forms;
using System.Drawing;
using System.Collections.Generic;
using System.Security.Cryptography;

class Program {
    static string regKeyName = "Firefox_Local_AutoBackup";
    
    static bool TryGetDirectorySize(string folderPath, out long size, out string error) {
        size = 0; error = "";
        if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath)) {
            error = "Каталог профиля не существует или недоступен."; return false;
        }
        try {
            string[] files = Directory.GetFiles(folderPath, "*", SearchOption.AllDirectories);
            foreach (string file in files) size += new FileInfo(file).Length;
            return true;
        } catch (Exception ex) {
            error = "Не удалось полностью определить размер профиля: " + ex.Message; return false;
        }
    }

    static void AppendLogWithRotation(string logPath, string text) {
        try {
            if (File.Exists(logPath) && new FileInfo(logPath).Length > 5 * 1024 * 1024)
                File.WriteAllText(logPath, "[ИНФО] Лог-файл превысил 5 МБ и был автоматически очищен.\r\n", Encoding.UTF8);
            File.AppendAllText(logPath, text, Encoding.UTF8);
        } catch {}
    }

    static string EncryptPassword(string password) {
        if (string.IsNullOrEmpty(password)) return "";
        byte[] data = Encoding.UTF8.GetBytes(password);
        byte[] encrypted = ProtectedData.Protect(data, null, DataProtectionScope.LocalMachine);
        return Convert.ToBase64String(encrypted);
    }

    static bool TryDecryptPassword(string encryptedBase64, out string password, out string error) {
        password = ""; error = "";
        if (string.IsNullOrEmpty(encryptedBase64)) return true;
        try {
            byte[] encrypted = Convert.FromBase64String(encryptedBase64);
            byte[] decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.LocalMachine);
            password = Encoding.UTF8.GetString(decrypted);
            return true;
        } catch (Exception ex) {
            error = "Не удалось расшифровать сохранённый пароль DPAPI: " + ex.Message;
            return false;
        }
    }

    // The password value is not included in command-line arguments. For
    // password-protected operations, 7-Zip prompts and receives the secret
    // through redirected UTF-8 standard input in RunProcess.
    static string QuoteArgument(string value) {
        StringBuilder result = new StringBuilder();
        result.Append('"');
        int backslashes = 0;
        for (int i = 0; i < value.Length; i++) {
            char ch = value[i];
            if (ch == '\\') backslashes++;
            else if (ch == '"') {
                result.Append('\\', backslashes * 2 + 1);
                result.Append('"');
                backslashes = 0;
            } else {
                if (backslashes > 0) result.Append('\\', backslashes);
                result.Append(ch);
                backslashes = 0;
            }
        }
        if (backslashes > 0) result.Append('\\', backslashes * 2);
        result.Append('"');
        return result.ToString();
    }

    static string BuildCommandLine(params string[] arguments) {
        List<string> quoted = new List<string>();
        foreach (string argument in arguments) quoted.Add(QuoteArgument(argument));
        return string.Join(" ", quoted.ToArray());
    }

    static bool RunProcess(string fileName, string arguments, int timeoutMilliseconds,
                           string standardInput, out int exitCode, out string output, out string error) {
        exitCode = -1; output = ""; error = "";
        Process process = null;
        StringBuilder captured = new StringBuilder();
        object captureLock = new object();
        try {
            ProcessStartInfo info = new ProcessStartInfo();
            info.FileName = fileName; info.Arguments = arguments;
            info.UseShellExecute = false; info.CreateNoWindow = true;
            info.WindowStyle = ProcessWindowStyle.Hidden;
            info.RedirectStandardOutput = true; info.RedirectStandardError = true;
            info.RedirectStandardInput = standardInput != null;

            process = new Process(); process.StartInfo = info;
            DataReceivedEventHandler capture = delegate(object sender, DataReceivedEventArgs e) {
                if (e.Data != null) lock (captureLock) captured.AppendLine(e.Data);
            };
            process.OutputDataReceived += capture; process.ErrorDataReceived += capture;
            if (!process.Start()) { error = "Не удалось запустить процесс " + fileName + "."; return false; }
            process.BeginOutputReadLine(); process.BeginErrorReadLine();

            if (standardInput != null) {
                byte[] inputBytes = Encoding.UTF8.GetBytes(standardInput + "\r\n");
                process.StandardInput.BaseStream.Write(inputBytes, 0, inputBytes.Length);
                process.StandardInput.BaseStream.Flush();
                process.StandardInput.Close();
            }

            if (!process.WaitForExit(timeoutMilliseconds)) {
                try { process.Kill(); } catch {}
                try { process.WaitForExit(); } catch {}
                error = "Превышено время ожидания процесса " + Path.GetFileName(fileName) + ".";
                lock (captureLock) output = captured.ToString();
                return false;
            }
            process.WaitForExit();
            exitCode = process.ExitCode;
            lock (captureLock) output = captured.ToString();
            return true;
        } catch (Exception ex) {
            error = "Ошибка запуска процесса " + Path.GetFileName(fileName) + ": " + ex.Message;
            lock (captureLock) output = captured.ToString();
            return false;
        } finally {
            if (process != null) process.Dispose();
        }
    }

    static string Find7Zip() {
        string[] candidates = new string[] {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe"),
            @"C:\Program Files\7-Zip\7z.exe", @"C:\Program Files (x86)\7-Zip\7z.exe"
        };
        foreach (string candidate in candidates)
            if (!string.IsNullOrEmpty(candidate) && File.Exists(candidate)) return candidate;
        return "";
    }

    static bool EnsureFirefoxClosed(out string error) {
        error = "";
        try {
            Process[] processes = Process.GetProcessesByName("firefox");
            int count = processes.Length;
            foreach (Process process in processes) process.Dispose();
            if (count > 0) {
                error = "Обнаружен запущенный процесс Firefox. Закройте Firefox на этом компьютере и повторите резервное копирование. Программа не будет принудительно завершать браузер.";
                return false;
            }
            return true;
        } catch (Exception ex) {
            error = "Не удалось проверить, закрыт ли Firefox: " + ex.Message;
            return false;
        }
    }

    static bool IsPathSameOrInside(string parentPath, string candidatePath) {
        string parent = Path.GetFullPath(parentPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string candidate = Path.GetFullPath(candidatePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(parent, candidate, StringComparison.OrdinalIgnoreCase)) return true;
        return candidate.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    static string MakeSafeFileName(string value) {
        StringBuilder safe = new StringBuilder();
        char[] invalid = Path.GetInvalidFileNameChars();
        foreach (char ch in value) {
            bool bad = false;
            foreach (char invalidChar in invalid) if (ch == invalidChar) { bad = true; break; }
            safe.Append(bad ? '_' : ch);
        }
        string result = safe.ToString().Trim().Trim('.');
        return string.IsNullOrEmpty(result) ? "FirefoxProfile" : result;
    }

    static bool Run7Zip(string zip7, string[] arguments, string passwordInput,
                        string logFile, out int exitCode, out string error) {
        string output;
        bool launched = RunProcess(zip7, BuildCommandLine(arguments), 2 * 60 * 60 * 1000,
                                   passwordInput, out exitCode, out output, out error);
        if (!string.IsNullOrEmpty(output)) AppendLogWithRotation(logFile, "\r\n[7-ZIP OUTPUT]:\r\n" + output);
        if (!launched) return false;
        if (exitCode != 0) {
            error = "7-Zip завершился с кодом " + exitCode + "." +
                    (string.IsNullOrEmpty(output) ? "" : " Вывод: " + output.Trim());
            return false;
        }
        return true;
    }

    static bool CreateBackup(string profileName, string profilePath, string backupDir,
                             string password, string logFile, out string archivePath, out string error) {
        archivePath = ""; error = "";
        string temporaryArchive = ""; string excludeFile = "";
        try {
            if (string.IsNullOrEmpty(profileName)) { error = "Не задано имя профиля Firefox."; return false; }
            if (string.IsNullOrEmpty(profilePath) || !Directory.Exists(profilePath)) {
                error = "Каталог выбранного профиля Firefox не существует или недоступен."; return false;
            }
            if (string.IsNullOrEmpty(backupDir)) { error = "Не задан каталог для резервных копий."; return false; }

            string fullProfilePath = Path.GetFullPath(profilePath);
            string fullBackupDir = Path.GetFullPath(backupDir);
            if (IsPathSameOrInside(fullProfilePath, fullBackupDir)) {
                error = "Каталог резервных копий нельзя размещать внутри резервируемого профиля Firefox."; return false;
            }
            Directory.CreateDirectory(fullBackupDir);

            long requiredSpace; string sizeError;
            if (!TryGetDirectorySize(fullProfilePath, out requiredSpace, out sizeError)) { error = sizeError; return false; }
            string targetRoot = Path.GetPathRoot(fullBackupDir);
            if (string.IsNullOrEmpty(targetRoot)) { error = "Не удалось определить целевой диск."; return false; }
            DriveInfo drive = new DriveInfo(targetRoot);
            if (!drive.IsReady) { error = "Целевой диск не готов или недоступен: " + targetRoot; return false; }
            if (drive.AvailableFreeSpace < requiredSpace) {
                error = "Недостаточно свободного места. Размер профиля около " +
                        (requiredSpace / 1024 / 1024) + " МБ, доступно " +
                        (drive.AvailableFreeSpace / 1024 / 1024) + " МБ.";
                return false;
            }

            // Persistent exclusive lock prevents overlapping runs targeting this folder.
            string lockPath = Path.Combine(fullBackupDir, ".firefox-backup-manager.lock");
            using (FileStream backupLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) {
                if (!EnsureFirefoxClosed(out error)) return false;
                string zip7 = Find7Zip();
                if (string.IsNullOrEmpty(zip7)) { error = "7-Zip не найден. Установите 7-Zip и повторите операцию."; return false; }

                string safeProfileName = MakeSafeFileName(profileName);
                string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                string unique = Guid.NewGuid().ToString("N").Substring(0, 8);
                string finalName = safeProfileName + "_" + stamp + "_" + unique + ".7z";
                string finalPath = Path.Combine(fullBackupDir, finalName);
                temporaryArchive = Path.Combine(fullBackupDir, safeProfileName + "_" + stamp + "_" + unique + ".partial.7z");
                excludeFile = Path.Combine(Path.GetTempPath(), "ffbm-excludes-" + Guid.NewGuid().ToString("N") + ".txt");
                File.WriteAllLines(excludeFile, new string[] {
                    "*cache2*", "*startupCache*", "*jumpListCache*", "*thumbnails*", "*shader-cache*"
                }, Encoding.UTF8);

                List<string> addArguments = new List<string>();
                addArguments.Add("a"); addArguments.Add("-t7z"); addArguments.Add("-mx5");
                addArguments.Add("-x@" + excludeFile);
                if (!string.IsNullOrEmpty(password)) {
                    addArguments.Add("-p");
                    addArguments.Add("-mhe=on");
                    addArguments.Add("-sccUTF-8");
                }
                addArguments.Add(temporaryArchive);
                addArguments.Add(Path.Combine(fullProfilePath, "*"));

                int addExitCode;
                if (!Run7Zip(zip7, addArguments.ToArray(),
                             string.IsNullOrEmpty(password) ? null : password,
                             logFile, out addExitCode, out error)) {
                    error = "Создание архива не удалось. " + error; return false;
                }
                if (!File.Exists(temporaryArchive) || new FileInfo(temporaryArchive).Length <= 0) {
                    error = "7-Zip завершился без ошибки, но временный архив отсутствует или пуст."; return false;
                }

                List<string> testArguments = new List<string>();
                testArguments.Add("t");
                // Do not pass "-p" without a value: 7-Zip interprets that as
                // an empty password instead of prompting for the archive password.
                // With no -p switch, 7-Zip prompts and reads the secret from stdin.
                if (!string.IsNullOrEmpty(password)) testArguments.Add("-sccUTF-8");
                testArguments.Add(temporaryArchive);
                int testExitCode;
                if (!Run7Zip(zip7, testArguments.ToArray(),
                             string.IsNullOrEmpty(password) ? null : password,
                             logFile, out testExitCode, out error)) {
                    error = "Проверка целостности нового архива не пройдена. " + error; return false;
                }

                if (!EnsureFirefoxClosed(out error)) {
                    error = "Firefox был запущен во время резервного копирования. " + error; return false;
                }
                if (File.Exists(finalPath)) { error = "Итоговый файл уже существует; публикация отменена."; return false; }
                File.Move(temporaryArchive, finalPath);
                temporaryArchive = "";
                archivePath = finalPath;
                AppendLogWithRotation(logFile, string.Format(
                    "[{0}] [УСПЕХ] Архив создан и проверен: {1}. Шифрование: {2}.\r\n",
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), finalName,
                    string.IsNullOrEmpty(password) ? "не включено" : "7z AES-256, заголовки зашифрованы"));
                return true;
            }
        } catch (Exception ex) {
            error = "Ошибка резервного копирования: " + ex.Message; return false;
        } finally {
            try { if (!string.IsNullOrEmpty(temporaryArchive) && File.Exists(temporaryArchive)) File.Delete(temporaryArchive); } catch {}
            try { if (!string.IsNullOrEmpty(excludeFile) && File.Exists(excludeFile)) File.Delete(excludeFile); } catch {}
        }
    }

    static void RetainOldBackups(string profileName, string backupDir, string logFile) {
        try {
            string prefix = MakeSafeFileName(profileName) + "_";
            DateTime cutoff = DateTime.Now.AddDays(-30);
            DirectoryInfo directory = new DirectoryInfo(backupDir);
            foreach (FileInfo file in directory.GetFiles("*.7z")) {
                if (!file.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (file.LastWriteTime < cutoff) {
                    try {
                        file.Delete();
                        AppendLogWithRotation(logFile, string.Format("[{0}] [РОТАЦИЯ] Удалена старая копия: {1}\r\n",
                            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), file.Name));
                    } catch (Exception ex) {
                        AppendLogWithRotation(logFile, "[ПРЕДУПРЕЖДЕНИЕ] Не удалось удалить старую копию " +
                                              file.Name + ": " + ex.Message + "\r\n");
                    }
                }
            }
        } catch (Exception ex) {
            AppendLogWithRotation(logFile, "[ПРЕДУПРЕЖДЕНИЕ] Ошибка ротации; существующие копии сохранены: " +
                                  ex.Message + "\r\n");
        }
    }

    static bool IsScheduledTaskRegistered() {
        int exitCode; string output; string error;
        bool launched = RunProcess("schtasks.exe", BuildCommandLine("/Query", "/TN", regKeyName),
                                   30000, null, out exitCode, out output, out error);
        return launched && exitCode == 0;
    }

    static bool RegisterScheduledTask(string exePath, out string error) {
        int exitCode;
        string output;
        string taskName = regKeyName.Replace("'", "''");
        string executable = exePath.Replace("'", "''");

        // Preserve the original battery-friendly settings while checking the
        // PowerShell exit code and verifying that the task is actually present.
        string command =
            "$ErrorActionPreference = 'Stop'; try { " +
            "$act = New-ScheduledTaskAction -Execute '" + executable + "' -Argument '--silent-backup'; " +
            "$trig = New-ScheduledTaskTrigger -AtStartup; " +
            "$set = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries; " +
            "Register-ScheduledTask -TaskName '" + taskName + "' -Action $act -Trigger $trig -Settings $set -User 'SYSTEM' -Force | Out-Null; " +
            "exit 0 } catch { Write-Error $_; exit 1 }";

        bool launched = RunProcess("powershell.exe",
            BuildCommandLine("-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-Command", command),
            30000, null, out exitCode, out output, out error);

        if (!launched || exitCode != 0) {
            if (string.IsNullOrEmpty(error)) error = "PowerShell не смог зарегистрировать задачу. " + output;
            return false;
        }
        if (!IsScheduledTaskRegistered()) {
            error = "Команда регистрации завершилась успешно, но задача не найдена при контрольной проверке.";
            return false;
        }
        return true;
    }

    static bool WriteConfigAtomic(string configFile, string[] lines, out string error) {
        error = "";
        string temporary = configFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            File.WriteAllLines(temporary, lines, Encoding.UTF8);
            if (File.Exists(configFile)) File.Replace(temporary, configFile, null);
            else File.Move(temporary, configFile);
            return true;
        } catch (Exception ex) {
            error = "Не удалось сохранить конфигурацию: " + ex.Message;
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch {}
            return false;
        }
    }

    [STAThread]
    static int Main(string[] args) {
        Application.EnableVisualStyles();
        string currentDir = AppDomain.CurrentDomain.BaseDirectory;
        string configFile = Path.Combine(currentDir, "config.ini");
        // РЕЖИМ 1: Фоновое резервное копирование при старте Windows.
        if (args.Length > 0 && args[0] == "--silent-backup") {
            if (!File.Exists(configFile)) return 2;
            string profName = "", profPath = "", backupDir = "", encryptedPass = "";
            bool hasProfileName = false, hasProfilePath = false, hasBackupDir = false, hasPasswordKey = false;
            try {
                foreach (string line in File.ReadAllLines(configFile, Encoding.UTF8)) {
                    if (line.StartsWith("ProfileName=", StringComparison.Ordinal)) {
                        profName = line.Substring("ProfileName=".Length); hasProfileName = true;
                    } else if (line.StartsWith("ProfilePath=", StringComparison.Ordinal)) {
                        profPath = line.Substring("ProfilePath=".Length); hasProfilePath = true;
                    } else if (line.StartsWith("BackupDir=", StringComparison.Ordinal)) {
                        backupDir = line.Substring("BackupDir=".Length); hasBackupDir = true;
                    } else if (line.StartsWith("BackupPassword=", StringComparison.Ordinal)) {
                        encryptedPass = line.Substring("BackupPassword=".Length); hasPasswordKey = true;
                    }
                }
            } catch { return 3; }

            if (!hasProfileName || !hasProfilePath || !hasBackupDir || !hasPasswordKey ||
                string.IsNullOrEmpty(profName) || string.IsNullOrEmpty(profPath) ||
                string.IsNullOrEmpty(backupDir) || !Directory.Exists(profPath)) return 4;

            string logFile;
            try {
                Directory.CreateDirectory(backupDir);
                logFile = Path.Combine(backupDir, "backup_log.txt");
            } catch { return 5; }

            AppendLogWithRotation(logFile, string.Format(
                "==================================================\r\n[{0}] [АВТОЗАПУСК ПК] Начало резервного копирования.\r\n",
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));

            string realPassword, passwordError;
            if (!TryDecryptPassword(encryptedPass, out realPassword, out passwordError)) {
                AppendLogWithRotation(logFile, "[ОШИБКА] " + passwordError + " Резервирование отменено; открытый архив не создавался.\r\n");
                return 6;
            }

            string createdArchive, backupError;
            if (!CreateBackup(profName, profPath, backupDir, realPassword, logFile,
                              out createdArchive, out backupError)) {
                AppendLogWithRotation(logFile, string.Format(
                    "[{0}] [ОШИБКА] Резервное копирование не выполнено: {1}\r\n==================================================\r\n",
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), backupError));
                return 7;
            }

            RetainOldBackups(profName, backupDir, logFile);
            AppendLogWithRotation(logFile, string.Format(
                "[{0}] [УСПЕХ] Резервное копирование завершено: {1}\r\n==================================================\r\n",
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), createdArchive));
            return 0;
        }

        // РЕЖИМ 2: Интерактивное графическое меню настроек
        Form menuForm = new Form() { Text = "Менеджер бэкапа Firefox", Width = 460, Height = 260, StartPosition = FormStartPosition.CenterScreen, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false };
        Label lblStatus = new Label() { Left = 20, Top = 20, Width = 400, Height = 60 };
        
        if (File.Exists(configFile)) {
            string pN = "", bD = "";
            string[] confLines = File.ReadAllLines(configFile, Encoding.UTF8);
            foreach (string l in confLines) {
                if (l.StartsWith("ProfileName=")) pN = l.Replace("ProfileName=", "").Trim();
                if (l.StartsWith("BackupDir=")) bD = l.Replace("BackupDir=", "").Trim();
            }
            bool taskRegistered = IsScheduledTaskRegistered();
            lblStatus.Text = string.Format("Конфигурация: сохранена\nСтатус автозапуска: {0}\nПрофиль: {1}\nПапка сохранения: {2}",
                taskRegistered ? "ЗАДАЧА НАЙДЕНА" : "ЗАДАЧА НЕ НАЙДЕНА", pN, bD);
        } else {
            lblStatus.Text = "Статус автозапуска: НЕ НАСТРОЕН / ОТКЛЮЧЕН";
        }

        Button btnSetup = new Button() { Text = "Включить / Настроить авто-бэкап", Left = 20, Top = 100, Width = 400, Height = 35 };
        Button btnDelete = new Button() { Text = "Отключить авто-бэкап и удалить конфиг", Left = 20, Top = 145, Width = 400, Height = 35 };

        btnSetup.Click += (s, e) => {
            string appDataRoaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string ffIni = Path.Combine(appDataRoaming, @"Mozilla\Firefox\profiles.ini");
            if (!File.Exists(ffIni)) {
                MessageBox.Show("Файл настроек Firefox (profiles.ini) не найден!", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            List<string> foundProfiles = new List<string>();
            string[] iniLines = File.ReadAllLines(ffIni, Encoding.UTF8);
            string currentName = "", currentPath = "", isRelative = "";
            
            foreach (string line in iniLines) {
                string l = line.Trim();
                if (l.StartsWith("[Profile")) {
                    if (!string.IsNullOrEmpty(currentName) && !string.IsNullOrEmpty(currentPath)) {
                        string rootDir = (isRelative == "1") ? Path.Combine(Path.GetDirectoryName(ffIni), currentPath) : currentPath;
                        
                        if (Directory.Exists(rootDir)) {
                            if (File.Exists(Path.Combine(rootDir, "places.sqlite")) || File.Exists(Path.Combine(rootDir, "logins.json"))) {
                                foundProfiles.Add(currentName + " | " + rootDir);
                            } else {
                                bool subDirFound = false;
                                foreach (string subDir in Directory.GetDirectories(rootDir)) {
                                    if (File.Exists(Path.Combine(subDir, "places.sqlite")) || File.Exists(Path.Combine(subDir, "logins.json"))) {
                                        foundProfiles.Add(currentName + " | " + subDir);
                                        subDirFound = true;
                                    }
                                }
                                if (!subDirFound) foundProfiles.Add(currentName + " | " + rootDir);
                            }
                        } else {
                            foundProfiles.Add(currentName + " | " + rootDir);
                        }
                    }
                    currentName = ""; currentPath = ""; isRelative = "";
                }
                else if (l.Contains("=")) {
                    string[] tokens = l.Split(new char[] { '=' }, 2);
                    if (tokens.Length == 2) {
                        string tokensKey = tokens[0].Trim();
                        string tokensVal = tokens[1].Trim();
                        if (tokensKey == "Name") currentName = tokensVal;
                        if (tokensKey == "IsRelative") isRelative = tokensVal;
                        if (tokensKey == "Path") currentPath = tokensVal.Replace('/', '\\');
                    }
                }
            }
            if (!string.IsNullOrEmpty(currentName) && !string.IsNullOrEmpty(currentPath)) {
                string rootDir = (isRelative == "1") ? Path.Combine(Path.GetDirectoryName(ffIni), currentPath) : currentPath;
                if (Directory.Exists(rootDir)) {
                    if (File.Exists(Path.Combine(rootDir, "places.sqlite")) || File.Exists(Path.Combine(rootDir, "logins.json"))) {
                        foundProfiles.Add(currentName + " | " + rootDir);
                    } else {
                        bool subDirFound = false;
                        foreach (string subDir in Directory.GetDirectories(rootDir)) {
                            if (File.Exists(Path.Combine(subDir, "places.sqlite")) || File.Exists(Path.Combine(subDir, "logins.json"))) {
                                foundProfiles.Add(currentName + " | " + subDir);
                                subDirFound = true;
                            }
                        }
                        if (!subDirFound) foundProfiles.Add(currentName + " | " + rootDir);
                    }
                } else {
                    foundProfiles.Add(currentName + " | " + rootDir);
                }
            }
            if (foundProfiles.Count == 0) {
                MessageBox.Show("Активных профилей Firefox не обнаружено!", "Внимание", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            
            Form profForm = new Form() { Text = "ШАГ 1 из 3: Выберите профиль Firefox", Width = 500, Height = 280, StartPosition = FormStartPosition.CenterScreen, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false };
            ListBox listBox = new ListBox() { Left = 20, Top = 20, Width = 445, Height = 150 };
            foreach (string p in foundProfiles) { listBox.Items.Add(p); }
            if (listBox.Items.Count > 0) listBox.SelectedIndex = 0;

            Button btnNext = new Button() { Text = "Далее", Left = 365, Top = 190, Width = 100, Height = 30 };
            profForm.Controls.AddRange(new Control[] { listBox, btnNext });
            btnNext.Click += (s2, e2) => { profForm.DialogResult = DialogResult.OK; profForm.Close(); };
            
            if (profForm.ShowDialog() != DialogResult.OK) return;
			
            string[] selectedData = listBox.SelectedItem.ToString().Split(new string[] { " | " }, StringSplitOptions.None);
            string chosenName = selectedData[0].Trim();
            string chosenPath = selectedData[1].Trim();

            FolderBrowserDialog fbd = new FolderBrowserDialog() { Description = "ШАГ 2 из 3: Выберите папку для сохранения архивов" };
            if (fbd.ShowDialog() != DialogResult.OK) return;
            string chosenBackupDir = fbd.SelectedPath.Trim();
            try {
                if (IsPathSameOrInside(chosenPath, chosenBackupDir)) {
                    MessageBox.Show("Папка резервных копий не может находиться внутри профиля Firefox. Выберите отдельный каталог.",
                        "Недопустимая папка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
            } catch (Exception ex) {
                MessageBox.Show("Не удалось проверить путь для резервных копий.\n\n" + ex.Message,
                    "Ошибка пути", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            Form passForm = new Form() { Text = "ШАГ 3 из 3: Безопасность архива", Width = 400, Height = 180, StartPosition = FormStartPosition.CenterScreen, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false };
            Label lblPass = new Label() { Text = "Задайте секретный пароль для защиты бэкапа (необязательно):\nЕсли пароль не нужен - просто оставьте поле пустым.", Left = 20, Top = 15, Width = 350, Height = 35 };
            TextBox txtPass = new TextBox() { Left = 20, Top = 55, Width = 340, PasswordChar = '*' };
            Button btnPassOK = new Button() { Text = "Завершить настройку", Left = 210, Top = 100, Width = 150, Height = 30 };
            passForm.Controls.AddRange(new Control[] { lblPass, txtPass, btnPassOK });
            btnPassOK.Click += (s3, e3) => { passForm.DialogResult = DialogResult.OK; passForm.Close(); };
            
            if (passForm.ShowDialog() != DialogResult.OK) return;
            string chosenPassword = txtPass.Text;

            string securePasswordString;
            try {
                securePasswordString = EncryptPassword(chosenPassword);
            } catch (Exception ex) {
                MessageBox.Show("Не удалось защитить пароль с помощью DPAPI. Настройка отменена.\n\n" + ex.Message,
                    "Ошибка защиты пароля", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            string startLog = Path.Combine(chosenBackupDir, "backup_log.txt");
            AppendLogWithRotation(startLog, string.Format(
                "==================================================\r\n[{0}] [ПЕРВЫЙ ЗАПУСК] Создание и проверка стартовой копии...\r\n",
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));

            string firstArchive, firstBackupError;
            if (!CreateBackup(chosenName, chosenPath, chosenBackupDir, chosenPassword, startLog,
                              out firstArchive, out firstBackupError)) {
                AppendLogWithRotation(startLog, "[ОШИБКА] Первая резервная копия не создана: " + firstBackupError + "\r\n");
                MessageBox.Show("Первая резервная копия не создана. Автозапуск не настраивался.\n\n" + firstBackupError,
                    "Ошибка резервного копирования", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            string[] configLines = new string[] {
                "ProfileName=" + chosenName,
                "ProfilePath=" + chosenPath,
                "BackupDir=" + chosenBackupDir,
                "BackupPassword=" + securePasswordString
            };
            string configError;
            if (!WriteConfigAtomic(configFile, configLines, out configError)) {
                MessageBox.Show("Резервная копия создана и проверена, но конфигурацию сохранить не удалось.\n\n" +
                    firstArchive + "\n\n" + configError,
                    "Ошибка сохранения настроек", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            string exePath = Path.Combine(currentDir, "Firefox_Backup_Manager.exe");
            string taskError;
            if (!RegisterScheduledTask(exePath, out taskError)) {
                AppendLogWithRotation(startLog, "[ОШИБКА] Первая копия создана, но задача автозапуска не зарегистрирована: " +
                                      taskError + "\r\n");
                MessageBox.Show("Первая резервная копия создана и проверена, но настроить автозапуск не удалось.\n\n" +
                    "Конфигурация сохранена; проверьте права администратора и повторите настройку.\n\n" + taskError,
                    "Автозапуск не настроен", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                menuForm.Close();
                return;
            }

            RetainOldBackups(chosenName, chosenBackupDir, startLog);
            AppendLogWithRotation(startLog, "[УСПЕХ] Первая резервная копия проверена; задача автозапуска зарегистрирована.\r\n");

            MessageBox.Show("Настройка завершена!\n\n1. Профиль Firefox определен.\n2. Первая резервная копия создана и проверена.\n3. Задача Планировщика Windows зарегистрирована от имени SYSTEM.",
                "Успех", MessageBoxButtons.OK, MessageBoxIcon.Information);
            menuForm.Close();
        };

        btnDelete.Click += (s, e) => {
            int exitCode;
            string output, commandError;
            bool launched = RunProcess("schtasks.exe", BuildCommandLine("/Delete", "/TN", regKeyName, "/F"),
                                       30000, null, out exitCode, out output, out commandError);
            if (!launched || (exitCode != 0 && IsScheduledTaskRegistered())) {
                MessageBox.Show("Не удалось подтвердить удаление задачи Планировщика. Конфигурация сохранена.\n\n" +
                    (string.IsNullOrEmpty(commandError) ? output : commandError),
                    "Ошибка отключения", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            try {
                if (File.Exists(configFile)) File.Delete(configFile);
            } catch (Exception ex) {
                MessageBox.Show("Задача удалена, но файл конфигурации не удалось удалить.\n\n" + ex.Message,
                    "Частичное завершение", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            MessageBox.Show("Задача автозапуска удалена, файл настроек удален. Архивы резервных копий не затронуты.",
                "Успех", MessageBoxButtons.OK, MessageBoxIcon.Information);
            menuForm.Close();
        };

        menuForm.Controls.AddRange(new Control[] { lblStatus, btnSetup, btnDelete });
        Application.Run(menuForm);
        return 0;
    }
}
