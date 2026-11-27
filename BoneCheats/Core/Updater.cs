using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MelonLoader;
using MelonLoader.Utils;
using Application = UnityEngine.Application;
using RuntimePlatform = UnityEngine.RuntimePlatform;

namespace BoneCheats.Core
{
    /// <summary>
    /// Checks GitHub Releases once at startup. PC: downloads the new DLL and swaps it in for the next launch.
    /// Quest / anything that isn't positively Windows: only tells the player to update by hand, touches no files.
    /// All network + disk work runs on a pool thread and only sets flags; every BoneLib call (notifications) happens on the main
    /// thread from Tick(). Notifications go through Game.Notify -> BoneLib's Notifier, which queues them and shows them one at a
    /// time once the player rig exists and the game isn't loading, so the updater has no readiness logic of its own.
    /// Nothing in here is allowed to throw into the mod.
    /// </summary>
    internal static class Updater
    {
        // ---- the only thing to edit per release (Main.cs's MelonInfo reads this too) ----
        public const string CurrentVersion = "1.3.0";

        private const string Repo = "UltraAiSlopCode/BoneCheats";
        private const string ApiUrl = "https://api.github.com/repos/" + Repo + "/releases/latest";
        private const string DllName = "BoneCheats.dll";

        private const string MsgRequired = "BoneCheats update required!";
        private const string MsgDownloading = "Downloading BoneCheats update...";
        private const string MsgDone = "BoneCheats update downloaded successfully! Restart BONELAB to finish updating.";
        private const string MsgFailed = "BoneCheats update failed! Please download it manually.";
        private const string MsgQuest = "BoneCheats update required! Please manually download the latest update.";

        private enum Platform { Pc, Quest, Unknown }
        private enum Stage { Idle, Checking, Downloading, Finished }

        private sealed class Release
        {
            public string Tag, Body, DllUrl, Sha256;
            public long Size;
        }

        private static HttpClient _http;   // created on the pool thread inside try/catch, never in a static initializer
        private static HttpClient Http { get { return _http ?? (_http = CreateClient()); } }

        private static Platform _platform;
        private static Stage _stage = Stage.Idle;

        // written by the pool thread, read by the main thread
        private static volatile bool _checkDone;
        private static Release _release;               // non-null = a newer release exists
        private static string _checkLog;               // one line to log from the main thread
        private static volatile int _download;         // 0 running, 1 prepared, 2 failed
        private static string _downloadLog;

        // ------------------------------------------------------------------ entry points (main thread)

        public static void Start()
        {
            try
            {
                if (_stage != Stage.Idle) return;
                _platform = DetectPlatform();
                MelonLogger.Msg("[BoneCheats] Updater: v" + CurrentVersion + ", platform = " + _platform);
                _stage = Stage.Checking;
                Task.Run(() => CheckWorker());
            }
            catch (Exception e) { _stage = Stage.Finished; MelonLogger.Warning("[BoneCheats] Updater disabled: " + e.Message); }
        }

        public static void Tick()
        {
            try
            {
                switch (_stage)
                {
                    case Stage.Idle:
                    case Stage.Finished:
                        return;

                    case Stage.Checking:
                        if (!_checkDone) return;
                        if (_checkLog != null) MelonLogger.Msg("[BoneCheats] Updater: " + _checkLog);
                        if (_release == null) { _stage = Stage.Finished; return; }
                        if (_platform != Platform.Pc)
                        {
                            Game.Notify(MsgQuest);                // manual path: one notification, nothing else
                            _stage = Stage.Finished;
                            return;
                        }
                        Game.Notify(MsgRequired);                 // BoneLib's queue shows these in order, when the game is ready
                        Game.Notify(MsgDownloading);
                        _stage = Stage.Downloading;
                        Release r = _release;
                        Task.Run(() => DownloadWorker(r));
                        return;

                    case Stage.Downloading:
                        if (_download == 0) return;
                        if (_downloadLog != null) MelonLogger.Msg("[BoneCheats] Updater: " + _downloadLog);
                        Game.Notify(_download == 1 ? MsgDone : MsgFailed);
                        _stage = Stage.Finished;
                        return;
                }
            }
            catch (Exception e) { _stage = Stage.Finished; MelonLogger.Warning("[BoneCheats] Updater stopped: " + e.Message); }
        }

