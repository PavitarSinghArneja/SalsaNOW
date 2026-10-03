using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SalsaNOW
{
    // Fork-only: the portable GitHub CLI (gh). Used only to log in with a one-time code typed on
    // another device (github.com/login/device), so no password or token is ever built into the exe.
    // Its login lives in a temp folder, so it is gone when the session ends.
    internal static class GhCli
    {
        private const string ReleaseApi = "https://api.github.com/repos/cli/cli/releases/latest";
        private const string FallbackUrl = "https://github.com/cli/cli/releases/download/v2.63.2/gh_2.63.2_windows_amd64.zip";

        public static string GhExe { get; private set; }
        public static string BinDir(string devRoot) { return Path.Combine(devRoot, "gh", "bin"); }

        private static string ConfigDir { get { return Path.Combine(Path.GetTempPath(), "SalsaNOW-gh"); } }

        public static async Task InstallAsync(string devRoot, Action<string> log)
        {
            string ghDir = Path.Combine(devRoot, "gh");
            string exe = Path.Combine(ghDir, "bin", "gh.exe");
            if (!File.Exists(exe))
            {
                string url = FallbackUrl;
                try
                {
                    string json = await GitHubBackupApi.DownloadStringAsync(ReleaseApi);
                    Match match = Regex.Match(json, @"""browser_download_url""\s*:\s*""(https://[^""]+/gh_[\d.]+_windows_amd64\.zip)""");
                    if (match.Success)
                        url = match.Groups[1].Value;
                }
                catch (Exception ex)
                {
                    log("Could not look up the latest GitHub CLI: " + ex.Message);
                }

                log("Installing GitHub CLI from " + url);
                string zip = Path.Combine(devRoot, "gh.zip");
                using (var wc = new WebClient())
                {
                    wc.Headers.Add("User-Agent", GitHubBackupApi.UserAgent);
                    await wc.DownloadFileTaskAsync(new Uri(url), zip);
                }

                if (Directory.Exists(ghDir))
                    Directory.Delete(ghDir, true);
                ZipFile.ExtractToDirectory(zip, ghDir);
                File.Delete(zip);
                if (!File.Exists(exe))
                    throw new InvalidOperationException("gh.exe not found after extracting.");
            }

            GhExe = exe;
        }

        public static async Task<bool> IsLoggedInAsync()
        {
            return (await RunAsync("auth status --hostname github.com")).Item1 == 0;
        }

        public static async Task<string> GetTokenAsync()
        {
            Tuple<int, string, string> result = await RunAsync("auth token --hostname github.com");
            return result.Item1 == 0 ? result.Item2.Trim() : null;
        }

        // Starts "gh auth login" with the device flow. onCode gets the one-time code as soon as gh prints it.
        // The returned process exits with code 0 once the code was entered on github.com/login/device.
        public static Process StartLogin(Action<string> onCode)
        {
            ProcessStartInfo psi = CreateStartInfo("auth login --hostname github.com --git-protocol https --web --insecure-storage --scopes repo");
            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            DataReceivedEventHandler handler = (s, e) =>
            {
                if (e.Data == null)
                    return;
                Match code = Regex.Match(e.Data, @"\b([A-Z0-9]{4}-[A-Z0-9]{4})\b");
                if (code.Success)
                    onCode(code.Groups[1].Value);
            };
            process.OutputDataReceived += handler;
            process.ErrorDataReceived += handler;
            process.Start();
            process.StandardInput.Close();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return process;
        }

        private static ProcessStartInfo CreateStartInfo(string arguments)
        {
            if (GhExe == null)
                throw new InvalidOperationException("The GitHub CLI is not installed yet.");

            Directory.CreateDirectory(ConfigDir);
            var psi = new ProcessStartInfo
            {
                FileName = GhExe,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.EnvironmentVariables["GH_CONFIG_DIR"] = ConfigDir;
            psi.EnvironmentVariables["GH_NO_UPDATE_NOTIFIER"] = "1";
            psi.EnvironmentVariables["GH_PROMPT_DISABLED"] = "1";
            psi.EnvironmentVariables["NO_COLOR"] = "1";
            return psi;
        }

        private static async Task<Tuple<int, string, string>> RunAsync(string arguments)
        {
            using (Process process = Process.Start(CreateStartInfo(arguments)))
            {
                process.StandardInput.Close();
                Task<string> stdout = process.StandardOutput.ReadToEndAsync();
                Task<string> stderr = process.StandardError.ReadToEndAsync();
                await Task.Run(() => process.WaitForExit());
                return Tuple.Create(process.ExitCode, await stdout, await stderr);
            }
        }
    }

    internal class GitHubException : Exception
    {
        public int Status { get; private set; }
        public GitHubException(int status, string message) : base(message) { Status = status; }
    }

    internal class ReleaseAsset
    {
        public long Id;
        public string Name;
        public long Size;
        public DateTime CreatedUtc;
    }

    // Fork-only: the few GitHub REST calls the Backups app needs, made with the token from GhCli.
    internal class GitHubBackupApi
    {
        public const string UserAgent = "SalsaNOW-Backups";
        private const string Api = "https://api.github.com";

        private readonly string _token;
        public string Owner { get; private set; }
        public string Repo { get; private set; }

        public GitHubBackupApi(string token) { _token = token; }

        public async Task ConnectAsync(string repoName)
        {
            Owner = (string)JObject.Parse(await SendAsync("GET", Api + "/user"))["login"];
            Repo = repoName;

            try
            {
                await SendAsync("GET", RepoUrl());
            }
            catch (GitHubException ex) when (ex.Status == 404)
            {
                // auto_init gives the repo a first commit, which releases need for their tags
                await SendAsync("POST", Api + "/user/repos", new JObject
                {
                    ["name"] = repoName,
                    ["private"] = true,
                    ["auto_init"] = true,
                    ["description"] = "Folder backups made by SalsaNOW. Each folder here is one slot; the zips are under Releases."
                });
            }
        }

        public string RepoWebUrl { get { return "https://github.com/" + Owner + "/" + Repo; } }

        // ---------- files in the repo ----------

        // Paths of every file in the repo (empty for a repo with no commits yet)
        public async Task<List<string>> ListFilesAsync()
        {
            try
            {
                JObject tree = JObject.Parse(await SendAsync("GET", RepoUrl("/git/trees/HEAD?recursive=1")));
                return tree["tree"].Where(t => (string)t["type"] == "blob").Select(t => (string)t["path"]).ToList();
            }
            catch (GitHubException ex) when (ex.Status == 404 || ex.Status == 409)
            {
                return new List<string>();
            }
        }

        // Returns (text, sha), or null when the file does not exist
        public async Task<Tuple<string, string>> GetFileAsync(string path)
        {
            try
            {
                JObject file = JObject.Parse(await SendAsync("GET", RepoUrl("/contents/" + EscapePath(path))));
                string text = Encoding.UTF8.GetString(Convert.FromBase64String(((string)file["content"]).Replace("\n", "")));
                return Tuple.Create(text, (string)file["sha"]);
            }
            catch (GitHubException ex) when (ex.Status == 404)
            {
                return null;
            }
        }

        public async Task PutFileAsync(string path, string text, string message)
        {
            for (int attempt = 0; ; attempt++)
            {
                Tuple<string, string> existing = await GetFileAsync(path);
                var body = new JObject
                {
                    ["message"] = message,
                    ["content"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(text))
                };
                if (existing != null)
                    body["sha"] = existing.Item2;

                try
                {
                    await SendAsync("PUT", RepoUrl("/contents/" + EscapePath(path)), body);
                    return;
                }
                catch (GitHubException ex) when ((ex.Status == 409 || ex.Status == 422) && attempt < 2)
                {
                    // Someone else changed the file in between; read it again and retry
                    await Task.Delay(1000);
                }
            }
        }

        public async Task DeleteFileAsync(string path, string message)
        {
            Tuple<string, string> existing = await GetFileAsync(path);
            if (existing == null)
                return;
            await SendAsync("DELETE", RepoUrl("/contents/" + EscapePath(path)), new JObject { ["message"] = message, ["sha"] = existing.Item2 });
        }

        // ---------- releases (one per slot, holding its zips) ----------

        public async Task<long> GetOrCreateReleaseAsync(string tag, string title)
        {
            long? id = await FindReleaseAsync(tag);
            if (id.HasValue)
                return id.Value;

            JObject created = JObject.Parse(await SendAsync("POST", RepoUrl("/releases"), new JObject
            {
                ["tag_name"] = tag,
                ["name"] = title,
                ["body"] = "Backups of the \"" + title + "\" slot, made by SalsaNOW. Newest first in the Backups app.",
                ["make_latest"] = "false"
            }));
            return (long)created["id"];
        }

        public async Task<long?> FindReleaseAsync(string tag)
        {
            try
            {
                return (long)JObject.Parse(await SendAsync("GET", RepoUrl("/releases/tags/" + Uri.EscapeDataString(tag))))["id"];
            }
            catch (GitHubException ex) when (ex.Status == 404)
            {
                return null;
            }
        }

        // Newest first
        public async Task<List<ReleaseAsset>> ListAssetsAsync(long releaseId)
        {
            var assets = new List<ReleaseAsset>();
            for (int page = 1; ; page++)
            {
                JArray batch = JArray.Parse(await SendAsync("GET", RepoUrl($"/releases/{releaseId}/assets?per_page=100&page={page}")));
                foreach (JToken a in batch)
                {
                    assets.Add(new ReleaseAsset
                    {
                        Id = (long)a["id"],
                        Name = (string)a["name"],
                        Size = (long)a["size"],
                        CreatedUtc = ((DateTime)a["created_at"]).ToUniversalTime()
                    });
                }
                if (batch.Count < 100)
                    break;
            }
            return assets.OrderByDescending(a => a.CreatedUtc).ThenByDescending(a => a.Name, StringComparer.Ordinal).ToList();
        }

        public async Task UploadAssetAsync(long releaseId, string file, string name, Action<long, long> progress)
        {
            string url = $"https://uploads.github.com/repos/{Owner}/{Repo}/releases/{releaseId}/assets?name={Uri.EscapeDataString(name)}";
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "POST";
            request.ContentType = "application/zip";
            AddHeaders(request, "application/vnd.github+json");
            request.AllowWriteStreamBuffering = false;
            request.Timeout = (int)TimeSpan.FromHours(3).TotalMilliseconds;
            request.ReadWriteTimeout = (int)TimeSpan.FromMinutes(5).TotalMilliseconds;

            using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                request.ContentLength = input.Length;
                using (Stream output = await request.GetRequestStreamAsync())
                    await CopyWithProgressAsync(input, output, input.Length, progress);
            }

            await ReadResponseAsync(request);
        }

        public async Task DownloadAssetAsync(long assetId, string file, Action<long, long> progress)
        {
            // The API answers with a redirect to a signed download link, which must be fetched without our token
            var request = (HttpWebRequest)WebRequest.Create(RepoUrl("/releases/assets/" + assetId));
            AddHeaders(request, "application/octet-stream");
            request.AllowAutoRedirect = false;

            string location;
            using (HttpWebResponse redirect = await GetResponseAsync(request))
            {
                location = redirect.Headers["Location"];
                if (location == null)
                {
                    // Served directly
                    await SaveResponseAsync(redirect, file, progress);
                    return;
                }
            }

            var download = (HttpWebRequest)WebRequest.Create(location);
            download.UserAgent = UserAgent;
            download.Timeout = (int)TimeSpan.FromHours(3).TotalMilliseconds;
            download.ReadWriteTimeout = (int)TimeSpan.FromMinutes(5).TotalMilliseconds;
            using (HttpWebResponse response = await GetResponseAsync(download))
                await SaveResponseAsync(response, file, progress);
        }

        public Task DeleteAssetAsync(long assetId)
        {
            return SendAsync("DELETE", RepoUrl("/releases/assets/" + assetId));
        }

        public async Task DeleteReleaseAndTagAsync(string tag)
        {
            long? id = await FindReleaseAsync(tag);
            if (id.HasValue)
                await SendAsync("DELETE", RepoUrl("/releases/" + id.Value));
            try { await SendAsync("DELETE", RepoUrl("/git/refs/tags/" + Uri.EscapeDataString(tag))); }
            catch (GitHubException ex) when (ex.Status == 404 || ex.Status == 422) { }
        }

        // ---------- plumbing ----------

        private string RepoUrl(string rest = "")
        {
            return $"{Api}/repos/{Owner}/{Repo}{rest}";
        }

        private static string EscapePath(string path)
        {
            return string.Join("/", path.Split('/').Select(Uri.EscapeDataString));
        }

        private void AddHeaders(HttpWebRequest request, string accept)
        {
            request.UserAgent = UserAgent;
            request.Accept = accept;
            request.Headers["Authorization"] = "Bearer " + _token;
            request.Headers["X-GitHub-Api-Version"] = "2022-11-28";
        }

        private async Task<string> SendAsync(string method, string url, JObject body = null)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = method;
            AddHeaders(request, "application/vnd.github+json");
            request.Timeout = 60000;

            if (body != null)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(body.ToString(Formatting.None));
                request.ContentType = "application/json";
                request.ContentLength = bytes.Length;
                using (Stream stream = await request.GetRequestStreamAsync())
                    await stream.WriteAsync(bytes, 0, bytes.Length);
            }

            return await ReadResponseAsync(request);
        }

        private static async Task<string> ReadResponseAsync(HttpWebRequest request)
        {
            using (HttpWebResponse response = await GetResponseAsync(request))
            using (var reader = new StreamReader(response.GetResponseStream()))
                return await reader.ReadToEndAsync();
        }

        // Turns HTTP errors into GitHubException with GitHub's own message
        private static async Task<HttpWebResponse> GetResponseAsync(HttpWebRequest request)
        {
            try
            {
                return (HttpWebResponse)await request.GetResponseAsync();
            }
            catch (WebException ex) when (ex.Response is HttpWebResponse)
            {
                var response = (HttpWebResponse)ex.Response;
                int status = (int)response.StatusCode;
                if (status >= 300 && status < 400)
                    return response;

                using (response)
                {
                    string message = ex.Message;
                    try
                    {
                        using (var reader = new StreamReader(response.GetResponseStream()))
                        {
                            string text = await reader.ReadToEndAsync();
                            message = (string)JObject.Parse(text)["message"] ?? text;
                        }
                    }
                    catch { }
                    throw new GitHubException(status, $"GitHub said {status}: {message}");
                }
            }
        }

        private static async Task SaveResponseAsync(HttpWebResponse response, string file, Action<long, long> progress)
        {
            using (Stream input = response.GetResponseStream())
            using (var output = new FileStream(file, FileMode.Create, FileAccess.Write))
                await CopyWithProgressAsync(input, output, response.ContentLength, progress);
        }

        private static async Task CopyWithProgressAsync(Stream input, Stream output, long total, Action<long, long> progress)
        {
            var buffer = new byte[1 << 20];
            long done = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                await output.WriteAsync(buffer, 0, read);
                done += read;
                progress?.Invoke(done, total);
            }
        }

        public static async Task<string> DownloadStringAsync(string url)
        {
            using (var wc = new WebClient())
            {
                wc.Headers.Add("User-Agent", UserAgent);
                return await wc.DownloadStringTaskAsync(url);
            }
        }
    }
}
