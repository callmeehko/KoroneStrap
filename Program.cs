using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Drawing;
using DiscordRPC;
using RpcButton = DiscordRPC.Button;

namespace KoroneStrapper
{
    internal static class Program
    {
        private const string DiscordAppId = "1427467434155180115";
        private const string BaseUrl = "https://www.pekora.zip/";
        private const string TargetProcessName = "ProjectXPlayerBeta";

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        [DllImport("kernel32.dll")] private static extern bool AllocConsole();
        [DllImport("kernel32.dll")] private static extern bool FreeConsole();

        private static async Task<int> Main(string[] args)
        {
            bool showConsole = args.Any(a => a.Equals("--console", StringComparison.OrdinalIgnoreCase));
            if (showConsole) { AllocConsole(); Console.WriteLine("Console mode enabled (--console)"); }

            try
            {
                var ticket = ParseTicket(args);
                if (string.IsNullOrWhiteSpace(ticket))
                {
                    Console.Error.WriteLine("Missing --AuthenticationTicket=\"...\"");
                    if (showConsole) { Console.WriteLine("Press any key to exit..."); Console.ReadKey(true); }
                    return 2;
                }

                using var cts = new CancellationTokenSource();
                var cancel = cts.Token;

                var cookies = new CookieContainer();
                cookies.Add(new Cookie(".DOGSECURITY", ticket, "/", "www.pekora.zip"));
                var handler = new HttpClientHandler { CookieContainer = cookies, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate };
                using var http = new HttpClient(handler) { BaseAddress = new Uri(BaseUrl) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("KoroneStrapper/0.1 (+discord-rpc)");

                var me = await GetAuthenticatedUserAsync(http, cancel);
                if (me?.Id is null)
                {
                    Console.Error.WriteLine("Failed to resolve authenticated user.");
                    if (showConsole) { Console.WriteLine("Press any key to exit..."); Console.ReadKey(true); }
                    return 3;
                }
                Console.WriteLine($"Authenticated as userId={me.Id}");

                var processWatchTask = WatchForProcessExitAsync(TargetProcessName, cancel);

                using var rpc = new DiscordRpcClient(DiscordAppId);
                rpc.Initialize();
                Console.WriteLine("Discord RPC initialized.");

                var set = await PollPresenceUntilSetOnceAsync(http, rpc, me.Id.Value, 5, cancel);
                if (!set.success)
                {
                    Console.WriteLine("No presence after 5 polls. Exiting.");
                    return 4;
                }

                Console.WriteLine("Presence set. Waiting for ProjectXPlayerBeta to exit…");

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                int presenceEnabled = 1;
                string latestState = set.initialState ?? "";
                bool IsEnabled() => Volatile.Read(ref presenceEnabled) == 1;
                void SetEnabled(bool enabled) => Volatile.Write(ref presenceEnabled, enabled ? 1 : 0);

                void ApplyPresence(string state)
                {
                    latestState = state ?? "";
                    try
                    {
                        if (!IsEnabled())
                        {
                            rpc.ClearPresence();
                            return;
                        }

                        var rich = new RichPresence
                        {
                            Details = set.gameName,
                            State = latestState,
                            Timestamps = new Timestamps { Start = set.startedAtUtc },
                            Assets = new Assets
                            {
                                LargeImageKey = string.IsNullOrWhiteSpace(set.iconUrl) ? null : set.iconUrl,
                                LargeImageText = set.gameName,
                                SmallImageKey = "kctatws"
                            },
                            Buttons = new[] { new RpcButton { Label = "View Game", Url = set.gameUrl } }
                        };
                        rpc.SetPresence(rich);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("Failed to apply presence: " + ex.Message);
                    }
                }

                using var tray = new TrayContext(set.placeId, set.gameName, set.gameUrl);

                tray.PresenceToggled += enabled =>
                {
                    SetEnabled(enabled);
                    if (enabled)
                    {
                        Console.WriteLine("Rich Presence enabled via tray; reapplying…");
                        ApplyPresence(latestState);
                    }
                    else
                    {
                        Console.WriteLine("Rich Presence disabled via tray; clearing…");
                        try { rpc.ClearPresence(); } catch { }
                    }
                };

                var logCts = new CancellationTokenSource();
                using var logWatcher = new KoroneLogWatcher(newState =>
                {
                    try
                    {
                        ApplyPresence(newState ?? "");
                        Console.WriteLine($"Presence state updated via SDK → {newState}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("Failed to update presence state: " + ex.Message);
                    }
                });

                _ = processWatchTask.ContinueWith(_ => tray.RequestExit(), TaskScheduler.FromCurrentSynchronizationContext());
                _ = Task.Run(() => logWatcher.StartAsync(logCts.Token));

                Application.Run(tray);

                try { logCts.Cancel(); } catch { }
                rpc.ClearPresence();
                Console.WriteLine("Game closed. Presence cleared. Exiting.");
                return 0;
            }
            catch (OperationCanceledException) { return 0; }
            catch (Exception ex) { Console.Error.WriteLine("Fatal error: " + ex); return 1; }
            finally
            {
                if (showConsole)
                {
                    Console.WriteLine("Press any key to close…");
                    Console.ReadKey(true);
                    FreeConsole();
                }
            }
        }

        private static string ParseTicket(string[] args)
        {
            foreach (var a in args)
                if (a.StartsWith("--AuthenticationTicket=", StringComparison.OrdinalIgnoreCase))
                    return a[(a.IndexOf('=') + 1)..].Trim('"');
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i].Equals("-t", StringComparison.OrdinalIgnoreCase))
                    return args[i + 1].Trim('"');
            return null;
        }