        // ------------------------------------------------------------------ platform

        /// <summary>
        /// Android signals first (Unity's Application.platform is exactly what BoneLib's IsAndroid uses; .NET's own OS check and the
        /// ANDROID_* env vars back it up). Auto-update is only allowed when the OS is positively Windows and none of those fired.
        /// Anything else gets the manual message, which is the safe side.
        /// </summary>
        private static Platform DetectPlatform()
        {
            bool android = false;
            try { if (Application.platform == RuntimePlatform.Android) android = true; } catch { }
            try { if (!android && OperatingSystem.IsAndroid()) android = true; } catch { }
            try
            {
                if (!android && (Environment.GetEnvironmentVariable("ANDROID_ROOT") != null
                              || Environment.GetEnvironmentVariable("ANDROID_DATA") != null)) android = true;
            }
            catch { }
            if (android) return Platform.Quest;

            try { if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return Platform.Pc; } catch { }
            return Platform.Unknown;
        }

        // ------------------------------------------------------------------ GitHub (pool thread)

        private static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };   // per-call timeouts via CancellationTokenSource
            c.DefaultRequestHeaders.UserAgent.ParseAdd("BoneCheats-Updater/" + CurrentVersion);   // GitHub rejects requests without one
            c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return c;
        }

        private static void CheckWorker()
        {
            try
            {
                if (_platform == Platform.Pc) CleanLeftovers();   // Quest / unknown: no file is ever touched
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
                using (HttpResponseMessage resp = Http.GetAsync(ApiUrl, cts.Token).GetAwaiter().GetResult())
                {
                    if (!resp.IsSuccessStatusCode) { _checkLog = "GitHub answered " + (int)resp.StatusCode + ", skipping update check."; return; }
                    string json = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    Release rel = ParseRelease(json);
                    if (rel == null) { _checkLog = "release response unusable, skipping."; return; }

                    Version latest, current;
                    if (!TryParseVersion(rel.Tag, out latest) || !TryParseVersion(CurrentVersion, out current))
                    { _checkLog = "couldn't read version from tag '" + rel.Tag + "', skipping."; return; }

                    if (latest <= current) { _checkLog = "up to date (" + CurrentVersion + ", latest " + rel.Tag + ")."; return; }

                    _checkLog = "update available: " + CurrentVersion + " -> " + rel.Tag
                              + (rel.DllUrl == null ? " (no " + DllName + " asset on the release!)" : "")
                              + Changelog(rel.Body);
                    _release = rel;
                }
            }
            catch (Exception e) { _checkLog = "update check failed (" + e.GetType().Name + ": " + e.Message + "), mod unaffected."; }
            finally { _checkDone = true; }
        }

        private static Release ParseRelease(string json)
        {
            var root = Json.Parse(json) as Dictionary<string, object>;
            string tag = root == null ? null : Str(root, "tag_name");
            if (tag == null) return null;
            var rel = new Release { Tag = tag, Body = Str(root, "body") };

            object raw;
            var assets = root.TryGetValue("assets", out raw) ? raw as List<object> : null;
            if (assets == null) return rel;

            Dictionary<string, object> best = null; int bestScore = 0;
            foreach (object o in assets)
            {
                var a = o as Dictionary<string, object>;
                if (a == null) continue;
                string name = Str(a, "name"), url = Str(a, "browser_download_url"), state = Str(a, "state");
                if (name == null || url == null) continue;
                if (state != null && state != "uploaded") continue;
                // Only a real .dll counts, so the source zip / any other attachment can never be picked.
                if (!name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                int score = name.Equals(DllName, StringComparison.OrdinalIgnoreCase) ? 2
                          : name.IndexOf("bonecheats", StringComparison.OrdinalIgnoreCase) >= 0 ? 1 : 0;
                if (score > bestScore) { best = a; bestScore = score; }
            }
            if (best == null) return rel;

            rel.DllUrl = Str(best, "browser_download_url");
            object size;
            if (best.TryGetValue("size", out size) && size is double) rel.Size = (long)(double)size;
            string digest = Str(best, "digest");   // "sha256:<hex>", present on newer uploads
            if (digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) rel.Sha256 = digest.Substring(7);
            return rel;
        }

        private static string Str(Dictionary<string, object> d, string key)
        {
            object v;
            return d.TryGetValue(key, out v) ? v as string : null;
        }

        /// <summary>Minimal JSON reader (objects, arrays, strings, numbers, bools, null). Exists so the updater doesn't depend on
        /// System.Text.Json being shipped by every loader (LemonLoader on Quest). Malformed input throws; callers catch.</summary>
        private sealed class Json
        {
            private readonly string _s; private int _i;
            private Json(string s) { _s = s; }
            public static object Parse(string s) { return new Json(s).Val(); }

            private void Ws() { while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++; }

            private object Val()
            {
                Ws();
                char c = _s[_i];
                if (c == '{')
                {
                    _i++; var d = new Dictionary<string, object>(); Ws();
                    if (_s[_i] == '}') { _i++; return d; }
                    while (true) { Ws(); string k = Text(); Ws(); _i++; d[k] = Val(); Ws(); if (_s[_i++] == '}') return d; }
                }
                if (c == '[')
                {
                    _i++; var l = new List<object>(); Ws();
                    if (_s[_i] == ']') { _i++; return l; }
                    while (true) { l.Add(Val()); Ws(); if (_s[_i++] == ']') return l; }
                }
                if (c == '"') return Text();
                int st = _i;
                while (_i < _s.Length && ",}] \t\r\n".IndexOf(_s[_i]) < 0) _i++;
                string t = _s.Substring(st, _i - st);
                if (t == "true") return true;
                if (t == "false") return false;
                if (t == "null") return null;
                return double.Parse(t, CultureInfo.InvariantCulture);
            }

            private string Text()
            {
                _i++; var sb = new StringBuilder();
                while (true)
                {
                    char c = _s[_i++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }
                    c = _s[_i++];
                    switch (c)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u': sb.Append((char)Convert.ToInt32(_s.Substring(_i, 4), 16)); _i += 4; break;
                        default: sb.Append(c); break;   // \" \\ \/
                    }
                }
            }
        }

        private static bool TryParseVersion(string s, out Version v)
        {
            v = null;
            if (s == null) return false;
            Match m = Regex.Match(s, @"\d+(\.\d+){0,3}");   // tolerates "v1.4.0", "1.4.0-beta", "BoneCheats 1.4.0"
            if (!m.Success) return false;
            string t = m.Value;
            while (t.Split('.').Length < 3) t += ".0";
            return Version.TryParse(t, out v);
        }

        private static string Changelog(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return "";
            string b = body.Trim().Replace("\r", "");
            if (b.Length > 600) b = b.Substring(0, 600) + "...";
            return "\n--- changelog ---\n" + b;
        }

        // ------------------------------------------------------------------ PC download + swap (pool thread)

        private static void DownloadWorker(Release rel)
        {
            string download = null;
            try
            {
                if (rel.DllUrl == null) throw new InvalidOperationException("release has no " + DllName + " asset");
                string live = LocateInstalledDll();
                string dir = Path.GetDirectoryName(live);
                download = live + ".download";   // not *.dll, so MelonLoader never picks the partial file up
                string staged = live + ".new";

                long written = 0, expected = rel.Size;
                SHA256 sha = SHA256.Create();
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120)))
                using (HttpResponseMessage resp = Http.GetAsync(rel.DllUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token).GetAwaiter().GetResult())
                {
                    resp.EnsureSuccessStatusCode();
                    if (resp.Content.Headers.ContentLength.HasValue) expected = resp.Content.Headers.ContentLength.Value;
                    using (Stream net = resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                    using (FileStream fs = new FileStream(download, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        byte[] buf = new byte[81920]; int n;
                        while ((n = net.ReadAsync(buf, 0, buf.Length, cts.Token).GetAwaiter().GetResult()) > 0)
                        { fs.Write(buf, 0, n); sha.TransformBlock(buf, 0, n, null, 0); written += n; }
                    }
                    sha.TransformFinalBlock(new byte[0], 0, 0);
                }

                // ---- verify before anything live is touched
                if (written <= 0) throw new InvalidDataException("downloaded file is empty");
                if (expected > 0 && written != expected) throw new InvalidDataException("size mismatch (" + written + " of " + expected + ")");
                if (rel.Sha256 != null && !string.Equals(BitConverter.ToString(sha.Hash).Replace("-", ""), rel.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("SHA-256 mismatch");
                using (FileStream fs = File.OpenRead(download))
                    if (fs.ReadByte() != 'M' || fs.ReadByte() != 'Z') throw new InvalidDataException("not a Windows DLL");
                if (AssemblyName.GetAssemblyName(download).Name != "BoneCheats") throw new InvalidDataException("not the BoneCheats assembly");

                if (File.Exists(staged)) File.Delete(staged);
                File.Move(download, staged);
                download = null;

                _downloadLog = Install(live, staged);
                _download = 1;
            }
            catch (Exception e)
            {
                _downloadLog = "update download failed (" + e.GetType().Name + ": " + e.Message + "). Installed DLL left untouched.";
                try { if (download != null && File.Exists(download)) File.Delete(download); } catch { }
                _download = 2;
            }
        }

        /// <summary>
        /// Windows won't let you overwrite or delete a loaded DLL but does let you rename it, so: live -> .old, staged -> live, and undo
        /// if the second step fails. If even the rename is refused, a hidden one-shot PowerShell waits for this process to exit and then
        /// moves the staged file over the DLL (Move-Item -Force replaces in one step, so there is never a moment without a DLL).
        /// </summary>
        private static string Install(string live, string staged)
        {
            string old = live + ".old";
            try
            {
                if (File.Exists(old)) File.Delete(old);
                File.Move(live, old);
                try { File.Move(staged, live); }
                catch { try { File.Move(old, live); } catch { } throw; }   // roll back: the old DLL is back before we give up
                return "new DLL in place, takes effect on next launch.";
            }
            catch (Exception swapError)
            {
                if (!File.Exists(live)) throw;   // should be impossible after the rollback; never claim success without a DLL
                string ps = "Wait-Process -Id " + Environment.ProcessId + " -ErrorAction SilentlyContinue; Start-Sleep -Seconds 1; "
                          + "Move-Item -LiteralPath '" + staged.Replace("'", "''") + "' -Destination '" + live.Replace("'", "''") + "' -Force";
                var psi = new ProcessStartInfo("powershell.exe",
                    "-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(ps)))
                { UseShellExecute = false, CreateNoWindow = true };
                if (Process.Start(psi) == null) throw new IOException("couldn't schedule the swap (" + swapError.Message + ")");
                return "DLL is locked (" + swapError.Message + "); swap scheduled for when the game exits.";
            }
        }

        private static string LocateInstalledDll()
        {
            string p = null;
            try { p = Assembly.GetExecutingAssembly().Location; } catch { }   // empty when MelonLoader loads the mod from bytes
            if (string.IsNullOrEmpty(p) || !File.Exists(p)) p = Path.Combine(MelonEnvironment.ModsDirectory, DllName);
            if (!File.Exists(p)) throw new FileNotFoundException("couldn't find the installed " + DllName, p);
            return p;
        }

        // Leftovers from a previous update. Runs before the check, so a stale .new can never outlive a launch.
        private static void CleanLeftovers()
        {
            try
            {
                string live = LocateInstalledDll();
                foreach (string ext in new[] { ".old", ".download", ".new" })
                    try { if (File.Exists(live + ext)) File.Delete(live + ext); } catch { }
            }
            catch { }
        }
    }
}
