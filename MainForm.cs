using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;
using NAudio.Wave;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TCPTunnel
{
    public class MainForm : Form
    {
        private WebView2 webView;
        private NotifyIcon trayIcon;
        private bool connected;
        private bool trayEnabled;
        private long bytesUp, bytesDown, lastUp, lastDown;
        private long baselineSentBytes, baselineRecvBytes;
        private bool trafficBaselineReady;
        private List<string> logs = new();
        private string settingsPath;
        private string accountSessionPath;
        private readonly object notifySoundLock = new();
        private readonly object runtimeLock = new();
        private WaveOutEvent notifyOutput;
        private AudioFileReader notifyReader;
        private Icon trayActiveIcon;
        private Icon trayInactiveIcon;
        private readonly Dictionary<string, TunnelRuntimeSession> tunnelRuntimes = new();
        private string currentConfigToken = string.Empty;
        private string currentFrpUser = string.Empty;
        private int currentRemotePort;
        private int currentLocalPort;
        private string currentServerHost = string.Empty;
        private int currentServerPort;
        private string sentStartupLogKey = string.Empty;
        private readonly object startupLogLock = new();
        private const string FrpcVersion = "0.69.0";
        private const string FrpcExeSha256 = "f8467a4f8d57cde5ba808a764b528147acd81db0955e51bee80fde0fea0e5243";
        private const string PublicClientNotice = "Неофициальный клиент";
        private const int StartupReadyHoldMs = 1200;
        private const int StartupVerificationTimeoutMs = 5000;
        private const int StartupConnectTimeoutMs = 10000;
        private static readonly object embeddedFrpcLock = new();
        private static string embeddedFrpcPath = string.Empty;
        private const string StateSecretPrefix = "dpapi-v1:";
        private const string TunnelTypeTcp = "tcp";
        private const string TunnelTypeTcpUdp = "tcp_udp";

        private sealed record TunnelConfigSpec(
            string ServerHost,
            int ServerPort,
            string FrpUser,
            int RemotePort,
            int LocalPort,
            string AuthToken,
            string LoginToken,
            string TunnelType,
            string ExpiresAt);

        private sealed record AccountUserState(
            int Id,
            string Name,
            string Email,
            string Role,
            decimal Balance,
            bool EmailVerified,
            bool TwoFaEnabled,
            string CreatedAt);

        private sealed record AccountTunnelState(
            int Id,
            string FrpUser,
            int RemotePort,
            int LocalPort,
            string TunnelType,
            string Status,
            string ExpiresAt,
            int PlanDays);

        private sealed record AccountSessionState(
            string Token,
            AccountUserState User);

        private sealed class TunnelRuntimeSession
        {
            public string Key { get; init; } = string.Empty;
            public string DisplayName { get; set; } = string.Empty;
            public Process Process { get; set; }
            public CancellationTokenSource Cancellation { get; set; }
            public bool Connected { get; set; }
            public string ServerHost { get; set; } = string.Empty;
            public int ServerPort { get; set; }
            public string FrpUser { get; set; } = string.Empty;
            public int RemotePort { get; set; }
            public int LocalPort { get; set; }
            public string LogToken { get; set; } = string.Empty;
        }

        public MainForm()
        {
            settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".tcptunnel_settings.json");
            accountSessionPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".tcptunnel_account.json");
            this.Text = "TCP Tunnel";
            this.ShowIcon = true;
            this.AutoScaleMode = AutoScaleMode.Dpi;
            this.ClientSize = new System.Drawing.Size(470, 680);
            this.FormBorderStyle = FormBorderStyle.FixedSingle;
            this.MaximizeBox = false;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = System.Drawing.Color.FromArgb(26, 26, 26);
            this.MinimizeBox = true;
            this.MaximumSize = new System.Drawing.Size(486, 720);
            this.MinimumSize = new System.Drawing.Size(486, 720);
            try
            {
                string appIconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui", "icon.ico");
                if (File.Exists(appIconPath))
                {
                    this.Icon = new System.Drawing.Icon(appIconPath);
                }
                else
                {
                    this.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
                }
            }
            catch
            {
                this.Icon = SystemIcons.Application;
            }

            webView = new WebView2();
            webView.Dock = DockStyle.Fill;
            this.Controls.Add(webView);

            this.Shown += (_, __) =>
            {
                this.ClientSize = new System.Drawing.Size(470, 680);
            };

            InitWebView();
            AppendLocalStartupLog();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            TryApplySystemTitleBarTheme();
        }

        private async void InitWebView()
        {
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Path.GetTempPath(), "TCPTunnel_WV2"));
            await webView.EnsureCoreWebView2Async(env);
            webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;

            webView.CoreWebView2.WebMessageReceived += OnWebMessage;
            webView.CoreWebView2.NavigationCompleted += (s, e) =>
            {
                webView.CoreWebView2.ExecuteScriptAsync(@"
                    const makeReplyKey = (prefix) => `${prefix}_${Date.now().toString(36)}_${Math.random().toString(36).slice(2, 8)}`;
                    const postWithReply = (cmd, payload, prefix) => new Promise(r => {
                        const responseKey = makeReplyKey(prefix);
                        window[responseKey] = r;
                        window.chrome.webview.postMessage(JSON.stringify({cmd, response_key: responseKey, ...payload}));
                    });
                    window.pywebview = { api: {
                        minimize: () => window.chrome.webview.postMessage(JSON.stringify({cmd:'minimize'})),
                        close_app: () => window.chrome.webview.postMessage(JSON.stringify({cmd:'close_app'})),
                        move_window: (dx,dy) => window.chrome.webview.postMessage(JSON.stringify({cmd:'move_window',dx,dy})),
                        start_drag: () => window.chrome.webview.postMessage(JSON.stringify({cmd:'start_drag'})),
                        load_state: () => new Promise(r => { window._resolve_state = r; window.chrome.webview.postMessage(JSON.stringify({cmd:'load_state'})); }),
                        save_state: (s) => window.chrome.webview.postMessage(JSON.stringify({cmd:'save_state',data:s})),
                        set_tray_enabled: (e) => window.chrome.webview.postMessage(JSON.stringify({cmd:'set_tray_enabled',enabled:e})),
                        connect_string: (s, profileKey) => postWithReply('connect_string', {data:s, profile_key:profileKey || ''}, 'connect'),
                        connect_config: (s, profileKey) => postWithReply('connect_config', {data:s, profile_key:profileKey || ''}, 'connect'),
                        disconnect: () => new Promise(r => { window._resolve_disconnect = r; window.chrome.webview.postMessage(JSON.stringify({cmd:'disconnect'})); }),
                        disconnect_tunnel: (profileKey) => postWithReply('disconnect_tunnel', {profile_key:profileKey || ''}, 'disconnect'),
                        get_status: (profileKey) => new Promise(r => { window._resolve_status = r; window.chrome.webview.postMessage(JSON.stringify({cmd:'get_status',profile_key:profileKey || ''})); }),
                        get_logs: () => new Promise(r => { window._resolve_logs = r; window.chrome.webview.postMessage(JSON.stringify({cmd:'get_logs'})); }),
                        get_traffic: () => new Promise(r => { window._resolve_traffic = r; window.chrome.webview.postMessage(JSON.stringify({cmd:'get_traffic'})); }),
                        play_notify_sound: () => new Promise(r => { window._resolve_play_notify_sound = r; window.chrome.webview.postMessage(JSON.stringify({cmd:'play_notify_sound'})); }),
                        pick_file: () => new Promise(r => { window._resolve_file = r; window.chrome.webview.postMessage(JSON.stringify({cmd:'pick_file'})); }),
                        get_update_info: () => new Promise(r => { window._resolve_update_info = r; window.chrome.webview.postMessage(JSON.stringify({cmd:'get_update_info'})); }),
                        account_get_state: () => new Promise(r => { window._resolve_account_state = r; window.chrome.webview.postMessage(JSON.stringify({cmd:'account_get_state'})); }),
                        account_refresh: () => new Promise(r => { window._resolve_account_state = r; window.chrome.webview.postMessage(JSON.stringify({cmd:'account_refresh'})); }),
                        account_login: (login, password) => new Promise(r => { window._resolve_account_auth = r; window.chrome.webview.postMessage(JSON.stringify({cmd:'account_login',login,password})); }),
                        account_verify_2fa: (challengeId, code) => new Promise(r => { window._resolve_account_auth = r; window.chrome.webview.postMessage(JSON.stringify({cmd:'account_verify_2fa',challenge_id:challengeId,code})); }),
                        account_resend_2fa: (challengeId) => new Promise(r => { window._resolve_account_resend = r; window.chrome.webview.postMessage(JSON.stringify({cmd:'account_resend_2fa',challenge_id:challengeId})); }),
                        account_logout: () => new Promise(r => { window._resolve_account_logout = r; window.chrome.webview.postMessage(JSON.stringify({cmd:'account_logout'})); }),
                        account_connect_tunnel: (tunnelId, profileKey) => postWithReply('account_connect_tunnel', {tunnel_id:tunnelId, profile_key:profileKey || ''}, 'connect'),
                        client_ready: () => window.chrome.webview.postMessage(JSON.stringify({cmd:'client_ready'})),
                        exit_app: () => window.chrome.webview.postMessage(JSON.stringify({cmd:'exit_app'})),
                        set_autostart: (e) => window.chrome.webview.postMessage(JSON.stringify({cmd:'set_autostart',enabled:e})),
                        get_autostart: () => new Promise(r => { window._resolve_autostart = r; window.chrome.webview.postMessage(JSON.stringify({cmd:'get_autostart'})); }),
                    }};
                    window.addEventListener('contextmenu', (event) => {
                        event.preventDefault();
                    }, true);
                    (() => {
                        let rightDrag = false;
                        let lastX = 0;
                        let lastY = 0;

                        const stopDrag = () => { rightDrag = false; };

                        window.addEventListener('mousedown', (event) => {
                            if (event.button !== 2) return;
                            rightDrag = true;
                            lastX = event.screenX;
                            lastY = event.screenY;
                            event.preventDefault();
                        }, true);

                        window.addEventListener('mousemove', (event) => {
                            if (!rightDrag) return;
                            const dx = event.screenX - lastX;
                            const dy = event.screenY - lastY;
                            lastX = event.screenX;
                            lastY = event.screenY;
                            if (dx === 0 && dy === 0) return;
                            window.chrome.webview.postMessage(JSON.stringify({ cmd: 'move_window', dx, dy }));
                        }, true);

                        window.addEventListener('mouseup', (event) => {
                            if (event.button === 2) stopDrag();
                        }, true);
                        window.addEventListener('blur', stopDrag, true);
                        window.addEventListener('mouseleave', stopDrag, true);
                    })();
                    window.dispatchEvent(new Event('pywebviewready'));
                ");
            };

            string htmlPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui", "index.html");
            webView.CoreWebView2.Navigate("file:///" + htmlPath.Replace("\\", "/"));
        }

        private void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            var msg = JObject.Parse(e.WebMessageAsJson is string s && s.StartsWith("\"") ? JsonConvert.DeserializeObject<string>(e.WebMessageAsJson) : e.WebMessageAsJson);
            string cmd = msg["cmd"]?.ToString() ?? "";
            string responseKey = msg["response_key"]?.ToString() ?? string.Empty;

            switch (cmd)
            {
                case "minimize": this.Invoke(() => this.WindowState = FormWindowState.Minimized); break;
                case "close_app": CloseApp(); break;
                case "move_window": MoveWin(msg); break;
                case "start_drag": StartDrag(); break;
                case "load_state": Respond(ResolveResponseKey(responseKey, "_resolve_state"), LoadState()); break;
                case "save_state": SaveState(msg["data"]?.ToString() ?? "{}"); break;
                case "set_tray_enabled": SetTray(msg["enabled"]?.ToObject<bool>() ?? false); break;
                case "connect_string": Task.Run(() => ConnectString(msg["data"]?.ToString() ?? "", msg["profile_key"]?.ToString() ?? string.Empty, responseKey)); break;
                case "connect_config": Task.Run(() => ConnectConfig(msg["data"]?.ToString() ?? "", msg["profile_key"]?.ToString() ?? string.Empty, responseKey)); break;
                case "disconnect": Task.Run(() => Disconnect(string.Empty, responseKey)); break;
                case "disconnect_tunnel": Task.Run(() => Disconnect(msg["profile_key"]?.ToString() ?? string.Empty, responseKey)); break;
                case "get_status": Respond(ResolveResponseKey(responseKey, "_resolve_status"), JsonConvert.SerializeObject(new
                {
                    connected,
                    active_profile_ids = GetActiveRuntimeKeys(),
                    selected_connected = IsRuntimeActive(msg["profile_key"]?.ToString() ?? string.Empty),
                    active_count = GetActiveRuntimeCount(),
                })); break;
                case "get_logs": Respond(ResolveResponseKey(responseKey, "_resolve_logs"), JsonConvert.SerializeObject(logs.ToArray())); break;
                case "get_traffic": GetTraffic(); break;
                case "pick_file": PickFile(); break;
                case "get_update_info": Respond(ResolveResponseKey(responseKey, "_resolve_update_info"), JsonConvert.SerializeObject(new
                {
                    success = true,
                    current_version = NormalizeVersion(Application.ProductVersion),
                    latest_version = NormalizeVersion(Application.ProductVersion),
                    download_url = string.Empty,
                    must_update = false,
                })); break;
                case "account_get_state":
                case "account_refresh": Respond(ResolveResponseKey(responseKey, "_resolve_account_state"), JsonConvert.SerializeObject(new { success = false, authenticated = false, error = "Аккаунт отключён в публичной версии" })); break;
                case "account_login": Respond(ResolveResponseKey(responseKey, "_resolve_account_auth"), JsonConvert.SerializeObject(new { success = false, error = "Аккаунт отключён в публичной версии" })); break;
                case "account_verify_2fa": Respond(ResolveResponseKey(responseKey, "_resolve_account_auth"), JsonConvert.SerializeObject(new { success = false, error = "Аккаунт отключён в публичной версии" })); break;
                case "account_resend_2fa": Respond(ResolveResponseKey(responseKey, "_resolve_account_resend"), JsonConvert.SerializeObject(new { success = false, error = "Аккаунт отключён в публичной версии" })); break;
                case "account_logout": Respond(ResolveResponseKey(responseKey, "_resolve_account_logout"), JsonConvert.SerializeObject(new { success = true })); break;
                case "account_connect_tunnel": Respond(ResolveResponseKey(responseKey, "_resolve_connect"), JsonConvert.SerializeObject(new { success = false, error = "Аккаунт отключён в публичной версии" })); break;
                case "client_ready": Task.Run(AppendLocalStartupLog); break;
                case "play_notify_sound": PlayNotifySound(); Respond(ResolveResponseKey(responseKey, "_resolve_play_notify_sound"), JsonConvert.SerializeObject(new { success = true })); break;
                case "exit_app": ForceExitApp(); break;
                case "set_autostart": SetAutostart(msg["enabled"]?.ToObject<bool>() ?? false); break;
                case "get_autostart": Respond(ResolveResponseKey(responseKey, "_resolve_autostart"), JsonConvert.SerializeObject(new { enabled = GetAutostartState() })); break;
            }
        }

        private void Respond(string resolver, string json)
        {
            this.Invoke(() => webView.CoreWebView2.ExecuteScriptAsync($"if(window.{resolver}) window.{resolver}({JsonConvert.SerializeObject(json)});"));
        }

        private static string ResolveResponseKey(string responseKey, string fallback)
        {
            return string.IsNullOrWhiteSpace(responseKey) ? fallback : responseKey;
        }

        private void MoveWin(JObject msg)
        {
            int dx = msg["dx"]?.ToObject<int>() ?? 0;
            int dy = msg["dy"]?.ToObject<int>() ?? 0;
            this.Invoke(() => { this.Location = new System.Drawing.Point(this.Location.X + dx, this.Location.Y + dy); });
        }

        private void CloseApp()
        {
            if (trayEnabled && trayIcon != null)
            {
                this.Invoke(() => this.Hide());
            }
            else
            {
                DoDisconnect();
                trayIcon?.Dispose();
                Environment.Exit(0);
            }
        }

        private void ForceExitApp()
        {
            try { DoDisconnect(); } catch { }
            try { trayIcon?.Dispose(); } catch { }
            trayIcon = null;
            lock (notifySoundLock)
            {
                try { notifyOutput?.Stop(); } catch { }
                try { notifyOutput?.Dispose(); } catch { }
                try { notifyReader?.Dispose(); } catch { }
                notifyOutput = null;
                notifyReader = null;
            }
            try { trayActiveIcon?.Dispose(); } catch { }
            try { trayInactiveIcon?.Dispose(); } catch { }
            trayActiveIcon = null;
            trayInactiveIcon = null;
            Environment.Exit(0);
        }

        private async Task<string> GetUpdateInfoJsonAsync()
        {
            var localVersion = NormalizeVersion(Application.ProductVersion);
            return JsonConvert.SerializeObject(new
            {
                success = true,
                current_version = localVersion,
                latest_version = localVersion,
                download_url = string.Empty,
                must_update = false,
            });
        }

        private Task SendRuntimeEventLogAsync(string eventType, string frpUser, int remotePort, int localPort, string logToken, bool dedupeStartup = false)
        {
            var safeEventType = (eventType ?? string.Empty).Trim();
            var safeFrpUser = (frpUser ?? string.Empty).Trim();
            var safeToken = (logToken ?? string.Empty).Trim();
            var portText = remotePort > 0 && localPort > 0 ? $" {remotePort}->{localPort}" : string.Empty;
            var source = string.IsNullOrWhiteSpace(safeEventType) ? "event" : safeEventType;

            lock (startupLogLock)
            {
                if (dedupeStartup)
                {
                    var dedupeKey = $"{source}|{safeFrpUser}|{portText}|{safeToken}|{Environment.MachineName}|{Environment.UserName}";
                    if (string.Equals(sentStartupLogKey, dedupeKey, StringComparison.Ordinal))
                    {
                        return Task.CompletedTask;
                    }

                    sentStartupLogKey = dedupeKey;
                }
            }

            var suffix = string.IsNullOrWhiteSpace(safeFrpUser) ? string.Empty : $" [{safeFrpUser}]";
            logs.Add($"{PublicClientNotice}: {source}{suffix}{portText} {Environment.UserName}@{Environment.MachineName}".Trim());
            return Task.CompletedTask;
        }

        private Task SendStartupLogAsync()
        {
            AppendLocalStartupLog();
            return Task.CompletedTask;
        }

        private void AppendLocalStartupLog()
        {
            logs.Add($"{PublicClientNotice}: {Environment.UserName} @ {Environment.MachineName}");
        }

        private void NotifyServerTunnelDisconnect(TunnelRuntimeSession runtime)
        {
            if (runtime == null || !runtime.Connected)
            {
                return;
            }

            Task.Run(() => SendRuntimeEventLogAsync("disconnect", runtime.FrpUser ?? string.Empty, runtime.RemotePort, runtime.LocalPort, string.Empty));
        }

        private (string frpUser, int remotePort, int localPort, string logToken) MergeClientContexts(
            (string frpUser, int remotePort, int localPort, string logToken) stateContext,
            (string frpUser, int remotePort, int localPort, string logToken) runtimeContext)
        {
            var frpUser = !string.IsNullOrWhiteSpace(runtimeContext.frpUser) ? runtimeContext.frpUser : stateContext.frpUser;
            var remotePort = runtimeContext.remotePort > 0 ? runtimeContext.remotePort : stateContext.remotePort;
            var localPort = runtimeContext.localPort > 0 ? runtimeContext.localPort : stateContext.localPort;
            var logToken = !string.IsNullOrWhiteSpace(runtimeContext.logToken) ? runtimeContext.logToken : stateContext.logToken;
            return (frpUser, remotePort, localPort, logToken);
        }

        private (string frpUser, int remotePort, int localPort, string logToken) GetRuntimeClientContext()
        {
            return (currentFrpUser, currentRemotePort, currentLocalPort, currentConfigToken);
        }

        private static string NormalizeRuntimeKey(string runtimeKey, string fallbackPrefix)
        {
            var normalized = (runtimeKey ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(normalized)
                ? $"{fallbackPrefix}_{Guid.NewGuid():N}"
                : normalized;
        }

        private TunnelRuntimeSession GetOrCreateRuntimeSession(string runtimeKey, string displayName)
        {
            lock (runtimeLock)
            {
                if (!tunnelRuntimes.TryGetValue(runtimeKey, out var runtime))
                {
                    runtime = new TunnelRuntimeSession
                    {
                        Key = runtimeKey,
                    };
                    tunnelRuntimes[runtimeKey] = runtime;
                }

                runtime.DisplayName = string.IsNullOrWhiteSpace(displayName) ? runtimeKey : displayName;
                return runtime;
            }
        }

        private TunnelRuntimeSession GetRuntimeSession(string runtimeKey)
        {
            lock (runtimeLock)
            {
                tunnelRuntimes.TryGetValue(runtimeKey ?? string.Empty, out var runtime);
                return runtime;
            }
        }

        private string[] GetActiveRuntimeKeys()
        {
            lock (runtimeLock)
            {
                return tunnelRuntimes.Values
                    .Where(runtime => runtime.Connected && runtime.Process != null && !runtime.Process.HasExited)
                    .Select(runtime => runtime.Key)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
            }
        }

        private bool IsRuntimeActive(string runtimeKey)
        {
            if (string.IsNullOrWhiteSpace(runtimeKey))
            {
                return false;
            }

            lock (runtimeLock)
            {
                return tunnelRuntimes.TryGetValue(runtimeKey, out var runtime) &&
                       runtime.Connected &&
                       runtime.Process != null &&
                       !runtime.Process.HasExited;
            }
        }

        private int GetActiveRuntimeCount()
        {
            return GetActiveRuntimeKeys().Length;
        }

        private void RefreshConnectedState()
        {
            connected = GetActiveRuntimeCount() > 0;
            try { this.Invoke(() => UpdateTrayIcon()); } catch { }
        }

        private void AppendRuntimeLog(string displayName, string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            var prefix = string.IsNullOrWhiteSpace(displayName) ? string.Empty : $"[{displayName}] ";
            logs.Add(prefix + message);
        }

        private (string frpUser, int remotePort, int localPort, string logToken) ExtractClientContext(string stateJson)
        {
            try
            {
                var settings = JObject.Parse(stateJson);
                var connString = settings["connString"]?.ToString() ?? string.Empty;
                var configText = settings["configText"]?.ToString() ?? string.Empty;
                var parsedFromConn = ParseClientConnectionString(connString);
                if (!string.IsNullOrWhiteSpace(parsedFromConn.frpUser))
                {
                    return parsedFromConn;
                }

                return ParseClientConfig(configText);
            }
            catch
            {
                return (string.Empty, 0, 0, string.Empty);
            }
        }

        private (string frpUser, int remotePort, int localPort, string logToken) ParseClientConnectionString(string connStr)
        {
            if (string.IsNullOrWhiteSpace(connStr))
            {
                return (string.Empty, 0, 0, string.Empty);
            }

            var query = ParseConnectionQuery(connStr);
            var frpUser = query["user"] ?? string.Empty;
            var remotePort = int.TryParse(query["remote_port"], out var rp) ? rp : 0;
            var localPort = int.TryParse(query["local_port"], out var lp) ? lp : 0;
            var logToken = query["login_token"] ?? query["log_token"] ?? string.Empty;
            return (frpUser, remotePort, localPort, logToken);
        }

        private (string frpUser, int remotePort, int localPort, string logToken) ParseClientConfig(string configText)
        {
            if (string.IsNullOrWhiteSpace(configText))
            {
                return (string.Empty, 0, 0, string.Empty);
            }

            var frpUser = Regex.Match(configText, @"^\s*user\s*=\s*""([^""]+)""", RegexOptions.Multiline).Groups[1].Value;
            var remotePortText = Regex.Match(configText, @"^\s*remotePort\s*=\s*(\d+)", RegexOptions.Multiline).Groups[1].Value;
            var localPortText = Regex.Match(configText, @"^\s*localPort\s*=\s*(\d+)", RegexOptions.Multiline).Groups[1].Value;
            var tokenMatch = Regex.Match(configText, @"^\s*#\s*(?:auth\.(?:login_token|log_token)|tcptunnel_log_token)\s*=\s*""?([^""\r\n]+)""?", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            return (
                frpUser,
                int.TryParse(remotePortText, out var rp) ? rp : 0,
                int.TryParse(localPortText, out var lp) ? lp : 0,
                tokenMatch.Success ? tokenMatch.Groups[1].Value.Trim() : string.Empty
            );
        }

        private static bool TryParseTunnelConfigSpec(string configText, out TunnelConfigSpec spec, out string error)
        {
            spec = new TunnelConfigSpec(string.Empty, 0, string.Empty, 0, 0, string.Empty, string.Empty, TunnelTypeTcp, string.Empty);
            error = string.Empty;

            var normalized = NormalizeConfigText(configText);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                error = "Empty config";
                return false;
            }

            var serverHost = Regex.Match(normalized, @"^\s*serverAddr\s*=\s*""([^""]+)""", RegexOptions.Multiline | RegexOptions.IgnoreCase).Groups[1].Value.Trim();
            var serverPortText = Regex.Match(normalized, @"^\s*serverPort\s*=\s*(\d+)", RegexOptions.Multiline | RegexOptions.IgnoreCase).Groups[1].Value.Trim();
            var frpUser = Regex.Match(normalized, @"^\s*user\s*=\s*""([^""]+)""", RegexOptions.Multiline | RegexOptions.IgnoreCase).Groups[1].Value.Trim();
            var remotePortText = Regex.Match(normalized, @"^\s*remotePort\s*=\s*(\d+)", RegexOptions.Multiline | RegexOptions.IgnoreCase).Groups[1].Value.Trim();
            var localPortText = Regex.Match(normalized, @"^\s*localPort\s*=\s*(\d+)", RegexOptions.Multiline | RegexOptions.IgnoreCase).Groups[1].Value.Trim();
            var authToken = Regex.Match(normalized, @"^\s*auth\.token\s*=\s*""([^""]+)""", RegexOptions.Multiline | RegexOptions.IgnoreCase).Groups[1].Value.Trim();
            var loginToken = ExtractConfigLogToken(normalized);
            var tunnelType = ExtractConfigTunnelType(normalized);
            var expiresAt = ExtractConfigExpiresAt(normalized);

            if (!int.TryParse(serverPortText, out var serverPort))
            {
                error = "Invalid server port in config";
                return false;
            }
            if (!int.TryParse(remotePortText, out var remotePort))
            {
                error = "Invalid remote port in config";
                return false;
            }
            if (!int.TryParse(localPortText, out var localPort))
            {
                error = "Invalid local port in config";
                return false;
            }

            spec = new TunnelConfigSpec(
                serverHost,
                serverPort,
                frpUser,
                remotePort,
                localPort,
                authToken,
                loginToken,
                tunnelType,
                expiresAt
            );

            return true;
        }

        private static string ValidateTunnelConfigSpec(TunnelConfigSpec spec)
        {
            if (spec.ServerPort < 1 || spec.ServerPort > 65535)
            {
                return "Ошибка конфига: неверный порт сервера";
            }

            if (string.IsNullOrWhiteSpace(spec.FrpUser))
            {
                return "Ошибка конфига: отсутствует user";
            }

            if (spec.RemotePort < 1 || spec.RemotePort > 65535 || spec.LocalPort < 1 || spec.LocalPort > 65535)
            {
                return "Ошибка конфига: неверные порты";
            }

            if (string.IsNullOrWhiteSpace(spec.ExpiresAt))
            {
                return "Ошибка конфига: отсутствует expires_at";
            }

            if (!DateTime.TryParse(spec.ExpiresAt, out _))
            {
                return "Ошибка конфига: неверная дата окончания";
            }

            return string.Empty;
        }

        private string BuildCanonicalTunnelConfig(TunnelConfigSpec spec)
        {
            var settings = LoadClientSettings();
            bool compress = settings["compression"]?.ToObject<bool>() ?? false;
            int keepalive = GetKeepaliveSeconds();
            int heartbeatInterval = Math.Max(10, Math.Min(keepalive, 20));
            int heartbeatTimeout = Math.Max(heartbeatInterval * 3, 45);
            string safeTunnelType = EscapeConfigValue(NormalizeTunnelType(spec.TunnelType));
            string safeExpires = (spec.ExpiresAt ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");

            var sb = new StringBuilder();
            sb.AppendLine($"# tunnel_type = \"{safeTunnelType}\"");
            sb.AppendLine($"serverAddr = \"{spec.ServerHost}\"");
            sb.AppendLine($"serverPort = {spec.ServerPort}");
            sb.AppendLine($"user = \"{spec.FrpUser}\"");
            if (!string.IsNullOrWhiteSpace(spec.ExpiresAt))
            {
                sb.AppendLine($"# expires_at = \"{safeExpires}\"");
            }
            sb.AppendLine();
            sb.AppendLine("transport.protocol = \"tcp\"");
            sb.AppendLine("transport.poolCount = 5");
            sb.AppendLine("transport.tcpMux = true");
            sb.AppendLine($"transport.tcpMuxKeepaliveInterval = {keepalive}");
            sb.AppendLine("transport.dialServerKeepalive = 7200");
            sb.AppendLine($"transport.heartbeatInterval = {heartbeatInterval}");
            sb.AppendLine($"transport.heartbeatTimeout = {heartbeatTimeout}");
            sb.AppendLine();
            sb.AppendLine("[[proxies]]");
            sb.AppendLine("name = \"tunnel\"");
            sb.AppendLine("type = \"tcp\"");
            sb.AppendLine("localIP = \"127.0.0.1\"");
            sb.AppendLine($"localPort = {spec.LocalPort}");
            sb.AppendLine($"remotePort = {spec.RemotePort}");
            sb.AppendLine("transport.useEncryption = true");
            sb.AppendLine($"transport.useCompression = {(compress ? "true" : "false")}");
            if (IsUdpTunnelType(spec.TunnelType))
            {
                sb.AppendLine();
                sb.AppendLine("[[proxies]]");
                sb.AppendLine("name = \"tunnel-udp\"");
                sb.AppendLine("type = \"udp\"");
                sb.AppendLine("localIP = \"127.0.0.1\"");
                sb.AppendLine($"localPort = {spec.LocalPort}");
                sb.AppendLine($"remotePort = {spec.RemotePort}");
            }
            return sb.ToString().Trim();
        }

        private static string EnsureEmbeddedFrpcExtracted()
        {
            lock (embeddedFrpcLock)
            {
                if (!string.IsNullOrWhiteSpace(embeddedFrpcPath) &&
                    File.Exists(embeddedFrpcPath) &&
                    FileHashMatches(embeddedFrpcPath, FrpcExeSha256))
                {
                    return embeddedFrpcPath;
                }
            }

            var bundledFrpcPath = TryPrepareBundledFrpc();
            if (!string.IsNullOrWhiteSpace(bundledFrpcPath))
            {
                return bundledFrpcPath;
            }

            return string.Empty;
        }

        private static string TryGetPreparedFrpcPath()
        {
            lock (embeddedFrpcLock)
            {
                if (!string.IsNullOrWhiteSpace(embeddedFrpcPath) &&
                    File.Exists(embeddedFrpcPath) &&
                    FileHashMatches(embeddedFrpcPath, FrpcExeSha256))
                {
                    return embeddedFrpcPath;
                }
            }

            var cacheDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TCP Tunnel",
                "frpc",
                $"v{FrpcVersion}");
            var cachedFrpcPath = Path.Combine(cacheDir, "frpc.exe");
            if (File.Exists(cachedFrpcPath) && FileHashMatches(cachedFrpcPath, FrpcExeSha256))
            {
                lock (embeddedFrpcLock)
                {
                    embeddedFrpcPath = cachedFrpcPath;
                }
                return cachedFrpcPath;
            }

            return string.Empty;
        }

        private static string TryPrepareBundledFrpc()
        {
            try
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var bundledFrpcPath = Path.Combine(baseDir, "frpc.exe");
                if (!File.Exists(bundledFrpcPath) || !FileHashMatches(bundledFrpcPath, FrpcExeSha256))
                {
                    return string.Empty;
                }

                var cacheDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "TCP Tunnel",
                    "frpc",
                    $"v{FrpcVersion}");
                Directory.CreateDirectory(cacheDir);

                var cachedFrpcPath = Path.Combine(cacheDir, "frpc.exe");
                if (!File.Exists(cachedFrpcPath) || !FileHashMatches(cachedFrpcPath, FrpcExeSha256))
                {
                    File.Copy(bundledFrpcPath, cachedFrpcPath, true);
                }

                if (!FileHashMatches(cachedFrpcPath, FrpcExeSha256))
                {
                    return string.Empty;
                }

                lock (embeddedFrpcLock)
                {
                    embeddedFrpcPath = cachedFrpcPath;
                }

                return cachedFrpcPath;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static byte[] DownloadVerifiedFrpcArchive() => Array.Empty<byte>();

        private static bool FileHashMatches(string path, string expectedHash)
        {
            try
            {
                using var stream = File.OpenRead(path);
                var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                return string.Equals(hash, (expectedHash ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static string ResolveApiBaseUrl()
        {
            return string.Empty;
        }

        private static string NormalizeVersion(string version)
        {
            var trimmed = (version ?? string.Empty).Trim();
            if (Version.TryParse(trimmed, out var parsed))
            {
                return parsed.Build >= 0 ? $"{parsed.Major}.{parsed.Minor}.{parsed.Build}" : $"{parsed.Major}.{parsed.Minor}";
            }
            return trimmed;
        }

        private static string ComputeLogSignature(string token, object payload)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return string.Empty;
            }

            var obj = JObject.FromObject(payload);
            var canonical = string.Join("|", new[]
            {
                obj["event_type"]?.ToString() ?? string.Empty,
                obj["frp_user"]?.ToString() ?? string.Empty,
                obj["remote_port"]?.ToString() ?? string.Empty,
                obj["local_port"]?.ToString() ?? string.Empty,
                obj["machine_name"]?.ToString() ?? string.Empty,
                obj["windows_user"]?.ToString() ?? string.Empty,
                obj["machine_fingerprint"]?.ToString() ?? string.Empty,
                obj["client_version"]?.ToString() ?? string.Empty,
                obj["timestamp"]?.ToString() ?? string.Empty,
            });

            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(token));
            return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        }

        private static string GetMachineFingerprint()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
                var machineGuid = key?.GetValue("MachineGuid")?.ToString() ?? string.Empty;
                var source = $"{machineGuid}|{Environment.MachineName}";
                return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
            }
            catch
            {
                return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.MachineName))).ToLowerInvariant();
            }
        }

        private sealed record ApiJsonResponse(bool Success, HttpStatusCode StatusCode, JToken Body, string Error);

        private string LoadProtectedJsonFile(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return "{}";
                }

                var raw = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(raw))
                {
                    return "{}";
                }

                if (raw.StartsWith(StateSecretPrefix, StringComparison.Ordinal))
                {
                    var protectedPayload = Convert.FromBase64String(raw.Substring(StateSecretPrefix.Length));
                    var plaintext = ProtectedData.Unprotect(protectedPayload, null, DataProtectionScope.CurrentUser);
                    return Encoding.UTF8.GetString(plaintext);
                }

                try
                {
                    JToken.Parse(raw);
                    SaveProtectedJsonFile(path, raw);
                }
                catch
                {
                }

                return raw;
            }
            catch
            {
                return "{}";
            }
        }

        private void SaveProtectedJsonFile(string path, string json)
        {
            try
            {
                var payload = Encoding.UTF8.GetBytes(json ?? "{}");
                var protectedPayload = ProtectedData.Protect(payload, null, DataProtectionScope.CurrentUser);
                File.WriteAllText(path, StateSecretPrefix + Convert.ToBase64String(protectedPayload));
            }
            catch
            {
            }
        }

        private void DeleteProtectedFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }

        private AccountSessionState LoadAccountSession()
        {
            try
            {
                var json = LoadProtectedJsonFile(accountSessionPath);
                var session = JsonConvert.DeserializeObject<AccountSessionState>(json);
                return string.IsNullOrWhiteSpace(session?.Token) ? null : session;
            }
            catch
            {
                return null;
            }
        }

        private void SaveAccountSession(AccountSessionState session)
        {
            if (session == null || string.IsNullOrWhiteSpace(session.Token))
            {
                return;
            }

            SaveProtectedJsonFile(accountSessionPath, JsonConvert.SerializeObject(session));
        }

        private void ClearAccountSession()
        {
            DeleteProtectedFile(accountSessionPath);
        }

        private Task<ApiJsonResponse> SendApiRequestAsync(HttpMethod method, string relativePath, object payload = null, string bearerToken = null)
        {
            return Task.FromResult(new ApiJsonResponse(false, 0, new JObject(), "Аккаунт отключён в публичной версии"));
        }

        private static JToken ParseJsonToken(string responseText)
        {
            if (string.IsNullOrWhiteSpace(responseText))
            {
                return new JObject();
            }

            try
            {
                return JToken.Parse(responseText);
            }
            catch
            {
                return new JObject { ["raw"] = responseText };
            }
        }

        private static AccountUserState MapAccountUser(JToken token)
        {
            if (token == null || token.Type != JTokenType.Object)
            {
                return null;
            }

            return new AccountUserState(
                token["id"]?.ToObject<int>() ?? 0,
                token["name"]?.ToString() ?? string.Empty,
                token["email"]?.ToString() ?? string.Empty,
                token["role"]?.ToString() ?? "user",
                token["balance"]?.ToObject<decimal?>() ?? 0m,
                token["email_verified"]?.ToObject<bool?>() ?? false,
                token["two_fa_enabled"]?.ToObject<bool?>() ?? false,
                token["created_at"]?.ToString() ?? string.Empty
            );
        }

        private static AccountTunnelState[] MapAccountTunnels(JToken token)
        {
            if (token == null || token.Type != JTokenType.Array)
            {
                return Array.Empty<AccountTunnelState>();
            }

            return token
                .Children<JToken>()
                .Select(item => new AccountTunnelState(
                    item["id"]?.ToObject<int>() ?? 0,
                    item["frp_user"]?.ToString() ?? string.Empty,
                    item["remote_port"]?.ToObject<int>() ?? 0,
                    item["local_port"]?.ToObject<int>() ?? 0,
                    NormalizeTunnelType(item["tunnel_type"]?.ToString() ?? item["tunnelType"]?.ToString() ?? item["TunnelType"]?.ToString()),
                    item["status"]?.ToString() ?? string.Empty,
                    item["expires_at"]?.ToString() ?? string.Empty,
                    item["plan_days"]?.ToObject<int>() ?? 0))
                .Where(item => item.Id > 0)
                .ToArray();
        }

        private async Task<string> BuildAuthenticatedAccountStateJsonAsync(string token, bool logLoginEvent)
        {
            var userResponse = await SendApiRequestAsync(HttpMethod.Get, "auth/me", bearerToken: token).ConfigureAwait(false);
            if (!userResponse.Success)
            {
                if (userResponse.StatusCode == HttpStatusCode.Unauthorized ||
                    userResponse.StatusCode == HttpStatusCode.Forbidden ||
                    userResponse.StatusCode == HttpStatusCode.NotFound)
                {
                    ClearAccountSession();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = false,
                    authenticated = false,
                    error = userResponse.Error,
                    tunnels = Array.Empty<object>(),
                });
            }

            var tunnelsResponse = await SendApiRequestAsync(HttpMethod.Get, "tunnels", bearerToken: token).ConfigureAwait(false);
            if (!tunnelsResponse.Success)
            {
                if (tunnelsResponse.StatusCode == HttpStatusCode.Unauthorized ||
                    tunnelsResponse.StatusCode == HttpStatusCode.Forbidden ||
                    tunnelsResponse.StatusCode == HttpStatusCode.NotFound)
                {
                    ClearAccountSession();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = false,
                    authenticated = false,
                    error = tunnelsResponse.Error,
                    tunnels = Array.Empty<object>(),
                });
            }

            var user = MapAccountUser(userResponse.Body);
            var tunnels = MapAccountTunnels(tunnelsResponse.Body);
            SaveAccountSession(new AccountSessionState(token, user));

            if (logLoginEvent)
            {
                _ = Task.Run(() => LogDesktopAccountLoginAsync(token));
            }

            return JsonConvert.SerializeObject(new
            {
                success = true,
                authenticated = true,
                user,
                tunnels,
            });
        }

        private async Task<string> GetAccountStateJsonAsync()
        {
            var session = LoadAccountSession();
            if (session == null || string.IsNullOrWhiteSpace(session.Token))
            {
                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    authenticated = false,
                    tunnels = Array.Empty<object>(),
                });
            }

            return await BuildAuthenticatedAccountStateJsonAsync(session.Token, false).ConfigureAwait(false);
        }

        private async Task<string> AccountLoginJsonAsync(string login, string password)
        {
            if (string.IsNullOrWhiteSpace(login) || string.IsNullOrWhiteSpace(password))
            {
                return JsonConvert.SerializeObject(new { success = false, error = "Введите логин и пароль" });
            }

            var response = await SendApiRequestAsync(HttpMethod.Post, "auth/login", new
            {
                login = login.Trim(),
                password,
            }).ConfigureAwait(false);

            if (!response.Success)
            {
                return JsonConvert.SerializeObject(new { success = false, error = response.Error });
            }

            if (response.Body["requires2fa"]?.ToObject<bool?>() == true)
            {
                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    requires_2fa = true,
                    challenge_id = response.Body["challenge_id"]?.ToObject<long>() ?? 0,
                    email_masked = response.Body["email_masked"]?.ToString() ?? string.Empty,
                    retry_after_sec = response.Body["retry_after_sec"]?.ToObject<int?>() ?? 0,
                });
            }

            var token = response.Body["token"]?.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(token))
            {
                return JsonConvert.SerializeObject(new { success = false, error = "Сервер не вернул токен входа" });
            }

            return await BuildAuthenticatedAccountStateJsonAsync(token, true).ConfigureAwait(false);
        }

        private async Task<string> AccountVerify2FaJsonAsync(long challengeId, string code)
        {
            if (challengeId <= 0 || string.IsNullOrWhiteSpace(code))
            {
                return JsonConvert.SerializeObject(new { success = false, error = "Введите код 2FA" });
            }

            var response = await SendApiRequestAsync(HttpMethod.Post, "auth/verify-login-2fa", new
            {
                challenge_id = challengeId,
                code = code.Trim(),
            }).ConfigureAwait(false);

            if (!response.Success)
            {
                return JsonConvert.SerializeObject(new { success = false, error = response.Error });
            }

            var token = response.Body["token"]?.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(token))
            {
                return JsonConvert.SerializeObject(new { success = false, error = "Сервер не вернул токен входа" });
            }

            return await BuildAuthenticatedAccountStateJsonAsync(token, true).ConfigureAwait(false);
        }

        private async Task<string> AccountResend2FaJsonAsync(long challengeId)
        {
            if (challengeId <= 0)
            {
                return JsonConvert.SerializeObject(new { success = false, error = "Некорректный challenge 2FA" });
            }

            var response = await SendApiRequestAsync(HttpMethod.Post, "auth/resend-login-2fa", new
            {
                challenge_id = challengeId,
            }).ConfigureAwait(false);

            if (!response.Success)
            {
                return JsonConvert.SerializeObject(new { success = false, error = response.Error });
            }

            return JsonConvert.SerializeObject(new
            {
                success = true,
                retry_after_sec = response.Body["retry_after_sec"]?.ToObject<int?>() ?? 0,
            });
        }

        private string AccountLogoutJson()
        {
            ClearAccountSession();
            return JsonConvert.SerializeObject(new
            {
                success = true,
                authenticated = false,
                tunnels = Array.Empty<object>(),
            });
        }

        private async Task LogDesktopAccountLoginAsync(string token)
        {
            try
            {
                await SendApiRequestAsync(HttpMethod.Post, "client/account-login", new
                {
                    machine_name = Environment.MachineName,
                    windows_user = Environment.UserName,
                    machine_fingerprint = GetMachineFingerprint(),
                    client_version = NormalizeVersion(Application.ProductVersion),
                }, token).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        private async Task ConnectAccountTunnelAsync(int tunnelId, string runtimeKey = "", string responseKey = "")
        {
            if (tunnelId <= 0)
            {
                Respond(ResolveResponseKey(responseKey, "_resolve_connect"), JsonConvert.SerializeObject(new { success = false, error = "Некорректный tunnel id" }));
                return;
            }

            if (IsCompatibilityModeEnabled())
            {
                Respond(ResolveResponseKey(responseKey, "_resolve_connect"), JsonConvert.SerializeObject(new { success = false, error = "Выключите режим совместимости для подключения к TCP Tunnel" }));
                return;
            }

            var session = LoadAccountSession();
            if (session == null || string.IsNullOrWhiteSpace(session.Token))
            {
                Respond(ResolveResponseKey(responseKey, "_resolve_connect"), JsonConvert.SerializeObject(new { success = false, error = "Сначала войдите в аккаунт" }));
                return;
            }

            var response = await SendApiRequestAsync(HttpMethod.Get, $"tunnels/{tunnelId}/config", bearerToken: session.Token).ConfigureAwait(false);
            if (!response.Success)
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized ||
                    response.StatusCode == HttpStatusCode.Forbidden && string.Equals(response.Error, "Account is blocked", StringComparison.OrdinalIgnoreCase) ||
                    response.StatusCode == HttpStatusCode.NotFound && string.Equals(response.Error, "User not found", StringComparison.OrdinalIgnoreCase))
                {
                    ClearAccountSession();
                }

                Respond(ResolveResponseKey(responseKey, "_resolve_connect"), JsonConvert.SerializeObject(new { success = false, error = response.Error }));
                return;
            }

            var config = response.Body["config"]?.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(config))
            {
                Respond(ResolveResponseKey(responseKey, "_resolve_connect"), JsonConvert.SerializeObject(new { success = false, error = "Сервер не вернул конфиг туннеля" }));
                return;
            }

            var apiLogToken = response.Body["client_log_token"]?.ToString() ?? string.Empty;
            var apiExpiresAt = response.Body["expires_at"]?.ToString() ?? string.Empty;
            var apiTunnelType = response.Body["tunnel_type"]?.ToString() ?? string.Empty;
            var preparedConfig = EnrichConfigWithApiMetadata(config, apiLogToken, apiExpiresAt, apiTunnelType);

            var profileKey = NormalizeRuntimeKey(runtimeKey, $"account_{tunnelId}");
            ConnectConfig(preparedConfig, profileKey, responseKey);
        }

        private string LoadState()
        {
            try
            {
                return LoadProtectedJsonFile(settingsPath);
            }
            catch
            {
                return "{}";
            }
        }

        private void SaveState(string json)
        {
            try
            {
                SaveProtectedJsonFile(settingsPath, json);
            }
            catch
            {
            }
        }

        private void TryPersistEncryptedState(string json)
        {
            try
            {
                SaveState(json);
            }
            catch
            {
            }
        }

        private void SetTray(bool enabled)
        {
            trayEnabled = enabled;
            if (enabled && trayIcon == null)
            {
                this.Invoke(() =>
                {
                    trayIcon = new NotifyIcon();
                    trayIcon.Text = "TCP Tunnel";
                    UpdateTrayIcon();
                    trayIcon.Visible = true;
                    trayIcon.DoubleClick += (s, e) => { this.Show(); this.WindowState = FormWindowState.Normal; };
                    var menu = new ContextMenuStrip();
                    menu.Items.Add("Показать", null, (s, e) => { this.Show(); this.WindowState = FormWindowState.Normal; });
                    menu.Items.Add("Выход", null, (s, e) => { DoDisconnect(); trayIcon.Dispose(); Environment.Exit(0); });
                    trayIcon.ContextMenuStrip = menu;
                });
            }
            else if (!enabled && trayIcon != null)
            {
                this.Invoke(() => { trayIcon.Dispose(); trayIcon = null; });
            }
        }

        private void UpdateTrayIcon()
        {
            if (trayIcon == null) return;
            try
            {
                trayInactiveIcon ??= BuildTrayStatusIcon(false);
                trayActiveIcon ??= BuildTrayStatusIcon(true);
                trayIcon.Icon = connected ? trayActiveIcon : trayInactiveIcon;
                trayIcon.Text = connected ? "TCP Tunnel - tunnel active" : "TCP Tunnel - disconnected";
            }
            catch
            {
                try { trayIcon.Icon = this.Icon; } catch { }
            }
        }

        private void PlayNotifySound()
        {
            try
            {
                var settings = JObject.Parse(LoadState());
                if (settings["notifications"]?.ToObject<bool>() == false) return;
            }
            catch { }

            Task.Run(() =>
            {
                try
                {
                    string mp3 = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui", "notify.mp3");
                    if (!File.Exists(mp3))
                    {
                        System.Media.SystemSounds.Exclamation.Play();
                        return;
                    }

                    lock (notifySoundLock)
                    {
                        try { notifyOutput?.Stop(); } catch { }
                        notifyOutput?.Dispose();
                        notifyReader?.Dispose();
                        notifyOutput = null;
                        notifyReader = null;

                        var reader = new AudioFileReader(mp3);
                        var output = new WaveOutEvent();
                        output.Init(reader);
                        output.PlaybackStopped += (_, __) =>
                        {
                            try { output.Dispose(); } catch { }
                            try { reader.Dispose(); } catch { }
                            lock (notifySoundLock)
                            {
                                if (ReferenceEquals(notifyOutput, output)) notifyOutput = null;
                                if (ReferenceEquals(notifyReader, reader)) notifyReader = null;
                            }
                        };

                        notifyReader = reader;
                        notifyOutput = output;
                        output.Play();
                    }
                }
                catch
                {
                    System.Media.SystemSounds.Exclamation.Play();
                }
            });
        }

        private void ConnectString(string connStr, string runtimeKey = "", string responseKey = "")
        {
            try
            {
                if (IsCompatibilityModeEnabled())
                {
                    Respond(ResolveResponseKey(responseKey, "_resolve_connect"), JsonConvert.SerializeObject(new { success = false, error = "Выключите режим совместимости для подключения к TCP Tunnel" }));
                    return;
                }

                string normalizedConnStr = NormalizeConnectionStringText(connStr);
                var q = ParseConnectionQuery(normalizedConnStr);
                var spec = new TunnelConfigSpec(
                    q["host"] ?? string.Empty,
                    int.TryParse(q["server_port"], out var serverPort) ? serverPort : 0,
                    q["user"] ?? string.Empty,
                    int.TryParse(q["remote_port"], out var remotePort) ? remotePort : 0,
                    int.TryParse(q["local_port"], out var localPort) ? localPort : 0,
                    q["auth_token"] ?? string.Empty,
                    q["login_token"] ?? q["log_token"] ?? string.Empty,
                    NormalizeTunnelType(q["tunnel_type"]),
                    q["expires_at"] ?? string.Empty
                );

                var configError = ValidateTunnelConfigSpec(spec);
                if (!string.IsNullOrWhiteSpace(configError))
                {
                    Respond(ResolveResponseKey(responseKey, "_resolve_connect"), JsonConvert.SerializeObject(new { success = false, error = configError }));
                    return;
                }

                var serverValidationError = ValidateTunnelConfigWithServerAsync(spec.ServerHost, spec.ServerPort, spec.FrpUser, spec.RemotePort, spec.LocalPort, spec.AuthToken, spec.LoginToken, spec.TunnelType, spec.ExpiresAt).GetAwaiter().GetResult();
                if (!string.IsNullOrWhiteSpace(serverValidationError))
                {
                    Respond(ResolveResponseKey(responseKey, "_resolve_connect"), JsonConvert.SerializeObject(new { success = false, error = serverValidationError }));
                    return;
                }

                var profileKey = NormalizeRuntimeKey(runtimeKey, spec.FrpUser);
                DisconnectTunnel(profileKey, false);
                Thread.Sleep(150);
                SetRuntimeTunnelContext(spec.ServerHost, spec.ServerPort, spec.FrpUser, spec.RemotePort, spec.LocalPort, spec.LoginToken);

                var canonicalConfig = BuildCanonicalTunnelConfig(spec);
                StartFrpc(profileKey, spec.FrpUser, canonicalConfig, spec.ServerHost, spec.ServerPort, spec.RemotePort, 1, true, responseKey);
            }
            catch (Exception ex)
            {
                Respond(ResolveResponseKey(responseKey, "_resolve_connect"), JsonConvert.SerializeObject(new { success = false, error = ex.Message }));
            }
        }

        private void ConnectConfig(string config, string runtimeKey = "", string responseKey = "")
        {
            string normalizedConfig = NormalizeConfigText(config);
            if (string.IsNullOrWhiteSpace(normalizedConfig))
            {
                Respond(ResolveResponseKey(responseKey, "_resolve_connect"), JsonConvert.SerializeObject(new { success = false, error = "Empty config" }));
                return;
            }

            if (IsCompatibilityModeEnabled())
            {
                var compatibilityError = ValidateCompatibilityFrpcConfig(normalizedConfig);
                if (!string.IsNullOrWhiteSpace(compatibilityError))
                {
                    Respond(ResolveResponseKey(responseKey, "_resolve_connect"), JsonConvert.SerializeObject(new { success = false, error = compatibilityError }));
                    return;
                }

                var serverHost = Regex.Match(normalizedConfig, @"^\s*serverAddr\s*=\s*""([^""]+)""", RegexOptions.Multiline | RegexOptions.IgnoreCase).Groups[1].Value.Trim();
                var serverPortText = Regex.Match(normalizedConfig, @"^\s*serverPort\s*=\s*(\d+)", RegexOptions.Multiline | RegexOptions.IgnoreCase).Groups[1].Value.Trim();
                var frpUser = Regex.Match(normalizedConfig, @"^\s*user\s*=\s*""([^""]+)""", RegexOptions.Multiline | RegexOptions.IgnoreCase).Groups[1].Value.Trim();
                var remotePortText = Regex.Match(normalizedConfig, @"^\s*remotePort\s*=\s*(\d+)", RegexOptions.Multiline | RegexOptions.IgnoreCase).Groups[1].Value.Trim();
                var localPortText = Regex.Match(normalizedConfig, @"^\s*localPort\s*=\s*(\d+)", RegexOptions.Multiline | RegexOptions.IgnoreCase).Groups[1].Value.Trim();
                var compatibilityServerPort = int.TryParse(serverPortText, out var parsedServerPort) ? parsedServerPort : 0;
                var compatibilityRemotePort = int.TryParse(remotePortText, out var parsedRemotePort) ? parsedRemotePort : 0;
                var compatibilityLocalPort = int.TryParse(localPortText, out var parsedLocalPort) ? parsedLocalPort : 0;
                var profileKey = NormalizeRuntimeKey(runtimeKey, frpUser);
                DisconnectTunnel(profileKey, false);
                Thread.Sleep(150);

                SetRuntimeTunnelContext(serverHost, compatibilityServerPort, frpUser, compatibilityRemotePort, compatibilityLocalPort, string.Empty);
                StartFrpc(profileKey, string.IsNullOrWhiteSpace(frpUser) ? "FRP" : frpUser, ApplyCompatibilityDefaults(normalizedConfig), serverHost, compatibilityServerPort, compatibilityRemotePort, 1, false, responseKey);
                return;
            }

            if (!TryParseTunnelConfigSpec(normalizedConfig, out var spec, out var parseError))
            {
                Respond(ResolveResponseKey(responseKey, "_resolve_connect"), JsonConvert.SerializeObject(new { success = false, error = parseError }));
                return;
            }

            var configError = ValidateTunnelConfigSpec(spec);
            if (!string.IsNullOrWhiteSpace(configError))
            {
                Respond(ResolveResponseKey(responseKey, "_resolve_connect"), JsonConvert.SerializeObject(new { success = false, error = configError }));
                return;
            }

            var serverValidationError = ValidateTunnelConfigWithServerAsync(spec.ServerHost, spec.ServerPort, spec.FrpUser, spec.RemotePort, spec.LocalPort, spec.AuthToken, spec.LoginToken, spec.TunnelType, spec.ExpiresAt).GetAwaiter().GetResult();
            if (!string.IsNullOrWhiteSpace(serverValidationError))
            {
                Respond(ResolveResponseKey(responseKey, "_resolve_connect"), JsonConvert.SerializeObject(new { success = false, error = serverValidationError }));
                return;
            }
            var secureProfileKey = NormalizeRuntimeKey(runtimeKey, spec.FrpUser);
            DisconnectTunnel(secureProfileKey, false);
            Thread.Sleep(150);
            SetRuntimeTunnelContext(spec.ServerHost, spec.ServerPort, spec.FrpUser, spec.RemotePort, spec.LocalPort, spec.LoginToken);
            StartFrpc(secureProfileKey, spec.FrpUser, BuildCanonicalTunnelConfig(spec), spec.ServerHost, spec.ServerPort, spec.RemotePort, 1, true, responseKey);
        }

        private static string ExtractConfigLogToken(string config)
        {
            if (string.IsNullOrWhiteSpace(config))
            {
                return string.Empty;
            }

            var match = Regex.Match(config, @"^\s*#\s*(?:auth\.(?:login_token|log_token)|tcptunnel_log_token)\s*=\s*""?([^""\r\n]+)""?", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value.Trim() : string.Empty;
        }

        private static string NormalizeTunnelType(string tunnelType)
        {
            var normalized = (tunnelType ?? string.Empty)
                .Trim()
                .ToLowerInvariant()
                .Replace("+", "_")
                .Replace("-", "_")
                .Replace(" ", "_");

            return normalized switch
            {
                "tcp_udp" => TunnelTypeTcpUdp,
                "tcpudp" => TunnelTypeTcpUdp,
                "udp" => TunnelTypeTcpUdp,
                _ => TunnelTypeTcp,
            };
        }

        private static bool IsUdpTunnelType(string tunnelType)
        {
            return string.Equals(NormalizeTunnelType(tunnelType), TunnelTypeTcpUdp, StringComparison.OrdinalIgnoreCase);
        }

        private static string ExtractConfigTunnelType(string config)
        {
            if (string.IsNullOrWhiteSpace(config))
            {
                return TunnelTypeTcp;
            }

            var commentMatch = Regex.Match(config, @"^\s*#\s*tunnel_type\s*=\s*""?([^""\r\n]+)""?", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            if (commentMatch.Success)
            {
                return NormalizeTunnelType(commentMatch.Groups[1].Value.Trim());
            }

            if (Regex.IsMatch(config, @"^\s*type\s*=\s*""udp""\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
            {
                return TunnelTypeTcpUdp;
            }

            return TunnelTypeTcp;
        }

        private static string EscapeConfigValue(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static string EnrichConfigWithApiMetadata(string config, string loginToken, string expiresAt, string tunnelType = "")
        {
            var normalized = NormalizeConfigText(config);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return normalized;
            }

            var builder = new StringBuilder();

            if (string.IsNullOrWhiteSpace(ExtractConfigLogToken(normalized)) && !string.IsNullOrWhiteSpace(loginToken))
            {
                builder.AppendLine($"# auth.login_token = \"{EscapeConfigValue(loginToken)}\"");
            }

            if (string.IsNullOrWhiteSpace(ExtractConfigExpiresAt(normalized)) && !string.IsNullOrWhiteSpace(expiresAt))
            {
                builder.AppendLine($"# expires_at = \"{EscapeConfigValue(expiresAt)}\"");
            }

            if (string.IsNullOrWhiteSpace(Regex.Match(normalized, @"^\s*#\s*tunnel_type\s*=\s*""?([^""\r\n]+)""?", RegexOptions.Multiline | RegexOptions.IgnoreCase).Groups[1].Value) &&
                !string.IsNullOrWhiteSpace(tunnelType))
            {
                builder.AppendLine($"# tunnel_type = \"{EscapeConfigValue(NormalizeTunnelType(tunnelType))}\"");
            }

            if (builder.Length == 0)
            {
                return normalized;
            }

            builder.AppendLine(normalized);
            return builder.ToString().Trim();
        }

        private static (string serverHost, int serverPort, string frpUser, int remotePort, int localPort, string authToken, string logToken, string expiresAt) ExtractConfigConnectionContext(string config)
        {
            if (string.IsNullOrWhiteSpace(config))
            {
                return (string.Empty, 0, string.Empty, 0, 0, string.Empty, string.Empty, string.Empty);
            }

            var serverHost = Regex.Match(config, @"^\s*serverAddr\s*=\s*""([^""]+)""", RegexOptions.Multiline | RegexOptions.IgnoreCase).Groups[1].Value.Trim();
            var serverPortText = Regex.Match(config, @"^\s*serverPort\s*=\s*(\d+)", RegexOptions.Multiline | RegexOptions.IgnoreCase).Groups[1].Value;
            var frpUser = Regex.Match(config, @"^\s*user\s*=\s*""([^""]+)""", RegexOptions.Multiline | RegexOptions.IgnoreCase).Groups[1].Value;
            var remotePortText = Regex.Match(config, @"^\s*remotePort\s*=\s*(\d+)", RegexOptions.Multiline).Groups[1].Value;
            var localPortText = Regex.Match(config, @"^\s*localPort\s*=\s*(\d+)", RegexOptions.Multiline).Groups[1].Value;
            var authToken = Regex.Match(config, @"^\s*auth\.token\s*=\s*""([^""]+)""", RegexOptions.Multiline | RegexOptions.IgnoreCase).Groups[1].Value.Trim();
            return (
                serverHost,
                int.TryParse(serverPortText, out var sp) ? sp : 0,
                frpUser,
                int.TryParse(remotePortText, out var rp) ? rp : 0,
                int.TryParse(localPortText, out var lp) ? lp : 0,
                authToken,
                ExtractConfigLogToken(config),
                ExtractConfigExpiresAt(config)
            );
        }

        private static bool IsAllowedTunnelHost(string host)
        {
            return !string.IsNullOrWhiteSpace(host);
        }

        private bool IsCompatibilityModeEnabled()
        {
            return true;
        }

        private static string ValidateCompatibilityFrpcConfig(string config)
        {
            var serverAddr = Regex.Match(config, @"^\s*serverAddr\s*=\s*""([^""]+)""", RegexOptions.Multiline | RegexOptions.IgnoreCase).Groups[1].Value.Trim();
            if (string.IsNullOrWhiteSpace(serverAddr))
            {
                return "FRP-конфиг должен содержать serverAddr";
            }
            if (IsAllowedTunnelHost(serverAddr))
            {
                return "Выключите режим совместимости для подключения к TCP Tunnel";
            }

            var serverPortText = Regex.Match(config, @"^\s*serverPort\s*=\s*(\d+)", RegexOptions.Multiline | RegexOptions.IgnoreCase).Groups[1].Value.Trim();
            if (!int.TryParse(serverPortText, out var serverPort) || serverPort < 1 || serverPort > 65535)
            {
                return "FRP-конфиг должен содержать корректный serverPort";
            }

            var localPortText = Regex.Match(config, @"^\s*localPort\s*=\s*(\d+)", RegexOptions.Multiline | RegexOptions.IgnoreCase).Groups[1].Value.Trim();
            if (!int.TryParse(localPortText, out var localPort) || localPort < 1 || localPort > 65535)
            {
                return "FRP-конфиг должен содержать хотя бы один корректный localPort";
            }

            return string.Empty;
        }

        private string ApplyCompatibilityDefaults(string config)
        {
            return NormalizeConfigText(config);
        }

        private static string ValidateSecureFrpcConfig(string config)
        {
            return ValidateCompatibilityFrpcConfig(config);
        }

        private static string ExtractConfigExpiresAt(string config)
        {
            if (string.IsNullOrWhiteSpace(config))
            {
                return string.Empty;
            }

            var match = Regex.Match(config, @"^\s*#\s*expires_at\s*=\s*""?([^""\r\n]+)""?", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value.Trim() : string.Empty;
        }

        private Task<string> ValidateTunnelConfigWithServerAsync(string serverHost, int serverPort, string frpUser, int remotePort, int localPort, string authToken, string loginToken, string tunnelType, string expiresAt)
        {
            return Task.FromResult(string.Empty);
        }

        private void SetRuntimeTunnelContext(string serverHost, int serverPort, string frpUser, int remotePort, int localPort, string logToken)
        {
            currentServerHost = serverHost ?? string.Empty;
            currentServerPort = serverPort;
            currentFrpUser = frpUser ?? string.Empty;
            currentRemotePort = remotePort;
            currentLocalPort = localPort;
            currentConfigToken = logToken ?? string.Empty;
            lock (startupLogLock)
            {
                sentStartupLogKey = string.Empty;
            }
        }

        private void StartFrpc(string runtimeKey, string displayName, string config, string serverHost, int serverPort, int remotePort, int attempt = 1, bool secureMode = true, string responseKey = "")
        {
            var configClean = NormalizeConfigText(config);
            var configValidationError = secureMode
                ? ValidateSecureFrpcConfig(configClean)
                : ValidateCompatibilityFrpcConfig(configClean);
            if (!string.IsNullOrWhiteSpace(configValidationError))
            {
                Respond(ResolveResponseKey(responseKey, "_resolve_connect"), JsonConvert.SerializeObject(new { success = false, error = configValidationError }));
                return;
            }

            string frpcPath = EnsureEmbeddedFrpcExtracted();
            if (string.IsNullOrWhiteSpace(frpcPath) || !File.Exists(frpcPath))
            {
                Respond(ResolveResponseKey(responseKey, "_resolve_connect"), JsonConvert.SerializeObject(new { success = false, error = "Не удалось подготовить frpc (скачивание/проверка)" }));
                return;
            }

            string configPath = Path.Combine(Path.GetTempPath(), $"frpc_tunnel_{Guid.NewGuid():N}.toml");
            var runtime = GetOrCreateRuntimeSession(runtimeKey, displayName);
            var reconnectContext = secureMode
                ? ExtractConfigConnectionContext(configClean)
                : (serverHost: string.Empty, serverPort: 0, frpUser: string.Empty, remotePort: 0, localPort: 0, authToken: string.Empty, logToken: string.Empty, expiresAt: string.Empty);
            runtime.ServerHost = serverHost ?? string.Empty;
            runtime.ServerPort = serverPort;
            runtime.FrpUser = reconnectContext.frpUser ?? string.Empty;
            runtime.RemotePort = remotePort;
            runtime.LocalPort = reconnectContext.localPort;
            runtime.LogToken = reconnectContext.logToken ?? string.Empty;

            var localTarget = ExtractLocalServiceTarget(configClean);
            string localServiceWarning = string.Empty;
            if (localTarget.port < 1 || localTarget.port > 65535)
            {
                Respond(ResolveResponseKey(responseKey, "_resolve_connect"), JsonConvert.SerializeObject(new { success = false, error = "Invalid localPort in FRP config" }));
                return;
            }
            if (!IsLocalServiceReachable(localTarget.host, localTarget.port, 450))
            {
                localServiceWarning = $"Warning: local service {localTarget.host}:{localTarget.port} is not reachable right now. Waiting for it while tunnel starts.";
            }

            File.WriteAllText(configPath, configClean);
            AppendRuntimeLog(displayName, "Starting frpc...");
            if (!string.IsNullOrWhiteSpace(localServiceWarning))
            {
                AppendRuntimeLog(displayName, localServiceWarning);
            }
            if (GetActiveRuntimeCount() == 0)
            {
                bytesUp = 0; bytesDown = 0; lastUp = 0; lastDown = 0;
                trafficBaselineReady = TryGetSystemNetworkTotals(out baselineSentBytes, out baselineRecvBytes);
            }
            try { this.Invoke(() => UpdateTrayIcon()); } catch { }

            runtime.Cancellation?.Cancel();
            runtime.Cancellation?.Dispose();
            runtime.Cancellation = new CancellationTokenSource();
            runtime.Connected = false;
            var startupResult = new TaskCompletionSource<(bool success, string error)>(TaskCreationOptions.RunContinuationsAsynchronously);
            int startupResolved = 0;
            int verificationStarted = 0;
            int startupErrorDetected = 0;
            bool proxyReportedReady = false;
            string lastFrpcLogLine = string.Empty;
            object lastFrpcLogLock = new object();

            void ResolveStartup(bool success, string error)
            {
                if (Interlocked.Exchange(ref startupResolved, 1) != 0)
                {
                    return;
                }

                startupResult.TrySetResult((success, error ?? string.Empty));
            }

            void FailStartup(string error)
            {
                ResolveStartup(false, error);
            }

            void StartVerification()
            {
                if (Interlocked.Exchange(ref verificationStarted, 1) != 0)
                {
                    return;
                }

                Task.Run(() =>
                {
                    var token = runtime.Cancellation?.Token ?? CancellationToken.None;
                    try
                    {
                        Task.Delay(StartupReadyHoldMs, token).Wait(token);
                    }
                    catch
                    {
                        return;
                    }

                    if (Volatile.Read(ref startupResolved) != 0 || token.IsCancellationRequested)
                    {
                        return;
                    }

                    if (Volatile.Read(ref startupErrorDetected) != 0)
                    {
                        FailStartup("Tunnel reported startup errors before readiness was confirmed.");
                        return;
                    }

                    if (!secureMode)
                    {
                        ResolveStartup(true, string.Empty);
                        return;
                    }

                    var verificationError = VerifyTunnelIsReachable(serverHost, remotePort, token, StartupVerificationTimeoutMs);
                    if (!string.IsNullOrWhiteSpace(verificationError))
                    {
                        FailStartup(verificationError);
                        return;
                    }

                    try
                    {
                        Task.Delay(1200, token).Wait(token);
                    }
                    catch
                    {
                        return;
                    }

                    if (Volatile.Read(ref startupResolved) != 0 || token.IsCancellationRequested)
                    {
                        return;
                    }

                    if (Volatile.Read(ref startupErrorDetected) != 0)
                    {
                        FailStartup("Tunnel reported startup errors after verification.");
                        return;
                    }

                    ResolveStartup(true, string.Empty);
                });
            }

            void HandleFrpcLogLine(string line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    return;
                }

                logs.Add(line);
                var plain = StripAnsiEscapeCodes(line);
                if (!string.IsNullOrWhiteSpace(plain))
                {
                    lock (lastFrpcLogLock)
                    {
                        lastFrpcLogLine = plain;
                    }
                }

                if (IsFrpcProxyStartedLine(line))
                {
                    proxyReportedReady = true;
                    StartVerification();
                    return;
                }

                if (TryExtractFrpcStartupError(line, out var startupError))
                {
                    Interlocked.Exchange(ref startupErrorDetected, 1);
                    FailStartup(startupError);
                }
            }

            Task.Run(() =>
            {
                Process process = null;
                try
                {
                    process = new Process();
                    process.StartInfo.FileName = frpcPath;
                    process.StartInfo.Arguments = $"-c \"{configPath}\"";
                    process.StartInfo.UseShellExecute = false;
                    process.StartInfo.RedirectStandardOutput = true;
                    process.StartInfo.RedirectStandardError = true;
                    process.StartInfo.CreateNoWindow = true;
                    process.OutputDataReceived += (_, e) => HandleFrpcLogLine(e.Data ?? string.Empty);
                    process.ErrorDataReceived += (_, e) => HandleFrpcLogLine(e.Data ?? string.Empty);
                    runtime.Process = process;
                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    Task.Run(SendStartupLogAsync);
                    process.WaitForExit();
                    var exitCode = process.ExitCode;
                    runtime.Connected = false;
                    runtime.Process = null;
                    AppendRuntimeLog(displayName, "Process exited");
                    string exitReason = string.Empty;
                    lock (lastFrpcLogLock)
                    {
                        exitReason = lastFrpcLogLine;
                    }
                    if (TryExtractFrpcStartupError(exitReason, out var parsedExitError))
                    {
                        ResolveStartup(false, parsedExitError);
                    }
                    else
                    {
                        var fallbackReason = string.IsNullOrWhiteSpace(exitReason)
                            ? $"frpc завершился до запуска туннеля (код {exitCode}). Проверьте токен, конфиг и доступность сервера."
                            : $"{exitReason} (код {exitCode})";
                        if (!proxyReportedReady)
                        {
                            ResolveStartup(false, fallbackReason);
                        }
                        else
                        {
                            ResolveStartup(false, fallbackReason);
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppendRuntimeLog(displayName, $"ERROR: {ex.Message}");
                    runtime.Connected = false;
                    runtime.Process = null;
                    ResolveStartup(false, ex.Message);
                }
                finally
                {
                    RefreshConnectedState();

                    try
                    {
                        if (!string.IsNullOrWhiteSpace(configPath) && File.Exists(configPath))
                        {
                            File.Delete(configPath);
                        }
                    }
                    catch { }

                    process?.Dispose();
                }
            });

            if (!startupResult.Task.Wait(TimeSpan.FromMilliseconds(StartupConnectTimeoutMs)))
            {
                var timeoutError = "Timed out while waiting for proxy startup confirmation.";
                AppendRuntimeLog(displayName, timeoutError);
                DisconnectTunnel(runtimeKey, false);
                Respond(ResolveResponseKey(responseKey, "_resolve_connect"), JsonConvert.SerializeObject(new { success = false, error = timeoutError }));
                return;
            }

            var startupState = startupResult.Task.Result;
            if (!startupState.success)
            {
                runtime.Connected = false;
                runtime.Process = null;
                RefreshConnectedState();
                DisconnectTunnel(runtimeKey, false);
                var startupError = string.IsNullOrWhiteSpace(startupState.error)
                    ? "Unable to start tunnel proxy."
                    : startupState.error;
                if (attempt < 4 && IsDuplicateProxyStartError(startupError))
                {
                    AppendRuntimeLog(displayName, $"Previous session is still releasing. Retrying connection ({attempt + 1}/4)...");
                    if (!string.IsNullOrWhiteSpace(reconnectContext.serverHost) &&
                        reconnectContext.serverPort > 0 &&
                        !string.IsNullOrWhiteSpace(reconnectContext.frpUser) &&
                        reconnectContext.remotePort > 0 &&
                        reconnectContext.localPort > 0 &&
                        !string.IsNullOrWhiteSpace(reconnectContext.authToken) &&
                        !string.IsNullOrWhiteSpace(reconnectContext.logToken) &&
                        !string.IsNullOrWhiteSpace(reconnectContext.expiresAt))
                    {
                        ValidateTunnelConfigWithServerAsync(
                            reconnectContext.serverHost,
                            reconnectContext.serverPort,
                            reconnectContext.frpUser,
                            reconnectContext.remotePort,
                            reconnectContext.localPort,
                            reconnectContext.authToken,
                            reconnectContext.logToken,
                            ExtractConfigTunnelType(configClean),
                            reconnectContext.expiresAt
                        ).GetAwaiter().GetResult();
                    }

                    Thread.Sleep(Math.Min(7000, 2500 * attempt));
                    StartFrpc(runtimeKey, displayName, configClean, serverHost, serverPort, remotePort, attempt + 1, secureMode, responseKey);
                    return;
                }
                Respond(ResolveResponseKey(responseKey, "_resolve_connect"), JsonConvert.SerializeObject(new { success = false, error = startupError }));
                return;
            }

            runtime.Connected = true;
            runtime.ServerHost = serverHost ?? string.Empty;
            runtime.ServerPort = serverPort;
            runtime.RemotePort = remotePort;
            RefreshConnectedState();
            Respond(ResolveResponseKey(responseKey, "_resolve_connect"), JsonConvert.SerializeObject(new { success = true, mode = "frpc", profile_key = runtimeKey, active_profile_ids = GetActiveRuntimeKeys() }));
        }

        private void DoDisconnect()
        {
            foreach (var runtimeKey in tunnelRuntimes.Keys.ToArray())
            {
                DisconnectTunnel(runtimeKey, false);
            }
            RefreshConnectedState();
        }

        private void TerminateExistingFrpcProcesses(string preferredFrpcPath)
        {
            try
            {
                var targetName = Path.GetFileNameWithoutExtension(preferredFrpcPath ?? "frpc");
                foreach (var process in Process.GetProcessesByName(targetName))
                {
                    try
                    {
                        if (process == null || process.HasExited)
                        {
                            continue;
                        }

                        var shouldKill = true;
                        try
                        {
                            var runningPath = process.MainModule?.FileName;
                            if (!string.IsNullOrWhiteSpace(preferredFrpcPath) && !string.IsNullOrWhiteSpace(runningPath))
                            {
                                shouldKill = string.Equals(
                                    Path.GetFullPath(runningPath),
                                    Path.GetFullPath(preferredFrpcPath),
                                    StringComparison.OrdinalIgnoreCase);
                            }
                        }
                        catch
                        {
                            shouldKill = true;
                        }

                        if (!shouldKill)
                        {
                            continue;
                        }

                        try { process.Kill(); } catch { }
                        try { process.WaitForExit(3000); } catch { }
                    }
                    finally
                    {
                        try { process.Dispose(); } catch { }
                    }
                }
            }
            catch
            {
            }
        }

        private void DisconnectTunnel(string runtimeKey, bool addDisconnectLog = true)
        {
            if (string.IsNullOrWhiteSpace(runtimeKey))
            {
                return;
            }

            var runtime = GetRuntimeSession(runtimeKey);
            if (runtime == null)
            {
                return;
            }

            NotifyServerTunnelDisconnect(runtime);
            runtime.Connected = false;
            try { runtime.Cancellation?.Cancel(); } catch { }
            try { runtime.Cancellation?.Dispose(); } catch { }
            runtime.Cancellation = null;

            var process = runtime.Process;
            runtime.Process = null;
            if (process != null)
            {
                try
                {
                    var hasExited = false;
                    try
                    {
                        hasExited = process.HasExited;
                    }
                    catch
                    {
                        hasExited = true;
                    }

                    if (!hasExited)
                    {
                        try
                        {
                            process.Kill();
                        }
                        catch
                        {
                        }
                    }
                }
                catch
                {
                }
            }

            RefreshConnectedState();
            if (addDisconnectLog)
            {
                AppendRuntimeLog(runtime.DisplayName, "Disconnected");
            }
        }

        private void Disconnect(string runtimeKey = "", string responseKey = "")
        {
            if (string.IsNullOrWhiteSpace(runtimeKey))
            {
                DoDisconnect();
                logs.Add("Disconnected");
            }
            else
            {
                DisconnectTunnel(runtimeKey, true);
            }

            Respond(ResolveResponseKey(responseKey, "_resolve_disconnect"), JsonConvert.SerializeObject(new
            {
                success = true,
                active_profile_ids = GetActiveRuntimeKeys(),
                active_count = GetActiveRuntimeCount(),
            }));
        }

        private void GetTraffic()
        {
            if (connected && trafficBaselineReady && TryGetSystemNetworkTotals(out var currentSent, out var currentRecv))
            {
                bytesUp = Math.Max(0, currentSent - baselineSentBytes);
                bytesDown = Math.Max(0, currentRecv - baselineRecvBytes);
            }

            long up = bytesUp - lastUp;
            long down = bytesDown - lastDown;
            lastUp = bytesUp;
            lastDown = bytesDown;
            Respond("_resolve_traffic", JsonConvert.SerializeObject(new { up, down, totalUp = bytesUp, totalDown = bytesDown }));
        }

        private static bool TryGetSystemNetworkTotals(out long totalSent, out long totalRecv)
        {
            totalSent = 0;
            totalRecv = 0;
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up)
                    {
                        continue;
                    }

                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                        nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                    {
                        continue;
                    }

                    var stats = nic.GetIPv4Statistics();
                    totalSent += stats.BytesSent;
                    totalRecv += stats.BytesReceived;
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private void PickFile()
        {
            this.Invoke(() =>
            {
                using var ofd = new OpenFileDialog();
                ofd.Filter = "FRP Config (*.frp;*.toml)|*.frp;*.toml|All Files (*.*)|*.*";
                if (ofd.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        string content = File.ReadAllText(ofd.FileName);
                        Respond("_resolve_file", content);
                    }
                    catch { Respond("_resolve_file", ""); }
                }
                else
                {
                    Respond("_resolve_file", "");
                }
            });
        }

        private static string NormalizeConfigText(string config)
        {
            if (string.IsNullOrWhiteSpace(config))
            {
                return string.Empty;
            }

            string normalized = config.Trim().Trim('\uFEFF');

            if (normalized.Length >= 2 && normalized[0] == '"' && normalized[^1] == '"')
            {
                try
                {
                    normalized = JsonConvert.DeserializeObject<string>(normalized) ?? normalized;
                }
                catch
                {
                }
            }

            normalized = normalized
                .Replace("\r\n", "\n")
                .Replace("\\r\\n", "\n")
                .Replace("\\n", "\n")
                .Replace("\\\"", "\"")
                .Replace("\n", Environment.NewLine);

            return normalized.Trim();
        }

        private string NormalizeConnectionStringText(string connStr)
        {
            if (string.IsNullOrWhiteSpace(connStr))
            {
                return string.Empty;
            }

            string normalized = connStr.Trim().Trim('\uFEFF');

            if (normalized.Length >= 2 && normalized[0] == '"' && normalized[^1] == '"')
            {
                try
                {
                    normalized = JsonConvert.DeserializeObject<string>(normalized) ?? normalized;
                }
                catch
                {
                }
            }

            return normalized.Trim();
        }

        private JObject LoadClientSettings()
        {
            try
            {
                return JObject.Parse(LoadState());
            }
            catch
            {
                return new JObject();
            }
        }

        private bool IsAutoReconnectEnabled()
        {
            var settings = LoadClientSettings();
            return settings["reconnect"]?.ToObject<bool>() ?? true;
        }

        private int GetKeepaliveSeconds()
        {
            var settings = LoadClientSettings();
            var raw = settings["keepalive"]?.ToString() ?? "30";
            if (!int.TryParse(raw, out var seconds))
            {
                seconds = 30;
            }

            if (seconds < 10) seconds = 10;
            if (seconds > 300) seconds = 300;
            return seconds;
        }

        private static string InsertLineBeforeProxySection(string config, string line)
        {
            var marker = Regex.Match(config, @"^\s*\[\[proxies\]\]\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            if (marker.Success)
            {
                var insertAt = marker.Index;
                var prefix = config[..insertAt].TrimEnd();
                var suffix = config[insertAt..].TrimStart('\r', '\n');
                return $"{prefix}{Environment.NewLine}{line}{Environment.NewLine}{Environment.NewLine}{suffix}";
            }

            var normalized = config.TrimEnd();
            return $"{normalized}{Environment.NewLine}{line}";
        }

        private static string UpsertConfigLine(string config, string regexPattern, string replacementLine, bool insertBeforeProxyWhenMissing = false)
        {
            if (Regex.IsMatch(config, regexPattern, RegexOptions.Multiline | RegexOptions.IgnoreCase))
            {
                return Regex.Replace(config, regexPattern, replacementLine, RegexOptions.Multiline | RegexOptions.IgnoreCase);
            }

            if (insertBeforeProxyWhenMissing)
            {
                return InsertLineBeforeProxySection(config, replacementLine);
            }

            var normalized = config.TrimEnd();
            return $"{normalized}{Environment.NewLine}{replacementLine}";
        }

        private string ApplyReliabilityDefaults(string config)
        {
            var normalized = NormalizeConfigText(config);
            var keepalive = GetKeepaliveSeconds();
            var heartbeatInterval = Math.Max(10, Math.Min(keepalive, 20));

            normalized = UpsertConfigLine(normalized, @"^\s*transport\.protocol\s*=\s*""[^""]+""\s*$", "transport.protocol = \"tcp\"", true);
            normalized = UpsertConfigLine(normalized, @"^\s*transport\.poolCount\s*=\s*\d+\s*$", "transport.poolCount = 5", true);
            normalized = UpsertConfigLine(normalized, @"^\s*transport\.tcpMux\s*=\s*(true|false)\s*$", "transport.tcpMux = true", true);
            normalized = UpsertConfigLine(normalized, @"^\s*transport\.tcpMuxKeepaliveInterval\s*=\s*\d+\s*$", $"transport.tcpMuxKeepaliveInterval = {keepalive}", true);
            normalized = UpsertConfigLine(normalized, @"^\s*transport\.dialServerKeepalive\s*=\s*\d+\s*$", "transport.dialServerKeepalive = 7200", true);
            normalized = UpsertConfigLine(normalized, @"^\s*transport\.heartbeatInterval\s*=\s*-?\d+\s*$", $"transport.heartbeatInterval = {heartbeatInterval}", true);
            normalized = UpsertConfigLine(normalized, @"^\s*transport\.heartbeatTimeout\s*=\s*-?\d+\s*$", $"transport.heartbeatTimeout = {Math.Max(heartbeatInterval * 3, 45)}", true);

            return normalized.Trim();
        }

        private static (string host, int port) ExtractLocalServiceTarget(string config)
        {
            if (string.IsNullOrWhiteSpace(config))
            {
                return ("127.0.0.1", 0);
            }

            var host = Regex.Match(config, @"^\s*localIP\s*=\s*""([^""]+)""", RegexOptions.Multiline).Groups[1].Value.Trim();
            if (string.IsNullOrWhiteSpace(host))
            {
                host = "127.0.0.1";
            }

            var localPortText = Regex.Match(config, @"^\s*localPort\s*=\s*(\d+)", RegexOptions.Multiline).Groups[1].Value;
            var port = int.TryParse(localPortText, out var parsedPort) ? parsedPort : 0;
            return (host, port);
        }

        private static bool IsLocalServiceReachable(string host, int port, int timeoutMs)
        {
            try
            {
                using var tcpClient = new TcpClient();
                var connectTask = tcpClient.ConnectAsync(host, port);
                if (!connectTask.Wait(timeoutMs))
                {
                    return false;
                }
                return tcpClient.Connected;
            }
            catch
            {
                return false;
            }
        }

        private static string StripAnsiEscapeCodes(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            return Regex.Replace(value, @"\x1B\[[0-9;]*[A-Za-z]", string.Empty).Trim();
        }

        private static bool IsFrpcProxyStartedLine(string line)
        {
            var normalized = StripAnsiEscapeCodes(line).ToLowerInvariant();
            return normalized.Contains("start proxy success") ||
                   normalized.Contains("proxy added:");
        }

        private static bool TryExtractFrpcStartupError(string line, out string error)
        {
            var normalized = StripAnsiEscapeCodes(line);
            var lower = normalized.ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(lower))
            {
                error = string.Empty;
                return false;
            }

            if (lower.Contains("start error:") ||
                lower.Contains("already exists") ||
                lower.Contains("login to server failed") ||
                lower.Contains("error unmarshaling json") ||
                lower.Contains("connect to server error") ||
                lower.Contains("token in login doesn't match token from configuration") ||
                lower.Contains("toml:") ||
                lower.Contains("invalid character at start of key") ||
                lower.Contains("no such host") ||
                lower.Contains("authentication failed") ||
                lower.Contains("authorization failed"))
            {
                error = normalized;
                return true;
            }

            error = string.Empty;
            return false;
        }

        private static bool IsDuplicateProxyStartError(string error)
        {
            var lower = StripAnsiEscapeCodes(error).ToLowerInvariant();
            return lower.Contains("already exists") ||
                   (lower.Contains("proxy") && lower.Contains("exists"));
        }

        private static string VerifyTunnelIsReachable(string host, int port, CancellationToken cancellationToken, int timeoutMs)
        {
            if (string.IsNullOrWhiteSpace(host) || port < 1 || port > 65535)
            {
                return "Invalid tunnel verification target";
            }

            var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(timeoutMs, 1000));
            Exception lastError = null;

            while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    using var tcpClient = new TcpClient();
                    var remaining = (int)Math.Max(250, Math.Min(1000, (deadline - DateTime.UtcNow).TotalMilliseconds));
                    var connectTask = tcpClient.ConnectAsync(host, port);
                    if (connectTask.Wait(remaining) && tcpClient.Connected)
                    {
                        return string.Empty;
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex;
                }

                try
                {
                    Task.Delay(350, cancellationToken).Wait(cancellationToken);
                }
                catch
                {
                    break;
                }
            }

            return lastError == null
                ? $"Tunnel verification failed for {host}:{port}"
                : $"Tunnel verification failed for {host}:{port}: {lastError.Message}";
        }

        private System.Collections.Specialized.NameValueCollection ParseConnectionQuery(string connStr)
        {
            if (Uri.TryCreate(connStr, UriKind.Absolute, out var absoluteUri))
            {
                return System.Web.HttpUtility.ParseQueryString(absoluteUri.Query);
            }

            int queryIndex = connStr.IndexOf('?');
            if (queryIndex >= 0 && queryIndex < connStr.Length - 1)
            {
                return System.Web.HttpUtility.ParseQueryString(connStr[(queryIndex + 1)..]);
            }

            if (connStr.Contains("="))
            {
                return System.Web.HttpUtility.ParseQueryString(connStr.TrimStart('?'));
            }

            throw new UriFormatException("Invalid connection string");
        }

        private void SetAutostart(bool enabled)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
                if (enabled)
                    key?.SetValue("TCP Tunnel", $"\"{Application.ExecutablePath}\"");
                else
                    key?.DeleteValue("TCP Tunnel", false);
            }
            catch { }
        }

        private bool GetAutostartState()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false);
                return key?.GetValue("TCP Tunnel") != null;
            }
            catch { return false; }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (trayEnabled && trayIcon != null)
            {
                e.Cancel = true;
                this.Hide();
            }
            else
            {
                DoDisconnect();
                trayIcon?.Dispose();
                lock (notifySoundLock)
                {
                    try { notifyOutput?.Stop(); } catch { }
                    notifyOutput?.Dispose();
                    notifyReader?.Dispose();
                    notifyOutput = null;
                    notifyReader = null;
                }
                trayActiveIcon?.Dispose();
                trayInactiveIcon?.Dispose();
                trayActiveIcon = null;
                trayInactiveIcon = null;
            }
            base.OnFormClosing(e);
        }

        private Icon BuildTrayStatusIcon(bool isActive)
        {
            using var canvas = new Bitmap(32, 32);
            using var graphics = Graphics.FromImage(canvas);

            graphics.Clear(Color.Transparent);
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;

            Color background = isActive ? Color.FromArgb(20, 120, 72) : Color.FromArgb(133, 142, 152);
            Color serverFill = isActive ? Color.FromArgb(227, 255, 239) : Color.FromArgb(248, 250, 252);
            Color serverStroke = isActive ? Color.FromArgb(8, 89, 56) : Color.FromArgb(94, 109, 126);
            Color accent = isActive ? Color.FromArgb(34, 197, 94) : Color.FromArgb(203, 213, 225);
            Color light = isActive ? Color.FromArgb(22, 163, 74) : Color.FromArgb(148, 163, 184);

            using var backBrush = new SolidBrush(background);
            graphics.FillEllipse(backBrush, 1, 1, 30, 30);

            using var rackPen = new Pen(serverStroke, 1.4f);
            using var rackBrush = new SolidBrush(serverFill);
            DrawServerRack(graphics, rackBrush, rackPen, 7, 7);
            DrawServerRack(graphics, rackBrush, rackPen, 7, 14);
            DrawServerRack(graphics, rackBrush, rackPen, 7, 21);

            using var accentBrush = new SolidBrush(accent);
            using var lightBrush = new SolidBrush(light);
            graphics.FillEllipse(accentBrush, 9, 10, 3, 3);
            graphics.FillEllipse(accentBrush, 9, 17, 3, 3);
            graphics.FillEllipse(accentBrush, 9, 24, 3, 3);

            graphics.FillRectangle(lightBrush, 14, 10, 8, 1);
            graphics.FillRectangle(lightBrush, 14, 17, 8, 1);
            graphics.FillRectangle(lightBrush, 14, 24, 8, 1);

            Rectangle badgeRect = new Rectangle(20, 20, 10, 10);
            using var badgeBrush = new SolidBrush(isActive ? Color.FromArgb(34, 197, 94) : Color.FromArgb(241, 245, 249));
            using var badgeBorder = new Pen(Color.FromArgb(255, 255, 255), 2f);
            graphics.FillEllipse(badgeBrush, badgeRect);
            graphics.DrawEllipse(badgeBorder, badgeRect);

            IntPtr hIcon = canvas.GetHicon();
            using var tempIcon = Icon.FromHandle(hIcon);
            Icon clonedIcon = (Icon)tempIcon.Clone();
            DestroyIcon(hIcon);
            return clonedIcon;
        }

        private void DrawServerRack(Graphics graphics, Brush fillBrush, Pen borderPen, int x, int y)
        {
            using var path = CreateRoundedRect(new RectangleF(x, y, 16, 5), 2.4f);
            graphics.FillPath(fillBrush, path);
            graphics.DrawPath(borderPen, path);
        }

        private System.Drawing.Drawing2D.GraphicsPath CreateRoundedRect(RectangleF rect, float radius)
        {
            float diameter = radius * 2f;
            var path = new System.Drawing.Drawing2D.GraphicsPath();
            path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
            path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
            path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        private const int WM_NCHITTEST = 0x84;
        private const int HTCLIENT = 1;
        private const int HTCAPTION = 2;
        private const int TITLEBAR_HEIGHT = 34;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_BORDER_COLOR = 34;
        private const int DWMWA_CAPTION_COLOR = 35;
        private const int DWMWA_TEXT_COLOR = 36;
        private const int WM_SETICON = 0x0080;
        private static readonly IntPtr ICON_SMALL = IntPtr.Zero;

        [DllImport("user32.dll")]
        static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);
        [DllImport("user32.dll", EntryPoint = "SendMessageW")]
        static extern IntPtr SendMessageIcon(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        static extern bool ReleaseCapture();
        [DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr hIcon);
        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

        private void TryApplySystemTitleBarTheme()
        {
            try
            {
                int useDark = 1;
                DwmSetWindowAttribute(this.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int));

                int captionColor = ToColorRef(System.Drawing.Color.FromArgb(17, 17, 17));
                DwmSetWindowAttribute(this.Handle, DWMWA_CAPTION_COLOR, ref captionColor, sizeof(int));

                int textColor = ToColorRef(System.Drawing.Color.FromArgb(230, 230, 230));
                DwmSetWindowAttribute(this.Handle, DWMWA_TEXT_COLOR, ref textColor, sizeof(int));

                int borderColor = ToColorRef(System.Drawing.Color.FromArgb(36, 36, 36));
                DwmSetWindowAttribute(this.Handle, DWMWA_BORDER_COLOR, ref borderColor, sizeof(int));
            }
            catch
            {
            }
        }

        private static int ToColorRef(System.Drawing.Color color)
        {
            return color.R | (color.G << 8) | (color.B << 16);
        }

        private void HideCaptionSmallIcon()
        {
            try
            {
                SendMessageIcon(this.Handle, WM_SETICON, ICON_SMALL, IntPtr.Zero);
            }
            catch
            {
            }
        }

        private void StartDrag()
        {
            this.Invoke(() =>
            {
                ReleaseCapture();
                SendMessage(this.Handle, 0xA1, 2, 0);
            });
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
        }
    }
}
