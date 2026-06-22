using CtYun;
using CtYun.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CtYun
{
    internal static class GlobalState
    {
        public static AppConfig Config { get; set; } = new();
        public static string ConfigPath { get; set; }
        public static string DataDir { get; set; }

        public static CancellationTokenSource GlobalCts { get; } = new();

        // Active keepalive cancellation tokens for each account name
        public static ConcurrentDictionary<string, CancellationTokenSource> ActiveWorkers { get; } = new();

        // Runtime statuses for each account user (phone)
        public static ConcurrentDictionary<string, AccountStatusInfo> AccountStatuses { get; } = new();

        // Pending logins waiting for SMS verification: User -> CtYunApi instance
        public static ConcurrentDictionary<string, CtYunApi> PendingLogins { get; } = new();

        // Pending login configurations waiting for SMS verification: User -> AccountConfig
        public static ConcurrentDictionary<string, AccountConfig> PendingConfigs { get; } = new();

        // Memory session token
        public static string CurrentToken { get; set; } = Guid.NewGuid().ToString("N");
    }

    internal class AccountStatusInfo
    {
        public bool IsRunning { get; set; }
        public string StatusText { get; set; } = "未运行";
        public List<DesktopStatusDto> Desktops { get; set; } = new();
    }
}

namespace CtYun
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            Utility.WriteLine(ConsoleColor.Green, $"版本：v {Assembly.GetEntryAssembly()?.GetName().Version}");

            GlobalState.DataDir = GetDataDir();
            Directory.CreateDirectory(GlobalState.DataDir);

            // Load initial config
            GlobalState.Config = LoadRuntimeConfig();

            // Set up web builder
            var builder = WebApplication.CreateBuilder(args);
            
            // Set port (default 8080)
            var portStr = Environment.GetEnvironmentVariable("PORT") ?? "8080";
            builder.WebHost.UseUrls($"http://*:{portStr}");

            // AOT compilation requires static type info chain
            builder.Services.ConfigureHttpJsonOptions(options =>
            {
                options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default);
            });

            var app = builder.Build();

            app.UseDefaultFiles();
            app.UseStaticFiles();

            // --- REST API Endpoints ---

            // Helper to authorize requests based on header X-Auth-Token or query parameter token (for SSE)
            bool AuthorizeRequest(HttpContext context)
            {
                string token = "";
                if (context.Request.Headers.TryGetValue("X-Auth-Token", out var tokenValue))
                {
                    token = tokenValue.ToString();
                }
                else if (context.Request.Query.TryGetValue("token", out var queryValue))
                {
                    token = queryValue.ToString();
                }
                return string.Equals(token, GlobalState.CurrentToken);
            }

            // Login endpoint
            app.MapPost("/api/login", (LoginRequest req) =>
            {
                var savedPassword = GlobalState.Config.AdminPassword ?? "admin";
                if (string.Equals(req.Password, savedPassword))
                {
                    return Results.Ok(new WebResponseBase { Status = "Success", Message = GlobalState.CurrentToken });
                }
                return Results.Ok(new WebResponseBase { Status = "Error", Message = "密码错误" });
            });

            // Change password endpoint
            app.MapPost("/api/change-password", (ChangePasswordRequest req, HttpContext context) =>
            {
                if (!AuthorizeRequest(context)) return Results.Unauthorized();

                var savedPassword = GlobalState.Config.AdminPassword ?? "admin";
                if (!string.Equals(req.OldPassword, savedPassword))
                {
                    return Results.Ok(new WebResponseBase { Success = false, Msg = "原密码错误" });
                }

                if (string.IsNullOrWhiteSpace(req.NewPassword))
                {
                    return Results.Ok(new WebResponseBase { Success = false, Msg = "新密码不能为空" });
                }

                GlobalState.Config.AdminPassword = req.NewPassword.Trim();
                SaveConfigToFile();
                return Results.Ok(new WebResponseBase { Success = true });
            });

            // 1. Get accounts and statuses
            app.MapGet("/api/accounts", (HttpContext context) =>
            {
                if (!AuthorizeRequest(context)) return Results.Unauthorized();

                var list = new List<AccountStatusDto>();
                
                // Active / saved accounts
                foreach (var account in GlobalState.Config.Accounts)
                {
                    var statusInfo = GlobalState.AccountStatuses.TryGetValue(account.User, out var s) 
                        ? s 
                        : new AccountStatusInfo { IsRunning = false, StatusText = "已停止" };

                    list.Add(new AccountStatusDto
                    {
                        Name = account.Name,
                        User = account.User,
                        IsRunning = statusInfo.IsRunning,
                        StatusText = statusInfo.StatusText,
                        Desktops = statusInfo.Desktops
                    });
                }

                // Pending logins waiting for SMS
                foreach (var pendingUser in GlobalState.PendingLogins.Keys)
                {
                    if (list.Any(a => a.User == pendingUser)) continue;

                    GlobalState.PendingConfigs.TryGetValue(pendingUser, out var pendingConfig);
                    list.Add(new AccountStatusDto
                    {
                        Name = pendingConfig?.Name ?? pendingUser,
                        User = pendingUser,
                        IsRunning = false,
                        StatusText = "等待验证码",
                        Desktops = new List<DesktopStatusDto>()
                    });
                }

                return Results.Ok(list);
            });

            // 2. Add and login account (or request SMS)
            app.MapPost("/api/accounts", async (AccountConfig account, HttpContext context) =>
            {
                if (!AuthorizeRequest(context)) return Results.Unauthorized();

                if (string.IsNullOrWhiteSpace(account.User) || string.IsNullOrWhiteSpace(account.Password))
                {
                    return Results.BadRequest(new WebResponseBase { Status = "Error", Message = "账号和密码不能为空" });
                }

                account.Name = string.IsNullOrWhiteSpace(account.Name) ? account.User : account.Name;
                account.DeviceCode = ResolveDeviceCode(account, GlobalState.DataDir);

                var api = new CtYunApi(account.DeviceCode);
                Utility.WriteLine(ConsoleColor.Cyan, $"[{account.Name}] 尝试网页登录...");

                var loginSuccess = await api.LoginAsync(account.User, account.Password);
                if (!loginSuccess)
                {
                    return Results.Ok(new WebResponseBase { Status = "Error", Message = "登录失败，密码可能错误或验证码识别失败" });
                }

                if (api.LoginInfo.BondedDevice)
                {
                    // Update configuration
                    var existing = GlobalState.Config.Accounts.FirstOrDefault(a => a.User == account.User);
                    if (existing != null)
                    {
                        GlobalState.Config.Accounts.Remove(existing);
                    }
                    GlobalState.Config.Accounts.Add(account);
                    SaveConfigToFile();

                    StartKeepAliveTask(account, api);
                    return Results.Ok(new WebResponseBase { Status = "Success", Message = "登录成功并启动保活" });
                }
                else
                {
                    Utility.WriteLine(ConsoleColor.Yellow, $"[{account.Name}] 当前设备未绑定，发送验证码短信中...");
                    var smsSent = await api.GetSmsCodeAsync(account.User);
                    if (!smsSent)
                    {
                        return Results.Ok(new WebResponseBase { Status = "Error", Message = "发送短信验证码失败" });
                    }

                    GlobalState.PendingLogins[account.User] = api;
                    GlobalState.PendingConfigs[account.User] = account;

                    var statusInfo = GlobalState.AccountStatuses.GetOrAdd(account.User, _ => new AccountStatusInfo());
                    statusInfo.IsRunning = false;
                    statusInfo.StatusText = "等待验证码";

                    return Results.Ok(new WebResponseBase { Status = "NeedSMS", Message = "短信验证码已发送" });
                }
            });

            // 3. Verify SMS and bind device
            app.MapPost("/api/accounts/verify", async (VerifySmsRequest req, HttpContext context) =>
            {
                if (!AuthorizeRequest(context)) return Results.Unauthorized();

                if (string.IsNullOrWhiteSpace(req.User) || string.IsNullOrWhiteSpace(req.Code))
                {
                    return Results.BadRequest(new WebResponseBase { Status = "Error", Message = "手机号和验证码不能为空" });
                }

                if (!GlobalState.PendingLogins.TryGetValue(req.User, out var api) ||
                    !GlobalState.PendingConfigs.TryGetValue(req.User, out var account))
                {
                    return Results.Ok(new WebResponseBase { Status = "Error", Message = "会话已超时，请重新添加账号进行登录" });
                }

                Utility.WriteLine(ConsoleColor.Cyan, $"[{account.Name}] 正在验证验证码: {req.Code}");
                var bindSuccess = await api.BindingDeviceAsync(req.Code.Trim());
                if (!bindSuccess)
                {
                    return Results.Ok(new WebResponseBase { Status = "Error", Message = "绑定失败，验证码可能错误或失效" });
                }

                // Add and save config
                var existing = GlobalState.Config.Accounts.FirstOrDefault(a => a.User == req.User);
                if (existing != null)
                {
                    GlobalState.Config.Accounts.Remove(existing);
                }
                GlobalState.Config.Accounts.Add(account);
                SaveConfigToFile();

                GlobalState.PendingLogins.TryRemove(req.User, out _);
                GlobalState.PendingConfigs.TryRemove(req.User, out _);

                StartKeepAliveTask(account, api);
                return Results.Ok(new WebResponseBase { Status = "Success", Message = "绑定设备成功，保活任务已启动" });
            });

            // 4. Start keepalive task manually
            app.MapPost("/api/accounts/start", async (AccountActionRequest req, HttpContext context) =>
            {
                if (!AuthorizeRequest(context)) return Results.Unauthorized();

                var account = GlobalState.Config.Accounts.FirstOrDefault(a => a.Name == req.Name || a.User == req.Name);
                if (account == null)
                {
                    return Results.Ok(new WebResponseBase { Success = false, Msg = "未找到配置账号" });
                }

                var api = new CtYunApi(account.DeviceCode);
                Utility.WriteLine(ConsoleColor.Cyan, $"[{account.Name}] 手动启动保活中...");

                if (await api.LoginAsync(account.User, account.Password))
                {
                    if (api.LoginInfo.BondedDevice)
                    {
                        StartKeepAliveTask(account, api);
                        return Results.Ok(new WebResponseBase { Success = true });
                    }
                    return Results.Ok(new WebResponseBase { Success = false, Msg = "该设备未绑定，请删除账号并重新添加以输入验证码" });
                }
                return Results.Ok(new WebResponseBase { Success = false, Msg = "登录验证失败，请检查密码或重试" });
            });

            // 5. Stop keepalive task manually
            app.MapPost("/api/accounts/stop", (AccountActionRequest req, HttpContext context) =>
            {
                if (!AuthorizeRequest(context)) return Results.Unauthorized();

                var success = StopKeepAliveTask(req.Name);
                return Results.Ok(new WebResponseBase { Success = success, Msg = success ? "" : "账号保活已处于停止状态" });
            });

            // 6. Delete account configuration
            app.MapDelete("/api/accounts/{name}", (string name, HttpContext context) =>
            {
                if (!AuthorizeRequest(context)) return Results.Unauthorized();

                StopKeepAliveTask(name);
                var account = GlobalState.Config.Accounts.FirstOrDefault(a => a.Name == name || a.User == name);
                if (account != null)
                {
                    GlobalState.Config.Accounts.Remove(account);
                    SaveConfigToFile();
                    GlobalState.AccountStatuses.TryRemove(account.User, out _);
                }
                return Results.Ok(new WebResponseBase { Success = true });
            });

            // 7. Server-Sent Events log streaming
            app.MapGet("/api/logs", async (HttpContext context) =>
            {
                if (!AuthorizeRequest(context))
                {
                    context.Response.StatusCode = 401;
                    return;
                }

                context.Response.ContentType = "text/event-stream";
                context.Response.Headers.CacheControl = "no-cache";
                context.Response.Headers.Connection = "keep-alive";

                var reader = Utility.LogChannel.Reader;
                try
                {
                    while (!context.RequestAborted.IsCancellationRequested)
                    {
                        if (await reader.WaitToReadAsync(context.RequestAborted))
                        {
                            while (reader.TryRead(out var log))
                            {
                                await context.Response.WriteAsync($"data: {log}\n\n", context.RequestAborted);
                                await context.Response.Body.FlushAsync(context.RequestAborted);
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // SSE connection disconnected
                }
            });

            // Auto-start active keepalive loops for existing accounts
            foreach (var account in GlobalState.Config.Accounts)
            {
                _ = Task.Run(async () =>
                {
                    var label = account.Name ?? account.User;
                    var api = new CtYunApi(account.DeviceCode);
                    Utility.WriteLine(ConsoleColor.Cyan, $"[{label}] [启动自检] 正在登录验证...");
                    
                    if (await api.LoginAsync(account.User, account.Password))
                    {
                        if (api.LoginInfo.BondedDevice)
                        {
                            StartKeepAliveTask(account, api);
                            return;
                        }
                        Utility.WriteLine(ConsoleColor.Yellow, $"[{label}] [启动自检] 该设备未绑定，请在控制面板删除重新添加绑定！");
                        var status = GlobalState.AccountStatuses.GetOrAdd(account.User, _ => new AccountStatusInfo());
                        status.StatusText = "等待验证码";
                    }
                    else
                    {
                        Utility.WriteLine(ConsoleColor.Red, $"[{label}] [启动自检] 自动登录失败，跳过该账号。");
                    }
                });
            }

            Utility.WriteLine(ConsoleColor.Green, $"[系统] Web 服务已启动，监听地址：http://localhost:{portStr}");

            // Wait for shutdown
            await app.RunAsync(GlobalState.GlobalCts.Token);
        }

        // --- Helper keepalive tasks management ---

        private static void StartKeepAliveTask(AccountConfig account, CtYunApi api)
        {
            var key = account.Name ?? account.User;
            StopKeepAliveTask(key);

            var cts = CancellationTokenSource.CreateLinkedTokenSource(GlobalState.GlobalCts.Token);
            GlobalState.ActiveWorkers[key] = cts;

            _ = Task.Run(() => RunAccountKeepAliveAsync(account, api, cts.Token), cts.Token);
        }

        private static bool StopKeepAliveTask(string name)
        {
            if (GlobalState.ActiveWorkers.TryRemove(name, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
                return true;
            }
            return false;
        }

        private static void UpdateAccountStatus(string label, string user, CancellationToken ct, Action<AccountStatusInfo> action)
        {
            bool shouldUpdate = false;
            if (GlobalState.ActiveWorkers.TryGetValue(label, out var activeCts))
            {
                if (activeCts.Token == ct)
                {
                    shouldUpdate = true;
                }
            }
            else
            {
                shouldUpdate = true;
            }

            if (shouldUpdate)
            {
                var status = GlobalState.AccountStatuses.GetOrAdd(user, _ => new AccountStatusInfo());
                action(status);
            }
        }

        private static void UpdateDesktopStatus(string label, string user, string desktopCode, string status, CancellationToken ct)
        {
            if (GlobalState.ActiveWorkers.TryGetValue(label, out var activeCts) && activeCts.Token == ct)
            {
                if (GlobalState.AccountStatuses.TryGetValue(user, out var statusInfo))
                {
                    var d = statusInfo.Desktops.FirstOrDefault(x => x.Code == desktopCode);
                    if (d != null)
                    {
                        d.Status = status;
                    }
                }
            }
        }

        private static async Task RunAccountKeepAliveAsync(AccountConfig account, CtYunApi api, CancellationToken ct)
        {
            var label = account.Name ?? account.User;
            
            UpdateAccountStatus(label, account.User, ct, s => {
                s.IsRunning = true;
                s.StatusText = "正在获取设备...";
                s.Desktops.Clear();
            });

            try
            {
                var desktopList = await api.GetLlientListAsync();
                if (desktopList == null || desktopList.Count == 0)
                {
                    Utility.WriteLine(ConsoleColor.Yellow, $"[{label}] 未获取到可用电脑。");
                    UpdateAccountStatus(label, account.User, ct, s => {
                        s.StatusText = "未配置云电脑/云手机";
                        s.IsRunning = false;
                    });
                    return;
                }

                UpdateAccountStatus(label, account.User, ct, s => {
                    s.StatusText = "连接网关中...";
                    s.Desktops.Clear();
                    foreach (var desktop in desktopList)
                    {
                        s.Desktops.Add(new DesktopStatusDto
                        {
                            Name = desktop.DesktopName,
                            Code = desktop.DesktopCode,
                            Status = desktop.UseStatusText
                        });
                    }
                });

                var activeDesktops = new List<Desktop>();

                foreach (var desktop in desktopList)
                {
                    if (desktop.UseStatusText != "运行中")
                    {
                        Utility.WriteLine(ConsoleColor.Red, $"[{label}][{desktop.DesktopCode}] [{desktop.UseStatusText}] 电脑未开机，正在开机...");
                    }

                    var connectResult = await api.ConnectAsync(desktop.DesktopId);
                    if (connectResult.Success && connectResult.Data?.DesktopInfo != null)
                    {
                        desktop.DesktopInfo = connectResult.Data.DesktopInfo;
                        activeDesktops.Add(desktop);
                        UpdateDesktopStatus(label, account.User, desktop.DesktopCode, "连接就绪", ct);
                    }
                    else
                    {
                        Utility.WriteLine(ConsoleColor.Red, $"[{label}] Connect Error: [{desktop.DesktopId}] {connectResult.Msg}");
                        UpdateDesktopStatus(label, account.User, desktop.DesktopCode, "连接出错: " + connectResult.Msg, ct);
                    }
                }

                if (activeDesktops.Count == 0)
                {
                    Utility.WriteLine(ConsoleColor.Yellow, $"[{label}] 没有可保活的电脑。");
                    UpdateAccountStatus(label, account.User, ct, s => {
                        s.StatusText = "设备连接失败";
                        s.IsRunning = false;
                    });
                    return;
                }

                UpdateAccountStatus(label, account.User, ct, s => {
                    s.StatusText = "保活运行中";
                });
                Utility.WriteLine(ConsoleColor.Yellow, $"[{label}] 保活任务启动：每 {GlobalState.Config.KeepAliveSeconds} 秒强制重连一次。");

                var keepAliveTasks = activeDesktops.Select(d => KeepAliveWorkerWithForcedReset(api, account, d, GlobalState.Config.KeepAliveSeconds, ct));
                await Task.WhenAll(keepAliveTasks);
            }
            catch (OperationCanceledException)
            {
                Utility.WriteLine(ConsoleColor.Yellow, $"[{label}] 保活已停止。");
            }
            catch (Exception ex)
            {
                Utility.WriteLine(ConsoleColor.Red, $"[{label}] 保活异常中断: {ex.Message}");
            }
            finally
            {
                UpdateAccountStatus(label, account.User, ct, s => {
                    s.IsRunning = false;
                    s.StatusText = "已停止";
                });
            }
        }

        private static async Task KeepAliveWorkerWithForcedReset(CtYunApi api, AccountConfig account, Desktop desktop, int keepAliveSeconds, CancellationToken globalToken)
        {
            var label = account.Name ?? account.User;
            var initialPayload = Convert.FromBase64String("UkVEUQIAAAACAAAAGgAAAAAAAAABAAEAAAABAAAAEgAAAAkAAAAECAAA");
            var uri = new Uri($"wss://{desktop.DesktopInfo.ClinkLvsOutHost}/clinkProxy/{desktop.DesktopId}/MAIN");

            while (!globalToken.IsCancellationRequested)
            {
                using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(globalToken);
                sessionCts.CancelAfter(TimeSpan.FromSeconds(keepAliveSeconds));

                using var client = new ClientWebSocket();
                var origin = string.Equals(desktop.DesktopInfo?.OsType, "Android", StringComparison.OrdinalIgnoreCase)
                    ? "https://pm.ctyun.cn"
                    : "https://pc.ctyun.cn";
                client.Options.SetRequestHeader("Origin", origin);
                client.Options.AddSubProtocol("binary");

                try
                {
                    Utility.WriteLine(ConsoleColor.Cyan,  $"[{label}][{desktop.DesktopCode}] === 新周期开始，尝试连接 ===");
                    UpdateDesktopStatus(label, account.User, desktop.DesktopCode, "正在连接...", globalToken);

                    await client.ConnectAsync(uri, sessionCts.Token);

                    var hostParts = desktop.DesktopInfo.ClinkLvsOutHost.Split(':', 2);
                    var connectMessage = new ConnecMessage
                    {
                        type = 1,
                        ssl = 1,
                        host = hostParts[0],
                        port = hostParts.Length > 1 ? hostParts[1] : "443",
                        ca = desktop.DesktopInfo.CaCert,
                        cert = desktop.DesktopInfo.ClientCert,
                        key = desktop.DesktopInfo.ClientKey,
                        servername = desktop.DesktopInfo.Host + ":" + desktop.DesktopInfo.Port,
                        oqs = 0
                    };

                    var msgBytes = JsonSerializer.SerializeToUtf8Bytes(connectMessage, AppJsonSerializerContext.Default.ConnecMessage);
                    await client.SendAsync(msgBytes, WebSocketMessageType.Text, true, sessionCts.Token);

                    await Task.Delay(500, sessionCts.Token);
                    await client.SendAsync(initialPayload, WebSocketMessageType.Binary, true, sessionCts.Token);

                    Utility.WriteLine(ConsoleColor.Green, $"[{label}][{desktop.DesktopCode}] 连接已就绪，保持 {keepAliveSeconds} 秒...");
                    UpdateDesktopStatus(label, account.User, desktop.DesktopCode, "保活中", globalToken);

                    try
                    {
                        await ReceiveLoop(api, client, account, desktop, sessionCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        Utility.WriteLine(ConsoleColor.Yellow,   $"[{label}][{desktop.DesktopCode}] 周期时间到，准备重连...");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Utility.WriteLine(ConsoleColor.Red,    $"[{label}][{desktop.DesktopCode}] 异常: {ex.Message}");
                    UpdateDesktopStatus(label, account.User, desktop.DesktopCode, "连接异常: " + ex.Message, globalToken);
                    await Task.Delay(5000, globalToken);
                }
                finally
                {
                    if (client.State == WebSocketState.Open)
                    {
                        await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Timeout Reset", CancellationToken.None);
                    }
                }
            }
        }

        private static async Task ReceiveLoop(CtYunApi api, ClientWebSocket ws, AccountConfig account, Desktop desktop, CancellationToken ct)
        {
            var buffer = new byte[8192];
            var encryptor = new Encryption();
            var label = account.Name ?? account.User;

            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close) break;

                if (result.Count == 0) continue;

                var data = buffer.AsSpan(0, result.Count).ToArray();
                var hex = BitConverter.ToString(data).Replace("-", "");
                if (hex.StartsWith("52454451", StringComparison.OrdinalIgnoreCase))
                {
                    Utility.WriteLine(ConsoleColor.Green, $"[{label}][{desktop.DesktopCode}] -> 收到保活校验");
                    var response = encryptor.Execute(data);
                    await ws.SendAsync(response, WebSocketMessageType.Binary, true, ct);
                    Utility.WriteLine(ConsoleColor.DarkGreen, $"[{label}][{desktop.DesktopCode}] -> 发送保活响应成功");
                    continue;
                }

                try
                {
                    var infos = SendInfo.FromBuffer(data);
                    foreach (var info in infos)
                    {
                        if (info.Type == 103)
                        {
                            var payload = Encoding.UTF8.GetBytes("{\"type\":1,\"userName\":\"" + api.LoginInfo.UserName + "\",\"userInfo\":\"\",\"userId\":" + api.LoginInfo.UserId + "}");
                            var byUserName = new SendInfo { Type = 118, Data = payload }.ToBuffer(true);
                            await ws.SendAsync(byUserName, WebSocketMessageType.Binary, true, ct);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Utility.WriteLine(ConsoleColor.DarkYellow, $"[{label}][{desktop.DesktopCode}] 消息解析失败: {ex.Message}");
                }
            }
        }

        // --- Helper Config management ---

        private static AppConfig LoadRuntimeConfig()
        {
            var dataDir = GlobalState.DataDir ?? GetDataDir();
            var config = LoadAccountsFromFile(dataDir) ?? LoadAccountsFromEnvironment();
            if (config == null)
            {
                config = new AppConfig();
            }

            foreach (var account in config.Accounts)
            {
                account.Name = FirstNotEmpty(account.Name, account.User);
                account.DeviceCode = ResolveDeviceCode(account, dataDir);
            }

            return config;
        }

        private static AppConfig LoadAccountsFromEnvironment()
        {
            var user = Environment.GetEnvironmentVariable("APP_USER");
            var password = Environment.GetEnvironmentVariable("APP_PASSWORD");
            if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(password))
            {
                return null;
            }

            return new AppConfig
            {
                Accounts =
                [
                    new AccountConfig
                    {
                        Name = Environment.GetEnvironmentVariable("APP_NAME"),
                        User = user,
                        Password = password,
                        DeviceCode = Environment.GetEnvironmentVariable("DEVICECODE")
                    }
                ]
            };
        }

        private static AppConfig LoadAccountsFromFile(string dataDir)
        {
            var configPath = Environment.GetEnvironmentVariable("CTYUN_CONFIG");
            if (string.IsNullOrWhiteSpace(configPath))
            {
                configPath = Path.Combine(dataDir, "accounts.json");
            }
            GlobalState.ConfigPath = configPath;

            if (!File.Exists(configPath))
            {
                return null;
            }

            try
            {
                var json = File.ReadAllText(configPath);
                var config = JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.AppConfig);
                Utility.WriteLine(ConsoleColor.Green, $"[系统] 已读取配置文件：{configPath}");
                return config;
            }
            catch (Exception ex)
            {
                Utility.WriteLine(ConsoleColor.Red, $"[系统] 读取配置文件失败：{ex.Message}");
                return null;
            }
        }

        private static void SaveConfigToFile()
        {
            try
            {
                var configPath = GlobalState.ConfigPath;
                if (string.IsNullOrWhiteSpace(configPath))
                {
                    configPath = Path.Combine(GlobalState.DataDir, "accounts.json");
                    GlobalState.ConfigPath = configPath;
                }
                var appConfig = new AppConfig
                {
                    Accounts = GlobalState.Config.Accounts,
                    KeepAliveSeconds = GlobalState.Config.KeepAliveSeconds
                };
                var json = JsonSerializer.Serialize(appConfig, AppJsonSerializerContext.Default.AppConfig);
                File.WriteAllText(configPath, json);
                Utility.WriteLine(ConsoleColor.Green, $"[系统] 配置文件已更新: {configPath}");
            }
            catch (Exception ex)
            {
                Utility.WriteLine(ConsoleColor.Red, $"[系统] 保存配置文件失败: {ex.Message}");
            }
        }

        private static string ResolveDeviceCode(AccountConfig account, string dataDir)
        {
            if (!string.IsNullOrWhiteSpace(account.DeviceCode))
            {
                return account.DeviceCode.Trim();
            }

            var devicesDir = Path.Combine(dataDir, "devices");
            Directory.CreateDirectory(devicesDir);
            var deviceCodePath = Path.Combine(devicesDir, SafeName(account.Name ?? account.User) + ".txt");
            if (!File.Exists(deviceCodePath))
            {
                File.WriteAllText(deviceCodePath, "web_" + GenerateRandomString(32));
            }

            return File.ReadAllText(deviceCodePath).Trim();
        }

        private static string GetDataDir()
        {
            var dataDir = Environment.GetEnvironmentVariable("CTYUN_DATA_DIR");
            if (!string.IsNullOrWhiteSpace(dataDir))
            {
                return dataDir;
            }

            return IsRunningInContainer() ? "/app/data" : AppContext.BaseDirectory;
        }

        private static string GenerateRandomString(int length)
        {
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
            return new string(Enumerable.Repeat(chars, length).Select(s => s[RandomNumberGenerator.GetInt32(s.Length)]).ToArray());
        }

        private static string AccountLabel(AccountConfig account) => account.Name ?? account.User;

        private static string SafeName(string value)
        {
            var source = string.IsNullOrWhiteSpace(value) ? "default" : value;
            var builder = new StringBuilder(source.Length);
            foreach (var ch in source)
            {
                builder.Append(char.IsLetterOrDigit(ch) ? ch : '_');
            }
            return builder.ToString();
        }

        private static string FirstNotEmpty(params string[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

        private static bool IsRunningInContainer() => File.Exists("/.dockerenv");
    }
}
