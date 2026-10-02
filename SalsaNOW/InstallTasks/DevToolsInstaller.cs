using Newtonsoft.Json.Linq;
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Threading.Tasks;

namespace SalsaNOW
{
    // Installs portable Node.js and OpenCode under the SalsaNOW folder on every launch,
    // so they are back even when the whole disk was reset since the last session.
    internal static class DevToolsInstaller
    {
        private const string NodeIndexUrl = "https://nodejs.org/dist/index.json";
        private const string OpenCodePackage = "opencode-ai";

        public static string NodeDirectory(string globalDirectory) => Path.Combine(globalDirectory, "DevTools", "node");
        public static string NpmPrefix(string globalDirectory) => Path.Combine(globalDirectory, "DevTools", "npm-global");

        public static async Task InstallAsync(string globalDirectory)
        {
            try
            {
                string devRoot = Path.Combine(globalDirectory, "DevTools");
                string nodeDir = NodeDirectory(globalDirectory);
                string npmPrefix = NpmPrefix(globalDirectory);
                string workDir = Path.Combine(Path.GetPathRoot(globalDirectory), "Work");

                Directory.CreateDirectory(devRoot);
                Directory.CreateDirectory(npmPrefix);
                Directory.CreateDirectory(workDir);

                if (!File.Exists(Path.Combine(nodeDir, "node.exe")))
                    await InstallNodeAsync(devRoot, nodeDir);

                if (!File.Exists(Path.Combine(npmPrefix, "opencode.cmd")))
                    await InstallOpenCodeAsync(nodeDir, npmPrefix);

                string launcher = WriteLauncher(devRoot, nodeDir, npmPrefix, workDir);

                string desktopLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "OpenCode.lnk");
                if (AppInstaller.ShouldCreateDesktopShortcut(globalDirectory, desktopLnk))
                    AppInstaller.CreateShortcut("OpenCode", desktopLnk, launcher, workDir);

                SalsaLogger.Info("Node.js and OpenCode are ready.");
            }
            catch (Exception ex)
            {
                SalsaLogger.Error("Node.js / OpenCode install failed: " + ex.Message);
            }
        }

        private static async Task InstallNodeAsync(string devRoot, string nodeDir)
        {
            string version = await GetLatestLtsVersionAsync();
            string folderName = $"node-{version}-win-x64";
            string zipPath = Path.Combine(devRoot, folderName + ".zip");
            string extracted = Path.Combine(devRoot, folderName);

            SalsaLogger.Info("Installing Node.js " + version);

            using (var wc = new WebClient())
                await wc.DownloadFileTaskAsync(new Uri($"https://nodejs.org/dist/{version}/{folderName}.zip"), zipPath);

            if (Directory.Exists(extracted)) Directory.Delete(extracted, true);
            if (Directory.Exists(nodeDir)) Directory.Delete(nodeDir, true);

            ZipFile.ExtractToDirectory(zipPath, devRoot);
            Directory.Move(extracted, nodeDir);
            File.Delete(zipPath);
        }

        private static async Task<string> GetLatestLtsVersionAsync()
        {
            string json;
            using (var wc = new WebClient())
                json = await wc.DownloadStringTaskAsync(NodeIndexUrl);

            // index.json is newest first; "lts" is false for Current releases and a codename for LTS ones
            foreach (JToken release in JArray.Parse(json))
            {
                if (release["lts"] == null || release["lts"].Type == JTokenType.Boolean)
                    continue;

                JArray files = release["files"] as JArray;
                if (files == null)
                    continue;

                foreach (JToken file in files)
                {
                    if ((string)file == "win-x64-zip")
                        return (string)release["version"];
                }
            }

            throw new InvalidOperationException("Could not find a Node.js LTS release for win-x64.");
        }

        private static async Task InstallOpenCodeAsync(string nodeDir, string npmPrefix)
        {
            SalsaLogger.Info("Installing OpenCode...");

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
            psi.EnvironmentVariables["PATH"] = nodeDir + ";" + npmPrefix + ";" + Environment.GetEnvironmentVariable("PATH");

            using (Process process = Process.Start(psi))
            {
                Task<string> stdout = process.StandardOutput.ReadToEndAsync();
                Task<string> stderr = process.StandardError.ReadToEndAsync();
                await Task.Run(() => process.WaitForExit());

                if (process.ExitCode != 0)
                    throw new InvalidOperationException($"npm exited with code {process.ExitCode}: {(await stderr).Trim()} {(await stdout).Trim()}");
            }
        }

        // OpenCode's free models need no API key, so the launcher only sets PATH and the working folder.
        private static string WriteLauncher(string devRoot, string nodeDir, string npmPrefix, string workDir)
        {
            string launcher = Path.Combine(devRoot, "OpenCode.cmd");
            File.WriteAllLines(launcher, new[]
            {
                "@echo off",
                $"set \"PATH={nodeDir};{npmPrefix};%PATH%\"",
                $"cd /d \"{workDir}\"",
                "call opencode.cmd %*"
            });
            return launcher;
        }
    }
}
