using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace ParrotCore
{
    public class DatabaseManager
    {
        private readonly Action<string> _log;
        private Process? _mariaDbProcess;
        public bool IsConnected { get; private set; } = false;

        public DatabaseManager(Action<string> log)
        {
            _log = log;
        }

        public static string BuildConnectionString(AppConfig cfg)
        {
            if (!string.IsNullOrWhiteSpace(cfg.CustomConnectionString))
                return cfg.CustomConnectionString;

            if (cfg.DatabaseMode == "local_sqlite" || cfg.DatabaseProvider.Equals("SQLite", StringComparison.OrdinalIgnoreCase))
            {
                string sqlitePath = cfg.GetResolvedSqlitePath();
                return $"Data Source={sqlitePath};Cache=Shared;";
            }

            return cfg.DatabaseProvider switch
            {
                "XAMPP" or "MariaDB" or "MySQL" =>
                    $"Server={cfg.DbHost};Port={cfg.DbPort};Database={cfg.DbName};Uid={cfg.DbUser};Pwd={cfg.DbPassword};ConnectionTimeout={cfg.ConnectionTimeoutSeconds};Pooling={cfg.UseConnectionPooling};",
                "AzureSQL" =>
                    $"Server=tcp:{cfg.DbHost},{cfg.DbPort};Initial Catalog={cfg.DbName};Persist Security Info=False;User ID={cfg.DbUser};Password={cfg.DbPassword};MultipleActiveResultSets=False;Encrypt=True;TrustServerCertificate=False;Connection Timeout={cfg.ConnectionTimeoutSeconds};",
                "SQLServer" =>
                    $"Server={cfg.DbHost},{cfg.DbPort};Database={cfg.DbName};User Id={cfg.DbUser};Password={cfg.DbPassword};TrustServerCertificate=True;Connection Timeout={cfg.ConnectionTimeoutSeconds};",
                "PostgreSQL" =>
                    $"Host={cfg.DbHost};Port={cfg.DbPort};Database={cfg.DbName};Username={cfg.DbUser};Password={cfg.DbPassword};Timeout={cfg.ConnectionTimeoutSeconds};Pooling={cfg.UseConnectionPooling};",
                "MongoDB" =>
                    string.IsNullOrEmpty(cfg.DbUser)
                        ? $"mongodb://{cfg.DbHost}:{cfg.DbPort}/{cfg.DbName}"
                        : $"mongodb://{Uri.EscapeDataString(cfg.DbUser)}:{Uri.EscapeDataString(cfg.DbPassword)}@{cfg.DbHost}:{cfg.DbPort}/{cfg.DbName}",
                _ => $"Server={cfg.DbHost};Port={cfg.DbPort};Database={cfg.DbName};User={cfg.DbUser};Password={cfg.DbPassword};"
            };
        }

        public async Task<bool> InitializeOrConnectAsync()
        {
            var cfg = AppConfig.Current;
            if (!cfg.EnableDatabase)
            {
                IsConnected = false;
                return false;
            }

            try
            {
                if (cfg.DatabaseMode == "local_sqlite" || cfg.DatabaseProvider.Equals("SQLite", StringComparison.OrdinalIgnoreCase))
                {
                    string sqliteFile = cfg.GetResolvedSqlitePath();
                    string portableSqlite = AppConfig.ToPortablePath(sqliteFile, "Engine/Data/parrotcore.db");
                    _log($"[Database] Initializing SQLite Engine at: {portableSqlite}");
                    await using var conn = new SqliteConnection($"Data Source={sqliteFile}");
                    await conn.OpenAsync();

                    var cmd = conn.CreateCommand();
                    cmd.CommandText = @"
                        CREATE TABLE IF NOT EXISTS _parrotcore_meta (
                            key TEXT PRIMARY KEY,
                            value TEXT,
                            updated_at TEXT
                        );
                        CREATE TABLE IF NOT EXISTS key_value_store (
                            collection TEXT NOT NULL,
                            item_key TEXT NOT NULL,
                            json_data TEXT NOT NULL,
                            updated_at TEXT NOT NULL,
                            PRIMARY KEY (collection, item_key)
                        );
                    ";
                    await cmd.ExecuteNonQueryAsync();

                    var metaCmd = conn.CreateCommand();
                    metaCmd.CommandText = "INSERT OR REPLACE INTO _parrotcore_meta (key, value, updated_at) VALUES ('status', 'active', $now);";
                    metaCmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
                    await metaCmd.ExecuteNonQueryAsync();

                    IsConnected = true;
                    cfg.IsConfigured = true;
                    AppConfig.Save();
                    _log($"[Database] SQLite ready ({Path.GetFileName(sqliteFile)}). Status: ONLINE.");
                }
                else
                {
                    if (cfg.DatabaseMode == "local_mariadb")
                    {
                        EnsureLocalMariaDbRunning(cfg);
                    }

                    _log($"[Database] Probing {cfg.DatabaseProvider} at {cfg.DbHost}:{cfg.DbPort} (DB: {cfg.DbName})...");
                    bool reachable = await ProbeTcpEndpointAsync(cfg.DbHost, cfg.DbPort, cfg.ConnectionTimeoutSeconds * 1000);
                    if (reachable)
                    {
                        IsConnected = true;
                        cfg.IsConfigured = true;
                        AppConfig.Save();
                        _log($"[Database] Connected to {cfg.DatabaseProvider} server at {cfg.DbHost}:{cfg.DbPort}!");
                    }
                    else
                    {
                        IsConnected = false;
                        _log($"[Database] WARNING: Could not reach {cfg.DatabaseProvider} on {cfg.DbHost}:{cfg.DbPort}.");
                    }
                }

                if (cfg.AutoGenerateSiteDbConfig)
                {
                    GenerateWebsiteDatabaseConfigFiles(cfg);
                }

                return IsConnected;
            }
            catch (Exception ex)
            {
                IsConnected = false;
                _log($"[Database Error] {ex.Message}");
                return false;
            }
        }

        public async Task<bool> RestartConnectionAsync()
        {
            _log("[Database] Restarting database connection pool...");
            IsConnected = false;
            await Task.Delay(250);
            return await InitializeOrConnectAsync();
        }

        public void EnsureLocalMariaDbRunning(AppConfig cfg)
        {
            try
            {
                var running = Process.GetProcessesByName("mysqld");
                if (running.Length > 0)
                {
                    _log($"[Database] Local MariaDB/XAMPP (mysqld PID: {running[0].Id}) is already running.");
                    return;
                }

                string binPath = cfg.ResolveMariaDbBinary();
                if (File.Exists(binPath))
                {
                    _log($"[Database] Starting local MariaDB/MySQL daemon ({Path.GetFileName(binPath)})...");
                    _mariaDbProcess = Process.Start(new ProcessStartInfo
                    {
                        FileName = binPath,
                        CreateNoWindow = true,
                        UseShellExecute = false
                    });
                    _log("[Database] Local MariaDB daemon started.");
                }
                else
                {
                    _log("[Database] Local MariaDB/MySQL binary (mysqld.exe) not found. Configure path in Database settings.");
                }
            }
            catch (Exception ex)
            {
                _log($"[Database] Failed to start local MariaDB: {ex.Message}");
            }
        }

        public static async Task<bool> ProbeTcpEndpointAsync(string host, int port, int timeoutMs = 2500)
        {
            try
            {
                using var client = new TcpClient();
                var connectTask = client.ConnectAsync(host, port);
                var completed = await Task.WhenAny(connectTask, Task.Delay(timeoutMs));
                return completed == connectTask && client.Connected;
            }
            catch
            {
                return false;
            }
        }

        public void GenerateWebsiteDatabaseConfigFiles(AppConfig cfg)
        {
            try
            {
                string webRoot = cfg.GetResolvedWebsitePath();
                if (!Directory.Exists(webRoot))
                    Directory.CreateDirectory(webRoot);

                string connStr = BuildConnectionString(cfg);
                bool isSqlite = cfg.DatabaseMode == "local_sqlite" || cfg.DatabaseProvider.Equals("SQLite", StringComparison.OrdinalIgnoreCase);

                string portableSqlite = isSqlite ? AppConfig.ToPortablePath(cfg.GetResolvedSqlitePath(), "Engine/Data/parrotcore.db") : "";
                var dbJsonObj = new
                {
                    enabled = cfg.EnableDatabase,
                    mode = cfg.DatabaseMode,
                    provider = isSqlite ? "SQLite" : cfg.DatabaseProvider,
                    host = isSqlite ? "localhost" : cfg.DbHost,
                    port = isSqlite ? 0 : cfg.DbPort,
                    database = isSqlite ? Path.GetFileName(cfg.GetResolvedSqlitePath()) : cfg.DbName,
                    user = isSqlite ? "" : cfg.DbUser,
                    sqliteFile = portableSqlite,
                    restApiEndpoint = (isSqlite && cfg.ExposeRestDbApi) ? $"http://localhost:{cfg.Port}/api/db" : "",
                    generatedAt = DateTime.UtcNow.ToString("O")
                };

                string jsonPath = Path.Combine(webRoot, "parrotcore.db.json");
                File.WriteAllText(jsonPath, JsonSerializer.Serialize(dbJsonObj, new JsonSerializerOptions { WriteIndented = true }));

                if (isSqlite && cfg.ExposeRestDbApi)
                {
                    string jsContent = $@"window.ParrotCoreDB = {{
    config: {{
        provider: 'SQLite',
        mode: '{cfg.DatabaseMode}',
        apiEndpoint: '/api/db'
    }},
    async status() {{
        const res = await fetch('/api/db/status');
        return await res.json();
    }},
    async query(sql, params = {{}}) {{
        const res = await fetch('/api/db/query', {{
            method: 'POST',
            headers: {{ 'Content-Type': 'application/json' }},
            body: JSON.stringify({{ sql, params }})
        }});
        return await res.json();
    }},
    async getItem(collection, key) {{
        const res = await fetch(`/api/db/kv/${{encodeURIComponent(collection)}}/${{encodeURIComponent(key)}}`);
        if (!res.ok) return null;
        return await res.json();
    }},
    async setItem(collection, key, data) {{
        const res = await fetch(`/api/db/kv/${{encodeURIComponent(collection)}}/${{encodeURIComponent(key)}}`, {{
            method: 'POST',
            headers: {{ 'Content-Type': 'application/json' }},
            body: JSON.stringify(data)
        }});
        return await res.json();
    }}
}};
";
                    File.WriteAllText(Path.Combine(webRoot, "parrotcore-db.js"), jsContent);
                }

                if (cfg.EnablePhpInterpreter || !isSqlite)
                {
                    string phpContent = isSqlite
                        ? @"<?php
define('DB_PROVIDER', 'SQLite');
define('DB_SQLITE_PATH', getenv('DB_SQLITE_PATH') ?: '../Engine/Data/parrotcore.db');
define('DB_DSN', 'sqlite:' . DB_SQLITE_PATH);
?>"
                        : $@"<?php
define('DB_PROVIDER', '{cfg.DatabaseProvider}');
define('DB_HOST', '{cfg.DbHost}');
define('DB_PORT', '{cfg.DbPort}');
define('DB_NAME', '{cfg.DbName}');
define('DB_USER', '{cfg.DbUser}');
define('DB_PASS', '{cfg.DbPassword}');
?>";
                    File.WriteAllText(Path.Combine(webRoot, "db_config.php"), phpContent);
                }

                string portableWeb = AppConfig.ToPortablePath(webRoot, "Web Site");
                _log($"[Database] Adapted and generated database config files for {dbJsonObj.provider} in '{portableWeb}'.");
            }
            catch (Exception ex)
            {
                _log($"[Database] Failed to write config files to website folder: {ex.Message}");
            }
        }

        public async Task<List<Dictionary<string, object?>>> ExecuteSqliteQueryAsync(string sql, Dictionary<string, object?>? parameters = null)
        {
            string sqliteFile = AppConfig.Current.GetResolvedSqlitePath();
            var rows = new List<Dictionary<string, object?>>();

            await using var conn = new SqliteConnection($"Data Source={sqliteFile}");
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;

            if (parameters != null)
            {
                foreach (var kv in parameters)
                {
                    cmd.Parameters.AddWithValue(kv.Key.StartsWith("$") || kv.Key.StartsWith("@") ? kv.Key : "@" + kv.Key, kv.Value ?? DBNull.Value);
                }
            }

            if (sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) ||
                sql.TrimStart().StartsWith("PRAGMA", StringComparison.OrdinalIgnoreCase))
            {
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var row = new Dictionary<string, object?>();
                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    }
                    rows.Add(row);
                }
            }
            else
            {
                int affected = await cmd.ExecuteNonQueryAsync();
                rows.Add(new Dictionary<string, object?> { ["affectedRows"] = affected });
            }

            return rows;
        }
    }
}
