using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SalsaNOW
{
    // One backed-up folder. Stored in the repo as "<slot name>/slot.json"; its zips are the assets of
    // the release tagged with Tag, newest first.
    internal class BackupSlot
    {
        [JsonProperty("name")] public string Name;
        [JsonProperty("folder")] public string Folder;          // portable form, e.g. %APPDATA%\Something\remote
        [JsonProperty("autoBackup")] public bool AutoBackup;
        [JsonProperty("tag")] public string Tag;
        [JsonProperty("lastBackupUtc")] public DateTime? LastBackupUtc;
        [JsonProperty("lastBackupBytes")] public long LastBackupBytes;
        [JsonProperty("lastBackupFile")] public string LastBackupFile;

        // This session only
        [JsonIgnore] public string RepoFolder;
        // Set once the slot was restored or backed up in this session. Until then automatic backup leaves it
        // alone, so a fresh (just reinstalled) folder can never become the newest backup by itself.
        [JsonIgnore] public string SyncedFingerprint;
        [JsonIgnore] public DateTime NextAutoCheckUtc;
        [JsonIgnore] public string Status = "";

        public string FullPath { get { return BackupEngine.ExpandPath(Folder); } }
    }

    internal class FolderScan
    {
        public string Fingerprint;
        public int FileCount;
        public long TotalBytes;
        public DateTime NewestWriteUtc;
        public List<string> Files = new List<string>();       // relative paths
        public List<string> EmptyDirs = new List<string>();   // relative paths
    }

    // Fork-only: everything the Backups window does, without any UI.
    internal class BackupEngine
    {
        public const string RepoName = "salsanow-backups";
        public const int DefaultAutoMinutes = 10;
        private const int KeepAutoVersions = 30;
        private const int KeepManualVersions = 30;
        private const long MaxZipBytes = 2L * 1024 * 1024 * 1024 - 1;   // GitHub's limit per release file
        private const string SettingsPath = "settings.json";

        private readonly SemaphoreSlim _busy = new SemaphoreSlim(1, 1);
        private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "SalsaNOW-Backups");

        public GitHubBackupApi Api { get; private set; }
        public List<BackupSlot> Slots { get; private set; }
        public int AutoMinutes { get; private set; }

        public event Action<string> Log;
        public event Action<string, int> Progress;   // text, percent (-1 = busy without a percentage)
        public event Action Changed;

        public BackupEngine()
        {
            Slots = new List<BackupSlot>();
            AutoMinutes = DefaultAutoMinutes;
        }

        public bool IsConnected { get { return Api != null; } }

        // ---------- connecting ----------

        public async Task ConnectAsync(string token)
        {
            var api = new GitHubBackupApi(token);
            await api.ConnectAsync(RepoName);
            Api = api;
            await ReloadAsync();
            Write("Connected to " + api.RepoWebUrl);
        }

        public async Task ReloadAsync()
        {
            var slots = new List<BackupSlot>();
            foreach (string path in await Api.ListFilesAsync())
            {
                Match match = Regex.Match(path, @"^([^/]+)/slot\.json$");
                if (!match.Success)
                    continue;

                Tuple<string, string> file = await Api.GetFileAsync(path);
                if (file == null)
                    continue;
                try
                {
                    BackupSlot slot = JsonConvert.DeserializeObject<BackupSlot>(file.Item1);
                    slot.RepoFolder = match.Groups[1].Value;
                    BackupSlot known = Slots.FirstOrDefault(s => s.Tag == slot.Tag);
                    if (known != null)
                    {
                        slot.SyncedFingerprint = known.SyncedFingerprint;
                        slot.NextAutoCheckUtc = known.NextAutoCheckUtc;
                        slot.Status = known.Status;
                    }
                    slots.Add(slot);
                }
                catch (Exception ex)
                {
                    Write($"Skipping {path}: {ex.Message}");
                }
            }

            Slots = slots.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

            Tuple<string, string> settings = await Api.GetFileAsync(SettingsPath);
            if (settings != null)
            {
                try
                {
                    int minutes = (int?)Newtonsoft.Json.Linq.JObject.Parse(settings.Item1)["autoBackupMinutes"] ?? DefaultAutoMinutes;
                    AutoMinutes = Math.Max(1, Math.Min(240, minutes));
                }
                catch { }
            }
            OnChanged();
        }

        public async Task SetAutoMinutesAsync(int minutes)
        {
            AutoMinutes = Math.Max(1, Math.Min(240, minutes));
            foreach (BackupSlot slot in Slots)
                slot.NextAutoCheckUtc = DateTime.UtcNow.AddMinutes(AutoMinutes);
            if (Api != null)
                await Api.PutFileAsync(SettingsPath, new Newtonsoft.Json.Linq.JObject { ["autoBackupMinutes"] = AutoMinutes }.ToString(), "Auto backup every " + AutoMinutes + " minutes");
        }

        // ---------- slots ----------

        public async Task<BackupSlot> AddSlotAsync(string name, string folder, bool autoBackup)
        {
            name = name.Trim();
            string repoFolder = ToRepoFolder(name);
            if (Slots.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase) || string.Equals(s.RepoFolder, repoFolder, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("There is already a slot called \"" + name + "\".");
            if (repoFolder == SettingsPath)
                throw new InvalidOperationException("Please pick another name.");

            string tag = "slot-" + ToSlug(name);
            for (int i = 2; Slots.Any(s => s.Tag == tag) || await Api.FindReleaseAsync(tag) != null; i++)
                tag = "slot-" + ToSlug(name) + "-" + i;

            var slot = new BackupSlot
            {
                Name = name,
                Folder = ToPortablePath(folder),
                AutoBackup = autoBackup,
                Tag = tag,
                RepoFolder = repoFolder
            };

            await RunExclusiveAsync("Creating slot " + name, () => SaveSlotAsync(slot, "Add slot " + name));
            Slots.Add(slot);
            Slots = Slots.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            OnChanged();
            return slot;
        }

        public async Task UpdateSlotAsync(BackupSlot slot, string folder, bool autoBackup)
        {
            string portable = ToPortablePath(folder);
            if (!string.Equals(portable, slot.Folder, StringComparison.OrdinalIgnoreCase))
                slot.SyncedFingerprint = null;   // a different folder: it has to be synced or backed up first
            slot.Folder = portable;
            slot.AutoBackup = autoBackup;
            await RunExclusiveAsync("Saving slot " + slot.Name, () => SaveSlotAsync(slot, "Edit slot " + slot.Name));
            OnChanged();
        }

        public async Task DeleteSlotAsync(BackupSlot slot)
        {
            await RunExclusiveAsync("Deleting slot " + slot.Name, async () =>
            {
                await Api.DeleteReleaseAndTagAsync(slot.Tag);
                await Api.DeleteFileAsync(slot.RepoFolder + "/slot.json", "Delete slot " + slot.Name);
            });
            Slots.Remove(slot);
            Write("Deleted slot " + slot.Name + " and all of its backups.");
            OnChanged();
        }

        private Task SaveSlotAsync(BackupSlot slot, string message)
        {
            return Api.PutFileAsync(slot.RepoFolder + "/slot.json", JsonConvert.SerializeObject(slot, Formatting.Indented), message);
        }

        public async Task<List<ReleaseAsset>> ListVersionsAsync(BackupSlot slot)
        {
            long? release = await Api.FindReleaseAsync(slot.Tag);
            return release.HasValue
                ? (await Api.ListAssetsAsync(release.Value)).Where(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)).ToList()
                : new List<ReleaseAsset>();
        }

        public static bool IsAutoVersion(ReleaseAsset asset)
        {
            return asset.Name.EndsWith("_auto.zip", StringComparison.OrdinalIgnoreCase);
        }

        // ---------- back up ----------

        public Task BackupAsync(BackupSlot slot)
        {
            return RunExclusiveAsync("Backing up " + slot.Name, () => BackupCoreAsync(slot, manual: true));
        }

        private async Task BackupCoreAsync(BackupSlot slot, bool manual)
        {
            string folder = slot.FullPath;
            if (!Directory.Exists(folder))
                throw new InvalidOperationException("The folder does not exist: " + folder);

            Directory.CreateDirectory(_tempDir);
            string zip = Path.Combine(_tempDir, slot.Tag + ".zip");
            FolderScan scan = null;

            try
            {
                // A file changing while it is zipped could end up half-written in the zip, so zip again until
                // nothing changed in between. A manual backup goes ahead after three tries, with a warning.
                for (int attempt = 1; ; attempt++)
                {
                    scan = ScanFolder(folder);
                    if (!manual && scan.FileCount == 0)
                        throw new InvalidOperationException("The folder is empty, so it was not backed up automatically.");

                    SetStatus(slot, "Zipping...");
                    Report("Zipping " + slot.Name, -1);
                    await Task.Run(() => CreateZip(folder, scan, zip));

                    if (ScanFolder(folder).Fingerprint == scan.Fingerprint)
                        break;
                    if (attempt == 3)
                    {
                        if (!manual)
                            throw new InvalidOperationException("Files kept changing while zipping; trying again later.");
                        Write($"Warning: files in {slot.Name} kept changing while zipping. Backed up anyway; back up again once the program using them is closed.");
                        break;
                    }
                    await Task.Delay(3000);
                }

                long size = new FileInfo(zip).Length;
                if (size > MaxZipBytes)
                    throw new InvalidOperationException($"The zip is {FormatBytes(size)}; GitHub accepts at most 2 GB per file. Pick a smaller folder.");

                DateTime now = DateTime.UtcNow;
                string assetName = $"{ToSlug(slot.Name)}_{now:yyyyMMdd-HHmmss}_{(manual ? "manual" : "auto")}.zip";
                long release = await Api.GetOrCreateReleaseAsync(slot.Tag, slot.Name);

                SetStatus(slot, "Uploading...");
                await Api.UploadAssetAsync(release, zip, assetName, (done, total) =>
                    Report($"Uploading {slot.Name}: {FormatBytes(done)} of {FormatBytes(total)}", total > 0 ? (int)(done * 100 / total) : -1));

                slot.LastBackupUtc = now;
                slot.LastBackupBytes = size;
                slot.LastBackupFile = assetName;
                slot.SyncedFingerprint = scan.Fingerprint;
                await SaveSlotAsync(slot, $"Back up {slot.Name} ({FormatBytes(size)}, {(manual ? "manual" : "auto")})");

                SetStatus(slot, (manual ? "Backed up " : "Auto backed up ") + DateTime.Now.ToString("HH:mm"));
                Write($"Backed up {slot.Name}: {scan.FileCount} files, {FormatBytes(size)} zipped.");

                await PruneAsync(release);
            }
            finally
            {
                TryDelete(zip);
            }
        }

        // Keeps the newest KeepAutoVersions automatic and KeepManualVersions manual backups of a slot
        private async Task PruneAsync(long release)
        {
            try
            {
                List<ReleaseAsset> assets = await Api.ListAssetsAsync(release);
                IEnumerable<ReleaseAsset> extra = assets.Where(IsAutoVersion).Skip(KeepAutoVersions)
                    .Concat(assets.Where(a => !IsAutoVersion(a)).Skip(KeepManualVersions));
                foreach (ReleaseAsset asset in extra.ToList())
                    await Api.DeleteAssetAsync(asset.Id);
            }
            catch (Exception ex)
            {
                Write("Could not remove old backups: " + ex.Message);
            }
        }

        // ---------- restore ----------

        public Task RestoreAsync(BackupSlot slot, ReleaseAsset version, string target)
        {
            return RunExclusiveAsync("Syncing " + slot.Name, () => RestoreCoreAsync(slot, version, Path.GetFullPath(target)));
        }

        private async Task RestoreCoreAsync(BackupSlot slot, ReleaseAsset version, string target)
        {
            Directory.CreateDirectory(_tempDir);
            string zip = Path.Combine(_tempDir, slot.Tag + "-restore.zip");
            string staging = target.TrimEnd('\\') + ".salsanow-restoring";

            try
            {
                SetStatus(slot, "Downloading...");
                await Api.DownloadAssetAsync(version.Id, zip, (done, total) =>
                    Report($"Downloading {slot.Name}: {FormatBytes(done)} of {FormatBytes(total > 0 ? total : version.Size)}", (int)(done * 100 / Math.Max(1, total > 0 ? total : version.Size))));

                SetStatus(slot, "Unzipping...");
                Report("Unzipping " + slot.Name, -1);
                string parent = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(parent))
                    Directory.CreateDirectory(parent);
                DeleteDirectory(staging);
                await Task.Run(() => ExtractZip(zip, staging));

                string aside = await Task.Run(() => SwapIn(staging, target));
                if (aside != null)
                    Write($"Your previous {Path.GetFileName(target)} folder was kept as {aside}");

                if (string.Equals(target.TrimEnd('\\'), slot.FullPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                {
                    slot.SyncedFingerprint = ScanFolder(target).Fingerprint;
                    slot.NextAutoCheckUtc = DateTime.UtcNow.AddMinutes(AutoMinutes);
                }

                SetStatus(slot, "Synced " + DateTime.Now.ToString("HH:mm"));
                Write($"Synced {slot.Name} ({version.CreatedUtc.ToLocalTime():g} backup) into {target}");
            }
            finally
            {
                TryDelete(zip);
                try { DeleteDirectory(staging); } catch { }
            }
        }

        // Puts the unzipped folder at target. Whatever was there is kept next to it as
        // "<name>_before_restore_<time>" (an empty folder is simply removed). Returns that path, or null.
        private static string SwapIn(string staging, string target)
        {
            string aside = target.TrimEnd('\\') + "_before_restore_" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var info = new DirectoryInfo(target);

            if (!info.Exists)
            {
                Directory.Move(staging, target);
                return null;
            }

            bool isEmpty = !info.EnumerateFileSystemInfos().Any();

            // A junction (a folder that really lives elsewhere) stays in place; only its contents are swapped
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                if (!isEmpty)
                    CopyDirectory(target, aside);
                foreach (FileSystemInfo item in info.EnumerateFileSystemInfos())
                {
                    if (item is DirectoryInfo dir) dir.Delete(true); else item.Delete();
                }
                CopyDirectory(staging, target);
                return isEmpty ? null : aside;
            }

            if (isEmpty)
            {
                Directory.Delete(target);
                Directory.Move(staging, target);
                return null;
            }

            try
            {
                Directory.Move(target, aside);
            }
            catch (IOException ex)
            {
                throw new InvalidOperationException("Something is still using files in " + target + ". Close the game or program using it and try again. (" + ex.Message + ")");
            }

            try
            {
                Directory.Move(staging, target);
            }
            catch
            {
                Directory.Move(aside, target);
                throw;
            }
            return aside;
        }

        // ---------- automatic backup ----------

        // Called every minute. Backs up slots marked for automatic backup whose files changed since they were
        // last synced or backed up in this session.
        public async Task AutoTickAsync()
        {
            if (Api == null || !await _busy.WaitAsync(0))
                return;

            try
            {
                foreach (BackupSlot slot in Slots.Where(s => s.AutoBackup).ToList())
                {
                    if (slot.SyncedFingerprint == null)
                    {
                        SetStatus(slot, "Auto backup starts after Sync or Back up");
                        continue;
                    }
                    if (DateTime.UtcNow < slot.NextAutoCheckUtc)
                        continue;

                    try
                    {
                        if (!Directory.Exists(slot.FullPath))
                        {
                            SetStatus(slot, "Folder missing");
                            slot.NextAutoCheckUtc = DateTime.UtcNow.AddMinutes(AutoMinutes);
                            continue;
                        }

                        FolderScan scan = ScanFolder(slot.FullPath);
                        if (scan.Fingerprint == slot.SyncedFingerprint)
                        {
                            SetStatus(slot, "No changes (checked " + DateTime.Now.ToString("HH:mm") + ")");
                            slot.NextAutoCheckUtc = DateTime.UtcNow.AddMinutes(AutoMinutes);
                            continue;
                        }

                        // Still being written: wait for a quiet moment
                        if (DateTime.UtcNow - scan.NewestWriteUtc < TimeSpan.FromSeconds(20))
                        {
                            SetStatus(slot, "Files are changing, retrying in a minute");
                            slot.NextAutoCheckUtc = DateTime.UtcNow.AddMinutes(1);
                            continue;
                        }

                        await BackupCoreAsync(slot, manual: false);
                        slot.NextAutoCheckUtc = DateTime.UtcNow.AddMinutes(AutoMinutes);
                    }
                    catch (Exception ex)
                    {
                        SetStatus(slot, "Auto backup failed, retrying in 2 min");
                        Write($"Auto backup of {slot.Name} failed: {ex.Message}");
                        slot.NextAutoCheckUtc = DateTime.UtcNow.AddMinutes(2);
                    }
                }
            }
            finally
            {
                Report("", 0);
                _busy.Release();
                OnChanged();
            }
        }

        // ---------- folders and zips ----------

        public static FolderScan ScanFolder(string root)
        {
            var scan = new FolderScan();
            var entries = new List<string>();
            var stack = new Stack<string>();
            stack.Push(root);

            while (stack.Count > 0)
            {
                string dir = stack.Pop();
                string[] files = Directory.GetFiles(dir);
                string[] dirs = Directory.GetDirectories(dir);
                string relDir = Relative(root, dir);

                if (files.Length == 0 && dirs.Length == 0 && relDir.Length > 0)
                {
                    scan.EmptyDirs.Add(relDir);
                    entries.Add("D|" + relDir);
                }

                foreach (string file in files)
                {
                    var info = new FileInfo(file);
                    string rel = Relative(root, file);
                    scan.Files.Add(rel);
                    scan.FileCount++;
                    scan.TotalBytes += info.Length;
                    if (info.LastWriteTimeUtc > scan.NewestWriteUtc)
                        scan.NewestWriteUtc = info.LastWriteTimeUtc;
                    entries.Add($"F|{rel}|{info.Length}|{info.LastWriteTimeUtc.Ticks}");
                }

                foreach (string sub in dirs)
                {
                    // Links inside the folder are not followed, so a link pointing back up can't loop forever
                    if (new DirectoryInfo(sub).Attributes.HasFlag(FileAttributes.ReparsePoint))
                        continue;
                    stack.Push(sub);
                }
            }

            entries.Sort(StringComparer.OrdinalIgnoreCase);
            using (var sha = SHA256.Create())
                scan.Fingerprint = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", entries)))).Replace("-", "");
            return scan;
        }

        private static void CreateZip(string root, FolderScan scan, string zipPath)
        {
            TryDelete(zipPath);
            using (var zipStream = new FileStream(zipPath, FileMode.Create, FileAccess.ReadWrite))
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
            {
                foreach (string dir in scan.EmptyDirs)
                    archive.CreateEntry(dir.Replace('\\', '/') + "/");

                foreach (string rel in scan.Files)
                {
                    string file = Path.Combine(root, rel);
                    ZipArchiveEntry entry = archive.CreateEntry(rel.Replace('\\', '/'), CompressionLevel.Optimal);
                    DateTime written = File.GetLastWriteTime(file);
                    if (written.Year >= 1980 && written.Year <= 2107)
                        entry.LastWriteTime = written;

                    using (Stream input = OpenShared(file))
                    using (Stream output = entry.Open())
                        input.CopyTo(output);
                }
            }
        }

        // Opens a file even while a program has it open, retrying a few times if it is locked
        private static Stream OpenShared(string file)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    return new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                }
                catch (IOException) when (attempt < 5 && File.Exists(file))
                {
                    Thread.Sleep(1000);
                }
                catch (IOException ex)
                {
                    throw new InvalidOperationException("Could not read " + file + " (is a program locking it?): " + ex.Message);
                }
            }
        }

        private static void ExtractZip(string zipPath, string destination)
        {
            string root = Path.GetFullPath(destination).TrimEnd('\\') + "\\";
            Directory.CreateDirectory(root);

            using (ZipArchive archive = ZipFile.OpenRead(zipPath))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    string path = Path.GetFullPath(Path.Combine(root, entry.FullName.Replace('/', '\\')));
                    if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("The backup contains an unsafe path: " + entry.FullName);

                    if (entry.FullName.EndsWith("/"))
                    {
                        Directory.CreateDirectory(path);
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    entry.ExtractToFile(path, true);
                }
            }
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (string file in Directory.GetFiles(source))
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
            foreach (string dir in Directory.GetDirectories(source))
                CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
        }

        // ---------- paths and names ----------

        private static readonly string[] PortableVariables = { "LOCALAPPDATA", "APPDATA", "USERPROFILE", "PUBLIC", "PROGRAMDATA" };

        // C:\Users\kiosk\AppData\Roaming\X -> %APPDATA%\X, so a slot still works if the user name changes
        public static string ToPortablePath(string path)
        {
            string full = Path.GetFullPath(path).TrimEnd('\\');
            foreach (string name in PortableVariables
                .Select(v => new { Name = v, Value = (Environment.GetEnvironmentVariable(v) ?? "").TrimEnd('\\') })
                .Where(v => v.Value.Length > 0)
                .OrderByDescending(v => v.Value.Length)
                .Select(v => v.Name))
            {
                string value = Environment.GetEnvironmentVariable(name).TrimEnd('\\');
                if (string.Equals(full, value, StringComparison.OrdinalIgnoreCase))
                    return "%" + name + "%";
                if (full.StartsWith(value + "\\", StringComparison.OrdinalIgnoreCase))
                    return "%" + name + "%" + full.Substring(value.Length);
            }
            return full;
        }

        public static string ExpandPath(string path)
        {
            return Environment.ExpandEnvironmentVariables(path ?? "");
        }

        private static string ToRepoFolder(string name)
        {
            var invalid = new HashSet<char>(Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }));
            string folder = new string(name.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
            if (folder.Length == 0 || folder.StartsWith("."))
                folder = "slot " + folder.TrimStart('.');
            return folder;
        }

        private static string ToSlug(string name)
        {
            string slug = Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
            if (slug.Length > 40)
                slug = slug.Substring(0, 40).Trim('-');
            return slug.Length == 0 ? "slot" : slug;
        }

        private static string Relative(string root, string path)
        {
            return path.Length <= root.TrimEnd('\\').Length ? "" : path.Substring(root.TrimEnd('\\').Length + 1);
        }

        public static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.#") + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / (1024.0 * 1024)).ToString("0.#") + " MB";
            return (bytes / (1024.0 * 1024 * 1024)).ToString("0.##") + " GB";
        }

        // ---------- helpers ----------

        private async Task RunExclusiveAsync(string what, Func<Task> action)
        {
            if (Api == null)
                throw new InvalidOperationException("Log in to GitHub first.");

            if (!await _busy.WaitAsync(0))
            {
                Report(what + " (waiting for the current task to finish)", -1);
                await _busy.WaitAsync();
            }

            try
            {
                Report(what, -1);
                await action();
            }
            finally
            {
                Report("", 0);
                _busy.Release();
                OnChanged();
            }
        }

        private void SetStatus(BackupSlot slot, string status)
        {
            slot.Status = status;
            OnChanged();
        }

        private void Write(string message) { Log?.Invoke(message); }
        private void Report(string text, int percent) { Progress?.Invoke(text, percent); }
        private void OnChanged() { Changed?.Invoke(); }

        private static void DeleteDirectory(string path)
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }

        private static void TryDelete(string file)
        {
            try { if (File.Exists(file)) File.Delete(file); } catch { }
        }
    }
}
