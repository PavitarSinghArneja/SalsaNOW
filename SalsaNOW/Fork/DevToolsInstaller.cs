using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SalsaNOW
{
    // Fork-only feature: installs portable Node.js, OpenCode and Git under the SalsaNOW folder on
    // every launch, so they are back even when the whole disk was reset since the last session.
    // It also opens the Backups app (Fork\FolderBackups), which keeps chosen folders backed up on GitHub.
    //
    // Kept deliberately self-contained so merges from the original SalsaNOW never touch it: it uses
    // no other SalsaNOW class outside Fork, and the only hooks into the original code are two lines in
    // Program.cs (plus <Compile> lines in SalsaNOW.csproj), all re-added by .github/scripts/apply-fork-hooks.sh.
    internal static class DevToolsInstaller
    {
        private const string NodeIndexUrl = "https://nodejs.org/dist/index.json";
        private const string NodeFallbackVersion = "v22.12.0";
        private const string GitReleaseApi = "https://api.github.com/repos/git-for-windows/git/releases/latest";
        private const string GitFallbackUrl = "https://github.com/git-for-windows/git/releases/download/v2.47.1.windows.1/PortableGit-2.47.1-64-bit.7z.exe";
        private const string OpenCodePackage = "opencode-ai";

        private static string _logFile;

        public static async Task InstallAsync(string globalDirectory)
        {
            string devRoot = Path.Combine(globalDirectory, "DevTools");
            string nodeDir = Path.Combine(devRoot, "node");
            string npmPrefix = Path.Combine(devRoot, "npm-global");
            string gitDir = Path.Combine(devRoot, "git");
            string gitCmd = Path.Combine(gitDir, "cmd");
            string ghBin = GhCli.BinDir(devRoot);
            string workDir = Path.Combine(Path.GetPathRoot(globalDirectory), "Work");

            // Runs before the first await, so it finishes before SalsaNOW's own desktop setup picks a wallpaper
            PlaceWallpaper(globalDirectory);

            try
            {
                Directory.CreateDirectory(devRoot);
                Directory.CreateDirectory(npmPrefix);
                Directory.CreateDirectory(workDir);
                _logFile = Path.Combine(devRoot, "devtools.log");
            }
            catch (Exception ex)
            {
                Log("DevTools folder setup failed: " + ex.Message);
                return;
            }

            string[] pathDirs = { nodeDir, npmPrefix, gitCmd, ghBin };

            // If SalsaNOW ever crashes, the reason ends up in devtools.log
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log("SalsaNOW crashed: " + e.ExceptionObject);
            Log($"SalsaNOW started (process {Process.GetCurrentProcess().Id}).");

            // Each tool is independent and starts right away: one failing or hanging never holds up the others.
            Task node = Task.Run(() => RunStep("Node.js", async () =>
            {
                if (!File.Exists(Path.Combine(nodeDir, "node.exe")))
                    await InstallNodeAsync(devRoot, nodeDir);
            }));

            Task openCode = Task.Run(async () =>
            {
                await node;
                await RunStep("OpenCode", async () =>
                {
                    if (File.Exists(Path.Combine(npmPrefix, "opencode.cmd")))
                        return;
                    if (!File.Exists(Path.Combine(nodeDir, "node.exe")))
                        throw new InvalidOperationException("Node.js is missing, skipping OpenCode.");
                    await InstallOpenCodeAsync(nodeDir, npmPrefix, gitCmd);
                });
            });

            Task git = Task.Run(async () =>
            {
                await RunStep("Git", async () =>
                {
                    if (!File.Exists(Path.Combine(gitCmd, "git.exe")))
                        await InstallGitAsync(devRoot, gitDir);
                });
            });

            StartShortcutWatcher(globalDirectory);

            // The Backups app opens on its own (it fetches the GitHub CLI itself and then asks for the login code)
            Task backups = Task.Run(() => RunStep("Backups app", () =>
            {
                if (BackupsApp.Start(devRoot))
                    CreateDesktopShortcut(globalDirectory, "Backups", BackupsApp.ExePath, devRoot, $"{BackupsApp.ShortcutArgument} \"{devRoot}\"", replace: true, icon: BackupsApp.WriteIconFile(devRoot));
                return Task.CompletedTask;
            }));

            // Set up front: folders that don't exist yet are harmless, and this way each tool works in new
            // terminals as soon as it is installed instead of only after all of them are.
            // The terminal startup lines come first: they are instant and are what makes new terminals on
            // GeForce NOW find the tools. The user PATH (registry) step can take a while and runs on its own.
            await RunStep("Terminal PATH", () =>
            {
                AddToTerminalStartup(pathDirs);
                return Task.CompletedTask;
            });
            Task paths = Task.Run(() => RunStep("PATH", () => AddToUserPathAsync(pathDirs)));

            await Task.WhenAll(node, openCode, git, backups, paths);
            Log("All setup steps finished.");
        }

        // ---------- Desktop shortcuts ----------

        // A plain thread, not the thread pool or the install steps: it creates the OpenCode and Git Bash shortcuts
        // as soon as their files exist, whatever happens to the rest of setup. It also logs once a minute, which
        // shows whether this SalsaNOW process is still running.
        private static void StartShortcutWatcher(string globalDirectory)
        {
            var thread = new Thread(() =>
            {
                DateTime started = DateTime.Now;
                DateTime nextAlive = started.AddMinutes(1);
                string lastError = null;
                while (DateTime.Now - started < TimeSpan.FromMinutes(45))
                {
                    try
                    {
                        if (EnsureShortcuts(globalDirectory))
                        {
                            Log("Desktop shortcuts: OpenCode and Git Bash are there.");
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        // Logged once per distinct error, not every 5 seconds
                        if (ex.Message != lastError)
                            Log("Desktop shortcuts failed: " + ex.Message);
                        lastError = ex.Message;
                    }

                    if (DateTime.Now >= nextAlive)
                    {
                        Log($"Still running (process {Process.GetCurrentProcess().Id}); waiting for Git Bash and OpenCode to finish installing...");
                        nextAlive = DateTime.Now.AddMinutes(1);
                    }
                    Thread.Sleep(5000);
                }
                Log("Gave up waiting for Git Bash and OpenCode after 45 minutes.");
            });
            thread.IsBackground = true;
            thread.Name = "DevTools shortcuts";
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        // Creates the OpenCode and Git Bash desktop shortcuts if their tools are installed and the shortcut is missing.
        // Returns true once both shortcuts exist (or were deliberately deleted by the user). Also used by the Backups
        // app as a fallback.
        private static readonly object ShortcutLock = new object();

        internal static bool EnsureShortcuts(string globalDirectory)
        {
            // The watcher thread and the Backups window both call this; one at a time
            lock (ShortcutLock)
                return EnsureShortcutsLocked(globalDirectory);
        }

        private static bool EnsureShortcutsLocked(string globalDirectory)
        {
            string devRoot = Path.Combine(globalDirectory, "DevTools");
            string workDir = Path.Combine(Path.GetPathRoot(globalDirectory), "Work");
            string gitBash = Path.Combine(devRoot, "git", "git-bash.exe");
            string npmPrefix = Path.Combine(devRoot, "npm-global");
            string[] pathDirs = { Path.Combine(devRoot, "node"), npmPrefix, Path.Combine(devRoot, "git", "cmd"), GhCli.BinDir(devRoot) };
            Directory.CreateDirectory(workDir);

            // git.exe is checked too, so the shortcut doesn't appear while Git is still being unpacked
            if (!ShortcutDone(globalDirectory, "Git Bash") && File.Exists(gitBash) && File.Exists(Path.Combine(devRoot, "git", "cmd", "git.exe")))
            {
                CreateDesktopShortcut(globalDirectory, "Git Bash", gitBash, workDir);
                Log("Created the Git Bash desktop shortcut.");
            }

            if (!ShortcutDone(globalDirectory, "OpenCode") && File.Exists(Path.Combine(npmPrefix, "opencode.cmd")))
            {
                CreateDesktopShortcut(globalDirectory, "OpenCode", WriteLauncher(devRoot, pathDirs, workDir), workDir);
                Log("Created the OpenCode desktop shortcut.");
            }

            return ShortcutDone(globalDirectory, "Git Bash") && ShortcutDone(globalDirectory, "OpenCode");
        }

        private static bool ShortcutDone(string globalDirectory, string name)
        {
            string fileName = name + ".lnk";
            return File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), fileName))
                || File.Exists(Path.Combine(globalDirectory, "Backup Shortcuts", fileName));
        }

        // Logs when a step starts, finishes or fails, and once a minute while it is still running,
        // so a step that hangs is visible in devtools.log.
        private static async Task RunStep(string name, Func<Task> step)
        {
            DateTime started = DateTime.Now;
            using (var stop = new CancellationTokenSource())
            {
                _ = HeartbeatAsync(name, started, stop.Token);
                try
                {
                    await step();
                    Log($"{name}: done ({Elapsed(started)}).");
                }
                catch (Exception ex)
                {
                    Log($"{name} setup failed after {Elapsed(started)}: {ex.Message}");
                }
                finally
                {
                    stop.Cancel();
                }
            }
        }

        private static async Task HeartbeatAsync(string name, DateTime started, CancellationToken token)
        {
            try
            {
                while (true)
                {
                    await Task.Delay(60 * 1000, token);
                    Log($"{name}: still working ({Elapsed(started)})...");
                }
            }
            catch (TaskCanceledException) { }
        }

        private static string Elapsed(DateTime started)
        {
            TimeSpan time = DateTime.Now - started;
            return time.TotalMinutes >= 1 ? $"{(int)time.TotalMinutes} min {time.Seconds} s" : $"{time.TotalSeconds:0.0} s";
        }

        // ---------- Wallpaper ----------

        // SalsaNOW uses the first picture in <SalsaNOW>\DesktopWallpaper as the desktop wallpaper.
        // Ours is built into the exe (Fork\Wallpaper.png) because the disk is reset between sessions.
        // Any other pictures already there are moved to DesktopWallpaper\Previous so ours is the one used.
        private static void PlaceWallpaper(string globalDirectory)
        {
            string[] imageExtensions = { ".bmp", ".jpg", ".jpeg", ".png", ".gif", ".tif", ".tiff", ".webp", ".jxr" };

            try
            {
                string wallpaperDir = Path.Combine(globalDirectory, "DesktopWallpaper");
                string target = Path.Combine(wallpaperDir, "SalsaNOWForkWallpaper.png");
                Directory.CreateDirectory(wallpaperDir);

                using (Stream resource = typeof(DevToolsInstaller).Assembly.GetManifestResourceStream("SalsaNOW.Fork.Wallpaper.png"))
                {
                    if (resource == null)
                        throw new InvalidOperationException("Wallpaper is missing from the exe.");

                    foreach (string file in Directory.GetFiles(wallpaperDir))
                    {
                        if (string.Equals(file, target, StringComparison.OrdinalIgnoreCase)
                            || !imageExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                            continue;

                        string previousDir = Path.Combine(wallpaperDir, "Previous");
                        Directory.CreateDirectory(previousDir);
                        string moved = Path.Combine(previousDir, Path.GetFileName(file));
                        if (File.Exists(moved))
                            File.Delete(moved);
                        File.Move(file, moved);
                    }

                    using (var output = new FileStream(target, FileMode.Create, FileAccess.Write))
                        resource.CopyTo(output);
                }
            }
            catch (Exception ex)
            {
                Log("Wallpaper setup failed: " + ex.Message);
            }
        }

        // ---------- Node.js ----------

        private static async Task InstallNodeAsync(string devRoot, string nodeDir)
        {
            string version = await GetLatestNodeLtsAsync();
            string folderName = $"node-{version}-win-x64";
            string zipPath = Path.Combine(devRoot, folderName + ".zip");
            string extracted = Path.Combine(devRoot, folderName);

            Log("Installing Node.js " + version);
            await DownloadAsync($"https://nodejs.org/dist/{version}/{folderName}.zip", zipPath);

            DeleteDirectory(extracted);
            DeleteDirectory(nodeDir);
            ZipFile.ExtractToDirectory(zipPath, devRoot);
            Directory.Move(extracted, nodeDir);
            File.Delete(zipPath);
        }

        private static async Task<string> GetLatestNodeLtsAsync()
        {
            try
            {
                string json = await DownloadStringAsync(NodeIndexUrl);

                // index.json is newest first; "lts" is false for Current releases and a codename for LTS ones
                foreach (Match release in Regex.Matches(json, @"\{[^{}]*\}"))
                {
                    string entry = release.Value;
                    if (!Regex.IsMatch(entry, @"""lts""\s*:\s*""") || !entry.Contains("\"win-x64-zip\""))
                        continue;

                    Match version = Regex.Match(entry, @"""version""\s*:\s*""(v[\d.]+)""");
                    if (version.Success)
                        return version.Groups[1].Value;
                }
            }
            catch (Exception ex)
            {
                Log("Could not look up the latest Node.js LTS: " + ex.Message);
            }

            return NodeFallbackVersion;
        }

        // ---------- Git (PortableGit, a self-extracting 7z archive that needs no admin) ----------

        private static async Task InstallGitAsync(string devRoot, string gitDir)
        {
            string url = await GetLatestPortableGitUrlAsync();
            string sfxPath = Path.Combine(devRoot, "PortableGit.7z.exe");

            Log("Installing Git from " + url);
            await DownloadAsync(url, sfxPath);

            DeleteDirectory(gitDir);
            using (Process process = Process.Start(new ProcessStartInfo
            {
                FileName = sfxPath,
                Arguments = $"-o\"{gitDir}\" -y",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            }))
            {
                if (process == null)
                    throw new InvalidOperationException("Could not start the Git extractor.");
                bool exited = await Task.Run(() => process.WaitForExit(10 * 60 * 1000));
                if (!exited)
                {
                    try { process.Kill(); } catch { }
                    throw new TimeoutException("The Git extractor did not finish within 10 minutes.");
                }
            }

            File.Delete(sfxPath);
            if (!File.Exists(Path.Combine(gitDir, "cmd", "git.exe")))
                throw new InvalidOperationException("git.exe not found after extracting.");
        }

        private static async Task<string> GetLatestPortableGitUrlAsync()
        {
            try
            {
                string json = await DownloadStringAsync(GitReleaseApi);
                Match match = Regex.Match(json, @"""browser_download_url""\s*:\s*""(https://[^""]+/PortableGit-[\d.]+-64-bit\.7z\.exe)""");
                if (match.Success)
                    return match.Groups[1].Value;
            }
            catch (Exception ex)
            {
                Log("Could not look up the latest Git release: " + ex.Message);
            }

            return GitFallbackUrl;
        }

        // ---------- OpenCode ----------

        private static async Task InstallOpenCodeAsync(string nodeDir, string npmPrefix, string gitCmd)
        {
            Log("Installing OpenCode with npm...");

            string npmCmd = Path.Combine(nodeDir, "npm.cmd");
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/d /s /c \"\"{npmCmd}\" install -g --prefix \"{npmPrefix}\" --allow-scripts={OpenCodePackage} {OpenCodePackage}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.EnvironmentVariables["PATH"] = string.Join(";", nodeDir, npmPrefix, gitCmd, Environment.GetEnvironmentVariable("PATH"));

            using (Process process = Process.Start(psi))
            {
                Task<string> stdout = process.StandardOutput.ReadToEndAsync();
                Task<string> stderr = process.StandardError.ReadToEndAsync();
                bool exited = await Task.Run(() => process.WaitForExit(20 * 60 * 1000));
                if (!exited)
                {
                    try { process.Kill(); } catch { }
                    throw new TimeoutException("npm did not finish within 20 minutes. Last output: " + Tail(stdout) + " " + Tail(stderr));
                }

                if (process.ExitCode != 0)
                    throw new InvalidOperationException($"npm exited with code {process.ExitCode}: {(await stderr).Trim()} {(await stdout).Trim()}");
            }
        }

        // The end of what a process printed so far, or "" if it is still writing
        private static string Tail(Task<string> output)
        {
            if (!output.IsCompleted || output.IsFaulted)
                return "";
            string text = output.Result.Trim();
            return text.Length > 300 ? "..." + text.Substring(text.Length - 300) : text;
        }

        // ---------- PATH, launcher and shortcut ----------

        // The original SalsaNOW rewrites the user PATH at startup too, so write, wait, re-check, and retry
        // in case the two writes overlapped and ours was lost.
        private static async Task AddToUserPathAsync(string[] directories)
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                List<string> missing;
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey("Environment"))
                {
                    string path = Convert.ToString(key.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames));
                    var entries = path.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim().TrimEnd('\\'));
                    missing = directories.Where(d => !entries.Contains(d.TrimEnd('\\'), StringComparer.OrdinalIgnoreCase)).ToList();

                    if (missing.Count == 0)
                    {
                        if (attempt > 0)
                            Log("User PATH updated.");
                        return;
                    }

                    string updated = path.TrimEnd(';');
                    foreach (string directory in missing)
                        updated = updated.Length == 0 ? directory : updated + ";" + directory;
                    key.SetValue("Path", updated, RegistryValueKind.ExpandString);
                }

                IntPtr result;
                SendMessageTimeout(new IntPtr(0xFFFF), 0x001A, IntPtr.Zero, "Environment", 0x0002, 1000, out result);
                await Task.Delay(3000);
            }

            throw new InvalidOperationException("PATH kept getting overwritten.");
        }

        private const string ProfileBlockStart = "# >>> SalsaNOW DevTools (added automatically) >>>";
        private const string ProfileBlockEnd = "# <<< SalsaNOW DevTools <<<";
        private const string CmdAutoRunMarker = "SALSANOW_DEVTOOLS";

        // The GFN desktop shell ignores PATH change broadcasts, so terminals it opens keep the old PATH.
        // PowerShell 7's profile and cmd's AutoRun run inside every new terminal, so they add the folders there.
        private static void AddToTerminalStartup(string[] directories)
        {
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string list = string.Join(", ", directories.Select(d => "'" + d.Replace("'", "''") + "'"));
            WriteMarkedBlock(Path.Combine(documents, "PowerShell", "profile.ps1"), new[]
            {
                "foreach ($d in @(" + list + ")) { if (($env:Path -split ';') -notcontains $d) { $env:Path = \"$d;$env:Path\" } }"
            }, "\r\n");

            // Git Bash: ~/.bashrc gets the folders in /i/... form. Git for Windows only reads .bashrc through
            // .bash_profile (and warns when it has to create one), so make sure that exists too.
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var bashLines = directories.Select(d =>
            {
                string unixPath = "/" + char.ToLowerInvariant(d[0]) + d.Substring(2).Replace('\\', '/');
                return $"case \":$PATH:\" in *\":{unixPath}:\"*) ;; *) export PATH=\"{unixPath}:$PATH\" ;; esac";
            }).ToArray();
            WriteMarkedBlock(Path.Combine(home, ".bashrc"), bashLines, "\n");

            string bashProfile = Path.Combine(home, ".bash_profile");
            if (!File.Exists(bashProfile))
                File.WriteAllText(bashProfile, "test -f ~/.bashrc && . ~/.bashrc\n");

            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Command Processor"))
            {
                string existing = Convert.ToString(key.GetValue("AutoRun", ""));
                if (existing.Length > 0 && !existing.Contains(CmdAutoRunMarker))
                {
                    Log("cmd AutoRun is already used by something else; leaving it alone.");
                    return;
                }

                // The marker variable stops nested cmd windows from adding the folders again
                key.SetValue("AutoRun",
                    $"if not defined {CmdAutoRunMarker} set \"PATH={string.Join(";", directories)};%PATH%\" & set {CmdAutoRunMarker}=1",
                    RegistryValueKind.String);
            }
        }

        // Replaces our marked block in a file, or appends it, leaving everything else in the file untouched.
        private static void WriteMarkedBlock(string file, string[] lines, string newline)
        {
            string block = string.Join(newline, new[] { ProfileBlockStart }.Concat(lines).Concat(new[] { ProfileBlockEnd }));
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            string text = File.Exists(file) ? File.ReadAllText(file) : "";

            int start = text.IndexOf(ProfileBlockStart, StringComparison.Ordinal);
            int end = start < 0 ? -1 : text.IndexOf(ProfileBlockEnd, start, StringComparison.Ordinal);
            if (start >= 0 && end >= 0)
                text = text.Remove(start, end + ProfileBlockEnd.Length - start).Insert(start, block);
            else
                text = (text.Length == 0 || text.EndsWith("\n") ? text : text + newline) + block + newline;

            File.WriteAllText(file, text);
        }

        private static string WriteLauncher(string devRoot, string[] pathDirs, string workDir)
        {
            string launcher = Path.Combine(devRoot, "OpenCode.cmd");
            File.WriteAllLines(launcher, new[]
            {
                "@echo off",
                $"set \"PATH={string.Join(";", pathDirs)};%PATH%\"",
                $"cd /d \"{workDir}\"",
                "call opencode.cmd %*"
            });
            return launcher;
        }

        // replace: rewrite an existing shortcut (used when its target can move, like the SalsaNOW exe itself)
        private static void CreateDesktopShortcut(string globalDirectory, string name, string target, string workDir, string arguments = null, bool replace = false, string icon = null)
        {
            string fileName = name + ".lnk";
            string desktopLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), fileName);

            // SalsaNOW moves shortcuts the user deleted into "Backup Shortcuts"; respect that choice.
            if ((File.Exists(desktopLnk) && !replace) || File.Exists(Path.Combine(globalDirectory, "Backup Shortcuts", fileName)))
                return;

            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            dynamic lnk = shell.CreateShortcut(desktopLnk);
            lnk.TargetPath = target;
            lnk.WorkingDirectory = workDir;
            if (arguments != null)
            {
                lnk.Arguments = arguments;
                lnk.WindowStyle = 7;   // minimized, so the console flashes less
            }
            if (icon != null)
                lnk.IconLocation = icon + ",0";
            lnk.Save();
        }

        // ---------- helpers ----------

        private static async Task DownloadAsync(string url, string path)
        {
            using (var wc = new WebClient())
            {
                wc.Headers.Add("User-Agent", "SalsaNOW-DevTools");
                await wc.DownloadFileTaskAsync(new Uri(url), path);
            }
        }

        private static async Task<string> DownloadStringAsync(string url)
        {
            using (var wc = new WebClient())
            {
                wc.Headers.Add("User-Agent", "SalsaNOW-DevTools");
                return await wc.DownloadStringTaskAsync(url);
            }
        }

        private static void DeleteDirectory(string path)
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }

        private static readonly object LogLock = new object();

        // File only, never the console: on GeForce NOW writing to SalsaNOW's console can block for good a few
        // seconds after startup, which froze every step at its next log line (shortcuts were never created).
        private static void Log(string message)
        {
            if (_logFile == null)
                return;

            string line = $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}";
            lock (LogLock)
            {
                for (int attempt = 1; attempt <= 3; attempt++)
                {
                    try
                    {
                        File.AppendAllText(_logFile, line);
                        return;
                    }
                    catch (Exception ex)
                    {
                        if (attempt == 3)
                        {
                            // Something keeps devtools.log locked: keep the line in a file of our own instead
                            string fallback = Path.Combine(Path.GetDirectoryName(_logFile), $"devtools-{Process.GetCurrentProcess().Id}.log");
                            try { File.AppendAllText(fallback, line.TrimEnd() + $"   (devtools.log: {ex.Message}){Environment.NewLine}"); } catch { }
                        }
                        else
                        {
                            Thread.Sleep(100);
                        }
                    }
                }
            }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);
    }
}
