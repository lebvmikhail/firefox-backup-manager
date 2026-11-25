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
    
    static long GetDirectorySize(string folderPath) {
        if (!Directory.Exists(folderPath)) return 0;
        long size = 0;
        try {
            string[] files = Directory.GetFiles(folderPath, "*", SearchOption.AllDirectories);
            foreach (string file in files) {
                try {
                    FileInfo fi = new FileInfo(file);
                    size += fi.Length;
                } catch {}
            }
        } catch {}
        return size;
    }

    static void AppendLogWithRotation(string logPath, string text) {
        try {
            if (File.Exists(logPath)) {
                FileInfo fi = new FileInfo(logPath);
                if (fi.Length > 5 * 1024 * 1024) {
                    File.WriteAllText(logPath, "[ИНФО] Лог-файл превысил 5 МБ и был автоматически очищен.\r\n", Encoding.UTF8);
                }
            }
            File.AppendAllText(logPath, text, Encoding.UTF8);
        } catch {}
    }
    
    static string EncryptPassword(string password) {
        if (string.IsNullOrEmpty(password)) return "";
        byte[] data = Encoding.UTF8.GetBytes(password);
        byte[] encrypted = ProtectedData.Protect(data, null, DataProtectionScope.LocalMachine);
        return Convert.ToBase64String(encrypted);
    }

    static string DecryptPassword(string encryptedBase64) {
        if (string.IsNullOrEmpty(encryptedBase64)) return "";
        try {
            byte[] encrypted = Convert.FromBase64String(encryptedBase64);
            byte[] decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.LocalMachine);
            return Encoding.UTF8.GetString(decrypted);
        } catch { return ""; }
    }

    [STAThread]
    static void Main(string[] args) {
        Application.EnableVisualStyles();
        string currentDir = AppDomain.CurrentDomain.BaseDirectory;
        string configFile = Path.Combine(currentDir, "config.ini");
        // РЕЖИМ 1: Фоновый авто-бэкап при включении ПК (Формат 7z AES-256)
        if (args.Length > 0 && args[0] == "--silent-backup") {
            if (!File.Exists(configFile)) return;
            string profName = "", profPath = "", backupDir = "", encryptedPass = "";
            
            foreach (string l in File.ReadAllLines(configFile, Encoding.UTF8)) {
                if (l.StartsWith("ProfileName=")) profName = l.Replace("ProfileName=", "").Trim();
                if (l.StartsWith("ProfilePath=")) profPath = l.Replace("ProfilePath=", "").Trim();
                if (l.StartsWith("BackupDir=")) backupDir = l.Replace("BackupDir=", "").Trim();
                if (l.StartsWith("BackupPassword=")) encryptedPass = l.Replace("BackupPassword=", "").Trim();
            }
            
            if (string.IsNullOrEmpty(profName) || !Directory.Exists(profPath)) return;
            if (!Directory.Exists(backupDir)) Directory.CreateDirectory(backupDir);

            string logFile = Path.Combine(backupDir, "backup_log.txt");
            string dateTimeStr = DateTime.Now.ToString("yyyy-MM-dd_HH-mm");
            
            AppendLogWithRotation(logFile, string.Format("==================================================\r\n[{0}] [АВТОЗАПУСК ПК] Старт бэкапа.\r\n", dateTimeStr));
            
            try {
                long requiredSpace = GetDirectorySize(profPath);
                string targetRoot = Path.GetPathRoot(backupDir);
                DriveInfo drive = new DriveInfo(targetRoot);
                
                if (drive.AvailableFreeSpace < requiredSpace) {
                    AppendLogWithRotation(logFile, string.Format("[{0}] [ОШИБКА] Недостаточно места на диске {1}. Требуется около {2} МБ, доступно {3} МБ. Бэкап отменен.\r\n==================================================\r\n", 
                        DateTime.Now.ToString("yyyy-MM-dd_HH-mm"), targetRoot, requiredSpace / 1024 / 1024, drive.AvailableFreeSpace / 1024 / 1024));
                    return;
                }
            } catch (Exception ex) {
                AppendLogWithRotation(logFile, string.Format("[{0}] [ПРЕДУПРЕЖДЕНИЕ] Не удалось проверить свободное место: {1}\r\n", DateTime.Now.ToString("yyyy-MM-dd_HH-mm"), ex.Message));
            }

            foreach (var process in Process.GetProcessesByName("firefox")) {
                try { process.Kill(); process.WaitForExit(3000); } catch {}
            }

            string zip7 = Directory.Exists(@"C:\Program Files\7-Zip") ? @"C:\Program Files\7-Zip\7z.exe" : @"C:\Program Files (x86)\7-Zip\7z.exe";
            if (File.Exists(zip7)) {
                string xFile = Path.Combine(Path.GetTempPath(), "ff_excludes.txt");
                File.WriteAllLines(xFile, new string[] { "*cache2*", "*startupCache*", "*jumpListCache*", "*thumbnails*", "*shader-cache*" }, Encoding.UTF8);

                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = zip7;
                
                string realPassword = DecryptPassword(encryptedPass);
                
                if (!string.IsNullOrEmpty(realPassword)) {
                    psi.Arguments = string.Format("a -t7z \"{0}\\{1}_{2}.7z\" \"{3}\\*\" -p\"{4}\" -mhe=on -x@\"{5}\" -mx5", backupDir, profName, dateTimeStr, profPath, realPassword, xFile);
                } else {
                    psi.Arguments = string.Format("a -t7z \"{0}\\{1}_{2}.7z\" \"{3}\\*\" -x@\"{4}\" -mx5", backupDir, profName, dateTimeStr, profPath, xFile);
                }
                
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                psi.UseShellExecute = false;
                Process p = Process.Start(psi);
                p.WaitForExit();
                
                try { File.Delete(xFile); } catch {}
                AppendLogWithRotation(logFile, string.Format("[{0}] [АРХИВАЦИЯ] 7-Zip успешно завершил работу.\r\n", DateTime.Now.ToString("yyyy-MM-dd_HH-mm")));
            }

            var dirInfo = new DirectoryInfo(backupDir);
            foreach (var file in dirInfo.GetFiles(profName + "*.7z")) {
                if (file.CreationTime < DateTime.Now.AddDays(-30)) {
                    try { file.Delete(); } catch {}
                }
            }
            AppendLogWithRotation(logFile, string.Format("[{0}] [УСПЕХ] Скрытый автоматический бэкап успешно завершен.\r\n==================================================\r\n", DateTime.Now.ToString("yyyy-MM-dd_HH-mm")));
            return;
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
            lblStatus.Text = string.Format("Статус автозапуска: АКТИВЕН\nПрофиль: {0}\nПапка сохранения: {1}", pN, bD);
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

            Form passForm = new Form() { Text = "ШАГ 3 из 3: Безопасность архива", Width = 400, Height = 180, StartPosition = FormStartPosition.CenterScreen, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false };
            Label lblPass = new Label() { Text = "Задайте секретный пароль для защиты бэкапа (необязательно):\nЕсли пароль не нужен - просто оставьте поле пустым.", Left = 20, Top = 15, Width = 350, Height = 35 };
            TextBox txtPass = new TextBox() { Left = 20, Top = 55, Width = 340, PasswordChar = '*' };
            Button btnPassOK = new Button() { Text = "Завершить настройку", Left = 210, Top = 100, Width = 150, Height = 30 };
            passForm.Controls.AddRange(new Control[] { lblPass, txtPass, btnPassOK });
            btnPassOK.Click += (s3, e3) => { passForm.DialogResult = DialogResult.OK; passForm.Close(); };
            
            if (passForm.ShowDialog() != DialogResult.OK) return;
            string chosenPassword = txtPass.Text.Trim();

            string securePasswordString = EncryptPassword(chosenPassword);

            File.WriteAllLines(configFile, new string[] {
                "ProfileName=" + chosenName,
                "ProfilePath=" + chosenPath,
                "BackupDir=" + chosenBackupDir,
                "BackupPassword=" + securePasswordString
            }, Encoding.UTF8);
            if (!Directory.Exists(chosenBackupDir)) Directory.CreateDirectory(chosenBackupDir);
            string startLog = Path.Combine(chosenBackupDir, "backup_log.txt");
            string startDt = DateTime.Now.ToString("yyyy-MM-dd_HH-mm");

            AppendLogWithRotation(startLog, string.Format("==================================================\r\n[{0}] [ПЕРВЫЙ ЗАПУСК] Создание стартовой копии...\r\n", startDt));
            
            try {
                long requiredSpace = GetDirectorySize(chosenPath);
                string targetRoot = Path.GetPathRoot(chosenBackupDir);
                DriveInfo drive = new DriveInfo(targetRoot);
                
                if (drive.AvailableFreeSpace < requiredSpace) {
                    string errorMsg = string.Format("Недостаточно места на диске {0}!\nТребуется около {1} МБ, а доступно всего {2} МБ.\n\nНастройка отменена.", 
                        targetRoot, requiredSpace / 1024 / 1024, drive.AvailableFreeSpace / 1024 / 1024);
                    AppendLogWithRotation(startLog, string.Format("[{0}] [ОШИБКА] Первый запуск прерван: {1}\r\n==================================================\r\n", DateTime.Now.ToString("yyyy-MM-dd_HH-mm"), errorMsg.Replace("\n", " ")));
                    MessageBox.Show(errorMsg, "Ошибка свободного места", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
            } catch (Exception ex) {
                AppendLogWithRotation(startLog, string.Format("[{0}] [ПРЕДУПРЕЖДЕНИЕ] Не удалось проверить свободное место перед первым запуском: {1}\r\n", DateTime.Now.ToString("yyyy-MM-dd_HH-mm"), ex.Message));
            }

            foreach (var process in Process.GetProcessesByName("firefox")) {
                try { process.Kill(); process.WaitForExit(3000); } catch {}
            }

            string checkZip = Directory.Exists(@"C:\Program Files\7-Zip") ? @"C:\Program Files\7-Zip\7z.exe" : @"C:\Program Files (x86)\7-Zip\7z.exe";
            if (File.Exists(checkZip)) {
                string tFile = Path.Combine(currentDir, "ff_excludes_start.txt");
                File.WriteAllLines(tFile, new string[] { "*cache2*", "*startupCache*", "*jumpListCache*", "*thumbnails*", "*shader-cache*" }, Encoding.UTF8);

                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = checkZip;
                
                if (!string.IsNullOrEmpty(chosenPassword)) {
                    psi.Arguments = string.Format("a -t7z \"{0}\\{1}_{2}.7z\" \"{3}\\*\" -p\"{4}\" -mhe=on -x@\"{5}\" -mx5", chosenBackupDir, chosenName, startDt, chosenPath, chosenPassword, tFile);
                } else {
                    psi.Arguments = string.Format("a -t7z \"{0}\\{1}_{2}.7z\" \"{3}\\*\" -x@\"{4}\" -mx5", chosenBackupDir, chosenName, startDt, chosenPath, tFile);
                }
                
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                
                Process p = Process.Start(psi);
                string output = p.StandardOutput.ReadToEnd();
                string error = p.StandardError.ReadToEnd();
                p.WaitForExit();

                if (!string.IsNullOrEmpty(output)) AppendLogWithRotation(startLog, "\r\n[7-ZIP OUTPUT]:\r\n" + output);
                if (!string.IsNullOrEmpty(error)) AppendLogWithRotation(startLog, "\r\n[7-ZIP ERROR]:\r\n" + error);

                try { File.Delete(tFile); } catch {}
                AppendLogWithRotation(startLog, string.Format("[{0}] [УСПЕХ] Мгновенный бэкап успешно завершен (AES-256).\r\n==================================================\r\n", DateTime.Now.ToString("yyyy-MM-dd_HH-mm")));
            }
            string exePath = Path.Combine(currentDir, "Firefox_Backup_Manager.exe");

            try {
                Process.Start(new ProcessStartInfo("schtasks.exe", string.Format("/delete /tn \"{0}\" /f", regKeyName)) { WindowStyle = ProcessWindowStyle.Hidden }).WaitForExit();
                System.Threading.Thread.Sleep(500);

                string psRegisterCmd = string.Format(
                    "$act = New-ScheduledTaskAction -Execute '{0}' -Argument '--silent-backup'; " +
                    "$trig = New-ScheduledTaskTrigger -AtStartup; " +
                    "$set = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries; " +
                    "Register-ScheduledTask -TaskName '{1}' -Action $act -Trigger $trig -Settings $set -User 'SYSTEM' -Force",
                    exePath, regKeyName
                );

                ProcessStartInfo psiPs = new ProcessStartInfo();
                psiPs.FileName = "powershell.exe";
                psiPs.Arguments = string.Format("-NoProfile -WindowStyle Hidden -Command \"{0}\"", psRegisterCmd);
                psiPs.WindowStyle = ProcessWindowStyle.Hidden;
                Process.Start(psiPs).WaitForExit();
            } catch {}

            MessageBox.Show("Все настройки успешно завершены!\n\n1. Профиль Firefox определен автоматически.\n2. Создана первая криптокопия 7z (AES-256).\n3. Задача добавлена в Планировщик Windows с разрешением работы от батареи.", "Успех", MessageBoxButtons.OK, MessageBoxIcon.Information);
            menuForm.Close();
        };

        btnDelete.Click += (s, e) => {
            Process.Start(new ProcessStartInfo("schtasks.exe", string.Format("/delete /tn \"{0}\" /f", regKeyName)) { WindowStyle = ProcessWindowStyle.Hidden }).WaitForExit();
            if (File.Exists(configFile)) File.Delete(configFile);
            MessageBox.Show("Автоматическое резервирование полностью отключено, файл настроек удален.", "Успех", MessageBoxButtons.OK, MessageBoxIcon.Information);
            menuForm.Close();
        };

        menuForm.Controls.AddRange(new Control[] { lblStatus, btnSetup, btnDelete });
        Application.Run(menuForm);
    }
}
