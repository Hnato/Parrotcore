using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace ParrotCore
{
    public class ProjectAuditReport
    {
        public string RootPath { get; set; } = "";
        public bool IsValid { get; set; }
        public int TotalFiles { get; set; }
        public int HtmlFiles { get; set; }
        public int PhpFiles { get; set; }
        public int CssFiles { get; set; }
        public int JsFiles { get; set; }
        public int AssetFiles { get; set; }
        public bool HasEntryFile { get; set; }
        public string DetectedEntryFile { get; set; } = "";
        public bool HasDbIntegration { get; set; }
        public List<string> Warnings { get; set; } = new();
        public List<string> PassedChecks { get; set; } = new();
    }

    public static class WebsiteWizard
    {
        private static readonly string[] _embeddedBackupFiles =
        {
            "index.html",
            "guide.html",
            "404.html",
            "Parrotcore.ico",
            "favicon.ico",
            "css/style.css",
            "js/app.js",
            "img/guide-01-dashboard.png",
            "img/guide-02-project-inspector.png",
            "img/guide-03-modes.png",
            "img/guide-04-network.png",
            "img/guide-05-database.png",
            "img/guide-06-website-php.png"
        };

        public static string GetBackupTemplateDirectory()
        {
            return Path.Combine(AppConfig.ResolveEngineDataDirectory(), "DefaultWebsite");
        }

        public static void EnsureBackupDirectoryPopulated()
        {
            try
            {
                string backupDir = GetBackupTemplateDirectory();
                if (!Directory.Exists(backupDir))
                    Directory.CreateDirectory(backupDir);

                var asm = Assembly.GetExecutingAssembly();
                foreach (var relPath in _embeddedBackupFiles)
                {
                    string targetPath = Path.Combine(backupDir, relPath.Replace('/', Path.DirectorySeparatorChar));
                    string? parent = Path.GetDirectoryName(targetPath);
                    if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                        Directory.CreateDirectory(parent);

                    if (!File.Exists(targetPath))
                    {
                        using var stream = asm.GetManifestResourceStream("DefaultWebsite/" + relPath);
                        if (stream != null)
                        {
                            using var fs = File.Create(targetPath);
                            stream.CopyTo(fs);
                        }
                    }
                }
            }
            catch { }
        }

        public static ProjectAuditReport InspectProject(AppConfig cfg, Action<string>? log = null)
        {
            EnsureBackupDirectoryPopulated();
            string webRoot = cfg.GetResolvedWebsitePath();
            var report = new ProjectAuditReport { RootPath = webRoot };

            if (!Directory.Exists(webRoot))
            {
                Directory.CreateDirectory(webRoot);
                report.Warnings.Add("Website folder did not exist and was created automatically.");
            }

            var allFiles = Directory.GetFiles(webRoot, "*.*", SearchOption.AllDirectories);
            report.TotalFiles = allFiles.Length;
            report.HtmlFiles = allFiles.Count(f => f.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".htm", StringComparison.OrdinalIgnoreCase));
            report.PhpFiles = allFiles.Count(f => f.EndsWith(".php", StringComparison.OrdinalIgnoreCase));
            report.CssFiles = allFiles.Count(f => f.EndsWith(".css", StringComparison.OrdinalIgnoreCase));
            report.JsFiles = allFiles.Count(f => f.EndsWith(".js", StringComparison.OrdinalIgnoreCase));
            report.AssetFiles = report.TotalFiles - report.HtmlFiles - report.PhpFiles - report.CssFiles - report.JsFiles;

            var defaultNames = cfg.DefaultFiles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            if (!defaultNames.Contains("index.php", StringComparer.OrdinalIgnoreCase))
                defaultNames.Add("index.php");

            foreach (var def in defaultNames)
            {
                string candidate = Path.Combine(webRoot, def);
                if (File.Exists(candidate))
                {
                    report.HasEntryFile = true;
                    report.DetectedEntryFile = def;
                    report.PassedChecks.Add($"Found entry document: {def}");
                    break;
                }
            }

            if (!report.HasEntryFile)
            {
                report.Warnings.Add($"Missing entry file ({cfg.DefaultFiles}) in website root.");
            }

            if (report.PhpFiles > 0)
            {
                if (cfg.EnablePhpInterpreter)
                {
                    string? phpBin = cfg.ResolvePhpBinary();
                    if (!string.IsNullOrEmpty(phpBin))
                        report.PassedChecks.Add($"PHP files detected ({report.PhpFiles}) - PHP interpreter ready ({Path.GetFileName(phpBin)}).");
                    else
                        report.Warnings.Add($"PHP files detected ({report.PhpFiles}), but PHP executable was not found. Configure PHP path in Website settings.");
                }
                else
                {
                    report.Warnings.Add($"PHP files detected ({report.PhpFiles}), but PHP Interpreter is disabled in Website settings.");
                }
            }

            if (File.Exists(Path.Combine(webRoot, "parrotcore-db.js")) ||
                File.Exists(Path.Combine(webRoot, "parrotcore.db.json")) ||
                File.Exists(Path.Combine(webRoot, "db_config.php")))
            {
                report.HasDbIntegration = true;
                report.PassedChecks.Add("Database configuration file detected in website folder.");
            }

            if (report.HtmlFiles > 0)
            {
                string firstHtml = Path.Combine(webRoot, report.HasEntryFile && report.DetectedEntryFile.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
                    ? report.DetectedEntryFile
                    : Path.GetFileName(allFiles.First(f => f.EndsWith(".html", StringComparison.OrdinalIgnoreCase))));
                if (File.Exists(firstHtml))
                {
                    string htmlContent = File.ReadAllText(firstHtml);
                    if (htmlContent.Contains("<meta name=\"viewport\"", StringComparison.OrdinalIgnoreCase))
                        report.PassedChecks.Add("Responsive viewport meta tag verified.");
                    else
                        report.Warnings.Add("Entry HTML is missing <meta name=\"viewport\"> for mobile responsiveness.");

                    if (htmlContent.Contains("<title>", StringComparison.OrdinalIgnoreCase))
                        report.PassedChecks.Add("Document <title> tag verified.");
                }
            }

            report.IsValid = report.HasEntryFile;

            if (log != null)
            {
                string portableWeb = AppConfig.ToPortablePath(webRoot, "Web Site");
                log($"[Wizard] Inspected '{portableWeb}': {report.TotalFiles} files ({report.HtmlFiles} HTML, {report.PhpFiles} PHP, {report.CssFiles} CSS, {report.JsFiles} JS).");
                foreach (var pass in report.PassedChecks)
                    log($"  [OK] {pass}");
                foreach (var warn in report.Warnings)
                    log($"  [WARN] {warn}");
            }

            return report;
        }

        public static void EnsureFaviconFiles(string webRoot)
        {
            try
            {
                string icoPath = Path.Combine(webRoot, "Parrotcore.ico");
                string favPath = Path.Combine(webRoot, "favicon.ico");
                if (File.Exists(icoPath) && File.Exists(favPath)) return;

                var asm = Assembly.GetExecutingAssembly();
                using var stream = asm.GetManifestResourceStream("ParrotCore.Parrotcore.ico")
                                ?? asm.GetManifestResourceStream("DefaultWebsite/Parrotcore.ico");
                if (stream != null)
                {
                    using var ms = new MemoryStream();
                    stream.CopyTo(ms);
                    byte[] bytes = ms.ToArray();
                    if (!File.Exists(icoPath)) File.WriteAllBytes(icoPath, bytes);
                    if (!File.Exists(favPath)) File.WriteAllBytes(favPath, bytes);
                }
            }
            catch { }
        }

        public static void ScaffoldStarterWebsite(AppConfig cfg, DatabaseManager dbManager, Action<string> log, bool overwrite = false)
        {
            EnsureBackupDirectoryPopulated();
            string webRoot = cfg.GetResolvedWebsitePath();
            if (!Directory.Exists(webRoot))
                Directory.CreateDirectory(webRoot);

            string backupDir = GetBackupTemplateDirectory();
            if (Directory.Exists(backupDir))
            {
                CopyDirectoryRecursive(backupDir, webRoot, overwrite);
            }

            var asm = Assembly.GetExecutingAssembly();
            foreach (var relPath in _embeddedBackupFiles)
            {
                string destFile = Path.Combine(webRoot, relPath.Replace('/', Path.DirectorySeparatorChar));
                string? parent = Path.GetDirectoryName(destFile);
                if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                    Directory.CreateDirectory(parent);

                if (overwrite || !File.Exists(destFile))
                {
                    using var stream = asm.GetManifestResourceStream("DefaultWebsite/" + relPath);
                    if (stream != null)
                    {
                        using var fs = File.Create(destFile);
                        stream.CopyTo(fs);
                    }
                }
            }

            EnsureFaviconFiles(webRoot);

            if (cfg.AutoGenerateSiteDbConfig)
            {
                dbManager.GenerateWebsiteDatabaseConfigFiles(cfg);
            }

            string portableWeb = AppConfig.ToPortablePath(webRoot, "Web Site");
            log($"[Wizard] Default website restored & validated in: {portableWeb}");
        }

        private static void CopyDirectoryRecursive(string sourceDir, string targetDir, bool overwrite)
        {
            Directory.CreateDirectory(targetDir);
            foreach (var file in Directory.GetFiles(sourceDir))
            {
                string destFile = Path.Combine(targetDir, Path.GetFileName(file));
                if (overwrite || !File.Exists(destFile))
                {
                    File.Copy(file, destFile, true);
                }
            }
            foreach (var subDir in Directory.GetDirectories(sourceDir))
            {
                string destSubDir = Path.Combine(targetDir, Path.GetFileName(subDir));
                CopyDirectoryRecursive(subDir, destSubDir, overwrite);
            }
        }
    }
}
