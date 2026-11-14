using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace ParrotCore
{
    public class AppConfig
    {
        public const int CurrentSchemaVersion = 2;
        public const string CurrentEngineVersion = "2.3.0";
        public const string ReleasesPageUrl = "https://github.com/Hnato/Parrotcore/releases";
        public const string LatestReleaseApiUrl = "https://api.github.com/repos/Hnato/Parrotcore/releases/latest";

        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        public string EngineVersion { get; set; } = CurrentEngineVersion;
        public string ReleaseChannelUrl { get; set; } = ReleasesPageUrl;

        public bool IsAdvancedMode { get; set; } = false;
        public bool LaunchAtStartup { get; set; } = false;
        public bool MinimizeToTrayOnClose { get; set; } = true;
        public bool CheckUpdatesOnStartup { get; set; } = true;
        public bool AutoStartServerOnLaunch { get; set; } = true;
        public string Language { get; set; } = "en";
        public string Theme { get; set; } = "dark";

        public int Port { get; set; } = 5009;
        public bool AutoFallbackPort { get; set; } = true;
        public string BindMode { get; set; } = "both";
        public bool EnableHttps { get; set; } = false;
        public int HttpsPort { get; set; } = 5010;
        public bool EnableCors { get; set; } = true;
        public string CorsAllowedOrigins { get; set; } = "*";
        public bool EnableWebSockets { get; set; } = true;
        public bool EnableRequestLogging { get; set; } = true;
        public int MaxRequestBodySizeMB { get; set; } = 100;
        public int KeepAliveTimeoutSeconds { get; set; } = 120;

        public bool EnableDatabase { get; set; } = true;
        public bool IsConfigured { get; set; } = false;
        public string DatabaseMode { get; set; } = "local_sqlite";
        public string DatabaseProvider { get; set; } = "SQLite";
        public string DbHost { get; set; } = "127.0.0.1";
        public int DbPort { get; set; } = 3306;
        public string DbName { get; set; } = "parrotcore_db";
        public string DbUser { get; set; } = "root";
        public string DbPassword { get; set; } = "";
        public string SqliteFilePath { get; set; } = "Engine/Data/parrotcore.db";
        public string MariaDbBinPath { get; set; } = "";
        public string CustomConnectionString { get; set; } = "";
        public bool UseConnectionPooling { get; set; } = true;
        public int ConnectionTimeoutSeconds { get; set; } = 15;
        public bool AutoGenerateSiteDbConfig { get; set; } = false;
        public bool ExposeRestDbApi { get; set; } = true;

        public string WebsiteRootPath { get; set; } = "Web Site";
        public string DefaultFiles { get; set; } = "index.php, index.html, index.htm, default.html";
        public bool EnablePhpInterpreter { get; set; } = true;
        public string PhpExecutablePath { get; set; } = "";
        public bool EnableDirectoryBrowsing { get; set; } = false;
        public bool EnableSpaFallback { get; set; } = true;
        public string SpaFallbackFile { get; set; } = "index.html";
        public bool EnableGzipCompression { get; set; } = true;
        public bool EnableStaticCache { get; set; } = false;
        public int CacheMaxAgeSeconds { get; set; } = 3600;
        public bool EnableLiveReloadWatcher { get; set; } = true;
        public bool ServeUnknownMimeTypes { get; set; } = true;
        public string Custom404Page { get; set; } = "404.html";
        public Dictionary<string, string> CustomHeaders { get; set; } = new()
        {
            { "X-Frame-Options", "SAMEORIGIN" }
        };

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? ExtensionData { get; set; }

        [JsonIgnore]
        public static AppConfig Current { get; private set; } = new AppConfig();

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        public static string ResolveProjectRoot()
        {
            string baseDir = AppContext.BaseDirectory;
            DirectoryInfo? dir = new DirectoryInfo(baseDir);
            while (dir != null)
            {
                string candidateWeb = Path.Combine(dir.FullName, "Web Site");
                string candidateEngine = Path.Combine(dir.FullName, "Engine");
                if (Directory.Exists(candidateWeb) || Directory.Exists(candidateEngine))
                {
                    return dir.FullName;
                }
                dir = dir.Parent;
            }
            return baseDir;
        }

        public static string ToPortablePath(string rawPath, string defaultRelative)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
                return defaultRelative;

            string trimmed = rawPath.Trim();
            if (!Path.IsPathFullyQualified(trimmed))
            {
                return trimmed.TrimStart('\\', '/').Replace('\\', '/');
            }

            try
            {
                string root = Path.GetFullPath(ResolveProjectRoot()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string full = Path.GetFullPath(trimmed);
                if (full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    return Path.GetRelativePath(root, full).Replace('\\', '/');
                }
            }
            catch { }

            return trimmed;
        }

        public static string ResolveEngineDataDirectory()
        {
            string root = ResolveProjectRoot();
            string engineDir = Path.Combine(root, "Engine");
            if (!Directory.Exists(engineDir))
            {
                engineDir = root;
            }
            string dataDir = Path.Combine(engineDir, "Data");
            if (!Directory.Exists(dataDir))
            {
                try { Directory.CreateDirectory(dataDir); } catch { }
            }
            return dataDir;
        }

        public static string ResolvePersistentAppDataDirectory()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string dir = Path.Combine(localAppData, "JGS-Team", "ParrotCore");
            if (!Directory.Exists(dir))
            {
                try { Directory.CreateDirectory(dir); } catch { }
            }
            return dir;
        }

        public static string GetConfigFilePath()
        {
            return Path.Combine(ResolveEngineDataDirectory(), "parrotcore.config.json");
        }

        public static string GetPersistentBackupConfigFilePath()
        {
            return Path.Combine(ResolvePersistentAppDataDirectory(), "parrotcore.config.json");
        }

        public string GetResolvedWebsitePath()
        {
            string cleaned = (WebsiteRootPath ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(cleaned) && Path.IsPathFullyQualified(cleaned))
            {
                if (!Directory.Exists(cleaned))
                {
                    try { Directory.CreateDirectory(cleaned); } catch { }
                }
                return cleaned;
            }

            string root = ResolveProjectRoot();
            string relPath = string.IsNullOrWhiteSpace(cleaned) ? "Web Site" : cleaned.TrimStart('\\', '/');
            string full = Path.GetFullPath(Path.Combine(root, relPath));
            if (!Directory.Exists(full))
            {
                try { Directory.CreateDirectory(full); } catch { }
            }
            return full;
        }

        public string GetResolvedSqlitePath()
        {
            string cleaned = (SqliteFilePath ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(cleaned) && Path.IsPathFullyQualified(cleaned))
            {
                string? pDir = Path.GetDirectoryName(cleaned);
                if (!string.IsNullOrEmpty(pDir) && !Directory.Exists(pDir))
                {
                    try { Directory.CreateDirectory(pDir); } catch { }
                }
                return cleaned;
            }

            string root = ResolveProjectRoot();
            string normalized = (string.IsNullOrWhiteSpace(cleaned) ? "Engine/Data/parrotcore.db" : cleaned.TrimStart('\\', '/'))
                .Replace('/', Path.DirectorySeparatorChar);
            if (!normalized.StartsWith("Engine", StringComparison.OrdinalIgnoreCase))
            {
                normalized = Path.Combine("Engine", "Data", Path.GetFileName(normalized));
            }
            string full = Path.GetFullPath(Path.Combine(root, normalized));
            string? dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                try { Directory.CreateDirectory(dir); } catch { }
            }
            return full;
        }

        public string ResolveMariaDbBinary()
        {
            if (!string.IsNullOrWhiteSpace(MariaDbBinPath) && File.Exists(MariaDbBinPath))
                return MariaDbBinPath;

            try
            {
                foreach (var drive in DriveInfo.GetDrives())
                {
                    if (!drive.IsReady || drive.DriveType != DriveType.Fixed) continue;
                    string root = drive.RootDirectory.FullName;
                    string[] relCandidates =
                    {
                        @"xampp\mysql\bin\mysqld.exe",
                        @"laragon\bin\mysql\mysql-8.0.30-winx64\bin\mysqld.exe",
                        @"wamp64\bin\mysql\mysql8.0.31\bin\mysqld.exe",
                        @"Program Files\MariaDB\bin\mysqld.exe",
                        @"Program Files\MySQL\MySQL Server 8.0\bin\mysqld.exe"
                    };
                    foreach (var rel in relCandidates)
                    {
                        string full = Path.Combine(root, rel);
                        if (File.Exists(full)) return full;
                    }
                }
            }
            catch { }

            string? pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(pathEnv))
            {
                foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    try
                    {
                        string bin = Path.Combine(dir, "mysqld.exe");
                        if (File.Exists(bin)) return bin;
                    }
                    catch { }
                }
            }

            return "";
        }

        public string ResolvePhpBinary()
        {
            if (!string.IsNullOrWhiteSpace(PhpExecutablePath) && File.Exists(PhpExecutablePath))
                return PhpExecutablePath;

            try
            {
                foreach (var drive in DriveInfo.GetDrives())
                {
                    if (!drive.IsReady || drive.DriveType != DriveType.Fixed) continue;
                    string root = drive.RootDirectory.FullName;
                    string[] relCandidates =
                    {
                        @"xampp\php\php-cgi.exe",
                        @"xampp\php\php.exe",
                        @"php\php-cgi.exe",
                        @"php\php.exe",
                        @"laragon\bin\php\php-cgi.exe",
                        @"wamp64\bin\php\php8.2.0\php-cgi.exe"
                    };
                    foreach (var rel in relCandidates)
                    {
                        string full = Path.Combine(root, rel);
                        if (File.Exists(full)) return full;
                    }
                }
            }
            catch { }

            string? pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(pathEnv))
            {
                foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    try
                    {
                        string cgi = Path.Combine(dir, "php-cgi.exe");
                        if (File.Exists(cgi)) return cgi;
                        string cli = Path.Combine(dir, "php.exe");
                        if (File.Exists(cli)) return cli;
                    }
                    catch { }
                }
            }

            return "";
        }

        public static void Load()
        {
            string primaryPath = GetConfigFilePath();
            string backupPath = GetPersistentBackupConfigFilePath();

            if (TryLoadFromFile(primaryPath, out var loadedPrimary) && loadedPrimary != null)
            {
                NormalizeAndMigrate(loadedPrimary);
                Current = loadedPrimary;
                SaveMirrorCopy(backupPath, Current);
                return;
            }

            if (TryLoadFromFile(backupPath, out var loadedBackup) && loadedBackup != null)
            {
                NormalizeAndMigrate(loadedBackup);
                Current = loadedBackup;
                Save();
                return;
            }

            Current = new AppConfig();
            Save();
        }

        private static bool TryLoadFromFile(string path, out AppConfig? config)
        {
            config = null;
            try
            {
                if (!File.Exists(path)) return false;
                string json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json)) return false;
                config = JsonSerializer.Deserialize<AppConfig>(json, _jsonOptions);
                return config != null;
            }
            catch
            {
                return false;
            }
        }

        private static void NormalizeAndMigrate(AppConfig cfg)
        {
            cfg.SchemaVersion = CurrentSchemaVersion;
            cfg.EngineVersion = CurrentEngineVersion;
            cfg.ReleaseChannelUrl = ReleasesPageUrl;

            if (string.IsNullOrWhiteSpace(cfg.Language)) cfg.Language = "en";
            if (string.IsNullOrWhiteSpace(cfg.Theme)) cfg.Theme = "dark";
            if (cfg.Port <= 0 || cfg.Port > 65535) cfg.Port = 5009;
            if (string.IsNullOrWhiteSpace(cfg.BindMode)) cfg.BindMode = "both";
            if (string.IsNullOrWhiteSpace(cfg.DatabaseMode)) cfg.DatabaseMode = "local_sqlite";
            if (string.IsNullOrWhiteSpace(cfg.DatabaseProvider)) cfg.DatabaseProvider = "SQLite";
            cfg.WebsiteRootPath = ToPortablePath(cfg.WebsiteRootPath, "Web Site");
            cfg.SqliteFilePath = ToPortablePath(cfg.SqliteFilePath, "Engine/Data/parrotcore.db");
            cfg.MariaDbBinPath = ToPortablePath(cfg.MariaDbBinPath, "");
            cfg.PhpExecutablePath = ToPortablePath(cfg.PhpExecutablePath, "");
            if (string.IsNullOrWhiteSpace(cfg.DefaultFiles)) cfg.DefaultFiles = "index.php, index.html, index.htm, default.html";
            cfg.CustomHeaders ??= new Dictionary<string, string> { { "X-Frame-Options", "SAMEORIGIN" } };
        }

        public static void Save()
        {
            try
            {
                NormalizeAndMigrate(Current);

                string primaryPath = GetConfigFilePath();
                string json = JsonSerializer.Serialize(Current, _jsonOptions);
                File.WriteAllText(primaryPath, json);

                string backupPath = GetPersistentBackupConfigFilePath();
                SaveMirrorCopy(backupPath, Current, json);

                ApplyWindowsStartup(Current.LaunchAtStartup);
            }
            catch { }
        }

        private static void SaveMirrorCopy(string backupPath, AppConfig cfg, string? serializedJson = null)
        {
            try
            {
                string json = serializedJson ?? JsonSerializer.Serialize(cfg, _jsonOptions);
                File.WriteAllText(backupPath, json);
            }
            catch { }
        }

        public static void ApplyWindowsStartup(bool enable)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
                if (key == null) return;
                if (enable)
                {
                    string exePath = Environment.ProcessPath ?? AppContext.BaseDirectory;
                    key.SetValue("ParrotCoreEngine", $"\"{exePath}\"");
                }
                else
                {
                    key.DeleteValue("ParrotCoreEngine", false);
                }
            }
            catch { }
        }

        public static async Task<(bool updateAvailable, string latestTag, string releaseUrl)> CheckForUpdatesAsync(Action<string>? log = null)
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
                client.DefaultRequestHeaders.UserAgent.ParseAdd($"ParrotCore/{CurrentEngineVersion} (Hnato; JGS-Team)");
                client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

                using var response = await client.GetAsync(LatestReleaseApiUrl);
                if (response.IsSuccessStatusCode)
                {
                    string body = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(body);
                    string tag = doc.RootElement.TryGetProperty("tag_name", out var tagProp)
                        ? (tagProp.GetString() ?? "").TrimStart('v', 'V')
                        : "";
                    string htmlUrl = doc.RootElement.TryGetProperty("html_url", out var urlProp)
                        ? (urlProp.GetString() ?? ReleasesPageUrl)
                        : ReleasesPageUrl;

                    if (!string.IsNullOrEmpty(tag) && Version.TryParse(NormalizeVersionString(tag), out var remoteVer) &&
                        Version.TryParse(CurrentEngineVersion, out var localVer))
                    {
                        if (remoteVer > localVer)
                        {
                            log?.Invoke($"[Updater] New version available: {tag} -> {htmlUrl}");
                            return (true, tag, htmlUrl);
                        }
                    }
                }
                log?.Invoke($"[Updater] ParrotCore is up to date ({CurrentEngineVersion} Hyacinth). Releases: {ReleasesPageUrl}");
            }
            catch
            {
                log?.Invoke($"[Updater] Release channel configured: {ReleasesPageUrl} (Version {CurrentEngineVersion} Hyacinth).");
            }
            return (false, CurrentEngineVersion, ReleasesPageUrl);
        }

        private static string NormalizeVersionString(string raw)
        {
            int spaceIdx = raw.IndexOf(' ');
            if (spaceIdx > 0) raw = raw[..spaceIdx];
            int dashIdx = raw.IndexOf('-');
            if (dashIdx > 0) raw = raw[..dashIdx];
            var parts = raw.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2) return $"{parts[0]}.{parts[1]}.0";
            return raw;
        }
    }
}
