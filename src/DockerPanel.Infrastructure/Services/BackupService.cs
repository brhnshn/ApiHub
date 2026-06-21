using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using DockerPanel.Domain.Interfaces;
using DockerPanel.Domain.Entities;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace DockerPanel.Infrastructure.Services;

public class BackupService : IBackupService
{
    private static bool _isBackupActive = false;
    private static readonly object _backupLock = new object();

    public bool IsBackupActive => _isBackupActive;

    public static event Func<Task>? OnBackupUpdated;

    private static void NotifyBackupUpdated()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                if (OnBackupUpdated != null)
                {
                    await OnBackupUpdated.Invoke();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Backup Notification Error] {ex.Message}");
            }
        });
    }

    private readonly IConfiguration _configuration;
    private readonly IAuditLogService _auditLogService;
    private const string LinuxBackupsDir = "/opt/dockerpanel/backups";
    private const string LinuxProjectsDir = "/opt/dockerpanel/projects";
    private const string LinuxNginxDir = "/etc/nginx/sites-available";
    private const string LinuxMailDir = "/opt/dockerpanel/mail";

    public BackupService(IConfiguration configuration, IAuditLogService auditLogService)
    {
        _configuration = configuration;
        _auditLogService = auditLogService;
    }

    private string GetBackupsPath()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var winPath = Path.Combine(AppContext.BaseDirectory, "opt_dockerpanel", "backups");
            if (!Directory.Exists(winPath)) Directory.CreateDirectory(winPath);
            return winPath;
        }
        if (!Directory.Exists(LinuxBackupsDir)) Directory.CreateDirectory(LinuxBackupsDir);
        return LinuxBackupsDir;
    }

    private string GetProjectsPath()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var winPath = Path.Combine(AppContext.BaseDirectory, "opt_dockerpanel", "projects");
            if (!Directory.Exists(winPath)) Directory.CreateDirectory(winPath);
            return winPath;
        }
        return LinuxProjectsDir;
    }

    private string GetNginxPath()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var winPath = Path.Combine(AppContext.BaseDirectory, "opt_dockerpanel", "nginx", "sites-available");
            if (!Directory.Exists(winPath)) Directory.CreateDirectory(winPath);
            return winPath;
        }
        return LinuxNginxDir;
    }

    private string GetMailPath()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var winPath = Path.Combine(AppContext.BaseDirectory, "opt_dockerpanel", "mail");
            if (!Directory.Exists(winPath)) Directory.CreateDirectory(winPath);
            return winPath;
        }
        return LinuxMailDir;
    }

    public async Task<List<BackupInfoDto>> GetBackupsAsync()
    {
        var backupsDir = GetBackupsPath();
        var list = new List<BackupInfoDto>();

        if (!Directory.Exists(backupsDir)) return list;

        var directories = Directory.GetDirectories(backupsDir, "backup_*");
        foreach (var dir in directories)
        {
            var folderName = Path.GetFileName(dir);
            var manifestPath = Path.Combine(dir, "manifest.json");

            if (File.Exists(manifestPath))
            {
                try
                {
                    var manifestJson = await File.ReadAllTextAsync(manifestPath);
                    var info = JsonSerializer.Deserialize<BackupInfoDto>(manifestJson);
                    if (info != null)
                    {
                        info.FolderName = folderName;
                        list.Add(info);
                        continue;
                    }
                }
                catch
                {
                    // Fail silently, generate basic info
                }
            }

            // Fallback: Generate basic info if manifest is missing/broken
            var timestamp = DateTimeOffset.UtcNow;
            var parts = folderName.Split('_');
            if (parts.Length >= 3 && DateTimeOffset.TryParse($"{parts[1]} {parts[2].Replace('-', ':')}", out var parsedDate))
            {
                timestamp = parsedDate;
            }

            list.Add(new BackupInfoDto
            {
                FolderName = folderName,
                Timestamp = timestamp,
                Status = "corrupt",
                ErrorMessage = "Manifest dosyası okunamadı veya eksik."
            });
        }

        return list;
    }

    public async Task TriggerBackupAsync(Guid userId)
    {
        lock (_backupLock)
        {
            if (_isBackupActive)
            {
                throw new InvalidOperationException("Yedekleme işlemi zaten arka planda çalışıyor!");
            }
            _isBackupActive = true;
        }

        try
        {
            var timestampStr = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd_HH-mm-ss");
            var folderName = $"backup_{timestampStr}";
            var backupsDir = GetBackupsPath();
            var targetBackupDir = Path.Combine(backupsDir, folderName);

            Directory.CreateDirectory(targetBackupDir);

            // Write initial "processing" manifest to prevent asynchronous race condition UI errors
            var initialManifest = new BackupInfoDto
            {
                FolderName = folderName,
                Timestamp = DateTimeOffset.UtcNow,
                Status = "processing",
                ErrorMessage = "Yedekleme işlemi devam ediyor..."
            };
            var initialManifestPath = Path.Combine(targetBackupDir, "manifest.json");
            await File.WriteAllTextAsync(initialManifestPath, JsonSerializer.Serialize(initialManifest, new JsonSerializerOptions { WriteIndented = true }));

            SystemLogQueue.Log("info", $"[Yedekleme] Süreç başlatıldı: {folderName}");
            NotifyBackupUpdated();

            var dbSize = "0 B";
            var projectsSize = "0 B";
            var nginxSize = "0 B";
            var mailSize = "0 B";
            var status = "success";
            string? errorMsg = null;

            try
            {
                // 1. Database Yedekleme (pg_dump)
                var dbFile = Path.Combine(targetBackupDir, "database.sql.gz");
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    // Windows Simülasyonu
                    await File.WriteAllTextAsync(dbFile, "-- Windows Simulated PostgreSQL Backup Dump\nSELECT 1;");
                    dbSize = "12 KB";
                }
                else
                {
                    var connStr = _configuration.GetConnectionString("DefaultConnection") ?? "";
                    var dbName = "dockerpanel_db";
                    var dbUser = "dp_admin";
                    var dbPassword = "dp_admin_password";
                    var host = "localhost";
                    var port = "5432";

                    try
                    {
                        var builder = new Npgsql.NpgsqlConnectionStringBuilder(connStr);
                        dbName = builder.Database ?? dbName;
                        dbUser = builder.Username ?? dbUser;
                        dbPassword = builder.Password ?? dbPassword;
                        host = builder.Host ?? host;
                        port = builder.Port.ToString();
                    }
                    catch (Exception ex)
                    {
                        SystemLogQueue.Log("warning", $"[Yedekleme] Bağlantı dizesi ayrıştırılamadı: {ex.Message}");
                    }

                    // PostgreSQL konteyner ismini bulmaya çalışalım
                    string? containerName = null;
                    try
                    {
                        Uri dockerUri = new Uri("unix:///var/run/docker.sock");
                        using var dockerClient = new DockerClientConfiguration(dockerUri).CreateClient();
                        var containers = await dockerClient.Containers.ListContainersAsync(new ContainersListParameters { All = true });
                        var dbContainer = containers.FirstOrDefault(c =>
                            c.Names.Any(name => name.Contains("db") || name.Contains("postgres") || name.Contains("postgresql")));
                        if (dbContainer != null)
                        {
                            var rawName = dbContainer.Names.FirstOrDefault(n => n.Contains("db") || n.Contains("postgres") || n.Contains("postgresql"));
                            if (!string.IsNullOrEmpty(rawName))
                            {
                                containerName = rawName.TrimStart('/');
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        SystemLogQueue.Log("warning", $"[Yedekleme] PostgreSQL konteyner ismi tespit edilemedi: {ex.Message}. Doğrudan sunucuda pg_dump denenecek.");
                    }

                    var dumpCmd = "";
                    if (!string.IsNullOrEmpty(containerName))
                    {
                        SystemLogQueue.Log("info", $"[Yedekleme] PostgreSQL Konteyneri Tespit Edildi: {containerName}. Konteyner içi pg_dump kullanılacak.");
                        dumpCmd = $"set -o pipefail; docker exec -i -e PGPASSWORD=\"{dbPassword}\" {containerName} pg_dump -U {dbUser} {dbName} | gzip > {dbFile}";
                    }
                    else
                    {
                        SystemLogQueue.Log("info", $"[Yedekleme] PostgreSQL Konteyneri bulunamadı, sunucu üzerinde pg_dump denenecek.");
                        dumpCmd = $"set -o pipefail; PGPASSWORD=\"{dbPassword}\" pg_dump -h {host} -p {port} -U {dbUser} {dbName} | gzip > {dbFile}";
                    }

                    SystemLogQueue.Log("info", $"[Yedekleme] Komut koşturuluyor: {dumpCmd}");

                    var psi = new ProcessStartInfo
                    {
                        FileName = "bash",
                        Arguments = $"-c \"{dumpCmd.Replace("\"", "\\\"")}\"",
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    using var process = Process.Start(psi);
                    if (process != null)
                    {
                        await process.WaitForExitAsync();
                        if (process.ExitCode != 0)
                        {
                            var err = await process.StandardError.ReadToEndAsync();
                            throw new Exception($"Database yedekleme hatası (ExitCode: {process.ExitCode}): {err}");
                        }
                    }
                    dbSize = GetFriendlyFileSize(dbFile);
                }

                // 2. Proje Dosyalarını Yedekleme
                var projectsFile = Path.Combine(targetBackupDir, "projects.tar.gz");
                var projectsPath = GetProjectsPath();
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    // Windows Simülasyonu: Zip as projects.tar.gz (actually a zip for test)
                    if (File.Exists(projectsFile)) File.Delete(projectsFile);
                    ZipFile.CreateFromDirectory(projectsPath, projectsFile);
                    projectsSize = GetFriendlyFileSize(projectsFile);
                }
                else
                {
                    SystemLogQueue.Log("info", $"$ sudo -n tar -czf {projectsFile} -C {projectsPath} .");
                    var psi = new ProcessStartInfo
                    {
                        FileName = "sudo",
                        Arguments = $"-n tar -czf {projectsFile} -C {projectsPath} .",
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    using var process = Process.Start(psi);
                    if (process != null)
                    {
                        await process.WaitForExitAsync();
                        if (process.ExitCode != 0)
                        {
                            var err = await process.StandardError.ReadToEndAsync();
                            throw new Exception($"Proje dosyaları yedekleme hatası: {err}");
                        }
                    }

                    // Fix ownership of the created archive to dockerpanel_api
                    var chownPsi = new ProcessStartInfo
                    {
                        FileName = "sudo",
                        Arguments = $"-n chown dockerpanel_api:dockerpanel_api {projectsFile}",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using var chownProc = Process.Start(chownPsi);
                    if (chownProc != null) await chownProc.WaitForExitAsync();

                    projectsSize = GetFriendlyFileSize(projectsFile);
                }

                // 3. Nginx Config Yedekleme
                var nginxFile = Path.Combine(targetBackupDir, "nginx.tar.gz");
                var nginxPath = GetNginxPath();
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    // Windows Simülasyonu
                    if (File.Exists(nginxFile)) File.Delete(nginxFile);
                    ZipFile.CreateFromDirectory(nginxPath, nginxFile);
                    nginxSize = GetFriendlyFileSize(nginxFile);
                }
                else
                {
                    SystemLogQueue.Log("info", $"$ sudo -n tar -czf {nginxFile} -C {nginxPath} .");
                    var psi = new ProcessStartInfo
                    {
                        FileName = "sudo",
                        Arguments = $"-n tar -czf {nginxFile} -C {nginxPath} .",
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    using var process = Process.Start(psi);
                    if (process != null)
                    {
                        await process.WaitForExitAsync();
                        if (process.ExitCode != 0)
                        {
                            var err = await process.StandardError.ReadToEndAsync();
                            throw new Exception($"Nginx config yedekleme hatası: {err}");
                        }
                    }

                    // Fix ownership of the created archive to dockerpanel_api
                    var chownPsi = new ProcessStartInfo
                    {
                        FileName = "sudo",
                        Arguments = $"-n chown dockerpanel_api:dockerpanel_api {nginxFile}",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using var chownProc = Process.Start(chownPsi);
                    if (chownProc != null) await chownProc.WaitForExitAsync();

                    nginxSize = GetFriendlyFileSize(nginxFile);
                }

                // 4. E-Posta / Mail Sunucusu Yedekleme
                var mailFile = Path.Combine(targetBackupDir, "mail.tar.gz");
                var mailPath = GetMailPath();
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    // Windows Simülasyonu
                    if (File.Exists(mailFile)) File.Delete(mailFile);
                    ZipFile.CreateFromDirectory(mailPath, mailFile);
                    mailSize = GetFriendlyFileSize(mailFile);
                }
                else
                {
                    SystemLogQueue.Log("info", $"$ sudo -n tar -czf {mailFile} -C {mailPath} .");
                    var psi = new ProcessStartInfo
                    {
                        FileName = "sudo",
                        Arguments = $"-n tar -czf {mailFile} -C {mailPath} .",
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    using var process = Process.Start(psi);
                    if (process != null)
                    {
                        await process.WaitForExitAsync();
                        if (process.ExitCode != 0)
                        {
                            var err = await process.StandardError.ReadToEndAsync();
                            throw new Exception($"E-posta dosyaları yedekleme hatası: {err}");
                        }
                    }

                    // Fix ownership of the created archive to dockerpanel_api
                    var chownPsi = new ProcessStartInfo
                    {
                        FileName = "sudo",
                        Arguments = $"-n chown dockerpanel_api:dockerpanel_api {mailFile}",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using var chownProc = Process.Start(chownPsi);
                    if (chownProc != null) await chownProc.WaitForExitAsync();

                    mailSize = GetFriendlyFileSize(mailFile);
                }
            }
            catch (Exception ex)
            {
                status = "failed";
                errorMsg = ex.Message;
                SystemLogQueue.Log("error", $"[Yedekleme] Hata oluştu: {ex.Message}");
            }

            // Get Total Size
            long totalBytes = 0;
            if (Directory.Exists(targetBackupDir))
            {
                foreach (var f in Directory.GetFiles(targetBackupDir))
                {
                    totalBytes += new FileInfo(f).Length;
                }
            }
            var totalSizeStr = FormatByteCount(totalBytes);

            // Write Manifest
            var manifest = new BackupInfoDto
            {
                FolderName = folderName,
                Timestamp = DateTimeOffset.UtcNow,
                DatabaseSize = dbSize,
                ProjectsSize = projectsSize,
                NginxSize = nginxSize,
                MailSize = mailSize,
                TotalSize = totalSizeStr,
                Status = status,
                ErrorMessage = errorMsg
            };

            var manifestPath = Path.Combine(targetBackupDir, "manifest.json");
            await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

            SystemLogQueue.Log("info", $"[Yedekleme] Süreç bitti: {folderName} | Statü: {status}");
            NotifyBackupUpdated();

            // Audit Log
            var auditDetails = JsonSerializer.Serialize(new { folder = folderName, status = status, size = totalSizeStr });
            await _auditLogService.LogAsync(userId, "BackupCreated", "Backup", Guid.Empty, auditDetails, "localhost", "System/Worker");

            // Remote VDS Eşitleme (SSH/SCP)
            if (status == "success")
            {
                try
                {
                    var settings = await GetRemoteBackupSettingsAsync();
                    if (settings.Enabled)
                    {
                        await SyncToRemoteVdsAsync(folderName, settings);
                    }
                }
                catch (Exception remoteEx)
                {
                    SystemLogQueue.Log("error", $"[Uzak Yedekleme] Senkronizasyon hatası: {remoteEx.Message}");
                }
            }

            // Clean older backups (Keep last 7 days)
            try
            {
                await CleanOldBackupsAsync();
            }
            catch (Exception cleanEx)
            {
                SystemLogQueue.Log("warning", $"Eski yedekler temizlenirken hata oluştu: {cleanEx.Message}");
            }
        }
        finally
        {
            lock (_backupLock)
            {
                _isBackupActive = false;
            }
        }
    }

    public async Task RestoreBackupAsync(Guid userId, string folderName, string type)
    {
        var backupsDir = GetBackupsPath();
        var targetBackupDir = Path.Combine(backupsDir, folderName);

        if (!Directory.Exists(targetBackupDir))
        {
            throw new FileNotFoundException("Belirtilen yedek klasörü bulunamadı!");
        }

        SystemLogQueue.Log("warning", $"[Geri Yükleme] Süreç başlatıldı: {folderName} (Tip: {type})");

        try
        {
            if (type.Equals("database", StringComparison.OrdinalIgnoreCase))
            {
                var dbFile = Path.Combine(targetBackupDir, "database.sql.gz");
                if (!File.Exists(dbFile)) throw new FileNotFoundException("Veritabanı yedek dosyası bulunamadı!");

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    SystemLogQueue.Log("info", "[Windows Simülasyonu] Veritabanı yedeği geri yükleniyor...");
                    await Task.Delay(1000);
                }
                else
                {
                    // Parse connection string
                    var connStr = _configuration.GetConnectionString("DefaultConnection") ?? "";
                    var dbName = "dockerpanel_db";
                    var dbUser = "dp_admin";
                    var dbPassword = "dp_admin_password";
                    var host = "localhost";
                    var port = "5432";

                    try
                    {
                        var builder = new Npgsql.NpgsqlConnectionStringBuilder(connStr);
                        dbName = builder.Database ?? dbName;
                        dbUser = builder.Username ?? dbUser;
                        dbPassword = builder.Password ?? dbPassword;
                        host = builder.Host ?? host;
                        port = builder.Port.ToString();
                    }
                    catch (Exception ex)
                    {
                        SystemLogQueue.Log("warning", $"[Geri Yükleme] Bağlantı dizesi ayrıştırılamadı: {ex.Message}");
                    }

                    // PostgreSQL konteyner ismini bulmaya çalışalım
                    string? containerName = null;
                    try
                    {
                        Uri dockerUri = new Uri("unix:///var/run/docker.sock");
                        using var dockerClient = new DockerClientConfiguration(dockerUri).CreateClient();
                        var containers = await dockerClient.Containers.ListContainersAsync(new ContainersListParameters { All = true });
                        var dbContainer = containers.FirstOrDefault(c =>
                            c.Names.Any(name => name.Contains("db") || name.Contains("postgres") || name.Contains("postgresql")));
                        if (dbContainer != null)
                        {
                            var rawName = dbContainer.Names.FirstOrDefault(n => n.Contains("db") || n.Contains("postgres") || n.Contains("postgresql"));
                            if (!string.IsNullOrEmpty(rawName))
                            {
                                containerName = rawName.TrimStart('/');
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        SystemLogQueue.Log("warning", $"[Geri Yükleme] PostgreSQL konteyner ismi tespit edilemedi: {ex.Message}. Doğrudan sunucuda psql denenecek.");
                    }

                    string restoreCmd = "";
                    if (!string.IsNullOrEmpty(containerName))
                    {
                        SystemLogQueue.Log("info", $"[Geri Yükleme] PostgreSQL Konteyneri Tespit Edildi: {containerName}. Konteyner içi psql ile geri yüklenecek.");
                        
                        // Terminate existing connections first
                        var termCmd = $"docker exec -i {containerName} psql -U {dbUser} -d postgres -c \"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{dbName}' AND pid <> pg_backend_pid();\"";
                        SystemLogQueue.Log("info", $"[Geri Yükleme] Bağlantılar sonlandırılıyor: {termCmd}");
                        var termPsi = new ProcessStartInfo
                        {
                            FileName = "bash",
                            Arguments = $"-c \"{termCmd.Replace("\"", "\\\"")}\"",
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        using var termProc = Process.Start(termPsi);
                        if (termProc != null) await termProc.WaitForExitAsync();

                        restoreCmd = $"set -o pipefail; gunzip -c {dbFile} | docker exec -i -e PGPASSWORD=\"{dbPassword}\" {containerName} psql -U {dbUser} -d {dbName}";
                    }
                    else
                    {
                        restoreCmd = $"set -o pipefail; gunzip -c {dbFile} | PGPASSWORD=\"{dbPassword}\" psql -h {host} -p {port} -U {dbUser} -d {dbName}";
                    }

                    SystemLogQueue.Log("info", $"[Geri Yükleme] Komut koşturuluyor: {restoreCmd}");
                    
                    var psi = new ProcessStartInfo
                    {
                        FileName = "bash",
                        Arguments = $"-c \"{restoreCmd.Replace("\"", "\\\"")}\"",
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using var process = Process.Start(psi);
                    if (process != null)
                    {
                        await process.WaitForExitAsync();
                        if (process.ExitCode != 0)
                        {
                            var err = await process.StandardError.ReadToEndAsync();
                            throw new Exception($"Database geri yükleme hatası (ExitCode: {process.ExitCode}): {err}");
                        }
                    }
                }
            }
            else if (type.Equals("projects", StringComparison.OrdinalIgnoreCase))
            {
                var projectsFile = Path.Combine(targetBackupDir, "projects.tar.gz");
                if (!File.Exists(projectsFile)) throw new FileNotFoundException("Proje yedek dosyası bulunamadı!");

                var projectsPath = GetProjectsPath();

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    // Clear and unzip
                    SystemLogQueue.Log("info", "[Windows Simülasyonu] Proje dizini temizleniyor ve yedek açılıyor...");
                    foreach (var sub in Directory.GetDirectories(projectsPath)) Directory.Delete(sub, true);
                    foreach (var f in Directory.GetFiles(projectsPath)) File.Delete(f);
                    ZipFile.ExtractToDirectory(projectsFile, projectsPath);
                }
                else
                {
                    SystemLogQueue.Log("info", $"$ sudo -n tar -xzf {projectsFile} -C {projectsPath}");
                    var psi = new ProcessStartInfo
                    {
                        FileName = "bash",
                        Arguments = $"-c \"sudo -n rm -rf {projectsPath}/* && sudo -n tar -xzf {projectsFile} -C {projectsPath} && sudo -n chown -R dockerpanel_api:dockerpanel_api {projectsPath}\"",
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using var process = Process.Start(psi);
                    if (process != null)
                    {
                        await process.WaitForExitAsync();
                        if (process.ExitCode != 0)
                        {
                            var err = await process.StandardError.ReadToEndAsync();
                            throw new Exception($"Proje dosyaları geri yükleme hatası: {err}");
                        }
                    }
                }
            }
            else if (type.Equals("nginx", StringComparison.OrdinalIgnoreCase))
            {
                var nginxFile = Path.Combine(targetBackupDir, "nginx.tar.gz");
                if (!File.Exists(nginxFile)) throw new FileNotFoundException("Nginx yedek dosyası bulunamadı!");

                var nginxPath = GetNginxPath();

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    SystemLogQueue.Log("info", "[Windows Simülasyonu] Nginx yapılandırma dizini temizleniyor ve yedek açılıyor...");
                    foreach (var f in Directory.GetFiles(nginxPath)) File.Delete(f);
                    ZipFile.ExtractToDirectory(nginxFile, nginxPath);
                }
                else
                {
                    SystemLogQueue.Log("info", $"$ sudo -n tar -xzf {nginxFile} -C {nginxPath}");
                    var psi = new ProcessStartInfo
                    {
                        FileName = "bash",
                        Arguments = $"-c \"sudo -n rm -rf {nginxPath}/* && sudo -n tar -xzf {nginxFile} -C {nginxPath} && sudo -n nginx -t && sudo -n systemctl reload nginx\"",
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using var process = Process.Start(psi);
                    if (process != null)
                    {
                        await process.WaitForExitAsync();
                        if (process.ExitCode != 0)
                        {
                            var err = await process.StandardError.ReadToEndAsync();
                            throw new Exception($"Nginx config geri yükleme hatası: {err}");
                        }
                    }
                }
            }
            else if (type.Equals("mail", StringComparison.OrdinalIgnoreCase))
            {
                var mailFile = Path.Combine(targetBackupDir, "mail.tar.gz");
                if (!File.Exists(mailFile)) throw new FileNotFoundException("E-posta yedek dosyası bulunamadı!");

                var mailPath = GetMailPath();

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    SystemLogQueue.Log("info", "[Windows Simülasyonu] E-posta dizini temizleniyor ve yedek açılıyor...");
                    foreach (var sub in Directory.GetDirectories(mailPath)) Directory.Delete(sub, true);
                    foreach (var f in Directory.GetFiles(mailPath)) File.Delete(f);
                    ZipFile.ExtractToDirectory(mailFile, mailPath);
                }
                else
                {
                    SystemLogQueue.Log("info", $"$ sudo -n tar -xzf {mailFile} -C {mailPath}");
                    var psi = new ProcessStartInfo
                    {
                        FileName = "bash",
                        Arguments = $"-c \"sudo -n rm -rf {mailPath}/* && sudo -n tar -xzf {mailFile} -C {mailPath} && docker restart dockerpanel-mailserver || true\"",
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using var process = Process.Start(psi);
                    if (process != null)
                    {
                        await process.WaitForExitAsync();
                        if (process.ExitCode != 0)
                        {
                            var err = await process.StandardError.ReadToEndAsync();
                            throw new Exception($"E-posta dosyaları geri yükleme hatası: {err}");
                        }
                    }
                }
            }

            SystemLogQueue.Log("info", $"[Geri Yükleme] İşlem başarıyla tamamlandı: {folderName} ({type})");

            // Audit Log
            await _auditLogService.LogAsync(userId, "BackupRestored", "Backup", Guid.Empty, JsonSerializer.Serialize(new { folder = folderName, type = type }), "localhost", "System/Web");
        }
        catch (Exception ex)
        {
            SystemLogQueue.Log("error", $"[Geri Yükleme] Hata oluştu: {ex.Message}");
            throw;
        }
    }

    public async Task DeleteBackupAsync(Guid userId, string folderName)
    {
        var backupsDir = GetBackupsPath();
        var targetBackupDir = Path.Combine(backupsDir, folderName);

        if (Directory.Exists(targetBackupDir))
        {
            Directory.Delete(targetBackupDir, true);
            SystemLogQueue.Log("info", $"[Yedekleme] Yedek silindi: {folderName}");

            // Audit Log
            await _auditLogService.LogAsync(userId, "BackupDeleted", "Backup", Guid.Empty, JsonSerializer.Serialize(new { folder = folderName }), "localhost", "System/Web");
            NotifyBackupUpdated();
        }
    }

    public async Task<Stream> DownloadBackupFileAsync(Guid userId, string folderName, string type)
    {
        var backupsDir = GetBackupsPath();
        var targetBackupDir = Path.Combine(backupsDir, folderName);

        if (!Directory.Exists(targetBackupDir))
        {
            throw new FileNotFoundException("Belirtilen yedek klasörü bulunamadı!");
        }

        var filename = type switch
        {
            "database" => "database.sql.gz",
            "projects" => "projects.tar.gz",
            "nginx" => "nginx.tar.gz",
            "mail" => "mail.tar.gz",
            _ => throw new ArgumentException("Geçersiz yedek dosya tipi!")
        };

        var filePath = Path.Combine(targetBackupDir, filename);
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"İstenen yedek dosyası ({filename}) bulunamadı!");
        }

        // Audit Log
        await _auditLogService.LogAsync(userId, "BackupDownloaded", "Backup", Guid.Empty, JsonSerializer.Serialize(new { folder = folderName, type = type }), "localhost", "System/Web");

        return File.OpenRead(filePath);
    }

    private async Task CleanOldBackupsAsync()
    {
        var backupsDir = GetBackupsPath();
        if (!Directory.Exists(backupsDir)) return;

        var directories = Directory.GetDirectories(backupsDir, "backup_*");
        var thresholdDate = DateTimeOffset.UtcNow.AddDays(-7);

        foreach (var dir in directories)
        {
            var folderName = Path.GetFileName(dir);
            var parts = folderName.Split('_');
            if (parts.Length >= 3 && DateTimeOffset.TryParse($"{parts[1]} {parts[2].Replace('-', ':')}", out var parsedDate))
            {
                if (parsedDate < thresholdDate)
                {
                    Directory.Delete(dir, true);
                    SystemLogQueue.Log("info", $"[Temizlik] 7 günden eski yedek otomatik temizlendi: {folderName}");
                }
            }
        }
        await Task.CompletedTask;
    }

    private string GetRemoteSettingsPath()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var winPath = Path.Combine(AppContext.BaseDirectory, "opt_dockerpanel", "remote_backup.json");
            var dir = Path.GetDirectoryName(winPath);
            if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            return winPath;
        }
        var linuxDir = "/opt/dockerpanel";
        if (!Directory.Exists(linuxDir)) Directory.CreateDirectory(linuxDir);
        return Path.Combine(linuxDir, "remote_backup.json");
    }

    public async Task<RemoteBackupSettingsDto> GetRemoteBackupSettingsAsync()
    {
        var filePath = GetRemoteSettingsPath();
        if (!File.Exists(filePath))
        {
            return new RemoteBackupSettingsDto();
        }
        try
        {
            var json = await File.ReadAllTextAsync(filePath);
            var settings = JsonSerializer.Deserialize<RemoteBackupSettingsDto>(json);
            return settings ?? new RemoteBackupSettingsDto();
        }
        catch
        {
            return new RemoteBackupSettingsDto();
        }
    }

    public async Task SaveRemoteBackupSettingsAsync(RemoteBackupSettingsDto settings)
    {
        if (settings.AuthType == "key" && !string.IsNullOrWhiteSpace(settings.KeyContent))
        {
            // If they provided custom key content, save it to the default key path
            var privateKeyPath = "/opt/dockerpanel/remote_id_rsa";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                privateKeyPath = Path.Combine(AppContext.BaseDirectory, "opt_dockerpanel", "remote_id_rsa");
                var dir = Path.GetDirectoryName(privateKeyPath);
                if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            }

            await File.WriteAllTextAsync(privateKeyPath, settings.KeyContent.Trim() + "\n");

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var chmodPsi = new ProcessStartInfo
                {
                    FileName = "chmod",
                    Arguments = $"600 {privateKeyPath}",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var chmodProc = Process.Start(chmodPsi);
                if (chmodProc != null)
                {
                    await chmodProc.WaitForExitAsync();
                }
            }
        }

        var filePath = GetRemoteSettingsPath();
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(filePath, json);
    }

    public async Task<string> GetSshPublicKeyAsync()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return "ssh-rsa AAAAB3NzaC1yc2EAAAADAQABAAACAQDc8YpE4N1... (Windows Geliştirme Simülasyonu)";
        }

        var privateKeyPath = "/opt/dockerpanel/remote_id_rsa";
        var publicKeyPath = "/opt/dockerpanel/remote_id_rsa.pub";

        try
        {
            // Ensure parent directory exists
            var dir = Path.GetDirectoryName(privateKeyPath);
            if (dir != null && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            if (!File.Exists(privateKeyPath))
            {
                // Generate key pair directly without bash wrapping to prevent shell quote escaping bugs
                var psi = new ProcessStartInfo
                {
                    FileName = "ssh-keygen",
                    Arguments = $"-t rsa -b 4096 -N \"\" -f {privateKeyPath}",
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var process = Process.Start(psi);
                if (process != null)
                {
                    await process.WaitForExitAsync();
                    if (process.ExitCode != 0)
                    {
                        var stderr = await process.StandardError.ReadToEndAsync();
                        throw new Exception($"ssh-keygen başarısız oldu (Çıkış Kodu: {process.ExitCode}). Detay: {stderr.Trim()}");
                    }
                }

                // Set private key permissions to 600 directly
                if (File.Exists(privateKeyPath))
                {
                    var chmodPsi = new ProcessStartInfo
                    {
                        FileName = "chmod",
                        Arguments = $"600 {privateKeyPath}",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using var chmodProc = Process.Start(chmodPsi);
                    if (chmodProc != null)
                    {
                        await chmodProc.WaitForExitAsync();
                    }
                }
            }

            if (File.Exists(publicKeyPath))
            {
                return await File.ReadAllTextAsync(publicKeyPath);
            }
            else
            {
                return "Hata: SSH Genel Anahtarı (.pub) dosyası sunucuda oluşturulamadı veya bulunamadı.";
            }
        }
        catch (Exception ex)
        {
            SystemLogQueue.Log("error", $"[SSH Keygen] Anahtar çifti üretilemedi: {ex.Message}");
            return $"Hata: SSH anahtarı üretilemedi. Detay: {ex.Message}";
        }
    }

    public async Task<(bool Success, string Message)> TestSshConnectionAsync(RemoteBackupSettingsDto settings)
    {
        var host = settings.Host;
        var portStr = settings.Port;
        var user = settings.User;
        var authType = settings.AuthType;

        if (string.IsNullOrWhiteSpace(host))
        {
            return (false, "Uzak sunucu IP adresi boş olamaz.");
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            await Task.Delay(1500);
            return (true, "Bağlantı Başarılı! (Windows Geliştirme Simülasyonu)");
        }

        string sshCmd;
        string? tempKeyPath = null;

        try
        {
            if (authType == "password")
            {
                // Check if sshpass is installed
                var checkSshpassPsi = new ProcessStartInfo
                {
                    FileName = "bash",
                    Arguments = "-c \"which sshpass\"",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var checkProc = Process.Start(checkSshpassPsi);
                bool sshpassExists = false;
                if (checkProc != null)
                {
                    await checkProc.WaitForExitAsync();
                    sshpassExists = checkProc.ExitCode == 0;
                }

                if (!sshpassExists)
                {
                    return (false, "Şifre tabanlı bağlantı testi başarısız: Sunucuda 'sshpass' kurulu değil. Lütfen 'sudo apt-get install -y sshpass' komutu ile sunucunuza sshpass yükleyin ya da güvenli olan 'SSH Anahtarı' yöntemini kullanın.");
                }

                sshCmd = $"sshpass -p '{settings.Password}' ssh -p {portStr} -o StrictHostKeyChecking=no -o ConnectTimeout=5 {user}@{host} \"echo 'OK'\"";
            }
            else
            {
                // Key auth
                var keyPath = settings.KeyPath;

                // If they pasted a custom private key content in the form but didn't save yet, we test with it
                if (!string.IsNullOrWhiteSpace(settings.KeyContent))
                {
                    tempKeyPath = Path.Combine(Path.GetTempPath(), $"temp_ssh_key_{Guid.NewGuid()}");
                    await File.WriteAllTextAsync(tempKeyPath, settings.KeyContent.Trim() + "\n");
                    
                    // set permission to 600
                    var chmodPsi = new ProcessStartInfo
                    {
                        FileName = "bash",
                        Arguments = $"-c \"chmod 600 {tempKeyPath}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using var chmodProc = Process.Start(chmodPsi);
                    if (chmodProc != null) await chmodProc.WaitForExitAsync();
                    
                    keyPath = tempKeyPath;
                }
                else
                {
                    // If KeyPath doesn't exist but is the default, generate it
                    if (!File.Exists(keyPath) && keyPath == "/opt/dockerpanel/remote_id_rsa")
                    {
                        await GetSshPublicKeyAsync();
                    }

                    if (File.Exists(keyPath))
                      {
                        // Ensure 600
                        var chmodPsi = new ProcessStartInfo
                        {
                            FileName = "bash",
                            Arguments = $"-c \"chmod 600 {keyPath}\"",
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        using var chmodProc = Process.Start(chmodPsi);
                        if (chmodProc != null) await chmodProc.WaitForExitAsync();
                    }
                }

                if (!File.Exists(keyPath))
                {
                    return (false, $"SSH Özel Anahtar dosyası bulunamadı: {keyPath}");
                }

                sshCmd = $"ssh -p {portStr} -i {keyPath} -o StrictHostKeyChecking=no -o ConnectTimeout=5 {user}@{host} \"echo 'OK'\"";
            }

            var psi = new ProcessStartInfo
            {
                FileName = "bash",
                Arguments = $"-c \"{sshCmd}\"",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process != null)
            {
                await process.WaitForExitAsync();
                if (process.ExitCode == 0)
                {
                    return (true, "Bağlantı Başarılı! Uzak sunucuya SSH üzerinden başarıyla erişildi.");
                }
                else
                {
                    var err = await process.StandardError.ReadToEndAsync();
                    if (string.IsNullOrWhiteSpace(err))
                    {
                        err = await process.StandardOutput.ReadToEndAsync();
                    }
                    return (false, $"Bağlantı Hatası (Exit Code: {process.ExitCode}): {err}");
                }
            }

            return (false, "SSH test süreci başlatılamadı.");
        }
        catch (Exception ex)
        {
            return (false, $"Bağlantı testi sırasında beklenmeyen hata oluştu: {ex.Message}");
        }
        finally
        {
            // Clean up temp key
            if (tempKeyPath != null && File.Exists(tempKeyPath))
            {
                try { File.Delete(tempKeyPath); } catch { }
            }
        }
    }

    private async Task SyncToRemoteVdsAsync(string folderName, RemoteBackupSettingsDto settings)
    {
        var host = settings.Host;
        var portStr = settings.Port;
        var user = settings.User;
        var remotePath = settings.RemotePath;
        var keyPath = settings.KeyPath;

        if (string.IsNullOrEmpty(host)) return;

        var localBackupDir = Path.Combine(GetBackupsPath(), folderName);

        SystemLogQueue.Log("info", $"[Uzak Yedekleme] Yedek {host} sunucusuna aktarılıyor: {folderName}...");

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            SystemLogQueue.Log("info", $"[Windows Simülasyonu] Uzak sunucuya ({host}) yedekleme simüle edildi.");
            await Task.Delay(1000);
            return;
        }

        // We use either sshpass (password auth) or key auth
        string scpCmd;
        if (settings.AuthType == "password")
        {
            // Check if sshpass is installed
            var checkSshpassPsi = new ProcessStartInfo
            {
                FileName = "bash",
                Arguments = "-c \"which sshpass\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var checkProc = Process.Start(checkSshpassPsi);
            bool sshpassExists = false;
            if (checkProc != null)
            {
                await checkProc.WaitForExitAsync();
                sshpassExists = checkProc.ExitCode == 0;
            }

            if (!sshpassExists)
            {
                throw new Exception("Şifre tabanlı transfer başarısız: Sunucuda 'sshpass' kurulu değil. Lütfen 'sudo apt-get install -y sshpass' komutu ile sshpass yükleyin ya da güvenli olan SSH Anahtarı yöntemini kullanın.");
            }

            scpCmd = $"sshpass -p '{settings.Password}' scp -P {portStr} -o StrictHostKeyChecking=no -r {localBackupDir} {user}@{host}:{remotePath}";
            SystemLogQueue.Log("info", $"[Uzak Yedekleme] Şifre kullanarak aktarım başlatılıyor (scp + sshpass)...");
        }
        else
        {
            // Key auth
            // Ensure key path is correct and exists
            if (!File.Exists(keyPath))
            {
                // Generate panel key automatically if it's the default path
                if (keyPath == "/opt/dockerpanel/remote_id_rsa")
                {
                    await GetSshPublicKeyAsync();
                }
            }

            // Double check chmod on the key path
            if (File.Exists(keyPath))
            {
                var chmodCmd = $"chmod 600 {keyPath}";
                var chmodPsi = new ProcessStartInfo
                {
                    FileName = "bash",
                    Arguments = $"-c \"{chmodCmd}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var chmodProc = Process.Start(chmodPsi);
                if (chmodProc != null)
                {
                    await chmodProc.WaitForExitAsync();
                }
            }

            scpCmd = $"scp -P {portStr} -i {keyPath} -o StrictHostKeyChecking=no -r {localBackupDir} {user}@{host}:{remotePath}";
            SystemLogQueue.Log("info", $"[Uzak Yedekleme] SSH anahtarı kullanarak aktarım başlatılıyor: scp -P {portStr} -i [KEY] -r {localBackupDir} {user}@{host}:{remotePath}");
        }

        // --- Adım 1: Uzak sunucuda hedef klasörü oluştur (yoksa) ---
        SystemLogQueue.Log("info", $"[Uzak Yedekleme] Uzak sunucuda hedef klasör kontrol ediliyor / oluşturuluyor: {remotePath}");
        string mkdirSshCmd;
        if (settings.AuthType == "password")
        {
            mkdirSshCmd = $"sshpass -p '{settings.Password}' ssh -p {portStr} -o StrictHostKeyChecking=no {user}@{host} \"mkdir -p {remotePath}\"";
        }
        else
        {
            mkdirSshCmd = $"ssh -p {portStr} -i {keyPath} -o StrictHostKeyChecking=no {user}@{host} \"mkdir -p {remotePath}\"";
        }

        var mkdirPsi = new ProcessStartInfo
        {
            FileName = "bash",
            Arguments = $"-c \"{mkdirSshCmd}\"",
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var mkdirProc = Process.Start(mkdirPsi);
        if (mkdirProc != null)
        {
            await mkdirProc.WaitForExitAsync();
            if (mkdirProc.ExitCode != 0)
            {
                var mkdirErr = await mkdirProc.StandardError.ReadToEndAsync();
                throw new Exception($"Uzak klasör oluşturulamadı (ExitCode: {mkdirProc.ExitCode}): {mkdirErr}");
            }
            SystemLogQueue.Log("info", $"[Uzak Yedekleme] Hedef klasör hazır: {user}@{host}:{remotePath}");
        }

        // --- Adım 2: SCP ile yedeği gönder ---
        var psi = new ProcessStartInfo
        {
            FileName = "bash",
            Arguments = $"-c \"{scpCmd}\"",
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process != null)
        {
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
            {
                var err = await process.StandardError.ReadToEndAsync();
                throw new Exception($"SCP transfer hatası (ExitCode: {process.ExitCode}): {err}");
            }
            SystemLogQueue.Log("info", $"[Uzak Yedekleme] Yedek başarıyla uzak sunucuya ({host}) kopyalandı!");
        }
    }

    private string GetFriendlyFileSize(string filePath)
    {
        if (!File.Exists(filePath)) return "0 KB";
        var length = new FileInfo(filePath).Length;
        return FormatByteCount(length);
    }

    private string FormatByteCount(long bytes)
    {
        string[] suffix = { "B", "KB", "MB", "GB", "TB" };
        int index = 0;
        double doubleBytes = bytes;
        while (doubleBytes >= 1024 && index < suffix.Length - 1)
        {
            doubleBytes /= 1024;
            index++;
        }
        return $"{doubleBytes:0.0} {suffix[index]}";
    }
}
