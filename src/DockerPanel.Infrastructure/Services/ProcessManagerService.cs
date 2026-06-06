using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DockerPanel.Domain.Interfaces;
using DockerPanel.Domain.Entities;
using DockerPanel.Domain.Security;

namespace DockerPanel.Infrastructure.Services;

public class ProcessManagerService : IProcessManagerService
{
    private static readonly SemaphoreSlim FileLock = new SemaphoreSlim(1, 1);
    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(false);
    private const string PreferredProjectsRoot = "/opt/dockerpanel/projects";
    private const string LegacyProjectsRoot = "/var/www";
    
    private string GetConfigPath()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            string winDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "project-manager");
            Directory.CreateDirectory(winDir);
            return Path.Combine(winDir, "projects.conf");
        }
        return "/etc/project-manager/projects.conf";
    }

    public class ProcessConfigEntry
    {
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string StartCommand { get; set; } = string.Empty;
        public string UserName { get; set; } = string.Empty;
    }

    private static string? TryFindProjectPathByCommand(string rootPath, string startCommand)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || !Directory.Exists(rootPath))
        {
            return null;
        }

        var match = Regex.Match(startCommand, @"dotnet\s+(""?)(?<dll>[^""\s]+\.dll)\1", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return null;
        }

        var dllName = Path.GetFileName(match.Groups["dll"].Value);
        try
        {
            return Directory
                .EnumerateFiles(rootPath, dllName, SearchOption.AllDirectories)
                .Select(Path.GetDirectoryName)
                .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
        }
        catch (Exception ex)
        {
            SystemLogQueue.Log("warning", $"[ProcessManager] {rootPath} altında proje dizini aranırken hata oluştu: {ex.Message}");
            return null;
        }
    }

    private string DetectStartCommand(string targetPath, string name, int port)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return $"dotnet {name}.dll --urls http://localhost:{port}";
        }

        if (!Directory.Exists(targetPath))
        {
            return $"dotnet {name}.dll --urls http://localhost:{port}";
        }

        try
        {
            // Search for all .dll files in the directory
            var dllFiles = Directory.GetFiles(targetPath, "*.dll");
            string? mainDll = null;

            foreach (var dll in dllFiles)
            {
                string filename = Path.GetFileName(dll);
                // Ignore standard system/third-party dlls
                if (filename.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase) ||
                    filename.StartsWith("System.", StringComparison.OrdinalIgnoreCase) ||
                    filename.StartsWith("Newtonsoft.", StringComparison.OrdinalIgnoreCase) ||
                    filename.StartsWith("AspNet.", StringComparison.OrdinalIgnoreCase) ||
                    filename.StartsWith("MudBlazor.", StringComparison.OrdinalIgnoreCase) ||
                    filename.StartsWith("EntityFramework.", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                
                // If a DLL has a matching runtimeconfig.json, it's definitely the main runnable entry dll!
                string configJson = Path.ChangeExtension(dll, ".runtimeconfig.json");
                if (File.Exists(configJson))
                {
                    mainDll = filename;
                    break;
                }
            }

            if (mainDll == null && dllFiles.Length > 0)
            {
                // Fallback to first non-system DLL
                foreach (var dll in dllFiles)
                {
                    string filename = Path.GetFileName(dll);
                    if (!filename.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase) &&
                        !filename.StartsWith("System.", StringComparison.OrdinalIgnoreCase))
                    {
                        mainDll = filename;
                        break;
                    }
                }
            }

            if (mainDll != null)
            {
                return $"dotnet {mainDll} --urls http://localhost:{port}";
            }
        }
        catch (Exception ex)
        {
            SystemLogQueue.Log("warning", $"[ProcessManager] Dll aranırken hata oluştu: {ex.Message}");
        }

        return $"dotnet {name}.dll --urls http://localhost:{port}";
    }

    private string DetectNodeEntryFile(string targetPath)
    {
        if (!Directory.Exists(targetPath))
        {
            return "server.js";
        }

        try
        {
            // 1. Check package.json
            string pkgPath = Path.Combine(targetPath, "package.json");
            if (File.Exists(pkgPath))
            {
                string content = File.ReadAllText(pkgPath);
                using var doc = System.Text.Json.JsonDocument.Parse(content);
                if (doc.RootElement.TryGetProperty("main", out var mainProp))
                {
                    string? mainVal = mainProp.GetString();
                    if (!string.IsNullOrWhiteSpace(mainVal))
                    {
                        return mainVal;
                    }
                }
            }

            // 2. Check common entry files
            string[] commonEntries = { "index.js", "app.js", "server.js", "main.js" };
            foreach (var entry in commonEntries)
            {
                if (File.Exists(Path.Combine(targetPath, entry)))
                {
                    return entry;
                }
            }
        }
        catch (Exception ex)
        {
            SystemLogQueue.Log("warning", $"[ProcessManager] Node entry tespiti esnasında hata: {ex.Message}");
        }

        return "server.js"; // Fallback
    }

    private async Task<List<ProcessConfigEntry>> ParsePipeConfigAsync()
    {
        var entries = new List<ProcessConfigEntry>();
        string path = GetConfigPath();
        
        if (!File.Exists(path)) return entries;

        await FileLock.WaitAsync();
        try
        {
            var lines = await File.ReadAllLinesAsync(path, Utf8WithoutBom);
            foreach (var line in lines)
            {
                var trimmed = line.Trim().TrimStart('\uFEFF');
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#") || trimmed.StartsWith(";")) continue;

                var parts = trimmed.Split('|');
                if (parts.Length >= 2)
                {
                    entries.Add(new ProcessConfigEntry
                    {
                        Name = parts[0].Trim(),
                        Path = parts[1].Trim(),
                        StartCommand = parts.Length >= 3 ? parts[2].Trim() : string.Empty,
                        UserName = parts.Length >= 4 ? parts[3].Trim() : "root"
                    });
                }
            }
        }
        finally
        {
            FileLock.Release();
        }

        return entries;
    }

    private async Task SavePipeConfigAsync(List<ProcessConfigEntry> entries)
    {
        string path = GetConfigPath();
        var sb = new StringBuilder();

        foreach (var entry in entries)
        {
            sb.AppendLine($"{entry.Name}|{entry.Path}|{entry.StartCommand}|{entry.UserName}");
        }

        await FileLock.WaitAsync();
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (dir != null && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            await File.WriteAllTextAsync(path, sb.ToString(), Utf8WithoutBom);
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task AddOrUpdateProcessConfigAsync(string name, int port, string? runtimeType = null, string? entryFile = null, string? customCommand = null)
    {
        InputValidator.ThrowIfInvalidProjectName(name, "Geçersiz proje ismi formatı!");
        InputValidator.ThrowIfUnsafePath(entryFile, "Geçersiz veya güvensiz giriş dosyası yolu!");

        SystemLogQueue.Log("info", $"[ProcessManager] projects.conf yapılandırma dosyası güncelleniyor: Proje={name}");
        
        string startCommand;
        var entries = await ParsePipeConfigAsync();
        var existing = entries.FirstOrDefault(e => e.Name.TrimStart('\uFEFF').Equals(name, StringComparison.OrdinalIgnoreCase));

        var preferredPath = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dockerpanel_projects", name)
            : $"{PreferredProjectsRoot}/{name}";

        var legacyPath = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? preferredPath
            : $"{LegacyProjectsRoot}/{name}";

        var existingPath = existing?.Path.TrimStart('\uFEFF') ?? string.Empty;
        var targetPath = preferredPath;

        if (!Directory.Exists(preferredPath))
        {
            if (!string.IsNullOrWhiteSpace(existingPath) && Directory.Exists(existingPath))
            {
                targetPath = existingPath;
            }
            else if (existing != null)
            {
                targetPath = TryFindProjectPathByCommand(LegacyProjectsRoot, existing.StartCommand) ?? targetPath;
            }
            else if (Directory.Exists(legacyPath))
            {
                targetPath = legacyPath;
            }
        }

        if (!string.IsNullOrWhiteSpace(customCommand))
        {
            startCommand = customCommand;
            // Prepend env if it starts with environment variable assignments to prevent exec syntax error
            if (customCommand.Contains('=') && !customCommand.TrimStart().StartsWith("env ", StringComparison.OrdinalIgnoreCase))
            {
                var firstToken = customCommand.TrimStart().Split(' ')[0];
                if (firstToken.Contains('=') && !firstToken.StartsWith("/") && !firstToken.StartsWith("./"))
                {
                    startCommand = "env " + customCommand;
                }
            }
        }
        else if (!string.IsNullOrWhiteSpace(runtimeType))
        {
            string cleanRuntime = runtimeType.ToLowerInvariant();
            string actualEntry = !string.IsNullOrWhiteSpace(entryFile) ? entryFile : string.Empty;

            if (cleanRuntime.Contains("dotnet") || cleanRuntime.Contains("c#") || cleanRuntime.Contains(".net"))
            {
                if (string.IsNullOrWhiteSpace(actualEntry))
                {
                    string detectedCommand = DetectStartCommand(targetPath, name, port);
                    var match = Regex.Match(detectedCommand, @"dotnet\s+(?<dll>[^\s]+\.dll)", RegexOptions.IgnoreCase);
                    if (match.Success)
                    {
                        actualEntry = match.Groups["dll"].Value;
                    }
                    else
                    {
                        actualEntry = $"{name}.dll";
                    }
                }

                actualEntry = actualEntry.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? actualEntry : actualEntry + ".dll";
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    startCommand = $"dotnet {actualEntry} --urls http://localhost:{port}";
                }
                else
                {
                    startCommand = $"dotnet {actualEntry} --urls http://0.0.0.0:{port}";
                }
            }
            else if (cleanRuntime.Contains("node"))
            {
                actualEntry = !string.IsNullOrWhiteSpace(entryFile) ? entryFile : string.Empty;
                if (string.IsNullOrWhiteSpace(actualEntry))
                {
                    actualEntry = DetectNodeEntryFile(targetPath);
                }
                startCommand = $"env PORT={port} node {actualEntry}";
            }
            else if (cleanRuntime.Contains("python"))
            {
                actualEntry = !string.IsNullOrWhiteSpace(entryFile) ? entryFile : "app.py";
                startCommand = $"env PORT={port} python {actualEntry}";
            }
            else
            {
                startCommand = DetectStartCommand(targetPath, name, port);
            }
        }
        else
        {
            startCommand = DetectStartCommand(targetPath, name, port);
        }

        string userName = "root";

        if (existing != null)
        {
            existing.Path = targetPath;
            if (!string.IsNullOrWhiteSpace(customCommand) ||
                !string.IsNullOrWhiteSpace(runtimeType) ||
                !string.IsNullOrWhiteSpace(entryFile) ||
                string.IsNullOrWhiteSpace(existing.StartCommand))
            {
                existing.StartCommand = startCommand;
            }
            existing.UserName = userName;
        }
        else
        {
            entries.Add(new ProcessConfigEntry
            {
                Name = name,
                Path = targetPath,
                StartCommand = startCommand,
                UserName = userName
            });
        }

        SystemLogQueue.Log("info", $"$ echo \"{name}|{targetPath}|{startCommand}|{userName}\" >> /etc/project-manager/projects.conf");
        await SavePipeConfigAsync(entries);
        SystemLogQueue.Log("info", $"[ProcessManager] projects.conf yapılandırması başarıyla diske yazıldı.");
    }

    public async Task DeleteProcessConfigAsync(string name)
    {
        SystemLogQueue.Log("warning", $"[ProcessManager] Proje yapılandırması siliniyor: Proje={name}");

        // Execute the bash deletion script with sudo to cleanly stop the process, remove PID and delete root-owned folders
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                await ExecuteCommandAsync("sudo", $"-n /usr/local/bin/project-manager.sh delete {name}", 30000);
            }
            catch (Exception ex)
            {
                SystemLogQueue.Log("warning", $"[ProcessManager] project-manager.sh delete komutu hata verdi: {ex.Message}");
            }
        }

        var entries = await ParsePipeConfigAsync();
        var toRemove = entries.FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (toRemove != null)
        {
            entries.Remove(toRemove);
            SystemLogQueue.Log("info", $"[ProcessManager] /etc/project-manager/projects.conf dosyasından [{name}] bölümü kaldırıldı.");
            await SavePipeConfigAsync(entries);
        }
    }

    private async Task ExecuteCommandAsync(string command, string args, int timeoutMs = 30000, string? workingDirectory = null)
    {
        SystemLogQueue.Log("info", $"$ {command} {args} (in {workingDirectory ?? "current dir"})");
        var psi = new ProcessStartInfo
        {
            FileName = command,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            psi.Environment["HOME"] = "/home/dockerpanel_api";
        }
        if (!string.IsNullOrEmpty(workingDirectory))
        {
            psi.WorkingDirectory = workingDirectory;
        }

        using var process = new Process { StartInfo = psi };
        process.Start();

        var timeoutTask = Task.Delay(timeoutMs);
        var runTask = Task.Run(() => process.WaitForExit());

        if (await Task.WhenAny(runTask, timeoutTask) == timeoutTask)
        {
            try { process.Kill(); } catch { }
            SystemLogQueue.Log("error", $"[Süreç Hatası] Komut zaman aşımına uğradı!");
            throw new Exception($"Süreç zaman aşımına uğradı: {command} {args}");
        }
        if (process.ExitCode != 0)
        {
            string err = await process.StandardError.ReadToEndAsync();
            string outStr = await process.StandardOutput.ReadToEndAsync();
            string fullErr = string.IsNullOrWhiteSpace(err) ? outStr : err;
            if (string.IsNullOrWhiteSpace(fullErr))
            {
                fullErr = $"Çıkış kodu {process.ExitCode} ile sonlandı (stdout/stderr boş).";
            }
            SystemLogQueue.Log("error", $"[Süreç Hatası] Komut başarısız oldu (Çıkış Kodu: {process.ExitCode}): {fullErr}");
            throw new Exception($"Süreç hatası (Kod: {process.ExitCode}): {fullErr}");
        }
        SystemLogQueue.Log("info", $"[Süreç] Komut başarıyla yürütüldü (Çıkış Kodu: 0).");
    }

    public async Task RestartProcessAsync(string name)
    {
        InputValidator.ThrowIfInvalidProjectName(name, "Geçersiz proje ismi!");

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            SystemLogQueue.Log("info", $"$ [Windows Simülasyonu] restart-process {name}");
            await Task.Delay(1000);
            SystemLogQueue.Log("info", $"[ProcessManager] {name} native süreci başarıyla yeniden başlatıldı.");
            return;
        }

        try
        {
            await ExecuteCommandAsync("sudo", $"-n /usr/local/bin/project-manager.sh restart {name}", 45000);
            SystemLogQueue.Log("info", $"[ProcessManager] {name} native süreci başarıyla yeniden başlatıldı.");
        }
        catch (Exception ex)
        {
            string logPath = $"/var/log/project-manager/{name}.log";
            if (File.Exists(logPath))
            {
                try
                {
                    var logLines = new List<string>();
                    using (var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var reader = new StreamReader(fs))
                    {
                        string? line;
                        while ((line = await reader.ReadLineAsync()) != null)
                        {
                            logLines.Add(line);
                            if (logLines.Count > 15) logLines.RemoveAt(0);
                        }
                    }
                    if (logLines.Count > 0)
                    {
                        string details = string.Join("\n", logLines);
                        throw new Exception($"{ex.Message}\nUygulama Hata Logları:\n{details}", ex);
                    }
                }
                catch { /* Ignore log reading errors to preserve original exception */ }
            }
            throw;
        }
    }

    public async Task RestartAllProcessesAsync()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            SystemLogQueue.Log("info", $"$ [Windows Simülasyonu] restart-all-processes");
            await Task.Delay(1000);
            SystemLogQueue.Log("info", $"[ProcessManager] Tüm native süreçler başarıyla yeniden başlatıldı.");
            return;
        }

        await ExecuteCommandAsync("sudo", "-n /usr/local/bin/project-manager.sh restart", 120000);
        SystemLogQueue.Log("info", $"[ProcessManager] Tüm native süreçler başarıyla yeniden başlatıldı.");
    }

    public async Task StopProcessAsync(string name)
    {
        InputValidator.ThrowIfInvalidProjectName(name, "Geçersiz proje ismi!");

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            SystemLogQueue.Log("warning", $"$ [Windows Simülasyonu] stop-process {name}");
            await Task.Delay(1000);
            SystemLogQueue.Log("info", $"[ProcessManager] {name} native süreci başarıyla durduruldu.");
            return;
        }

        await ExecuteCommandAsync("sudo", $"-n /usr/local/bin/project-manager.sh stop {name}", 45000);
        SystemLogQueue.Log("info", $"[ProcessManager] {name} native süreci başarıyla durduruldu.");
    }

    public async Task StartProcessAsync(string name)
    {
        InputValidator.ThrowIfInvalidProjectName(name, "Geçersiz proje ismi!");

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            SystemLogQueue.Log("info", $"$ [Windows Simülasyonu] start-process {name}");
            await Task.Delay(1000);
            SystemLogQueue.Log("info", $"[ProcessManager] {name} native süreci başarıyla başlatıldı.");
            return;
        }

        try
        {
            await ExecuteCommandAsync("sudo", $"-n /usr/local/bin/project-manager.sh start {name}", 45000);
            SystemLogQueue.Log("info", $"[ProcessManager] {name} native süreci başarıyla başlatıldı.");
        }
        catch (Exception ex)
        {
            string logPath = $"/var/log/project-manager/{name}.log";
            if (File.Exists(logPath))
            {
                try
                {
                    var logLines = new List<string>();
                    using (var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var reader = new StreamReader(fs))
                    {
                        string? line;
                        while ((line = await reader.ReadLineAsync()) != null)
                        {
                            logLines.Add(line);
                            if (logLines.Count > 15) logLines.RemoveAt(0);
                        }
                    }
                    if (logLines.Count > 0)
                    {
                        string details = string.Join("\n", logLines);
                        throw new Exception($"{ex.Message}\nUygulama Hata Logları:\n{details}", ex);
                    }
                }
                catch { /* Ignore log reading errors to preserve original exception */ }
            }
            throw;
        }
    }

    public async Task<IEnumerable<string>> GetProcessLogsAsync(string name, int tailLines = 100)
    {
        InputValidator.ThrowIfInvalidProjectName(name, "Geçersiz proje ismi!");

        // Native log dosyası /var/log/project-manager/[name].log dizinindedir
        string logPath = $"/var/log/project-manager/{name}.log";

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "project-manager", $"{name}.log");
            if (!File.Exists(logPath))
            {
                // Simüle log akışı üretelim
                return new[]
                {
                    $"[Native Simulation]: {name} native süreci başarıyla başlatıldı.",
                    $"[Native Simulation]: Node.js API sunucusu port 3000 üzerinde dinleniyor...",
                    $"[Native Simulation]: Veritabanı bağlantı havuzu başarıyla yüklendi."
                };
            }
        }

        if (!File.Exists(logPath))
        {
            return new[] { $"Henüz log satırı bulunmamaktadır: {logPath}" };
        }

        var lines = new List<string>();
        using (var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(fs))
        {
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                lines.Add(line);
                if (lines.Count > tailLines)
                {
                    lines.RemoveAt(0);
                }
            }
        }
        return lines;
    }

    public async Task<bool> IsProcessRunningAsync(string name)
    {
        if (!InputValidator.IsProjectName(name)) return false;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            SystemLogQueue.Log("info", $"$ [Windows Simülasyonu] check-process {name}");
            return true;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "sudo",
                Arguments = $"-n /usr/local/bin/project-manager.sh status {name}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
 
            using var process = new Process { StartInfo = psi };
            process.Start();
 
            var waitTask = process.WaitForExitAsync();
            if (await Task.WhenAny(waitTask, Task.Delay(10000)) != waitTask)
            {
                try { process.Kill(); } catch { }
                SystemLogQueue.Log("warning", $"[ProcessManager] {name} durum kontrolü zaman aşımına uğradı.");
                return false;
            }
 
            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            var statusText = $"{output}\n{error}";
            var statusLines = statusText
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim().TrimStart('\uFEFF'))
                .ToList();
 
            var exactStatusLine = statusLines.FirstOrDefault(line =>
            {
                var parts = line.Split('|', 3);
                return parts.Length >= 2 && parts[0].Trim().Equals(name, StringComparison.OrdinalIgnoreCase);
            });
 
            if (!string.IsNullOrWhiteSpace(exactStatusLine))
            {
                var state = exactStatusLine.Split('|', 3)[1].Trim();
                if (state.Equals("Running", StringComparison.OrdinalIgnoreCase)) return true;
                if (state.Equals("Stopped", StringComparison.OrdinalIgnoreCase)) return false;
            }
 
            var statusLine = statusLines
                .FirstOrDefault(line => line.Contains(name, StringComparison.OrdinalIgnoreCase));
 
            if (!string.IsNullOrWhiteSpace(statusLine))
            {
                if (statusLine.Contains("Running", StringComparison.OrdinalIgnoreCase)) return true;
                if (statusLine.Contains("Stopped", StringComparison.OrdinalIgnoreCase)) return false;
            }
 
            if (!statusText.Contains("Project Statuses", StringComparison.OrdinalIgnoreCase) &&
                statusText.Contains("Running", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
 
            if (statusText.Contains("Stopped", StringComparison.OrdinalIgnoreCase) ||
                statusText.Contains("No Sockets found", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
 
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            SystemLogQueue.Log("warning", $"[ProcessManager] Süreç durumu kontrol edilirken hata oluştu: {ex.Message}");
            return false;
        }
    }

    public async Task RestoreDependenciesAsync(string name, string path, string? runtimeType)
    {
        if (string.IsNullOrWhiteSpace(runtimeType)) return;
        string cleanRuntime = runtimeType.ToLowerInvariant();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            SystemLogQueue.Log("info", $"[Windows Simülasyonu] Bağımlılıklar geri yükleniyor: {name} ({cleanRuntime})");
            return;
        }

        try
        {
            if (cleanRuntime.Contains("node"))
            {
                if (File.Exists(Path.Combine(path, "package.json")))
                {
                    SystemLogQueue.Log("info", $"[ProcessManager] Node.js bağımlılıkları yükleniyor: npm install (Proje: {name})");
                    // Run npm install (give it a longer timeout, e.g., 2 minutes)
                    await ExecuteCommandAsync("npm", "install --no-audit --no-fund", 120000, path);
                }
            }
            else if (cleanRuntime.Contains("python"))
            {
                if (File.Exists(Path.Combine(path, "requirements.txt")))
                {
                    SystemLogQueue.Log("info", $"[ProcessManager] Python bağımlılıkları yükleniyor: pip install (Proje: {name})");
                    await ExecuteCommandAsync("pip", "install -r requirements.txt", 120000, path);
                }
            }
            else if (cleanRuntime.Contains("dotnet") || cleanRuntime.Contains("c#") || cleanRuntime.Contains(".net"))
            {
                SystemLogQueue.Log("info", $"[ProcessManager] .NET bağımlılıkları geri yükleniyor: dotnet restore (Proje: {name})");
                await ExecuteCommandAsync("dotnet", "restore", 120000, path);
            }
        }
        catch (Exception ex)
        {
            SystemLogQueue.Log("error", $"[ProcessManager] '{name}' bağımlılıkları yüklenirken hata oluştu: {ex.Message}");
            Console.WriteLine($"[ProcessManager Error] '{name}' bağımlılıkları yüklenirken hata oluştu: {ex.ToString()}");
            throw new Exception($"Bağımlılıklar yüklenemedi: {ex.Message}", ex);
        }
    }
}
