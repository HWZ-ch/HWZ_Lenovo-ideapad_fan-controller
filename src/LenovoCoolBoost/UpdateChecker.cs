using System;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;

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

        /// <summary>数据来源：releases / version.json。</summary>
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
    /// 数据来源优先级：
    ///   1) GitHub Releases API 的 latest 发布（tag_name）
    ///   2) 仓库根目录的 version.json（没有发布 Release 时兜底）
    /// 全程只读 GET，失败一律返回 Error，不抛异常。
    /// </summary>
    public static class UpdateChecker
    {
        private const string Owner = "HWZ-ch";
        private const string Repo = "HWZ_Lenovo-ideapad_fan-controller";

        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromMilliseconds(8000) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("LenovoCoolBoost-UpdateChecker");
            c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return c;
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

            // 1) 先试 Releases
            string tag, body, url;
            if (TryGetLatestRelease(out tag, out body, out url))
            {
                result.LatestRaw = tag;
                result.Latest = ParseVersion(tag);
                result.ReleaseNotes = body;
                result.DownloadUrl = url;
                result.Source = "releases";
            }
            else
            {
                // 2) 回退到 version.json
                string ver, notes2, dl2;
                if (TryGetVersionJson(out ver, out notes2, out dl2))
                {
                    result.LatestRaw = ver;
                    result.Latest = ParseVersion(ver);
                    result.ReleaseNotes = notes2;
                    result.DownloadUrl = dl2;
                    result.Source = "version.json";
                }
                else
                {
                    result.Error = "无法访问 GitHub（网络不可用或被拦截）";
                    return result;
                }
            }

            if (result.Latest == null)
            {
                result.Error = "远端版本号无法解析：" + (result.LatestRaw ?? "(空)");
                return result;
            }

            result.HasUpdate = result.Latest > result.Current;
            if (string.IsNullOrEmpty(result.DownloadUrl))
                result.DownloadUrl = string.Format("https://github.com/{0}/{1}/releases", Owner, Repo);
            return result;
        }

        private static bool TryGetLatestRelease(out string tag, out string body, out string url)
        {
            tag = null; body = null; url = null;
            try
            {
                string api = string.Format("https://api.github.com/repos/{0}/{1}/releases/latest", Owner, Repo);
                string json = GetString(api);
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

        private static bool TryGetVersionJson(out string version, out string notes, out string url)
        {
            version = null; notes = null; url = null;
            try
            {
                string raw = string.Format(
                    "https://raw.githubusercontent.com/{0}/{1}/main/version.json", Owner, Repo);
                string json = GetString(raw);
                if (json == null) return false;

                using (var doc = JsonDocument.Parse(json))
                {
                    var root = doc.RootElement;
                    if (root.TryGetProperty("version", out var v)) version = v.GetString();
                    if (root.TryGetProperty("notes", out var n)) notes = n.GetString();
                    if (root.TryGetProperty("download_url", out var d)) url = d.GetString();
                }
                return !string.IsNullOrEmpty(version);
            }
            catch { return false; }
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

        /// <summary>在后台执行检查并在完成后回调（回调在调用线程的同步上下文之外，请自行切回 UI 线程）。</summary>
        public static void CheckAsync(Action<UpdateCheckResult> done)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                UpdateCheckResult r;
                try { r = Check(); }
                catch (Exception ex)
                {
                    r = new UpdateCheckResult
                    {
                        Current = CurrentVersion,
                        Error = ex.Message
                    };
                }
                try { if (done != null) done(r); } catch { }
            });
        }
    }
}
