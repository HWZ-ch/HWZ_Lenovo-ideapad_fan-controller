using System;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace LenovoCoolBoost
{
    /// <summary>一次版本检查的结果。</summary>
    public sealed class UpdateCheckResult
    {
        /// <summary>本地程序版本。</summary>
        public Version Current { get; set; }

        /// <summary>仓库上的最新版本；取不到时为 null。</summary>
        public Version Latest { get; set; }

        /// <summary>仓库上的版本字符串原样（例如 "v1.3.0"）。</summary>
        public string LatestRaw { get; set; }

        /// <summary>是否存在更新。</summary>
        public bool HasUpdate { get; set; }

        /// <summary>更新说明（来自 Release 正文）。</summary>
        public string ReleaseNotes { get; set; }

        /// <summary>下载/查看页面地址。</summary>
        public string DownloadUrl { get; set; }

        /// <summary>数据来源：releases / contents-api / raw。</summary>
        public string Source { get; set; }

        /// <summary>检查失败时的原因；成功时为 null。</summary>
        public string Error { get; set; }

        public string Describe()
        {
            string cur = "v" + (Current != null ? Current.ToString(3) : "?");
            if (Error != null)
                return string.Format("版本 {0}（检查更新失败：{1}）", cur, Error);
            if (Latest == null)
                return string.Format("版本 {0}（未能获取远端版本）", cur);
            return HasUpdate
                ? string.Format("版本 {0} → 有新版本 v{1}", cur, Latest.ToString(3))
                : string.Format("版本 {0}（已是最新）", cur);
        }
    }

    /// <summary>
    /// 从 GitHub 仓库自动获取最新版本并与本地版本比对。
    ///
    /// 数据来源按优先级依次尝试（任何一步成功即返回）：
    ///   1) GitHub Releases API 的 latest（api.github.com）
    ///   2) 仓库 version.json，走 **Contents API**（同样是 api.github.com）
    ///   3) 仓库 version.json，走 raw.githubusercontent
    ///
    /// 为什么第 2 步用 Contents API 而不是直接读 raw：某些受限网络里
    /// raw.githubusercontent.com / jsdelivr 不可达，但 api.github.com 可以通。
    ///
    /// 全程只读 GET，失败一律写进 Error，不抛异常、不影响程序其余功能。
    /// </summary>
    public static class UpdateChecker
    {
        private const string Owner = "HWZ-ch";
        private const string Repo = "HWZ_Lenovo-ideapad_fan-controller";

        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("LenovoCoolBoost-UpdateChecker");
            c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            // 让中间的加速/代理层不要回放旧缓存（实测有代理会缓存 /releases/latest，
            // 导致新版本已发布或已删除时仍读到旧结果）
            c.DefaultRequestHeaders.CacheControl =
                new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true, NoStore = true };
            return c;
        }

        /// <summary>给 URL 追加一个无意义的参数，绕过中间层的 URL 缓存。</summary>
        private static string NoCache(string url)
        {
            return url + (url.Contains("?") ? "&" : "?") + "ts=" + DateTime.UtcNow.Ticks;
        }

        /// <summary>本程序版本（取自程序集，永远与实际二进制一致）。</summary>
        public static Version CurrentVersion
        {
            get
            {
                try
                {
                    var v = Assembly.GetExecutingAssembly().GetName().Version;
                    return v ?? new Version(0, 0, 0);
                }
                catch { return new Version(0, 0, 0); }
            }
        }

        /// <summary>执行一次检查。可安全地在后台线程调用。</summary>
        public static UpdateCheckResult Check()
        {
            var result = new UpdateCheckResult { Current = CurrentVersion };

            string ver, notes, url, source;

            // 1) Releases
            if (TryGetLatestRelease(out ver, out notes, out url))
            {
                source = "releases";
            }
            // 2) version.json via Contents API
            else if (TryGetVersionJsonViaApi(out ver, out notes, out url))
            {
                source = "contents-api";
            }
            // 3) version.json via raw
            else if (TryGetVersionJsonViaRaw(out ver, out notes, out url))
            {
                source = "raw";
            }
            else
            {
                result.Error = "无法获取仓库版本（网络受限或仓库不可达）";
                return result;
            }

            result.LatestRaw = ver;
            result.Latest = ParseVersion(ver);
            result.ReleaseNotes = notes;
            result.DownloadUrl = url;
            result.Source = source;

            if (result.Latest == null)
            {
                result.Error = "远端版本号无法解析：" + (ver ?? "(空)");
                return result;
            }

            result.HasUpdate = result.Latest > result.Current;
            if (string.IsNullOrEmpty(result.DownloadUrl))
                result.DownloadUrl = string.Format("https://github.com/{0}/{1}/releases", Owner, Repo);
            return result;
        }

        // ------------------------------------------------------------------
        // 来源 1：GitHub Releases latest
        // ------------------------------------------------------------------
        private static bool TryGetLatestRelease(out string tag, out string body, out string url)
        {
            tag = null; body = null; url = null;
            try
            {
                string api = string.Format("https://api.github.com/repos/{0}/{1}/releases/latest", Owner, Repo);
                string json = GetString(NoCache(api));
                if (json == null) return false;

                using (var doc = JsonDocument.Parse(json))
                {
                    var root = doc.RootElement;
                    if (root.TryGetProperty("message", out _)) return false;   // {"message":"Not Found"}
                    if (!root.TryGetProperty("tag_name", out var t)) return false;
                    tag = t.GetString();
                    if (root.TryGetProperty("body", out var b)) body = b.GetString();
                    if (root.TryGetProperty("html_url", out var h)) url = h.GetString();

                    // 优先取 exe 资源的直链
                    if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var a in assets.EnumerateArray())
                        {
                            if (a.TryGetProperty("name", out var n) &&
                                n.GetString() != null &&
                                n.GetString().EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                                a.TryGetProperty("browser_download_url", out var d))
                            {
                                url = d.GetString();
                                break;
                            }
                        }
                    }
                }
                return !string.IsNullOrEmpty(tag);
            }
            catch { return false; }
        }

        // ------------------------------------------------------------------
        // 来源 2：version.json via Contents API（api.github.com，受限网络也能通）
        // ------------------------------------------------------------------
        private static bool TryGetVersionJsonViaApi(out string version, out string notes, out string url)
        {
            version = null; notes = null; url = null;
            try
            {
                string api = string.Format("https://api.github.com/repos/{0}/{1}/contents/version.json", Owner, Repo);
                string json = GetString(NoCache(api));
                if (json == null) return false;

                using (var doc = JsonDocument.Parse(json))
                {
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("content", out var c)) return false;
                    string enc = null;
                    if (root.TryGetProperty("encoding", out var e)) enc = e.GetString();
                    if (enc != "base64") return false;

                    string b64 = (c.GetString() ?? "").Replace("\n", "").Replace("\r", "");
                    byte[] bytes = Convert.FromBase64String(b64);
                    string text = Encoding.UTF8.GetString(bytes);
                    return ParseVersionJson(text, out version, out notes, out url);
                }
            }
            catch { return false; }
        }

        // ------------------------------------------------------------------
        // 来源 3：version.json via raw（部分网络不可达，作为最后兜底）
        // ------------------------------------------------------------------
        private static bool TryGetVersionJsonViaRaw(out string version, out string notes, out string url)
        {
            version = null; notes = null; url = null;
            try
            {
                string raw = string.Format(
                    "https://raw.githubusercontent.com/{0}/{1}/main/version.json", Owner, Repo);
                string json = GetString(NoCache(raw));
                if (json == null) return false;
                return ParseVersionJson(json, out version, out notes, out url);
            }
            catch { return false; }
        }

        private static bool ParseVersionJson(string json, out string version, out string notes, out string url)
        {
            version = null; notes = null; url = null;
            try
            {
                using (var doc = JsonDocument.Parse(json))
                {
                    var root = doc.RootElement;
                    if (root.TryGetProperty("version", out var v)) version = v.GetString();
                    if (root.TryGetProperty("notes", out var n)) notes = n.GetString();
                    if (root.TryGetProperty("download_url", out var d)) url = d.GetString();
                }
            }
            catch { return false; }
            return !string.IsNullOrEmpty(version);
        }

        private static string GetString(string url)
        {
            try
            {
                using (var resp = Http.GetAsync(url).GetAwaiter().GetResult())
                {
                    if (!resp.IsSuccessStatusCode) return null;
                    return resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                }
            }
            catch { return null; }
        }

        /// <summary>把 "v1.3.0" / "1.3" / "1.3.0.0" 解析成 Version。</summary>
        public static Version ParseVersion(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim();
            if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase)) s = s.Substring(1);

            // 去掉可能的后缀，如 "1.3.0-beta.1"
            int dash = s.IndexOfAny(new[] { '-', '+', ' ' });
            if (dash > 0) s = s.Substring(0, dash);

            Version v;
            return Version.TryParse(s, out v) ? v : null;
        }

        /// <summary>在后台执行检查并在完成后回调（回调不在 UI 线程，请自行切回）。</summary>
        public static void CheckAsync(Action<UpdateCheckResult> done)
        {
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                UpdateCheckResult r;
                try { r = Check(); }
                catch (Exception ex)
                {
                    r = new UpdateCheckResult { Current = CurrentVersion, Error = ex.Message };
                }
                try { if (done != null) done(r); } catch { }
            });
        }
    }
}
