﻿using ServiceStack.Text;
using SharpCompress.Archive;
using SharpCompress.Common;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Windows;

namespace PSO2ModManager
{
    public class ModManager
    {
        public ObservableCollection<Mod> AvailableMods { get; private set; }
        public ObservableCollection<Mod> InstalledMods { get; private set; }
        public bool Downloading { get; set; } = false;
        public int DownloadPercent { get; set; } = 0;

        public Mod SelectedMod {
            get { return selectedMod; }
            set {
                selectedMod = value;
                if (OnSelectionChanged != null)
                    OnSelectionChanged();
            }
        }

        private Mod m;
        private Settings settings;
        private Mod selectedMod;
        private readonly string zipPath = AppDomain.CurrentDomain.BaseDirectory + "\\mods\\";
        private readonly string apiGetURL = "http://pso2mod.com/wp-json/wp/v2/posts/";

        public delegate void SelectedModChangedEventHandler();

        public event SelectedModChangedEventHandler OnSelectionChanged;

        public delegate void GenericErrorHandler(string message);

        public event GenericErrorHandler OnError;

        public delegate void DownloadCompleteEventHandler(bool success, string errorMessage = null);

        public event DownloadCompleteEventHandler OnDownloadComplete;

        public delegate void DownloadPercentChanged(int percent);

        public event DownloadPercentChanged OnDownloadPercentPercentChanged;

        public ModManager(string PSO2Dir = null) {
            if (!File.Exists(Settings.SettingsPath) || PSO2Dir != null) {
                settings = new Settings();
                settings.PSO2Dir = PSO2Dir;
                settings.AvailableMods = new List<Mod>();
                settings.InstalledMods = new List<Mod>();
            } else {
                settings = JsonSerializer.DeserializeFromString<Settings>(File.ReadAllText(Settings.SettingsPath));
            }
            if (!Directory.Exists(settings.PSO2Dir)) {
                Environment.Exit(0);
            }
            AvailableMods = new ObservableCollection<Mod>(settings.AvailableMods);
            InstalledMods = new ObservableCollection<Mod>(settings.InstalledMods);
            this.OnDownloadComplete += AfterDownload;
            UpdateSettings();
            CheckBrokenMods();
        }

        /// <summary>
        /// Checks that settings file is correct
        /// </summary>
        public static bool CheckForSettings() {
            if (!File.Exists(Settings.SettingsPath)) {
                return false;
            } else {
                var testSettings = JsonSerializer.DeserializeFromString<Settings>(File.ReadAllText(Settings.SettingsPath));
                return testSettings.IsValid();
            }
        }

