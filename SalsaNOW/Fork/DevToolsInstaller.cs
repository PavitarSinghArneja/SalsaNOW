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
using System.Threading.Tasks;

namespace SalsaNOW
{
    // Fork-only feature: installs portable Node.js, OpenCode and Git under the SalsaNOW folder on
    // every launch, so they are back even when the whole disk was reset since the last session.
    //
    // Kept deliberately self-contained so merges from the original SalsaNOW never touch it: it uses
    // no other SalsaNOW class, and the only hook into the original code is one line in Program.cs
    // (plus one <Compile> line in SalsaNOW.csproj), both re-added by .github/scripts/apply-fork-hooks.sh.
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
            string workDir = Path.Combine(Path.GetPathRoot(globalDirectory), "Work");

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

            // Each tool is independent: one failing never stops the others.
            Task node = RunStep("Node.js", async () =>
            {
                if (!File.Exists(Path.Combine(nodeDir, "node.exe")))
                    await InstallNodeAsync(devRoot, nodeDir);
            });

            Task git = RunStep("Git", async () =>
            {
                if (!File.Exists(Path.Combine(gitCmd, "git.exe")))
                    await InstallGitAsync(devRoot, gitDir);
            });

            await node;
            await RunStep("OpenCode", async () =>
            {
                if (File.Exists(Path.Combine(npmPrefix, "opencode.cmd")))
                    return;
                if (!File.Exists(Path.Combine(nodeDir, "node.exe")))
                    throw new InvalidOperationException("Node.js is missing, skipping OpenCode.");
                await InstallOpenCodeAsync(nodeDir, npmPrefix, gitCmd);
            });
            await git;

            string[] pathDirs = { nodeDir, npmPrefix, gitCmd };
            await RunStep("PATH", () => AddToUserPathAsync(pathDirs));
            await RunStep("Terminal PATH", () =>
            {
                AddToTerminalStartup(pathDirs);
                return Task.CompletedTask;
            });

            await RunStep("OpenCode shortcut", () =>
            {
                string launcher = WriteLauncher(devRoot, pathDirs, workDir);
                CreateDesktopShortcut(globalDirectory, "OpenCode", launcher, workDir);
                return Task.CompletedTask;
            });

            Log("Node.js, OpenCode and Git setup finished.");
        }

        private static async Task RunStep(string name, Func<Task> step)
        {
            try
            {
                await step();
            }
            catch (Exception ex)
            {
                Log(name + " setup failed: " + ex.Message);
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
                await Task.Run(() => process.WaitForExit(10 * 60 * 1000));
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
            Log("Installing OpenCode...");

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
                await Task.Run(() => process.WaitForExit());

                if (process.ExitCode != 0)
                    throw new InvalidOperationException($"npm exited with code {process.ExitCode}: {(await stderr).Trim()} {(await stdout).Trim()}");
            }
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
                SendMessageTimeout(new IntPtr(0xFFFF), 0x001A, IntPtr.Zero, "Environment", 0x0002, 5000, out result);
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
            WriteMarkedBlock(Path.Combine(documents, "PowerShell", "profile.ps1"), string.Join(Environment.NewLine, new[]
            {
                ProfileBlockStart,
                "foreach ($d in @(" + list + ")) { if (($env:Path -split ';') -notcontains $d) { $env:Path = \"$d;$env:Path\" } }",
                ProfileBlockEnd
            }));

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
        private static void WriteMarkedBlock(string file, string block)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            string text = File.Exists(file) ? File.ReadAllText(file) : "";

            int start = text.IndexOf(ProfileBlockStart, StringComparison.Ordinal);
            int end = start < 0 ? -1 : text.IndexOf(ProfileBlockEnd, start, StringComparison.Ordinal);
            if (start >= 0 && end >= 0)
                text = text.Remove(start, end + ProfileBlockEnd.Length - start).Insert(start, block);
            else
                text = (text.Length == 0 || text.EndsWith("\n") ? text : text + Environment.NewLine) + block + Environment.NewLine;

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

        private static void CreateDesktopShortcut(string globalDirectory, string name, string target, string workDir)
        {
            string fileName = name + ".lnk";
            string desktopLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), fileName);

            // SalsaNOW moves shortcuts the user deleted into "Backup Shortcuts"; respect that choice.
            if (File.Exists(desktopLnk) || File.Exists(Path.Combine(globalDirectory, "Backup Shortcuts", fileName)))
                return;

            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            dynamic lnk = shell.CreateShortcut(desktopLnk);
            lnk.TargetPath = target;
            lnk.WorkingDirectory = workDir;
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

        private static void Log(string message)
        {
            Console.WriteLine("[+] " + message);
            if (_logFile == null)
                return;

            lock (LogLock)
            {
                try { File.AppendAllText(_logFile, $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}"); }
                catch { }
            }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);
    }
}
