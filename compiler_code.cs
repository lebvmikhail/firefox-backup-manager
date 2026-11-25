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
using System.Globalization;
using System.Security.Cryptography;
using System.Xml;
using System.Security.Principal;

class Program {
    static string legacyRegKeyName = "Firefox_Local_AutoBackup";
    
    static bool TryGetDirectorySize(string folderPath, out long size, out string error) {
        size = 0; error = "";
        if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath)) {
            error = "Каталог профиля не существует или недоступен."; return false;
        }
        try {
            FileAttributes rootAttributes = File.GetAttributes(folderPath);
            if ((rootAttributes & FileAttributes.ReparsePoint) != 0) {
                error = "Каталог профиля является ссылкой/junction. Для безопасного резервирования выберите реальный каталог профиля.";
                return false;
            }

            // Walk explicitly so directory junctions and symbolic links are not
            // followed or silently included in a backup that RestoreBackup rejects.
            Stack<string> pendingDirectories = new Stack<string>();
            pendingDirectories.Push(folderPath);
            while (pendingDirectories.Count > 0) {
                string currentDirectory = pendingDirectories.Pop();
                foreach (string entry in Directory.GetFileSystemEntries(currentDirectory)) {
                    FileAttributes attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) {
                        error = "В профиле обнаружена символическая ссылка или junction: " +
                                Path.GetFileName(entry) + ". Удалите ссылку или используйте обычную копию профиля.";
                        size = 0;
                        return false;
                    }

                    if ((attributes & FileAttributes.Directory) != 0) {
                        pendingDirectories.Push(entry);
                    } else {
                        size = checked(size + new FileInfo(entry).Length);
                    }
                }
            }
            return true;
        } catch (Exception ex) {
            size = 0;
            error = "Не удалось полностью определить размер профиля: " + ex.Message;
            return false;
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
        byte[] encrypted = ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encrypted);
    }

    static bool TryDecryptPassword(string encryptedBase64, out string password, out string error) {
        password = ""; error = "";
        if (string.IsNullOrEmpty(encryptedBase64)) return true;
        try {
            byte[] encrypted = Convert.FromBase64String(encryptedBase64);
            byte[] decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
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
            const int maxCapturedOutputCharacters = 16 * 1024 * 1024;
            bool captureTruncated = false;
            DataReceivedEventHandler capture = delegate(object sender, DataReceivedEventArgs e) {
                if (e.Data != null) {
                    lock (captureLock) {
                        if (captured.Length < maxCapturedOutputCharacters) {
                            int remaining = maxCapturedOutputCharacters - captured.Length;
                            if (e.Data.Length <= remaining) captured.AppendLine(e.Data);
                            else {
                                captured.Append(e.Data.Substring(0, remaining));
                                captured.AppendLine();
                                if (!captureTruncated) {
                                    captured.AppendLine("[OUTPUT TRUNCATED]");
                                    captureTruncated = true;
                                }
                            }
                        } else if (!captureTruncated) {
                            captured.AppendLine("[OUTPUT TRUNCATED]");
                            captureTruncated = true;
                        }
                    }
                }
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
            // Do not leave a child archive/utility process running after an I/O failure.
            try {
                if (process != null && !process.HasExited) {
                    process.Kill();
                    process.WaitForExit();
                }
            } catch {}
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

    static string GetUserDataDirectory() {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(localAppData))
            throw new InvalidOperationException("Не удалось определить LocalAppData текущего пользователя.");
        return Path.Combine(localAppData, "Firefox Backup Manager");
    }

    // Task Scheduler task names are global within a Windows installation.
    // Include this account's SID so multiple Windows users can configure backups independently.
    static string GetUserScheduledTaskName() {
        return "Firefox_Local_AutoBackup_" + GetCurrentUserSid().Replace("-", "");
    }

    static bool IsRunningAsSystem() {
        try {
            WindowsIdentity identity = WindowsIdentity.GetCurrent();
            return identity != null && identity.User != null &&
                   string.Equals(identity.User.Value, "S-1-5-18", StringComparison.Ordinal);
        } catch {
            return true;
        }
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


    static bool IsSafeArchiveEntryPath(string relativePath, string extractionRoot, out string error) {
        error = "";
        if (string.IsNullOrEmpty(relativePath)) {
            error = "В архиве обнаружен пустой путь.";
            return false;
        }

        string normalized = relativePath.Replace('/', '\\');
        if (Path.IsPathRooted(normalized) || normalized.StartsWith("\\", StringComparison.Ordinal) ||
            normalized.IndexOf(':') >= 0) {
            error = "В архиве обнаружен абсолютный путь или альтернативный поток данных.";
            return false;
        }

        char[] invalidCharacters = Path.GetInvalidFileNameChars();
        string[] segments = normalized.Split(new char[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) {
            error = "В архиве обнаружен недопустимый путь.";
            return false;
        }

        foreach (string segment in segments) {
            if (segment == "." || segment == ".." || segment.EndsWith(".", StringComparison.Ordinal) ||
                segment.EndsWith(" ", StringComparison.Ordinal)) {
                error = "В архиве обнаружен небезопасный компонент пути.";
                return false;
            }
            foreach (char invalidCharacter in invalidCharacters) {
                if (segment.IndexOf(invalidCharacter) >= 0) {
                    error = "В архиве обнаружено имя файла, недопустимое в Windows.";
                    return false;
                }
            }

            string deviceName = segment.Split('.')[0].ToUpperInvariant();
            if (deviceName == "CON" || deviceName == "PRN" || deviceName == "AUX" || deviceName == "NUL" ||
                deviceName == "CONIN$" || deviceName == "CONOUT$" ||
                (deviceName.Length == 4 && (deviceName.StartsWith("COM", StringComparison.Ordinal) ||
                                            deviceName.StartsWith("LPT", StringComparison.Ordinal)) &&
                 deviceName[3] >= '1' && deviceName[3] <= '9')) {
                error = "В архиве обнаружено зарезервированное имя устройства Windows.";
                return false;
            }
        }

        try {
            string fullRoot = Path.GetFullPath(extractionRoot);
            string fullEntry = Path.GetFullPath(Path.Combine(fullRoot, normalized));
            if (string.Equals(fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                              fullEntry.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                              StringComparison.OrdinalIgnoreCase) ||
                !IsPathSameOrInside(fullRoot, fullEntry)) {
                error = "Путь из архива выходит за пределы каталога восстановления.";
                return false;
            }
            return true;
        } catch (Exception ex) {
            error = "Не удалось проверить путь в архиве: " + ex.Message;
            return false;
        }
    }

    static bool ValidateArchiveEntries(string zip7, string archivePath, string password,
                                       string extractionRoot, bool checkFreeSpace, out string error) {
        error = "";
        int exitCode;
        string output;
        bool launched = RunProcess(zip7,
            BuildCommandLine("l", "-slt", "-sccUTF-8", archivePath),
            2 * 60 * 60 * 1000, password ?? "", out exitCode, out output, out error);
        if (!launched) return false;
        if (exitCode != 0) {
            error = "Не удалось прочитать список содержимого архива (код 7-Zip: " + exitCode + ").";
            return false;
        }
        if (output.IndexOf("[OUTPUT TRUNCATED]", StringComparison.Ordinal) >= 0) {
            error = "Список архива слишком велик для безопасной проверки путей; восстановление отменено.";
            return false;
        }

        bool archivePathSkipped = false;
        bool isSevenZipArchive = false;
        bool haveCurrentEntry = false;
        bool currentIsFolder = false;
        bool currentSizeSeen = false;
        int entryCount = 0;
        bool hasProfileMarker = false;
        long totalUncompressedSize = 0;
        HashSet<string> entryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string[] lines = output.Split(new string[] { "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);

        foreach (string line in lines) {
            if (line.StartsWith("Type = ", StringComparison.Ordinal) &&
                string.Equals(line.Substring("Type = ".Length).Trim(), "7z", StringComparison.OrdinalIgnoreCase))
                isSevenZipArchive = true;

            if (line.StartsWith("Path = ", StringComparison.Ordinal)) {
                if (!archivePathSkipped) {
                    // The first Path field describes the archive itself.
                    archivePathSkipped = true;
                    continue;
                }

                if (haveCurrentEntry && !currentIsFolder && !currentSizeSeen) {
                    error = "В списке архива отсутствует размер одного из файлов.";
                    return false;
                }

                string entryPath = line.Substring("Path = ".Length);
                string normalizedEntryName = entryPath.Replace('/', (char)92);
                if (string.Equals(normalizedEntryName, "places.sqlite", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(normalizedEntryName, "logins.json", StringComparison.OrdinalIgnoreCase))
                    hasProfileMarker = true;

                string pathError;
                if (!IsSafeArchiveEntryPath(entryPath, extractionRoot, out pathError)) {
                    error = "Небезопасное содержимое архива: " + pathError;
                    return false;
                }

                string normalizedPath = Path.GetFullPath(Path.Combine(
                    extractionRoot, entryPath.Replace('/', (char)92)));
                if (!entryPaths.Add(normalizedPath)) {
                    error = "В архиве обнаружены повторяющиеся имена файлов или каталогов.";
                    return false;
                }

                entryCount++;
                if (entryCount > 100000) {
                    error = "В архиве слишком много объектов (более 100 000); восстановление отменено.";
                    return false;
                }

                haveCurrentEntry = true;
                currentIsFolder = false;
                currentSizeSeen = false;
                continue;
            }

            if (line.StartsWith("Folder = ", StringComparison.Ordinal)) {
                currentIsFolder = string.Equals(
                    line.Substring("Folder = ".Length).Trim(), "+", StringComparison.Ordinal);
            }

            if (line.StartsWith("Size = ", StringComparison.Ordinal)) {
                string rawSize = line.Substring("Size = ".Length).Trim();
                long entrySize;
                if (!haveCurrentEntry ||
                    !long.TryParse(rawSize, NumberStyles.None, CultureInfo.InvariantCulture, out entrySize) ||
                    entrySize < 0) {
                    error = "В архиве обнаружен некорректный размер файла.";
                    return false;
                }
                if (entrySize > long.MaxValue - totalUncompressedSize) {
                    error = "Суммарный размер файлов в архиве превышает поддерживаемый диапазон.";
                    return false;
                }
                totalUncompressedSize += entrySize;
                currentSizeSeen = true;
            }

            if (line.StartsWith("Symbolic Link = ", StringComparison.Ordinal) ||
                line.StartsWith("Hard Link = ", StringComparison.Ordinal)) {
                error = "Архив содержит ссылку файловой системы; автоматическое восстановление таких архивов запрещено.";
                return false;
            }
        }

        if (haveCurrentEntry && !currentIsFolder && !currentSizeSeen) {
            error = "В списке архива отсутствует размер одного из файлов.";
            return false;
        }
        if (!archivePathSkipped || !isSevenZipArchive || entryCount == 0) {
            error = "Архив пуст, повреждён или не является архивом формата 7z.";
            return false;
        }
        if (!hasProfileMarker) {
            error = "В архиве не найден корневой файл places.sqlite или logins.json; он не похож на резервную копию профиля Firefox.";
            return false;
        }

        if (checkFreeSpace) {
        try {
            string targetRoot = Path.GetPathRoot(extractionRoot);
            if (string.IsNullOrEmpty(targetRoot)) {
                error = "Не удалось определить целевой диск для восстановления.";
                return false;
            }
            DriveInfo targetDrive = new DriveInfo(targetRoot);
            if (!targetDrive.IsReady || totalUncompressedSize > targetDrive.AvailableFreeSpace) {
                error = "Для восстановления недостаточно свободного места на целевом диске.";
                return false;
            }
        } catch (Exception ex) {
            error = "Не удалось проверить свободное место для восстановления: " + ex.Message;
            return false;
        }
        }

        return true;
    }


    static bool VerifyBackupArchive(string archivePath, string password, out string error) {
        error = "";
        string temporaryLog = "";
        try {
            if (string.IsNullOrEmpty(archivePath) || !File.Exists(archivePath)) {
                error = "Выбранный архив не существует или недоступен.";
                return false;
            }
            if (!string.Equals(Path.GetExtension(archivePath), ".7z", StringComparison.OrdinalIgnoreCase)) {
                error = "Выберите архив с расширением .7z.";
                return false;
            }

            string zip7 = Find7Zip();
            if (string.IsNullOrEmpty(zip7)) {
                error = "7-Zip не найден. Установите 7-Zip и повторите операцию.";
                return false;
            }

            string fullArchivePath = Path.GetFullPath(archivePath);
            if (!ValidateArchiveEntries(zip7, fullArchivePath, password, Path.GetTempPath(), false, out error))
                return false;

            temporaryLog = Path.Combine(Path.GetTempPath(), "ffbm-verify-" + Guid.NewGuid().ToString("N") + ".log");
            int testExitCode;
            if (!Run7Zip(zip7, new string[] { "t", "-sccUTF-8", fullArchivePath },
                         password ?? "", temporaryLog, out testExitCode, out error)) {
                error = "Проверка архива не пройдена. Проверьте пароль и целостность файла. " + error;
                return false;
            }
            return true;
        } catch (Exception ex) {
            error = "Ошибка проверки резервной копии: " + ex.Message;
            return false;
        } finally {
            try {
                if (!string.IsNullOrEmpty(temporaryLog) && File.Exists(temporaryLog))
                    File.Delete(temporaryLog);
            } catch {}
        }
    }


    // Validate an existing ancestor before creating the destination, then validate
    // the completed path. This blocks pre-existing junction redirection before the
    // first write; it is not a handle-based defense against a concurrent TOCTOU race.
    static bool PrepareBackupDestination(string backupDir, out string fullBackupDir, out string error) {
        fullBackupDir = ""; error = "";
        try {
            if (string.IsNullOrEmpty(backupDir)) {
                error = "Не задан каталог для резервных копий.";
                return false;
            }
            fullBackupDir = Path.GetFullPath(backupDir);
            if (File.Exists(fullBackupDir)) {
                error = "Путь назначения существует как файл, а не как каталог.";
                return false;
            }
            string existingAncestor = fullBackupDir;
            while (!Directory.Exists(existingAncestor)) {
                string parent = Path.GetDirectoryName(existingAncestor);
                if (string.IsNullOrEmpty(parent) ||
                    string.Equals(parent, existingAncestor, StringComparison.OrdinalIgnoreCase)) {
                    error = "Не удалось найти существующий родительский каталог назначения.";
                    return false;
                }
                existingAncestor = parent;
            }
            if (!EnsurePathHasNoReparsePoints(existingAncestor, out error)) {
                error = "Родительский путь назначения содержит файловую ссылку: " + error;
                return false;
            }
            Directory.CreateDirectory(fullBackupDir);
            if (!EnsurePathHasNoReparsePoints(fullBackupDir, out error)) {
                error = "Путь для резервных копий содержит файловую ссылку: " + error;
                return false;
            }
            return true;
        } catch (Exception ex) {
            error = "Не удалось безопасно подготовить каталог резервных копий: " + ex.Message;
            return false;
        }
    }

    static bool EnsurePathHasNoReparsePoints(string path, out string error) {
        error = "";
        try {
            string fullPath = Path.GetFullPath(path);
            string root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root)) {
                error = "Не удалось определить корневой каталог пути.";
                return false;
            }

            string current = root;
            FileAttributes rootAttributes = File.GetAttributes(current);
            if ((rootAttributes & FileAttributes.ReparsePoint) != 0) {
                error = "Путь проходит через точку подключения или другую файловую ссылку: " + current;
                return false;
            }

            string remainder = fullPath.Substring(root.Length);
            string[] segments = remainder.Split(new char[] {
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar
            }, StringSplitOptions.RemoveEmptyEntries);

            foreach (string segment in segments) {
                current = Path.Combine(current, segment);
                if (!Directory.Exists(current) && !File.Exists(current)) {
                    error = "Компонент пути не существует или недоступен: " + current;
                    return false;
                }
                FileAttributes attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0) {
                    error = "Путь проходит через символическую ссылку или junction: " + current;
                    return false;
                }
            }
            return true;
        } catch (Exception ex) {
            error = "Не удалось проверить путь на наличие файловых ссылок: " + ex.Message;
            return false;
        }
    }

    static bool EnsureNoReparsePoints(string rootPath, out string error) {
        error = "";
        try {
            Stack<string> pending = new Stack<string>();
            pending.Push(rootPath);
            while (pending.Count > 0) {
                string current = pending.Pop();
                foreach (string entry in Directory.GetFileSystemEntries(current)) {
                    FileAttributes attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) {
                        error = "В распакованном содержимом обнаружена ссылка файловой системы: " +
                                Path.GetFileName(entry);
                        return false;
                    }
                    if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                }
            }
            return true;
        } catch (Exception ex) {
            error = "Не удалось проверить распакованные файлы: " + ex.Message;
            return false;
        }
    }


    static void DeleteDirectoryNoFollow(string path) {
        FileAttributes attributes;
        try {
            attributes = File.GetAttributes(path);
        } catch (FileNotFoundException) {
            return;
        } catch (DirectoryNotFoundException) {
            return;
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0) {
            // Delete the link itself; never recurse into its target.
            if ((attributes & FileAttributes.Directory) != 0) Directory.Delete(path, false);
            else File.Delete(path);
            return;
        }

        if ((attributes & FileAttributes.Directory) != 0) {
            foreach (string entry in Directory.GetFileSystemEntries(path))
                DeleteDirectoryNoFollow(entry);
            Directory.Delete(path, false);
        } else {
            File.Delete(path);
        }
    }

    static bool RestoreBackup(string archivePath, string restoreParent, string password, string logFile,
                              out string restoredPath, out string error) {
        restoredPath = "";
        error = "";
        string stagingDirectory = "";
        try {
            if (string.IsNullOrEmpty(archivePath) || !File.Exists(archivePath)) {
                error = "Выбранный архив не существует или недоступен.";
                return false;
            }
            if (!string.Equals(Path.GetExtension(archivePath), ".7z", StringComparison.OrdinalIgnoreCase)) {
                error = "Для восстановления выберите архив с расширением .7z.";
                return false;
            }
            if (string.IsNullOrEmpty(restoreParent) || !Directory.Exists(restoreParent)) {
                error = "Каталог, в который будет помещено восстановление, не существует или недоступен.";
                return false;
            }

            string fullArchivePath = Path.GetFullPath(archivePath);
            string fullRestoreParent = Path.GetFullPath(restoreParent);
            string zip7 = Find7Zip();
            if (string.IsNullOrEmpty(zip7)) {
                error = "7-Zip не найден. Установите 7-Zip и повторите операцию.";
                return false;
            }

            stagingDirectory = Path.Combine(fullRestoreParent, ".ffbm-restore-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stagingDirectory);

            if (!ValidateArchiveEntries(zip7, fullArchivePath, password, stagingDirectory, true, out error))
                return false;

            int testExitCode;
            if (!Run7Zip(zip7, new string[] { "t", "-sccUTF-8", fullArchivePath },
                         password ?? "", logFile, out testExitCode, out error)) {
                error = "Проверка архива не пройдена. Проверьте пароль и целостность файла. " + error;
                return false;
            }

            int extractExitCode;
            string[] extractArguments = new string[] {
                "x", "-y", "-sccUTF-8", "-o" + stagingDirectory, fullArchivePath
            };
            if (!Run7Zip(zip7, extractArguments, password ?? "", logFile,
                         out extractExitCode, out error)) {
                error = "Не удалось распаковать архив. " + error;
                return false;
            }

            if (!File.Exists(Path.Combine(stagingDirectory, "places.sqlite")) &&
                !File.Exists(Path.Combine(stagingDirectory, "logins.json"))) {
                error = "В распакованном каталоге не найдены основные файлы профиля Firefox.";
                return false;
            }

            string reparseError;
            if (!EnsureNoReparsePoints(stagingDirectory, out reparseError)) {
                error = reparseError;
                return false;
            }

            string safeBaseName = MakeSafeFileName(Path.GetFileNameWithoutExtension(fullArchivePath));
            if (safeBaseName.Length > 80) safeBaseName = safeBaseName.Substring(0, 80);
            string finalDirectory = Path.Combine(fullRestoreParent, safeBaseName + "_restored");
            if (Directory.Exists(finalDirectory) || File.Exists(finalDirectory)) {
                finalDirectory = Path.Combine(fullRestoreParent,
                    safeBaseName + "_restored_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) +
                    "_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            }

            Directory.Move(stagingDirectory, finalDirectory);
            stagingDirectory = "";
            restoredPath = finalDirectory;
            AppendLogWithRotation(logFile, string.Format(
                "[{0}] [ВОССТАНОВЛЕНИЕ] Архив проверен и распакован в отдельный каталог: {1}\r\n",
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), finalDirectory));
            return true;
        } catch (Exception ex) {
            error = "Ошибка восстановления резервной копии: " + ex.Message;
            return false;
        } finally {
            try {
                if (!string.IsNullOrEmpty(stagingDirectory))
                    DeleteDirectoryNoFollow(stagingDirectory);
            } catch {}
        }
    }

    static bool CreateBackup(string profileName, string profilePath, string backupDir,
                             string password, string logFile, out string archivePath, out string error) {
        return CreateBackupCore(profileName, profilePath, backupDir, password, logFile, true,
                                out archivePath, out error);
    }

    // The CI harness tests the archive engine independently of unrelated processes
    // installed on hosted runners. Production entry points always use CreateBackup(),
    // which enables both Firefox process checks.
    static bool CreateBackupCore(string profileName, string profilePath, string backupDir,
                                 string password, string logFile, bool enforceFirefoxClosed,
                                 out string archivePath, out string error) {
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
            if (!File.Exists(Path.Combine(fullProfilePath, "places.sqlite")) &&
                !File.Exists(Path.Combine(fullProfilePath, "logins.json"))) {
                error = "Выбранный каталог не похож на профиль Firefox: не найден корневой файл places.sqlite или logins.json.";
                return false;
            }
            if (!EnsurePathHasNoReparsePoints(fullProfilePath, out error)) {
                error = "Путь к профилю содержит файловую ссылку: " + error;
                return false;
            }
            // Refuse before creating the destination directory or lock file if Firefox is open.
            if (enforceFirefoxClosed && !EnsureFirefoxClosed(out error)) return false;
            string preparedBackupDir;
            if (!PrepareBackupDestination(fullBackupDir, out preparedBackupDir, out error)) return false;
            fullBackupDir = preparedBackupDir;

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
                if (enforceFirefoxClosed && !EnsureFirefoxClosed(out error)) return false;
                string zip7 = Find7Zip();
                if (string.IsNullOrEmpty(zip7)) { error = "7-Zip не найден. Установите 7-Zip и повторите операцию."; return false; }

                string safeProfileName = MakeSafeFileName(profileName);
                string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
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

                if (enforceFirefoxClosed && !EnsureFirefoxClosed(out error)) {
                    error = "Firefox был запущен во время резервного копирования. " + error; return false;
                }
                string verifiedPathError;
                if (!EnsurePathHasNoReparsePoints(fullProfilePath, out verifiedPathError) ||
                    !EnsurePathHasNoReparsePoints(fullBackupDir, out verifiedPathError)) {
                    error = "Повторная проверка путей перед публикацией не пройдена: " + verifiedPathError;
                    return false;
                }
                long verifiedProfileSize;
                string verifiedProfileError;
                if (!TryGetDirectorySize(fullProfilePath, out verifiedProfileSize, out verifiedProfileError)) {
                    error = "Повторная проверка профиля перед публикацией не пройдена: " + verifiedProfileError;
                    return false;
                }
                if (verifiedProfileSize != requiredSpace) {
                    error = "Размер профиля изменился во время резервного копирования. Итоговый архив не опубликован; повторите операцию.";
                    return false;
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

    static bool IsManagedBackupFile(string fileName, string profileName) {
        string prefix = MakeSafeFileName(profileName) + "_";
        string baseName = Path.GetFileNameWithoutExtension(fileName);
        if (!baseName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;

        // The application writes: Profile_yyyy-MM-dd_HH-mm-ss_XXXXXXXX.7z
        // Only this exact pattern is eligible for automatic retention deletion.
        string suffix = baseName.Substring(prefix.Length);
        if (suffix.Length != 28 || suffix[19] != '_') return false;

        DateTime timestamp;
        if (!DateTime.TryParseExact(suffix.Substring(0, 19), "yyyy-MM-dd_HH-mm-ss",
                                    CultureInfo.InvariantCulture, DateTimeStyles.None, out timestamp))
            return false;

        string id = suffix.Substring(20);
        for (int i = 0; i < id.Length; i++)
            if (!Uri.IsHexDigit(id[i])) return false;
        return true;
    }

    static void RetainOldBackups(string profileName, string backupDir, string logFile) {
        try {
            DateTime cutoff = DateTime.Now.AddDays(-30);
            DirectoryInfo directory = new DirectoryInfo(backupDir);
            foreach (FileInfo file in directory.GetFiles("*.7z")) {
                if (!IsManagedBackupFile(file.Name, profileName)) continue;
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

    // Resolve Windows utilities from the OS system directory, never through the
    // executable/current-directory/PATH search order.
    static string GetSystemExecutablePath(string relativePath) {
        string systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (string.IsNullOrEmpty(systemDirectory))
            throw new InvalidOperationException("Не удалось определить системный каталог Windows.");
        string root = Path.GetFullPath(systemDirectory);
        string candidate = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!IsPathSameOrInside(root, candidate))
            throw new InvalidOperationException("Путь системной утилиты выходит за пределы системного каталога Windows.");
        return candidate;
    }

    static bool IsScheduledTaskRegistered(string taskName) {
        int exitCode; string output; string error;
        bool launched = RunProcess(GetSystemExecutablePath("schtasks.exe"), BuildCommandLine("/Query", "/TN", taskName),
                                   30000, null, out exitCode, out output, out error);
        return launched && exitCode == 0;
    }

    static bool IsScheduledTaskRegistered() {
        return IsScheduledTaskRegistered(GetUserScheduledTaskName());
    }

    // Old releases used the fixed task name Firefox_Local_AutoBackup and ran as SYSTEM.
    // The per-user task name is unique to the account and must use an interactive limited token.
    static bool IsTaskUsingUnsafeSecurityContext(string taskName, bool requireCurrentUser) {
        int exitCode; string output; string error;
        bool launched = RunProcess(GetSystemExecutablePath("schtasks.exe"), BuildCommandLine("/Query", "/TN", taskName, "/XML"),
                                   30000, null, out exitCode, out output, out error);
        if (!launched || exitCode != 0 || string.IsNullOrEmpty(output)) return true;
        try {
            XmlDocument document = new XmlDocument();
            document.LoadXml(output);
            XmlNamespaceManager namespaces = new XmlNamespaceManager(document.NameTable);
            namespaces.AddNamespace("task", "http://schemas.microsoft.com/windows/2004/02/mit/task");
            XmlNode userNode = document.SelectSingleNode("//task:Principals/task:Principal/task:UserId", namespaces);
            XmlNode logonNode = document.SelectSingleNode("//task:Principals/task:Principal/task:LogonType", namespaces);
            XmlNode runLevelNode = document.SelectSingleNode("//task:Principals/task:Principal/task:RunLevel", namespaces);
            if (userNode == null) return true;

            string configuredUser = userNode.InnerText.Trim();
            // Task Scheduler's XML schema defaults omitted values to InteractiveToken
            // and LeastPrivilege. Preserve those documented safe defaults.
            string configuredLogon = logonNode == null ? "InteractiveToken" : logonNode.InnerText.Trim();
            string configuredRunLevel = runLevelNode == null ? "LeastPrivilege" : runLevelNode.InnerText.Trim();
            if (string.Equals(configuredUser, "SYSTEM", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(configuredUser, "NT AUTHORITY\\SYSTEM", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(configuredUser, "S-1-5-18", StringComparison.OrdinalIgnoreCase)) return true;
            if (!string.Equals(configuredLogon, "InteractiveToken", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(configuredRunLevel, "LeastPrivilege", StringComparison.OrdinalIgnoreCase)) return true;

            if (requireCurrentUser) {
                WindowsIdentity identity = WindowsIdentity.GetCurrent();
                string currentName = identity == null ? "" : identity.Name;
                string currentSid = identity == null || identity.User == null ? "" : identity.User.Value;
                if (!string.Equals(configuredUser, currentName, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(configuredUser, currentSid, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        } catch {
            return true;
        }
    }

    static bool IsScheduledTaskUsingUnsafeSecurityContext() {
        return IsTaskUsingUnsafeSecurityContext(GetUserScheduledTaskName(), true);
    }

    static bool IsLegacySystemTaskRegistered() {
        if (!IsScheduledTaskRegistered(legacyRegKeyName)) return false;
        return IsTaskUsingUnsafeSecurityContext(legacyRegKeyName, false);
    }

    static bool RemoveScheduledTask(out string error) {
        int exitCode; string output;
        string taskName = GetUserScheduledTaskName();
        bool launched = RunProcess(GetSystemExecutablePath("schtasks.exe"), BuildCommandLine("/Delete", "/TN", taskName, "/F"),
                                   30000, null, out exitCode, out output, out error);
        if (!launched) return false;
        if (IsScheduledTaskRegistered(taskName)) {
            error = "Задача Планировщика всё ещё существует после команды удаления. " + output;
            return false;
        }
        return true;
    }

    static bool RegisterScheduledTask(string exePath, out string error) {
        error = "";
        string fullExecutablePath;
        try {
            fullExecutablePath = Path.GetFullPath(exePath);
            if (!File.Exists(fullExecutablePath) ||
                (File.GetAttributes(fullExecutablePath) & FileAttributes.ReparsePoint) != 0) {
                error = "Исполняемый файл задачи не существует или является файловой ссылкой.";
                return false;
            }
        } catch (Exception ex) {
            error = "Не удалось проверить путь приложения для задачи: " + ex.Message;
            return false;
        }

        int exitCode;
        string output;
        string taskName = GetUserScheduledTaskName().Replace("'", "''");
        string executable = fullExecutablePath.Replace("'", "''");
        string taskUser;
        try {
            taskUser = GetCurrentUserName().Replace("'", "''");
        } catch (Exception ex) {
            error = "Не удалось определить текущую учётную запись для задачи Планировщика: " + ex.Message;
            return false;
        }

        // The task runs only in this user's interactive session, with LUA privileges.
        // It therefore has no more rights than the account that selected the destination.
        string command =
            "$ErrorActionPreference = 'Stop'; try { " +
            "$act = New-ScheduledTaskAction -Execute '" + executable + "' -Argument '--silent-backup'; " +
            "$trig = New-ScheduledTaskTrigger -AtLogOn -User '" + taskUser + "'; " +
            "$set = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable; " +
            "$principal = New-ScheduledTaskPrincipal -UserId '" + taskUser + "' -LogonType Interactive -RunLevel Limited; " +
            "Register-ScheduledTask -TaskName '" + taskName + "' -Action $act -Trigger $trig -Settings $set -Principal $principal -Force | Out-Null; " +
            "exit 0 } catch { Write-Error $_; exit 1 }";

        bool launched = RunProcess(GetSystemExecutablePath(Path.Combine("WindowsPowerShell", "v1.0", "powershell.exe")),
            BuildCommandLine("-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-Command", command),
            30000, null, out exitCode, out output, out error);

        if (!launched || exitCode != 0) {
            if (string.IsNullOrEmpty(error)) error = "PowerShell не смог зарегистрировать задачу для текущего пользователя. " + output;
            return false;
        }
        if (!IsScheduledTaskRegistered() || IsScheduledTaskUsingUnsafeSecurityContext()) {
            error = "Задача зарегистрирована, но проверка её пользовательского контекста и минимальных прав не пройдена.";
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

    static void RunIcacls(string path, string[] arguments) {
        string systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string icacls = Path.Combine(systemDirectory, "icacls.exe");
        if (!File.Exists(icacls)) throw new IOException("Не найден системный icacls.exe.");

        string[] allArguments = new string[arguments.Length + 1];
        allArguments[0] = path;
        Array.Copy(arguments, 0, allArguments, 1, arguments.Length);

        int exitCode; string output; string error;
        bool launched = RunProcess(icacls, BuildCommandLine(allArguments), 30000, null,
                                   out exitCode, out output, out error);
        if (!launched || exitCode != 0)
            throw new IOException("Не удалось применить ACL через icacls.exe. " +
                                  (string.IsNullOrEmpty(error) ? output : error));
    }

    static string GetCurrentUserSid() {
        WindowsIdentity identity = WindowsIdentity.GetCurrent();
        if (identity == null || identity.User == null)
            throw new InvalidOperationException("Не удалось определить SID текущего пользователя.");
        return identity.User.Value;
    }

    static string GetCurrentUserName() {
        WindowsIdentity identity = WindowsIdentity.GetCurrent();
        if (identity == null || string.IsNullOrEmpty(identity.Name))
            throw new InvalidOperationException("Не удалось определить имя текущей учётной записи Windows.");
        return identity.Name;
    }

    static void ApplyPrivateDirectorySecurity(string directoryPath) {
        string userSid = "*" + GetCurrentUserSid() + ":(OI)(CI)F";
        RunIcacls(directoryPath, new string[] { "/reset", "/Q" });
        RunIcacls(directoryPath, new string[] {
            "/inheritance:r", "/grant:r",
            userSid, "*S-1-5-18:(OI)(CI)F", "*S-1-5-32-544:(OI)(CI)F",
            "/Q"
        });
    }

    static void ApplyPrivateFileSecurity(string filePath) {
        string userSid = "*" + GetCurrentUserSid() + ":F";
        RunIcacls(filePath, new string[] { "/reset", "/Q" });
        RunIcacls(filePath, new string[] {
            "/inheritance:r", "/grant:r",
            userSid, "*S-1-5-18:F", "*S-1-5-32-544:F",
            "/Q"
        });
    }

    // The private config is stored in LocalAppData and encrypted with DPAPI CurrentUser.
    // Its DACL grants access only to this user, LocalSystem, and local Administrators.
    // The config file therefore relies on a protected DACL as a second boundary.
    static bool EnsurePrivateConfigDirectory(string applicationDirectory,
                                             out string configFile, out string error) {
        string privateDirectory = Path.Combine(applicationDirectory, "PrivateConfig");
        configFile = Path.Combine(privateDirectory, "config.ini");
        error = "";
        try {
            Directory.CreateDirectory(privateDirectory);
            if ((File.GetAttributes(privateDirectory) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("PrivateConfig не должен быть символической ссылкой или точкой соединения.");

            ApplyPrivateDirectorySecurity(privateDirectory);
            if (Directory.GetDirectories(privateDirectory).Length > 0)
                throw new IOException("В PrivateConfig обнаружены вложенные каталоги. Удалите их вручную и повторите запуск.");

            foreach (string existingFile in Directory.GetFiles(privateDirectory)) {
                if ((File.GetAttributes(existingFile) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("В PrivateConfig обнаружена ссылка вместо обычного файла: " + Path.GetFileName(existingFile));
                ApplyPrivateFileSecurity(existingFile);
            }

            string legacyConfig = Path.Combine(applicationDirectory, "config.ini");
            if (File.Exists(legacyConfig)) {
                if ((File.GetAttributes(legacyConfig) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Старый config.ini является ссылкой. Автоматическая миграция отменена.");

                if (!File.Exists(configFile)) {
                    string[] legacyLines = File.ReadAllLines(legacyConfig, Encoding.UTF8);
                    string writeError;
                    if (!WriteConfigAtomic(configFile, legacyLines, out writeError))
                        throw new IOException("Не удалось перенести старую конфигурацию: " + writeError);
                    ApplyPrivateFileSecurity(configFile);
                }

                ApplyPrivateFileSecurity(legacyConfig);
                File.Delete(legacyConfig);
                if (File.Exists(legacyConfig))
                    throw new IOException("Старая конфигурация осталась на диске после попытки удаления.");
            }

            if (File.Exists(configFile)) ApplyPrivateFileSecurity(configFile);
            return true;
        } catch (Exception ex) {
            error = "Не удалось защитить каталог конфигурации (PrivateConfig): " + ex.Message;
            return false;
        }
    }

    static bool IsFullyQualifiedWindowsPath(string path) {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) return false;
        string root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root) || root.Length < 3) return false;
        return true;
    }

    static bool TryReadBackupConfig(string configPath,
                                    out string profileName, out string profilePath,
                                    out string backupDirectory, out string encryptedPassword,
                                    out string error) {
        profileName = "";
        profilePath = "";
        backupDirectory = "";
        encryptedPassword = "";
        error = "";

        try {
            FileInfo configInfo = new FileInfo(configPath);
            if (!configInfo.Exists) {
                error = "Файл конфигурации не найден.";
                return false;
            }
            if (configInfo.Length <= 0 || configInfo.Length > 65536) {
                error = "Размер конфигурации недопустим (ожидается от 1 байта до 64 КиБ).";
                return false;
            }

            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);
            string[] lines = File.ReadAllLines(configPath, Encoding.UTF8);
            foreach (string rawLine in lines) {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal) ||
                    line.StartsWith(";", StringComparison.Ordinal)) continue;

                int separator = line.IndexOf('=');
                if (separator <= 0) {
                    error = "Обнаружена строка конфигурации без пары «ключ=значение».";
                    return false;
                }

                string key = line.Substring(0, separator).Trim();
                string value = line.Substring(separator + 1).Trim();
                if (key != "ProfileName" && key != "ProfilePath" &&
                    key != "BackupDir" && key != "BackupPassword") {
                    error = "Обнаружен неизвестный ключ конфигурации: " + key;
                    return false;
                }
                if (values.ContainsKey(key)) {
                    error = "Обнаружен повторяющийся ключ конфигурации: " + key;
                    return false;
                }
                values.Add(key, value);
            }

            string[] requiredKeys = new string[] {
                "ProfileName", "ProfilePath", "BackupDir", "BackupPassword"
            };
            foreach (string key in requiredKeys) {
                if (!values.ContainsKey(key)) {
                    error = "В конфигурации отсутствует обязательный ключ: " + key;
                    return false;
                }
            }

            profileName = values["ProfileName"];
            string rawProfilePath = values["ProfilePath"];
            string rawBackupDirectory = values["BackupDir"];
            encryptedPassword = values["BackupPassword"];

            // An empty BackupPassword is valid and means that the user deliberately
            // chose an unencrypted archive. The key itself is still mandatory.
            if (string.IsNullOrWhiteSpace(profileName) || profileName.Length > 256 ||
                string.IsNullOrWhiteSpace(rawProfilePath) ||
                string.IsNullOrWhiteSpace(rawBackupDirectory)) {
                error = "Имя профиля, путь к профилю и каталог резервных копий обязательны.";
                return false;
            }

            if (!IsFullyQualifiedWindowsPath(rawProfilePath)) {
                error = "Путь к профилю должен быть абсолютным (например, C:\\Users\\User\\Firefox\\Profiles\\...).";
                return false;
            }
            if (!IsFullyQualifiedWindowsPath(rawBackupDirectory)) {
                error = "Путь для резервных копий должен быть абсолютным.";
                return false;
            }

            profilePath = Path.GetFullPath(rawProfilePath);
            backupDirectory = Path.GetFullPath(rawBackupDirectory);
            if (!Directory.Exists(profilePath)) {
                error = "Каталог выбранного профиля Firefox не существует.";
                return false;
            }
            if (File.Exists(backupDirectory)) {
                error = "Путь для резервных копий указывает на файл, а не на каталог.";
                return false;
            }
            if (IsPathSameOrInside(profilePath, backupDirectory)) {
                error = "Каталог резервных копий не может находиться внутри профиля Firefox.";
                return false;
            }

            // Empty is the documented "no archive password" setting.
            // Non-empty values must contain a bounded DPAPI payload.
            if (!string.IsNullOrEmpty(encryptedPassword)) {
                byte[] protectedPassword = Convert.FromBase64String(encryptedPassword);
                if (protectedPassword.Length == 0 || protectedPassword.Length > 16384) {
                    error = "DPAPI-значение пароля имеет недопустимый размер.";
                    return false;
                }
            }
            return true;
        } catch (FormatException) {
            error = "Значение BackupPassword не является корректной строкой Base64.";
            return false;
        } catch (Exception ex) {
            error = "Не удалось прочитать или проверить конфигурацию: " + ex.Message;
            return false;
        }
    }

    [STAThread]
    static int Main(string[] args) {
        Application.EnableVisualStyles();
        // No invocation mode may run backup or setup operations as LocalSystem.
        // A legacy SYSTEM task exits before touching configuration or destination files.
        if (IsRunningAsSystem()) return 8;

        string currentDir = AppDomain.CurrentDomain.BaseDirectory;
        string userDataDirectory;
        string configFile;
        try {
            userDataDirectory = GetUserDataDirectory();
            configFile = Path.Combine(userDataDirectory, "PrivateConfig", "config.ini");
        } catch {
            return 10;
        }

        // РЕЖИМ 1: Фоновое резервное копирование при входе текущего пользователя.
        if (args.Length > 0 && args[0] == "--silent-backup") {
            string privateConfigError;
            if (!EnsurePrivateConfigDirectory(userDataDirectory, out configFile, out privateConfigError)) return 9;
            if (!File.Exists(configFile)) return 2;
            string profName, profPath, backupDir, encryptedPass, configValidationError;
            if (!TryReadBackupConfig(configFile, out profName, out profPath, out backupDir,
                                     out encryptedPass, out configValidationError)) return 4;

            string logFile;
            try {
                logFile = Path.Combine(Path.GetFullPath(backupDir), "backup_log.txt");
            } catch { return 5; }

            string realPassword, passwordError;
            if (!TryDecryptPassword(encryptedPass, out realPassword, out passwordError)) return 6;

            string createdArchive, backupError;
            if (!CreateBackup(profName, profPath, backupDir, realPassword, logFile,
                              out createdArchive, out backupError)) return 7;

            RetainOldBackups(profName, backupDir, logFile);
            AppendLogWithRotation(logFile, string.Format(
                "[{0}] [УСПЕХ] Резервное копирование завершено: {1}\\r\\n==================================================\\r\\n",
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), createdArchive));
            return 0;
        }

        // Never silently replace a task created by the previous SYSTEM-based release.
        // Its removal requires an explicit administrator action; old archives remain untouched.
        if (IsLegacySystemTaskRegistered()) {
            MessageBox.Show(
                "Обнаружена задача Firefox Backup Manager старой версии, работающая от SYSTEM. " +
                "Новая версия больше не использует SYSTEM для резервного копирования.\\n\\n" +
                "Один раз откройте Терминал или PowerShell от имени администратора и выполните:\\n" +
                "schtasks /Delete /TN Firefox_Local_AutoBackup /F\\n\\n" +
                "Затем снова запустите приложение и настройте резервирование заново. " +
                "Старые архивы и прежняя конфигурация не удаляются автоматически.",
                "Требуется обновление задачи автозапуска", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 11;
        }

        if (IsScheduledTaskRegistered() && IsScheduledTaskUsingUnsafeSecurityContext()) {
            string unsafeTaskName = GetUserScheduledTaskName();
            MessageBox.Show(
                "Найдена задача с именем текущей учётной записи, но её XML не подтверждает " +
                "запуск от имени текущего пользователя с InteractiveToken и LeastPrivilege. " +
                "Новая версия не будет автоматически заменять такую задачу.\\n\\n" +
                "Проверьте задачу в Планировщике. Если она устарела, удалите её командой:\\n" +
                "schtasks /Delete /TN \"" + unsafeTaskName + "\" /F",
                "Небезопасная задача автозапуска", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 12;
        }

        string configError;
        if (!EnsurePrivateConfigDirectory(userDataDirectory, out configFile, out configError)) {
            MessageBox.Show(configError + "\\n\\nАвтозапуск не будет настроен, пока пользовательская конфигурация не защищена.",
                "Ошибка защиты конфигурации", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 9;
        }

        // РЕЖИМ 2: Интерактивное графическое меню настроек
        Form menuForm = new Form() { Text = "Менеджер бэкапа Firefox", Width = 460, Height = 355, StartPosition = FormStartPosition.CenterScreen, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false };
        Label lblStatus = new Label() { Left = 20, Top = 20, Width = 400, Height = 60 };
        
        if (File.Exists(configFile)) {
            string pN, pP, bD, storedPassword, validationError;
            if (TryReadBackupConfig(configFile, out pN, out pP, out bD, out storedPassword, out validationError)) {
                bool taskRegistered = IsScheduledTaskRegistered();
                lblStatus.Text = string.Format("Конфигурация: сохранена\nСтатус автозапуска: {0}\nПрофиль: {1}\nПапка сохранения: {2}",
                    taskRegistered ? "ЗАДАЧА НАЙДЕНА" : "ЗАДАЧА НЕ НАЙДЕНА", pN, bD);
            } else {
                lblStatus.Text = "ОШИБКА КОНФИГУРАЦИИ: " + validationError;
            }
        } else {
            lblStatus.Text = "Статус автозапуска: НЕ НАСТРОЕН / ОТКЛЮЧЕН";
        }

        Button btnSetup = new Button() { Text = "Включить / Настроить авто-бэкап", Left = 20, Top = 100, Width = 400, Height = 35 };
        Button btnDelete = new Button() { Text = "Отключить авто-бэкап и удалить конфиг", Left = 20, Top = 145, Width = 400, Height = 35 };
        Button btnRestore = new Button() { Text = "Восстановить архив в отдельную папку", Left = 20, Top = 190, Width = 400, Height = 35 };
        Button btnVerify = new Button() { Text = "Проверить архив", Left = 20, Top = 235, Width = 400, Height = 35 };

        btnRestore.Click += (s, e) => {
            OpenFileDialog archiveDialog = new OpenFileDialog();
            archiveDialog.Title = "Выберите резервную копию Firefox";
            archiveDialog.Filter = "Архивы 7-Zip (*.7z)|*.7z";
            archiveDialog.CheckFileExists = true;
            archiveDialog.Multiselect = false;
            if (archiveDialog.ShowDialog() != DialogResult.OK) return;

            FolderBrowserDialog restoreDialog = new FolderBrowserDialog();
            restoreDialog.Description = "Выберите родительский каталог. Программа создаст внутри него новую папку и не перезапишет существующие данные.";
            if (restoreDialog.ShowDialog() != DialogResult.OK) return;

            Form restorePasswordForm = new Form() {
                Text = "Пароль резервной копии",
                Width = 410, Height = 175, StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false
            };
            Label restorePasswordLabel = new Label() {
                Text = "Пароль архива (для незашифрованной копии оставьте поле пустым):",
                Left = 15, Top = 15, Width = 365, Height = 35
            };
            TextBox restorePasswordBox = new TextBox() {
                Left = 15, Top = 55, Width = 365, PasswordChar = '*'
            };
            Button restorePasswordOk = new Button() {
                Text = "Продолжить", Left = 270, Top = 95, Width = 110, Height = 30,
                DialogResult = DialogResult.OK
            };
            restorePasswordForm.Controls.AddRange(new Control[] {
                restorePasswordLabel, restorePasswordBox, restorePasswordOk
            });
            restorePasswordForm.AcceptButton = restorePasswordOk;
            if (restorePasswordForm.ShowDialog() != DialogResult.OK) return;

            string restoreLog = Path.Combine(restoreDialog.SelectedPath, "restore_log.txt");
            string restoredPath, restoreError;
            if (RestoreBackup(archiveDialog.FileName, restoreDialog.SelectedPath,
                              restorePasswordBox.Text, restoreLog, out restoredPath, out restoreError)) {
                MessageBox.Show("Восстановление завершено. Исходные файлы не изменялись.\n\nКаталог профиля:\n" +
                    restoredPath + "\n\nПодключите этот каталог через менеджер профилей Firefox после проверки содержимого.",
                    "Успешное восстановление", MessageBoxButtons.OK, MessageBoxIcon.Information);
            } else {
                MessageBox.Show("Восстановление отменено. Исходные файлы не изменялись.\n\n" + restoreError,
                    "Ошибка восстановления", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };

        btnVerify.Click += (s, e) => {
            OpenFileDialog verifyDialog = new OpenFileDialog();
            verifyDialog.Title = "Выберите резервную копию Firefox для проверки";
            verifyDialog.Filter = "Архивы 7-Zip (*.7z)|*.7z";
            verifyDialog.CheckFileExists = true;
            verifyDialog.Multiselect = false;
            if (verifyDialog.ShowDialog() != DialogResult.OK) return;

            Form verifyPasswordForm = new Form() {
                Text = "Пароль резервной копии",
                Width = 410, Height = 175, StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false
            };
            Label verifyPasswordLabel = new Label() {
                Text = "Пароль архива (для незашифрованной копии оставьте поле пустым):",
                Left = 15, Top = 15, Width = 365, Height = 35
            };
            TextBox verifyPasswordBox = new TextBox() {
                Left = 15, Top = 55, Width = 365, PasswordChar = '*'
            };
            Button verifyPasswordOk = new Button() {
                Text = "Проверить", Left = 270, Top = 95, Width = 110, Height = 30,
                DialogResult = DialogResult.OK
            };
            verifyPasswordForm.Controls.AddRange(new Control[] {
                verifyPasswordLabel, verifyPasswordBox, verifyPasswordOk
            });
            verifyPasswordForm.AcceptButton = verifyPasswordOk;
            if (verifyPasswordForm.ShowDialog() != DialogResult.OK) return;

            string verifyError;
            if (VerifyBackupArchive(verifyDialog.FileName, verifyPasswordBox.Text, out verifyError)) {
                MessageBox.Show("Архив прошёл проверку структуры и целостности 7-Zip. Распаковка не выполнялась.",
                    "Проверка завершена", MessageBoxButtons.OK, MessageBoxIcon.Information);
            } else {
                MessageBox.Show("Проверка архива не пройдена.\\n\\n" + verifyError,
                    "Ошибка проверки", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };

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

            string startLog = Path.Combine(Path.GetFullPath(chosenBackupDir), "backup_log.txt");

            string firstArchive, firstBackupError;
            if (!CreateBackup(chosenName, chosenPath, chosenBackupDir, chosenPassword, startLog,
                              out firstArchive, out firstBackupError)) {
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
                    "Конфигурация сохранена; проверьте права текущей учётной записи на регистрацию задачи и повторите настройку.\n\n" + taskError,
                    "Автозапуск не настроен", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                menuForm.Close();
                return;
            }

            RetainOldBackups(chosenName, chosenBackupDir, startLog);
            AppendLogWithRotation(startLog, "[УСПЕХ] Первая резервная копия проверена; задача автозапуска зарегистрирована.\r\n");

            MessageBox.Show("Настройка завершена!\n\n1. Профиль Firefox определен.\n2. Первая резервная копия создана и проверена.\n3. Задача Планировщика Windows зарегистрирована для текущей учётной записи с ограниченными правами.",
                "Успех", MessageBoxButtons.OK, MessageBoxIcon.Information);
            menuForm.Close();
        };

        btnDelete.Click += (s, e) => {
            int exitCode;
            string output, commandError;
            bool launched = RunProcess(GetSystemExecutablePath("schtasks.exe"), BuildCommandLine("/Delete", "/TN", GetUserScheduledTaskName(), "/F"),
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
            MessageBox.Show("Задача автозапуска текущего пользователя удалена, файл настроек удалён. Архивы резервных копий не затронуты.",
                "Успех", MessageBoxButtons.OK, MessageBoxIcon.Information);
            menuForm.Close();
        };

        menuForm.Controls.AddRange(new Control[] { lblStatus, btnSetup, btnDelete, btnRestore, btnVerify });
        Application.Run(menuForm);
        return 0;
    }
}