        private static async Task<UserMe> GetAuthenticatedUserAsync(HttpClient http, CancellationToken cancel)
        {
            using var resp = await http.GetAsync("apisite/users/v1/users/authenticated", cancel);
            resp.EnsureSuccessStatusCode();
            await using var s = await resp.Content.ReadAsStreamAsync(cancel);
            return await JsonSerializer.DeserializeAsync<UserMe>(s, JsonOpts, cancel);
        }

        private static async Task<(bool success, long placeId, string gameName, string gameUrl, DateTime startedAtUtc, string iconUrl, string initialState)>
            PollPresenceUntilSetOnceAsync(HttpClient http, DiscordRpcClient rpc, long userId, int maxAttempts, CancellationToken cancel)
        {
            var delay = TimeSpan.FromSeconds(5);
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                cancel.ThrowIfCancellationRequested();
                try
                {
                    var presence = await GetUserPresenceAsync(http, userId.ToString(), cancel);
                    var p = presence?.UserPresences?.FirstOrDefault();
                    if (p != null && string.Equals(p.UserPresenceType, "InGame", StringComparison.OrdinalIgnoreCase))
                    {
                        var placeId = p.PlaceId;
                        var place = await GetPlaceAsync(http, placeId, cancel);
                        if (place == null || string.IsNullOrWhiteSpace(place.Name))
                        {
                            Console.WriteLine($"Presence found but place details missing for {placeId}, retrying… ({attempt}/{maxAttempts})");
                        }
                        else
                        {
                            var icon = await GetIconAsync(http, place.UniverseId, cancel);
                            var iconUrl = icon?.Data?.FirstOrDefault()?.ImageUrl;
                            var startedAt = DateTimeOffset.UtcNow;
                            var gameUrl = $"https://www.pekora.zip/games/{placeId}/--";
                            var initialState = $"{place.Year}";

                            var rich = new RichPresence
                            {
                                Details = place.Name,
                                State = initialState,
                                Timestamps = new Timestamps { Start = startedAt.UtcDateTime },
                                Assets = new Assets
                                {
                                    LargeImageKey = string.IsNullOrWhiteSpace(iconUrl) ? null : iconUrl,
                                    LargeImageText = place.Name,
                                    SmallImageKey = "kctatws"
                                },
                                Buttons = new[] { new RpcButton { Label = "View Game", Url = gameUrl } }
                            };
                            rpc.SetPresence(rich);
                            Console.WriteLine($"Presence set → {place.Name} (placeId {placeId}).");
                            return (true, placeId, place.Name, gameUrl, startedAt.UtcDateTime, iconUrl, initialState);
                        }
                    }
                    else
                    {
                        Console.WriteLine($"User not in-game yet; polling again… ({attempt}/{maxAttempts})");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Poll error: {ex.Message} ({attempt}/{maxAttempts})");
                }
                if (attempt < maxAttempts) await Task.Delay(delay, cancel);
            }
            return (false, 0L, null, null, default, null, null);
        }