        /// <summary>
        /// From a provided REST API endpoint download a mod.
        /// </summary>
        public async void DownloadMod(string url) {
            Downloading = true;
            string modZipPath;
            string modImagePath;
            string modExtractPath;
            string modString = string.Empty;
            using (WebClient wc = new WebClient()) {
                wc.DownloadProgressChanged += WCDownloadPercentChanged;
                try {
                    modString = await wc.DownloadStringTaskAsync(url);
                } catch (WebException ex) {
                    if (OnDownloadComplete != null) {
                        OnDownloadComplete(false, ex.Message);
                    }
                    return;
                }
                JsonObject json = JsonObject.Parse(modString);
                // Let's make sure the slug doesn't have weird stuff.
                json["slug"] = string.Join(" ", json["slug"].Split(Path.GetInvalidFileNameChars().Concat(Path.GetInvalidPathChars()).ToArray()));
                if (json["compatible"] != "Yes") {
                    if (OnDownloadComplete != null) {
                        OnDownloadComplete(false, "Mod not compatible with mod tool");
                    }
                    return;
                }
                m = new Mod(
                    json["id"],
                    json.ArrayObjects("title")[0]["rendered"],
                    DateTime.ParseExact(json["modified"].Replace("T", " "), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    json.ArrayObjects("content")[0]["rendered"],
                    json["author_name"],
                    url,
                    json["File"],
                    json["slug"],
                    json["slug"] + ".jpg"
                    );
                modZipPath = zipPath + m.Slug + ".zip";
                modExtractPath = Mod.InstallPath + m.Slug + "\\";
                modImagePath = Mod.ImagePath + m.Thumbnail;

                // Cleanup previous versions and files.
                Delete(m.Slug);
                if (File.Exists(modZipPath)) {
                    File.Delete(modZipPath);
                }
                if (Directory.Exists(modExtractPath)) {
                    Directory.Delete(modExtractPath, true);
                }
                if (File.Exists(modImagePath)) {
                    File.Delete(modImagePath);
                }

                // And start downloading stuff.
                try {
                    await wc.DownloadFileTaskAsync(new System.Uri(json["image"]), modImagePath);
                } catch (WebException we) {
                    if (OnDownloadComplete != null) {
                        OnDownloadComplete(false, we.Message);
                    }
                    return;
                }
                try {
                    await wc.DownloadFileTaskAsync(new System.Uri(m.File), modZipPath);
                } catch (WebException we) {
                    if (OnDownloadComplete != null) {
                        OnDownloadComplete(false, we.Message);
                    }
                    return;
                }

                using (var archive = ArchiveFactory.Open(modZipPath)) {
                    foreach (var entry in archive.Entries) {
                        entry.WriteToDirectory(modExtractPath, ExtractOptions.ExtractFullPath | ExtractOptions.Overwrite);
                    }
                }

                // Because some people like to package the files inside a folder, let's fix this.
                if (Directory.GetFiles(modExtractPath).Count() == 0 && Directory.GetDirectories(modExtractPath).Count() == 1) {
                    var dir = Directory.GetDirectories(modExtractPath).First();
                    var files = Directory.GetFiles(dir);
                    var dirs = Directory.GetDirectories(dir);
                    foreach (var f in files) {
                        File.Move(f, modExtractPath + "\\" + Path.GetFileName(f));
                    }
                    foreach (var d in dirs) {
                        Directory.Move(d, modExtractPath + "\\" + Path.GetFileName(d));
                    }
                    Directory.Delete(dir);
                }

                File.Delete(modZipPath);
                AvailableMods.Add(m);
                if (OnDownloadComplete != null) {
                    OnDownloadComplete(true);
                }
                UpdateSettings();
            }
        }

        /// <summary>
        /// Even hook so when the web client progress changes the mod manager
        /// progress updates accordingly
        /// </summary>
        private void WCDownloadPercentChanged(object sender, DownloadProgressChangedEventArgs e) {
            if (OnDownloadPercentPercentChanged != null) {
                OnDownloadPercentPercentChanged(e.ProgressPercentage);
            }
        }

        /// <summary>
        /// Event hook to make sure that downloding is false after download finishes
        /// </summary>
        private void AfterDownload(bool result, string message = null) {
            Downloading = false;
        }

        /// <summary>
        /// Returns true if the mod is installed
        /// </summary>
        public bool IsInstalled(Mod m) {
            return InstalledMods.Contains(m);
        }

        /// <summary>
        /// Installs/Uninstalls the selected mod
        /// </summary>
        public void ToggleMod() {
            if (AvailableMods.Where(x => x.Slug == SelectedMod.Slug).Count() != 0) {
                Install(SelectedMod);
            } else if (InstalledMods.Where(x => x.Slug == SelectedMod.Slug).Count() != 0) {
                Uninstall(SelectedMod);
            }
        }

        /// <summary>
        /// Saves the settings file.
        /// </summary>
        private void UpdateSettings() {
            settings.AvailableMods = AvailableMods.ToList();
            settings.InstalledMods = InstalledMods.ToList();
            File.WriteAllText(Settings.SettingsPath, JsonSerializer.SerializeToString(settings));
        }

        /// <summary>
        /// Checks all installed mods to see if they got broken by an update.
        /// </summary>
        private void CheckBrokenMods() {
            List<Mod> brokenMods = new List<Mod>();
            foreach (Mod m in InstalledMods) {
                if (m.ContentsMD5 != null) {
                    foreach (KeyValuePair<string, string> ice in m.ContentsMD5) {
                        if (Helpers.CheckMD5(settings.PSO2Dir + "\\" + ice.Key) != ice.Value) {
                            brokenMods.Add(m);
                            if (m.ToolInfo == null) {
                                m.ToolInfo = Mod.ModBrokenMessage;
                            } else {
                                m.ToolInfo += Mod.ModBrokenMessage;
                            }
                            break;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Checks for updates on all mods.
        /// </summary>
        public async Task<bool> CheckForUpdates() {
            bool res = true;
            foreach (Mod m in AvailableMods.Concat(InstalledMods)) {
                res = await CheckForUpdates(m);
                if (!res) {
                    break;
                }
            }
            return res;
        }

        /// <summary>
        /// Checks for updates on a mod.
        /// </summary>
        private async Task<bool> CheckForUpdates(Mod m) {
            m.Busy = true;
            string modString = string.Empty;
            using (WebClient wc = new WebClient()) {
                try {
                    modString = await wc.DownloadStringTaskAsync(apiGetURL + m.Id);
                } catch (WebException ex) {
                    if (OnError != null) {
                        OnError(m.Name + "error:" + ex.Message);
                    }
                    m.Busy = false;
                    return true;
                }
                JsonObject json = JsonObject.Parse(modString);
                var updated = DateTime.ParseExact(json["modified"].Replace("T", " "), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                if (updated > m.Date) {
                    m.UpdateAvailable = true;
                    UpdateSettings();
                }
            }
            m.Busy = false;
            return true;
        }

        /// <summary>
        /// Updates the selected mod.
        /// </summary
        public void UpdateMod() {
            UpdateMod(SelectedMod);
        }

        /// <summary>
        /// Checks for updates on a mod.
        /// </summary
        public void UpdateMod(Mod m) {
            DownloadMod(apiGetURL + m.Id);
        }

        /// <summary>
        /// Installs a mod into the game.
        /// </summary>
        public void Install(Mod m) {
            string mPath = Mod.InstallPath + m.Slug;
            string mBackupPath = Mod.BackupPath + m.Slug;
            if (!Directory.Exists(mBackupPath)) {
                Directory.CreateDirectory(mBackupPath);
            }
            // Before installing get the list of affected ice files and make sure no installed
            // mod uses the same files
            if (!ModCollision(m)) {
                m.ContentsMD5 = new Dictionary<string, string>();
                foreach (var f in Directory.GetFiles(mPath)) {
                    var fileName = Path.GetFileName(f);
                    if (!Mod.ModSettingsFiles.Contains(fileName)) {
                        File.Copy(settings.PSO2Dir + "\\" + Path.GetFileName(f), mBackupPath + "\\" + fileName, true);
                        File.Copy(mPath + "\\" + Path.GetFileName(f), settings.PSO2Dir + "\\" + fileName, true);
                        // This is done here because some mods will have dynamic/setup content so we create
                        // the md5 hash over the file we copy on the pso2 dir
                        m.ContentsMD5.Add(Path.GetFileName(f), Helpers.CheckMD5(settings.PSO2Dir + "\\" + fileName));
                    }
                }
                InstalledMods.Add(m);
                AvailableMods.Remove(m);
                UpdateSettings();
            } else {
                MessageBox.Show("Another installed mod is using the same files this mod will try to overwrite. Installation cancelled",
                                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Checks if the mod collisions with any other installed mod.
        /// </summary>
        public bool ModCollision(Mod m) {
            string mPath = Mod.InstallPath + m.Slug;
            var files = Directory.GetFiles(mPath).Select(x => Path.GetFileName(x));
            var collision = InstalledMods.SelectMany(x => Directory.GetFiles(Mod.InstallPath + x.Slug).Select(y => Path.GetFileName(y))).Intersect(files);
            if (collision.Count() != 0) {
                return true;
            }
            return false;
        }

        /// <summary>
        /// Uninstalls a game mod.
        /// </summary>
        public void Uninstall(Mod m) {
            string modPath = AppDomain.CurrentDomain.BaseDirectory + "\\mods\\" + m.Slug;
            string backupPath = AppDomain.CurrentDomain.BaseDirectory + "\\backups\\" + m.Slug;
            if (Directory.Exists(backupPath)) {
                foreach (var f in Directory.GetFiles(modPath)) {
                    File.Copy(backupPath + "\\" + Path.GetFileName(f), settings.PSO2Dir + "\\" + Path.GetFileName(f), true);
                }
                Directory.Delete(backupPath, true);
            } else {
                MessageBox.Show("The backup is missing or something... mod won't really uninstall, do a file check plz");
            }
            if (m.ToolInfo == null)
                m.ToolInfo = "";
            m.ToolInfo.Replace(Mod.ModBrokenMessage, "");
            InstalledMods.Remove(m);
            AvailableMods.Add(m);
            UpdateSettings();
        }

        /// <summary>
        /// Removes the selected mod.
        /// </summary>
        public void Delete() {
            Delete(this.SelectedMod);
            this.SelectedMod = null;
        }

        /// <summary>
        /// Deletes any mod that has the same slug.
        /// </summary>
        public void Delete(string slug) {
            var available = AvailableMods.Where(x => x.Slug == slug);
            var installed = InstalledMods.Where(x => x.Slug == slug);
            if (available.Count() != 0) {
                Delete(available.First());
            }
            if (installed.Count() != 0) {
                Delete(installed.First());
            }
        }

        /// <summary>
        /// Deletes a mod.
        /// </summary>
        public void Delete(Mod m) {
            string modPath = Mod.InstallPath + m.Slug;
            string imagePath = Mod.ImagePath + m.Thumbnail;
            if (InstalledMods.Where(x => x.Slug == m.Slug).Count() != 0) {
                Uninstall(SelectedMod);
            }
            AvailableMods.Remove(m);
            UpdateSettings();
            Directory.Delete(modPath, true);
            File.Delete(m.Thumbnail);
        }
    }
}