using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ParrotCore
{
    public class ServerHost
    {
        private WebApplication? _app;
        private readonly Action<string> _logAction;
        private FileSystemWatcher? _watcher;
        private long _totalRequests;
        private long _totalBytesTransferred;

        public DatabaseManager DbManager { get; }
        public int SelectedPort { get; private set; } = 5009;
        public bool IsRunning { get; private set; } = false;
        public long TotalRequests => Interlocked.Read(ref _totalRequests);
        public long TotalBytesTransferred => Interlocked.Read(ref _totalBytesTransferred);

        public ServerHost(Action<string> logAction)
        {
            _logAction = logAction;
            DbManager = new DatabaseManager(logAction);
        }

        public async Task<int> StartAsync()
        {
            var cfg = AppConfig.Current;
            string webRoot = cfg.GetResolvedWebsitePath();
            if (!Directory.Exists(webRoot))
                Directory.CreateDirectory(webRoot);

            if (!Directory.EnumerateFileSystemEntries(webRoot).Any())
            {
                _logAction("[Wizard] Website folder is empty. Creating starter template...");
                WebsiteWizard.ScaffoldStarterWebsite(cfg, DbManager, _logAction);
            }
            else
            {
                WebsiteWizard.InspectProject(cfg, _logAction);
            }

            if (cfg.EnableDatabase)
            {
                await DbManager.InitializeOrConnectAsync();
            }

            int targetPort = cfg.Port;
            if (int.TryParse(Environment.GetEnvironmentVariable("PORT"), out var envPort))
                targetPort = envPort;

            if (IsPortInUse(targetPort))
            {
                if (cfg.AutoFallbackPort)
                {
                    SelectedPort = FindAvailablePort(targetPort + 1);
                    _logAction($"[Network] Port {targetPort} is busy. Auto-switched to port {SelectedPort}.");
                }
                else
                {
                    SelectedPort = targetPort;
                    _logAction($"[Network] WARNING: Port {targetPort} appears to be in use!");
                }
            }
            else
            {
                SelectedPort = targetPort;
            }

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = Array.Empty<string>(),
                ContentRootPath = AppContext.BaseDirectory,
                WebRootPath = webRoot
            });

            builder.Logging.ClearProviders();

            builder.WebHost.ConfigureKestrel(serverOptions =>
            {
                serverOptions.Limits.MaxRequestBodySize = (long)cfg.MaxRequestBodySizeMB * 1024 * 1024;
                serverOptions.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(Math.Max(15, cfg.KeepAliveTimeoutSeconds));
            });

            if (cfg.EnableCors)
            {
                builder.Services.AddCors(options =>
                {
                    options.AddDefaultPolicy(policy =>
                    {
                        if (cfg.CorsAllowedOrigins == "*" || string.IsNullOrWhiteSpace(cfg.CorsAllowedOrigins))
                            policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
                        else
                        {
                            var origins = cfg.CorsAllowedOrigins.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                            policy.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod();
                        }
                    });
                });
            }

            if (cfg.EnableGzipCompression)
            {
                builder.Services.AddResponseCompression(options =>
                {
                    options.EnableForHttps = true;
                    options.Providers.Add<GzipCompressionProvider>();
                    options.Providers.Add<BrotliCompressionProvider>();
                });
            }

            if (cfg.EnableDirectoryBrowsing)
            {
                builder.Services.AddDirectoryBrowser();
            }

            _app = builder.Build();

            _app.Use(async (context, next) =>
            {
                var sw = Stopwatch.StartNew();
                Interlocked.Increment(ref _totalRequests);
                if (context.Request.ContentLength.HasValue && context.Request.ContentLength.Value > 0)
                {
                    Interlocked.Add(ref _totalBytesTransferred, context.Request.ContentLength.Value);
                }

                foreach (var kv in cfg.CustomHeaders)
                {
                    if (!context.Response.Headers.ContainsKey(kv.Key))
                        context.Response.Headers[kv.Key] = kv.Value;
                }

                var originalBody = context.Response.Body;
                await using var countingStream = new CountingStream(originalBody, bytes => Interlocked.Add(ref _totalBytesTransferred, bytes));
                context.Response.Body = countingStream;

                try
                {
                    await next();
                }
                finally
                {
                    context.Response.Body = originalBody;
                }

                sw.Stop();

                if (cfg.EnableRequestLogging)
                {
                    string ip = context.Connection.RemoteIpAddress?.ToString() ?? "local";
                    string method = context.Request.Method;
                    string path = context.Request.Path + context.Request.QueryString;
                    int status = context.Response.StatusCode;
                    _logAction($"[{DateTime.Now:HH:mm:ss}] {method} {path} -> {status} ({sw.ElapsedMilliseconds}ms) [{ip}]");
                }
            });

            if (cfg.EnableCors) _app.UseCors();
            if (cfg.EnableGzipCompression) _app.UseResponseCompression();
            if (cfg.EnableWebSockets) _app.UseWebSockets();

            _app.MapGet("/Parrotcore.ico", async (HttpContext ctx) => await ServeFaviconAsync(ctx, webRoot));
            _app.MapGet("/favicon.ico", async (HttpContext ctx) => await ServeFaviconAsync(ctx, webRoot));

            if (cfg.EnablePhpInterpreter)
            {
                _app.Use(async (context, next) =>
                {
                    string reqPath = context.Request.Path.Value ?? "/";
                    string? phpFile = ResolvePhpTargetFile(webRoot, reqPath, cfg);
                    if (phpFile != null && File.Exists(phpFile))
                    {
                        await ExecutePhpScriptAsync(context, webRoot, phpFile, cfg);
                        return;
                    }
                    await next();
                });
            }

            var api = _app.MapGroup("/api");
            api.MapGet("/status", () => Results.Ok(new
            {
                engine = "ParrotCore",
                version = "2.3 Hyacinth",
                author = "Hnato",
                status = "Running",
                bindMode = cfg.BindMode,
                port = SelectedPort,
                websiteRoot = AppConfig.ToPortablePath(webRoot, "Web Site"),
                databaseEnabled = cfg.EnableDatabase,
                databaseProvider = cfg.DatabaseProvider,
                databaseConnected = DbManager.IsConnected,
                phpEnabled = cfg.EnablePhpInterpreter,
                timestamp = DateTime.UtcNow
            }));

            if (cfg.EnableDatabase && cfg.ExposeRestDbApi)
            {
                var dbApi = api.MapGroup("/db");
                dbApi.MapGet("/status", () => Results.Ok(new
                {
                    enabled = cfg.EnableDatabase,
                    connected = DbManager.IsConnected,
                    mode = cfg.DatabaseMode,
                    provider = cfg.DatabaseProvider,
                    host = cfg.DbHost,
                    database = cfg.DbName
                }));

                dbApi.MapPost("/query", async (HttpRequest req) =>
                {
                    try
                    {
                        using var doc = await JsonDocument.ParseAsync(req.Body);
                        string sql = doc.RootElement.GetProperty("sql").GetString() ?? "";
                        var rows = await DbManager.ExecuteSqliteQueryAsync(sql);
                        return Results.Ok(new { success = true, rows });
                    }
                    catch (Exception ex)
                    {
                        return Results.BadRequest(new { success = false, error = ex.Message });
                    }
                });

                dbApi.MapGet("/kv/{collection}/{key}", async (string collection, string key) =>
                {
                    try
                    {
                        var rows = await DbManager.ExecuteSqliteQueryAsync(
                            "SELECT json_data, updated_at FROM key_value_store WHERE collection = @c AND item_key = @k",
                            new Dictionary<string, object?> { ["@c"] = collection, ["@k"] = key });
                        if (rows.Count == 0) return Results.NotFound(new { error = "Key not found" });
                        return Results.Ok(rows[0]);
                    }
                    catch (Exception ex)
                    {
                        return Results.BadRequest(new { error = ex.Message });
                    }
                });

                dbApi.MapPost("/kv/{collection}/{key}", async (string collection, string key, HttpRequest req) =>
                {
                    try
                    {
                        using var reader = new StreamReader(req.Body);
                        string rawJson = await reader.ReadToEndAsync();
                        await DbManager.ExecuteSqliteQueryAsync(
                            "INSERT OR REPLACE INTO key_value_store (collection, item_key, json_data, updated_at) VALUES (@c, @k, @d, @t)",
                            new Dictionary<string, object?>
                            {
                                ["@c"] = collection,
                                ["@k"] = key,
                                ["@d"] = rawJson,
                                ["@t"] = DateTime.UtcNow.ToString("O")
                            });
                        return Results.Ok(new { success = true, collection, key });
                    }
                    catch (Exception ex)
                    {
                        return Results.BadRequest(new { error = ex.Message });
                    }
                });
            }

            var fileProvider = new PhysicalFileProvider(webRoot);
            var defaultFilesOpt = new DefaultFilesOptions { FileProvider = fileProvider };
            defaultFilesOpt.DefaultFileNames.Clear();
            foreach (var f in cfg.DefaultFiles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                defaultFilesOpt.DefaultFileNames.Add(f);
            }
            _app.UseDefaultFiles(defaultFilesOpt);

            _app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = fileProvider,
                ServeUnknownFileTypes = cfg.ServeUnknownMimeTypes,
                DefaultContentType = "application/octet-stream",
                OnPrepareResponse = ctx =>
                {
                    if (cfg.EnableStaticCache)
                        ctx.Context.Response.Headers["Cache-Control"] = $"public,max-age={cfg.CacheMaxAgeSeconds}";
                    else
                        ctx.Context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
                }
            });

            if (cfg.EnableDirectoryBrowsing)
            {
                _app.UseDirectoryBrowser(new DirectoryBrowserOptions { FileProvider = fileProvider });
            }

            _app.MapFallback(async context =>
            {
                if (context.Request.Path.StartsWithSegments("/api"))
                {
                    context.Response.StatusCode = 404;
                    await context.Response.WriteAsJsonAsync(new { error = "API endpoint not found" });
                    return;
                }

                if (cfg.EnableSpaFallback)
                {
                    var spaFile = fileProvider.GetFileInfo(cfg.SpaFallbackFile);
                    if (spaFile.Exists)
                    {
                        context.Response.ContentType = "text/html; charset=utf-8";
                        await using var stream = spaFile.CreateReadStream();
                        await stream.CopyToAsync(context.Response.Body);
                        return;
                    }
                }

                context.Response.StatusCode = 404;
                var errFile = fileProvider.GetFileInfo(cfg.Custom404Page);
                if (errFile.Exists)
                {
                    context.Response.ContentType = "text/html; charset=utf-8";
                    await using var stream = errFile.CreateReadStream();
                    await stream.CopyToAsync(context.Response.Body);
                }
                else
                {
                    await context.Response.WriteAsync("404 - File not found in website directory");
                }
            });

            StartLiveFileWatcher(webRoot, cfg.EnableLiveReloadWatcher);

            string bindUrl = cfg.BindMode switch
            {
                "localhost" => $"http://127.0.0.1:{SelectedPort}",
                "all" => $"http://0.0.0.0:{SelectedPort}",
                _ => $"http://0.0.0.0:{SelectedPort}"
            };

            string portableWeb = AppConfig.ToPortablePath(webRoot, "Web Site");
            _logAction($"[Engine] Broadcasting website from: {portableWeb}");
            if (cfg.BindMode == "localhost")
            {
                _logAction($"[Network] Listening on Localhost: http://localhost:{SelectedPort}");
            }
            else
            {
                _logAction($"[Network] Broadcast active on: http://0.0.0.0:{SelectedPort}");
                _logAction($"[Network] Localhost access:    http://localhost:{SelectedPort}");
                string? lanIp = GetLocalLanIp();
                if (!string.IsNullOrEmpty(lanIp))
                    _logAction($"[Network] LAN Network access:  http://{lanIp}:{SelectedPort}");
            }

            _ = _app.RunAsync(bindUrl);
            IsRunning = true;

            return SelectedPort;
        }

        private static async Task ServeFaviconAsync(HttpContext ctx, string webRoot)
        {
            string localIco = Path.Combine(webRoot, "Parrotcore.ico");
            if (!File.Exists(localIco))
                localIco = Path.Combine(webRoot, "favicon.ico");

            ctx.Response.ContentType = "image/x-icon";
            if (File.Exists(localIco))
            {
                await ctx.Response.SendFileAsync(localIco);
                return;
            }

            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            await using var stream = asm.GetManifestResourceStream("ParrotCore.Parrotcore.ico");
            if (stream != null)
            {
                await stream.CopyToAsync(ctx.Response.Body);
            }
            else
            {
                ctx.Response.StatusCode = 404;
            }
        }

        private static string? ResolvePhpTargetFile(string webRoot, string requestPath, AppConfig cfg)
        {
            string relative = requestPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
            string fullCandidate = Path.GetFullPath(Path.Combine(webRoot, relative));
            string fullRoot = Path.GetFullPath(webRoot);

            if (!fullCandidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                return null;

            if (fullCandidate.EndsWith(".php", StringComparison.OrdinalIgnoreCase) && File.Exists(fullCandidate))
                return fullCandidate;

            if (Directory.Exists(fullCandidate))
            {
                var defaults = cfg.DefaultFiles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                if (!defaults.Contains("index.php", StringComparer.OrdinalIgnoreCase))
                    defaults.Add("index.php");

                foreach (var def in defaults)
                {
                    string candidateFile = Path.Combine(fullCandidate, def);
                    if (File.Exists(candidateFile))
                    {
                        if (def.EndsWith(".php", StringComparison.OrdinalIgnoreCase))
                            return candidateFile;
                        return null;
                    }
                }
            }

            return null;
        }

        private async Task ExecutePhpScriptAsync(HttpContext context, string webRoot, string scriptFullPath, AppConfig cfg)
        {
            string? phpExe = cfg.ResolvePhpBinary();
            if (string.IsNullOrEmpty(phpExe) || !File.Exists(phpExe))
            {
                _logAction("[PHP] PHP request received, but PHP binary (php-cgi.exe / php.exe) was not found.");
                context.Response.StatusCode = 500;
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.WriteAsync(
                    "<!DOCTYPE html><html><head><meta charset='utf-8'><title>PHP Interpreter Not Found</title>" +
                    "<style>body{background:#111714;color:#ebf4ee;font-family:'Segoe UI',sans-serif;padding:40px}div{max-width:680px;margin:0 auto;background:#18221d;border:1px solid #2a3d33;padding:28px;border-radius:12px}h2{color:#e74c3c;margin-top:0}code{color:#2ecc71;background:#101714;padding:2px 6px;border-radius:4px}</style></head>" +
                    "<body><div><h2>PHP Interpreter Not Found</h2><p>To execute <code>.php</code> files, install PHP (or XAMPP/Laragon/WAMP) or select your <code>php-cgi.exe</code> / <code>php.exe</code> binary in <b>Settings &rarr; Website</b>.</p></div></body></html>");
                return;
            }

            bool isCgi = Path.GetFileName(phpExe).Contains("cgi", StringComparison.OrdinalIgnoreCase);
            string queryString = context.Request.QueryString.HasValue ? context.Request.QueryString.Value!.TrimStart('?') : "";

            var psi = new ProcessStartInfo
            {
                FileName = phpExe,
                Arguments = isCgi ? $"-q \"{scriptFullPath}\"" : $"\"{scriptFullPath}\"",
                WorkingDirectory = Path.GetDirectoryName(scriptFullPath) ?? webRoot,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            psi.Environment["REDIRECT_STATUS"] = "200";
            psi.Environment["GATEWAY_INTERFACE"] = "CGI/1.1";
            psi.Environment["SERVER_PROTOCOL"] = "HTTP/1.1";
            psi.Environment["SERVER_SOFTWARE"] = "ParrotCore/2.3";
            psi.Environment["REQUEST_METHOD"] = context.Request.Method;
            psi.Environment["SCRIPT_FILENAME"] = scriptFullPath;
            psi.Environment["SCRIPT_NAME"] = context.Request.Path.Value ?? "/index.php";
            psi.Environment["REQUEST_URI"] = (context.Request.Path.Value ?? "/") + context.Request.QueryString.Value;
            psi.Environment["DOCUMENT_ROOT"] = webRoot;
            psi.Environment["QUERY_STRING"] = queryString;
            psi.Environment["CONTENT_TYPE"] = context.Request.ContentType ?? "";
            psi.Environment["CONTENT_LENGTH"] = (context.Request.ContentLength ?? 0).ToString();
            psi.Environment["REMOTE_ADDR"] = context.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1";
            psi.Environment["SERVER_NAME"] = context.Request.Host.Host ?? "localhost";
            psi.Environment["SERVER_PORT"] = SelectedPort.ToString();
            psi.Environment["HTTP_HOST"] = context.Request.Host.Value ?? $"localhost:{SelectedPort}";
            psi.Environment["HTTP_USER_AGENT"] = context.Request.Headers.UserAgent.ToString();
            psi.Environment["HTTP_ACCEPT"] = context.Request.Headers.Accept.ToString();
            psi.Environment["HTTP_COOKIE"] = context.Request.Headers.Cookie.ToString();

            psi.Environment["DB_PROVIDER"] = cfg.DatabaseProvider;
            psi.Environment["DB_HOST"] = cfg.DbHost;
            psi.Environment["DB_PORT"] = cfg.DbPort.ToString();
            psi.Environment["DB_NAME"] = cfg.DbName;
            psi.Environment["DB_USER"] = cfg.DbUser;
            psi.Environment["DB_PASS"] = cfg.DbPassword;
            psi.Environment["DB_SQLITE_PATH"] = cfg.GetResolvedSqlitePath();

            try
            {
                using var proc = Process.Start(psi);
                if (proc == null)
                {
                    context.Response.StatusCode = 500;
                    await context.Response.WriteAsync("Failed to start PHP interpreter process.");
                    return;
                }

                if (context.Request.ContentLength > 0 || HttpMethods.IsPost(context.Request.Method) || HttpMethods.IsPut(context.Request.Method))
                {
                    await context.Request.Body.CopyToAsync(proc.StandardInput.BaseStream);
                }
                proc.StandardInput.Close();

                using var ms = new MemoryStream();
                await proc.StandardOutput.BaseStream.CopyToAsync(ms);
                string stderr = await proc.StandardError.ReadToEndAsync();
                await proc.WaitForExitAsync();

                if (!string.IsNullOrWhiteSpace(stderr))
                {
                    _logAction($"[PHP Warning/Error] {stderr.Trim()}");
                }

                byte[] outputBytes = ms.ToArray();
                int bodyOffset = 0;
                string contentType = "text/html; charset=utf-8";

                if (isCgi && outputBytes.Length > 0)
                {
                    int sepIndex = FindHeaderSeparator(outputBytes, out int sepLength);
                    if (sepIndex >= 0)
                    {
                        string headerBlock = Encoding.UTF8.GetString(outputBytes, 0, sepIndex);
                        bodyOffset = sepIndex + sepLength;
                        foreach (var rawLine in headerBlock.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            int colon = rawLine.IndexOf(':');
                            if (colon <= 0) continue;
                            string hName = rawLine[..colon].Trim();
                            string hVal = rawLine[(colon + 1)..].Trim();

                            if (hName.Equals("Status", StringComparison.OrdinalIgnoreCase))
                            {
                                var parts = hVal.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                                if (parts.Length > 0 && int.TryParse(parts[0], out int stCode))
                                    context.Response.StatusCode = stCode;
                            }
                            else if (hName.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                            {
                                contentType = hVal;
                            }
                            else if (hName.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase))
                            {
                                context.Response.Headers.Append("Set-Cookie", hVal);
                            }
                            else
                            {
                                context.Response.Headers[hName] = hVal;
                            }
                        }
                    }
                }

                context.Response.ContentType = contentType;
                if (bodyOffset < outputBytes.Length)
                {
                    await context.Response.Body.WriteAsync(outputBytes.AsMemory(bodyOffset, outputBytes.Length - bodyOffset));
                }
            }
            catch (Exception ex)
            {
                _logAction($"[PHP Error] {ex.Message}");
                context.Response.StatusCode = 500;
                await context.Response.WriteAsync($"PHP Execution Error: {ex.Message}");
            }
        }

        private static int FindHeaderSeparator(byte[] buffer, out int sepLength)
        {
            for (int i = 0; i < buffer.Length - 3; i++)
            {
                if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
                {
                    sepLength = 4;
                    return i;
                }
                if (buffer[i] == '\n' && buffer[i + 1] == '\n')
                {
                    sepLength = 2;
                    return i;
                }
            }
            sepLength = 0;
            return -1;
        }

        public async Task StopAsync()
        {
            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
                _watcher = null;
            }

            if (_app != null)
            {
                await _app.StopAsync();
                await _app.DisposeAsync();
                _app = null;
            }
            IsRunning = false;
        }

        public async Task<int> RestartAsync()
        {
            _logAction("[Engine] Restarting ParrotCore 2.3 Hyacinth...");
            await StopAsync();
            await Task.Delay(300);
            return await StartAsync();
        }

        private void StartLiveFileWatcher(string webRoot, bool enabled)
        {
            if (_watcher != null)
            {
                _watcher.Dispose();
                _watcher = null;
            }
            if (!enabled || !Directory.Exists(webRoot)) return;

            try
            {
                _watcher = new FileSystemWatcher(webRoot)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime
                };
                _watcher.Changed += (s, e) => _logAction($"[LiveWatch] File modified: {Path.GetRelativePath(webRoot, e.FullPath)}");
                _watcher.Created += (s, e) => _logAction($"[LiveWatch] File added: {Path.GetRelativePath(webRoot, e.FullPath)}");
                _watcher.Deleted += (s, e) => _logAction($"[LiveWatch] File removed: {Path.GetRelativePath(webRoot, e.FullPath)}");
                _watcher.EnableRaisingEvents = true;
            }
            catch { }
        }

        public string GetPreferredBrowserUrl()
        {
            var cfg = AppConfig.Current;
            int port = SelectedPort > 0 ? SelectedPort : cfg.Port;
            if (cfg.BindMode == "all")
            {
                string? lan = GetLocalLanIp();
                return !string.IsNullOrEmpty(lan) ? $"http://{lan}:{port}" : $"http://127.0.0.1:{port}";
            }
            return $"http://localhost:{port}";
        }

        public static string? GetLocalLanIp()
        {
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up ||
                        ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                        continue;

                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily == AddressFamily.InterNetwork &&
                            !IPAddress.IsLoopback(ua.Address))
                        {
                            return ua.Address.ToString();
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        public static bool IsPortInUse(int port)
        {
            try
            {
                var props = IPGlobalProperties.GetIPGlobalProperties();
                var listeners = props.GetActiveTcpListeners();
                return listeners.Any(ep => ep.Port == port);
            }
            catch
            {
                return false;
            }
        }

        public static string DiagnosePortAndFirewall(int port)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"=== Diagnostic Report for TCP Port {port} ===");

            bool inUse = IsPortInUse(port);
            if (inUse)
            {
                sb.AppendLine($"[!] Port {port} is currently ACTIVE / IN USE.");
                try
                {
                    var psi = new ProcessStartInfo("netstat", "-ano -p tcp")
                    {
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using var proc = Process.Start(psi);
                    if (proc != null)
                    {
                        string output = proc.StandardOutput.ReadToEnd();
                        foreach (var line in output.Split('\n'))
                        {
                            if (line.Contains($":{port} ") && line.Contains("LISTENING"))
                            {
                                var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                                if (parts.Length >= 5 && int.TryParse(parts[^1], out int pid))
                                {
                                    try
                                    {
                                        var p = Process.GetProcessById(pid);
                                        sb.AppendLine($"    -> Process holding port {port}: {p.ProcessName} (PID: {pid})");
                                    }
                                    catch
                                    {
                                        sb.AppendLine($"    -> Process holding port {port}: PID {pid}");
                                    }
                                }
                            }
                        }
                    }
                }
                catch { }
            }
            else
            {
                sb.AppendLine($"[OK] Port {port} is FREE and available for binding.");
            }

            string? lanIp = GetLocalLanIp();
            sb.AppendLine($"[OK] Local Loopback (127.0.0.1): Available");
            sb.AppendLine($"[OK] LAN Adapter (0.0.0.0): {(lanIp ?? "No external LAN IP detected")}");
            sb.AppendLine($"[Info] Windows Firewall: Ensure inbound TCP port {port} is allowed when broadcasting on 0.0.0.0.");
            return sb.ToString().TrimEnd();
        }

        private static int FindAvailablePort(int startPort)
        {
            for (int port = startPort; port < startPort + 200; port++)
            {
                if (!IsPortInUse(port)) return port;
            }
            return startPort;
        }
    }

    internal sealed class CountingStream : Stream
    {
        private readonly Stream _inner;
        private readonly Action<long> _onBytesWritten;

        public CountingStream(Stream inner, Action<long> onBytesWritten)
        {
            _inner = inner;
            _onBytesWritten = onBytesWritten;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;
        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count)
        {
            _inner.Write(buffer, offset, count);
            if (count > 0) _onBytesWritten(count);
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await _inner.WriteAsync(buffer, offset, count, cancellationToken);
            if (count > 0) _onBytesWritten(count);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await _inner.WriteAsync(buffer, cancellationToken);
            if (buffer.Length > 0) _onBytesWritten(buffer.Length);
        }
    }
}