        private static async Task<UserPresenceResponse> GetUserPresenceAsync(HttpClient http, string userId, CancellationToken cancel)
        {
            var payload = new UserIdsPayload { UserIds = new[] { userId } };
            using var content = new StringContent(System.Text.Json.JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var resp = await http.PostAsync("apisite/presence/v1/presence/users", content, cancel);
            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync(cancel);
            return await System.Text.Json.JsonSerializer.DeserializeAsync<UserPresenceResponse>(stream, JsonOpts, cancel);
        }

        private static async Task<PlaceDetail> GetPlaceAsync(HttpClient http, long placeId, CancellationToken cancel)
        {
            using var resp = await http.GetAsync($"apisite/games/v1/games/multiget-place-details?placeIds={placeId}", cancel);
            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync(cancel);
            var arr = await System.Text.Json.JsonSerializer.DeserializeAsync<PlaceDetail[]>(stream, JsonOpts, cancel);
            return arr?.FirstOrDefault(p => p.PlaceId == placeId);
        }

        private static async Task<IconResponse> GetIconAsync(HttpClient http, long universeId, CancellationToken cancel)
        {
            using var resp = await http.GetAsync($"apisite/thumbnails/v1/games/icons?size=150x150&format=png&universeIds={universeId}", cancel);
            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync(cancel);
            return await System.Text.Json.JsonSerializer.DeserializeAsync<IconResponse>(stream, JsonOpts, cancel);
        }

        private static async Task WatchForProcessExitAsync(string processName, CancellationToken cancel)
        {
            while (!cancel.IsCancellationRequested)
            {
                var procs = Process.GetProcessesByName(processName);
                if (procs.Length > 0)
                {
                    var waits = procs.Select(p => WaitForExitAsyncSafe(p, cancel)).ToArray();
                    await Task.WhenAll(waits);
                    var remaining = Process.GetProcessesByName(processName);
                    if (remaining.Length == 0) break;
                    continue;
                }
                await Task.Delay(1000, cancel);
            }
        }

        private static async Task WaitForExitAsyncSafe(Process p, CancellationToken cancel)
        {
            try
            {
                using (p)
                {
#if NET8_0_OR_GREATER
                    await p.WaitForExitAsync(cancel);
#else
                    await Task.Run(() => p.WaitForExit(), cancel);
#endif
                }
            }
            catch { }
        }

        private sealed class TrayContext : ApplicationContext
        {
            private readonly NotifyIcon ni;
            private readonly ContextMenuStrip menu;
            private readonly ToolStripMenuItem openItem;
            private readonly ToolStripMenuItem exitItem;
            private readonly ToolStripMenuItem presenceToggleItem;
            private readonly MethodInfo showMenuMi;
            private readonly Icon trayIcon;

            public event Action<bool> PresenceToggled;

            public TrayContext(long placeId, string gameName, string gameUrl)
            {
                menu = new ContextMenuStrip();

                var infoLabel = new ToolStripMenuItem($"Game: {gameName}\nPlaceId: {placeId}") { Enabled = false };
                openItem = new ToolStripMenuItem("Open Game Page");
                exitItem = new ToolStripMenuItem("Exit");

                presenceToggleItem = new ToolStripMenuItem("Discord Rich Presence")
                {
                    CheckOnClick = true,
                    Checked = true,
                    ToolTipText = "Enable/disable Discord Rich Presence updates."
                };
                presenceToggleItem.CheckedChanged += (_, __) =>
                {
                    try { PresenceToggled?.Invoke(presenceToggleItem.Checked); } catch { }
                };

                openItem.Click += (_, __) =>
                {
                    if (!string.IsNullOrWhiteSpace(gameUrl))
                        try { Process.Start(new ProcessStartInfo { FileName = gameUrl, UseShellExecute = true }); } catch { }
                };
                exitItem.Click += (_, __) => ExitThread();

                menu.Items.Add(infoLabel);
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add(openItem);
                menu.Items.Add(presenceToggleItem);
                menu.Items.Add(exitItem);

                trayIcon = LoadEmbeddedIcon("KoroneStrapper.Resources.icon.ico");

                ni = new NotifyIcon
                {
                    Icon = trayIcon,
                    Visible = true,
                    Text = $"{gameName} (Korone Strapper)",
                    ContextMenuStrip = menu
                };

                showMenuMi = typeof(NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic);
                ni.MouseClick += (_, e) => { if (e.Button == MouseButtons.Right || e.Button == MouseButtons.Left) ShowShellMenu(); };
            }

            private static Icon LoadEmbeddedIcon(string resourceName)
            {
                var asm = Assembly.GetExecutingAssembly();
                using var s = asm.GetManifestResourceStream(resourceName)
                    ?? throw new InvalidOperationException($"Missing resource: {resourceName}");
                using var temp = new Icon(s);
                return (Icon)temp.Clone();
            }

            private void ShowShellMenu()
            {
                try { if (showMenuMi != null) { showMenuMi.Invoke(ni, null); return; } } catch { }
                menu.Show(Cursor.Position);
            }

            public void RequestExit() => ExitThread();

            protected override void ExitThreadCore()
            {
                try
                {
                    ni.Visible = false;
                    ni.Icon?.Dispose();
                    trayIcon?.Dispose();
                    ni.Dispose();
                }
                catch { }
                base.ExitThreadCore();
            }
        }

        private sealed class UserMe { [JsonPropertyName("id")] public long? Id { get; set; } }
        private sealed class UserIdsPayload { [JsonPropertyName("userIds")] public string[] UserIds { get; set; } }
        private sealed class UserPresenceResponse { [JsonPropertyName("userPresences")] public UserPresence[] UserPresences { get; set; } }
        private sealed class UserPresence
        {
            [JsonPropertyName("userPresenceType")] public string UserPresenceType { get; set; }
            [JsonPropertyName("placeId")] public long PlaceId { get; set; }
            [JsonPropertyName("userId")] public long UserId { get; set; }
            [JsonPropertyName("lastOnline")] public DateTimeOffset LastOnline { get; set; }
        }
        private sealed class PlaceDetail
        {
            [JsonPropertyName("placeId")] public long PlaceId { get; set; }
            [JsonPropertyName("name")] public string Name { get; set; }
            [JsonPropertyName("universeId")] public long UniverseId { get; set; }
            [JsonPropertyName("year")] public long Year { get; set; }
        }
        private sealed class IconResponse { [JsonPropertyName("data")] public IconData[] Data { get; set; } }
        private sealed class IconData
        {
            [JsonPropertyName("imageUrl")] public string ImageUrl { get; set; }
            [JsonPropertyName("targetId")] public long TargetId { get; set; }
            [JsonPropertyName("state")] public string State { get; set; }
        }

        private sealed class KoroneLogWatcher : IDisposable
        {
            private readonly Regex lineRegex = new(@"\[\s*KORONESTRAPSDK\s*\]\s*\(?([A-Za-z0-9+/=]+)\)?", RegexOptions.Compiled);
            private readonly Action<string> onPresenceState;
            private FileSystemWatcher fsw;
            private FileStream fs;
            private StreamReader sr;
            private readonly string logsDir;
            private string currentLog;
            private readonly object reopenLock = new();
            private volatile bool requestReopen;

            public KoroneLogWatcher(Action<string> onPresenceState)
            {
                this.onPresenceState = onPresenceState;
                logsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox", "logs");
            }

            public async Task StartAsync(CancellationToken cancel)
            {
                if (!Directory.Exists(logsDir)) return;

                fsw = new FileSystemWatcher(logsDir)
                {
                    Filter = "*.*",
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.LastWrite | NotifyFilters.Size
                };
                fsw.Created += (_, e) => { if (IsLogFile(e.FullPath)) requestReopen = true; };
                fsw.Renamed += (_, e) => { if (IsLogFile(e.FullPath)) requestReopen = true; };
                fsw.EnableRaisingEvents = true;

                await Task.Delay(300, cancel);
                if (!OpenLatestLog()) return;

                while (!cancel.IsCancellationRequested)
                {
                    var line = await sr.ReadLineAsync();
                    if (line is null)
                    {
                        if (requestReopen || !File.Exists(currentLog))
                        {
                            lock (reopenLock)
                            {
                                requestReopen = false;
                                OpenLatestLog(reopen: true);
                            }
                        }
                        await Task.Delay(200, cancel);
                        continue;
                    }
                    TryHandleLine(line);
                }
            }

            private static bool IsLogFile(string path)
            {
                var ext = Path.GetExtension(path);
                return string.Equals(ext, ".log", StringComparison.OrdinalIgnoreCase) || string.Equals(ext, ".txt", StringComparison.OrdinalIgnoreCase);
            }

            private bool OpenLatestLog(bool reopen = false)
            {
                try
                {
                    var latest = GetNewestCreatedLogOrTxt(logsDir);
                    if (string.IsNullOrEmpty(latest))
                        return false;

                    if (!reopen && currentLog == latest && fs != null && sr != null)
                        return true;

                    DisposeStream();
                    Thread.Sleep(100);

                    currentLog = latest;
                    fs = new FileStream(currentLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                    fs.Seek(0, SeekOrigin.End);
                    sr.DiscardBufferedData();
                    Console.WriteLine($"Tailing log: {currentLog}");
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            private static string GetNewestCreatedLogOrTxt(string dir)
                => new DirectoryInfo(dir)
                    .GetFiles()
                    .Where(f => string.Equals(f.Extension, ".log", StringComparison.OrdinalIgnoreCase) || string.Equals(f.Extension, ".txt", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(f => f.CreationTimeUtc)
                    .FirstOrDefault()?.FullName;

            private void TryHandleLine(string line)
            {
                var m = lineRegex.Match(line);
                if (!m.Success) return;

                var b64 = m.Groups[1].Value.Trim();
                try
                {
                    var bytes = Convert.FromBase64String(b64);
                    var json = Encoding.UTF8.GetString(bytes);

                    using var doc = JsonDocument.Parse(json);
                    if (!doc.RootElement.TryGetProperty("activityType", out var t) || !doc.RootElement.TryGetProperty("value", out var v))
                        return;

                    var type = t.GetString();
                    if (!string.Equals(type, "presenceStateChange", StringComparison.OrdinalIgnoreCase))
                        return;

                    var value = v.GetString() ?? "";
                    if (value.Length > 200) value = value[..200];

                    onPresenceState(value);
                }
                catch { }
            }

            private void DisposeStream()
            {
                try { sr?.Dispose(); } catch { }
                try { fs?.Dispose(); } catch { }
                sr = null;
                fs = null;
            }

            public void Dispose()
            {
                try { fsw?.Dispose(); } catch { }
                DisposeStream();
            }
        }
    }
}
