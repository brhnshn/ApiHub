using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using DockerPanel.Infrastructure.Data;
using DockerPanel.Domain.Interfaces;
using DockerPanel.Domain.Entities;
using DockerPanel.Domain.Enums;
using DockerPanel.Domain.Security;


namespace DockerPanel.Infrastructure.Services;

public class NginxProxyService : INginxService
{
    private readonly IServiceProvider _serviceProvider;

    public NginxProxyService(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }
    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(false);
    private const string TemplatePath = "/opt/dockerpanel/nginx-template.conf";
    private const string SitesAvailableDir = "/etc/nginx/sites-available";
    private const string SitesEnabledDir = "/etc/nginx/sites-enabled";

    private string ResolvePath(string targetPath)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var localPath = Path.Combine(AppContext.BaseDirectory, "opt_dockerpanel", targetPath.TrimStart('/'));
            var dir = Path.GetDirectoryName(localPath);
            if (dir != null && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            return localPath;
        }
        
        var targetDir = Path.GetDirectoryName(targetPath);
        if (targetDir != null && !Directory.Exists(targetDir))
        {
            Directory.CreateDirectory(targetDir);
        }
        return targetPath;
    }

    private async Task EnsureTemplateExistsAsync()
    {
        var resolvedTemplate = ResolvePath(TemplatePath);
        bool shouldWrite = !File.Exists(resolvedTemplate);
        if (!shouldWrite)
        {
            try
            {
                var currentContent = await File.ReadAllTextAsync(resolvedTemplate, Utf8WithoutBom);
                if (!currentContent.Contains("@backend_down"))
                {
                    shouldWrite = true;
                }
            }
            catch { }
        }

        if (shouldWrite)
        {
            var defaultTemplate = @"server {
    listen 80;
    server_name {{Subdomain}}.{{Domain}};

    location / {
        proxy_pass http://127.0.0.1:{{ContainerPort}};
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;

        # WebSockets desteği (SignalR akışı için)
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection ""upgrade"";
    }

    # Proje kapalıyken veya çökmüşken panel yönlendirmesini (SW cache trap) önleyen 502 sayfası
    error_page 502 503 504 @backend_down;
    location @backend_down {
        return 502 '<html><head><title>Uygulama Calismiyor</title><style>body{font-family:sans-serif;text-align:center;padding:100px 20px;background:#0e1511;color:#dde4dd}h1{font-size:36px;color:#ffb4ab}p{font-size:18px;color:#86948a}</style></head><body><h1>Uygulama Calismiyor (502 Bad Gateway)</h1><p>Bu proje su anda kapali veya baslatilamadi. Lutfen kontrol panelinden loglarini inceleyin.</p></body></html>';
        add_header Content-Type text/html;
    }
}";
            await File.WriteAllTextAsync(resolvedTemplate, defaultTemplate, Utf8WithoutBom);
        }
    }

    private async Task ExecuteCommandAsync(string command, string args, int timeoutMs = 10000)
    {
        var psi = new ProcessStartInfo
        {
            FileName = command,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        var timeoutTask = Task.Delay(timeoutMs);
        var runTask = Task.Run(() => process.WaitForExit());

        if (await Task.WhenAny(runTask, timeoutTask) == timeoutTask)
        {
            try { process.Kill(); } catch { }
            throw new Exception($"Nginx komut süreci zaman aşımına uğradı: {command} {args}");
        }

        if (process.ExitCode != 0)
        {
            string err = await process.StandardError.ReadToEndAsync();
            if (command == "sudo" &&
                (err.Contains("password is required", StringComparison.OrdinalIgnoreCase) ||
                 err.Contains("a terminal is required", StringComparison.OrdinalIgnoreCase) ||
                 err.Contains("not in the sudoers file", StringComparison.OrdinalIgnoreCase)))
            {
                var requiredCommand = args.StartsWith("-n ", StringComparison.OrdinalIgnoreCase)
                    ? args[3..]
                    : args;

                throw new Exception(
                    "Parolasiz sudo yetkisi eksik. Sunucuda dockerpanel-api servis kullanicisi icin " +
                    "/etc/sudoers.d/dockerpanel_api dosyasinda NOPASSWD yetkileri tanimli olmali. " +
                    $"Calistirilamayan komut: {requiredCommand}. Gerekli komutlar: " +
                    "/usr/sbin/nginx -t, /usr/sbin/systemctl reload nginx veya /usr/sbin/service nginx reload, /usr/bin/certbot *.");
            }
            throw new Exception($"Nginx komut hatası (Kod: {process.ExitCode}): {err}");
        }
    }

    private async Task ReloadNginxAsync()
    {
        var reloadCommands = new[]
        {
            "/usr/bin/systemctl reload nginx",
            "/bin/systemctl reload nginx",
            "/usr/sbin/systemctl reload nginx",
            "/usr/sbin/service nginx reload"
        };

        var errors = new List<string>();
        foreach (var reloadCommand in reloadCommands.Distinct())
        {
            try
            {
                SystemLogQueue.Log("info", $"$ sudo -n {reloadCommand}");
                await ExecuteCommandAsync("sudo", $"-n {reloadCommand}");
                return;
            }
            catch (Exception ex)
            {
                errors.Add($"{reloadCommand}: {ex.Message}");
            }
        }

        throw new Exception("Nginx reload komutlarinin hicbiri basarili olmadi. Denenen komutlar: " + string.Join(" | ", errors));
    }

    private async Task<List<(string Domain, string? CertPath, string? KeyPath)>> DetectPanelDomainsAsync(int apiPort)
    {
        var domains = new List<(string Domain, string? CertPath, string? KeyPath)>();
        string enabledDir = ResolvePath(SitesEnabledDir);
        if (!Directory.Exists(enabledDir)) return domains;

        var files = Directory.GetFiles(enabledDir);
        foreach (var file in files)
        {
            try
            {
                string filename = Path.GetFileName(file);
                if (filename.Equals("000-default-panel.conf", StringComparison.OrdinalIgnoreCase))
                    continue;

                var content = await File.ReadAllTextAsync(file, Utf8WithoutBom);
                // Check if this config proxies to the panel API port
                if (content.Contains($"127.0.0.1:{apiPort}") || content.Contains($"localhost:{apiPort}"))
                {
                    // Find all server_name lines
                    var matches = Regex.Matches(content, @"server_name\s+([^;]+);", RegexOptions.IgnoreCase);
                    var fileDomains = new List<string>();
                    foreach (Match match in matches)
                    {
                        var names = match.Groups[1].Value.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (var name in names)
                        {
                            var cleanName = name.Trim().ToLower();
                            if (cleanName != "_" && cleanName != "localhost" && !fileDomains.Contains(cleanName))
                            {
                                fileDomains.Add(cleanName);
                            }
                        }
                    }

                    // Extract SSL paths if present
                    string? certPath = null;
                    string? keyPath = null;
                    var certMatch = Regex.Match(content, @"ssl_certificate\s+([^;]+);", RegexOptions.IgnoreCase);
                    var keyMatch = Regex.Match(content, @"ssl_certificate_key\s+([^;]+);", RegexOptions.IgnoreCase);
                    if (certMatch.Success) certPath = certMatch.Groups[1].Value.Trim().Trim('"', '\'');
                    if (keyMatch.Success) keyPath = keyMatch.Groups[1].Value.Trim().Trim('"', '\'');

                    foreach (var domain in fileDomains)
                    {
                        if (!domains.Any(d => d.Domain == domain))
                        {
                            domains.Add((domain, certPath, keyPath));
                        }
                    }
                }
            }
            catch { }
        }
        return domains;
    }

    private async Task EnsureDefaultPanelConfigAsync()
    {
        // Kırık veya hatalı isimlendirilmiş sites-enabled/sites-available dosyalarını temizle (Auto-healing)
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                string enabledDir = ResolvePath(SitesEnabledDir);
                if (Directory.Exists(enabledDir))
                {
                    foreach (var file in Directory.GetFiles(enabledDir))
                    {
                        var filename = Path.GetFileName(file);
                        if (filename.StartsWith(".") || filename.Contains("@") || filename.Contains(" "))
                        {
                            File.Delete(file);
                            SystemLogQueue.Log("warning", $"[Nginx Cleanup] Hatalı sembolik bağ silindi: {filename}");
                        }
                    }
                }
                
                string availableDir = ResolvePath(SitesAvailableDir);
                if (Directory.Exists(availableDir))
                {
                    foreach (var file in Directory.GetFiles(availableDir))
                    {
                        var filename = Path.GetFileName(file);
                        if (filename.StartsWith(".") || filename.Contains("@") || filename.Contains(" "))
                        {
                            File.Delete(file);
                            SystemLogQueue.Log("warning", $"[Nginx Cleanup] Hatalı konfigürasyon dosyası silindi: {filename}");
                        }
                    }
                }
            }
            catch (Exception cleanupEx)
            {
                SystemLogQueue.Log("warning", $"[Nginx Cleanup] Temizlik sırasında hata: {cleanupEx.Message}");
            }
        }

        // 1. Remove default Nginx symlink to prevent default_server conflict
        var defaultLink = Path.Combine(SitesEnabledDir, "default");
        var resolvedDefaultLink = ResolvePath(defaultLink);
        if (File.Exists(resolvedDefaultLink) || new FileInfo(resolvedDefaultLink).LinkTarget != null)
        {
            try
            {
                File.Delete(resolvedDefaultLink);
                SystemLogQueue.Log("info", "[Nginx] Varsayılan Nginx sites-enabled linki silindi (default_server çakışmasını önlemek için).");
            }
            catch (Exception ex)
            {
                SystemLogQueue.Log("warning", $"[Nginx] Varsayılan Nginx sites-enabled linki silinemedi: {ex.Message}");
            }
        }

        // 2. Get current API port dynamically from environment variable
        int apiPort = 5002;
        var urls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
        if (!string.IsNullOrEmpty(urls))
        {
            var match = Regex.Match(urls, @":(\d+)");
            if (match.Success && int.TryParse(match.Groups[1].Value, out int port))
            {
                apiPort = port;
            }
        }

        // Detect panel domains and their SSL certificate paths from existing configurations
        var panelDomains = await DetectPanelDomainsAsync(apiPort);
        var domainNames = panelDomains.Select(pd => pd.Domain).ToList();
        SystemLogQueue.Log("info", $"[Nginx] Algılanan Panel Domainleri: {string.Join(", ", domainNames)}");

        var configFilename = "000-default-panel.conf";
        var availablePath = Path.Combine(SitesAvailableDir, configFilename);
        var resolvedAvailablePath = ResolvePath(availablePath);

        var sb = new System.Text.StringBuilder();

        // Server block 1: Fallback Default Server (Port 80)
        sb.AppendLine($@"server {{
    listen 80 default_server;
    listen [::]:80 default_server;
    server_name _;

    location / {{
        proxy_pass http://127.0.0.1:{apiPort};
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;

        # WebSockets desteği (SignalR akışı için)
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection ""upgrade"";
    }}
}}");

        // Server block 2: Explicit Port 80 blocks for panel domains to win over wildcards
        if (domainNames.Count > 0)
        {
            sb.AppendLine($@"server {{
    listen 80;
    server_name {string.Join(" ", domainNames)};

    location / {{
        proxy_pass http://127.0.0.1:{apiPort};
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;

        # WebSockets desteği (SignalR akışı için)
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection ""upgrade"";
    }}
}}");
        }

        // Server block 3: Explicit Port 443 SSL blocks for panel domains (if certificates were parsed)
        foreach (var pd in panelDomains)
        {
            if (!string.IsNullOrEmpty(pd.CertPath) && !string.IsNullOrEmpty(pd.KeyPath))
            {
                sb.AppendLine($@"server {{
    listen 443 ssl;
    server_name {pd.Domain};

    ssl_certificate {pd.CertPath};
    ssl_certificate_key {pd.KeyPath};

    # Güvenlik Ayarları
    ssl_protocols TLSv1.2 TLSv1.3;
    ssl_ciphers HIGH:!aNULL:!MD5;
    ssl_prefer_server_ciphers on;

    location / {{
        proxy_pass http://127.0.0.1:{apiPort};
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;

        # WebSockets desteği (SignalR akışı için)
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection ""upgrade"";
    }}
}}");
            }
        }

        var compiledConfig = sb.ToString();

        // Check if it already exists and is identical
        if (File.Exists(resolvedAvailablePath))
        {
            try
            {
                var existingContent = await File.ReadAllTextAsync(resolvedAvailablePath, Utf8WithoutBom);
                if (existingContent.Trim() == compiledConfig.Trim())
                {
                    // Check if symlink exists
                    var enabledPathCheck = Path.Combine(SitesEnabledDir, configFilename);
                    var resolvedEnabledPathCheck = ResolvePath(enabledPathCheck);
                    if (File.Exists(resolvedEnabledPathCheck) || new FileInfo(resolvedEnabledPathCheck).LinkTarget != null)
                    {
                        return; // Up to date and linked
                    }
                }
            }
            catch { }
        }

        await File.WriteAllTextAsync(resolvedAvailablePath, compiledConfig, Utf8WithoutBom);
        SystemLogQueue.Log("info", $"[Nginx] Panel default_server ve korumalı vhost konfigürasyonları sites-available altına yazıldı.");

        var enabledPath = Path.Combine(SitesEnabledDir, configFilename);
        var resolvedEnabledPath = ResolvePath(enabledPath);

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                var linkInfo = new FileInfo(resolvedEnabledPath);
                if (linkInfo.Exists || linkInfo.LinkTarget != null)
                {
                    linkInfo.Delete();
                }
                File.CreateSymbolicLink(resolvedEnabledPath, resolvedAvailablePath);
                SystemLogQueue.Log("info", $"[Nginx] Panel default_server ve korumalı vhost symlink oluşturuldu.");
            }
            catch (Exception symEx)
            {
                SystemLogQueue.Log("warning", $"[Nginx] Panel default_server symlink oluşturulamadı: {symEx.Message}");
            }
        }
        else
        {
            await File.WriteAllTextAsync(resolvedEnabledPath, compiledConfig, Utf8WithoutBom);
        }
    }

    public async Task ProvisionSubdomainAsync(string subdomainName, string domainName, string containerName, int containerPort, ProjectType projectType = ProjectType.DockerContainer, string? staticPath = null, bool? enablePhp = null)
    {
        // Güvenlik Girdisi Doğrulama
        if (!InputValidator.IsSubdomainName(subdomainName) || !InputValidator.IsDomainName(domainName))
        {
            throw new ArgumentException("Geçersiz subdomain veya alan adı formatı!");
        }

        // Ensure default panel server block exists to prevent default_server hijack
        await EnsureDefaultPanelConfigAsync();

        string cleanSubdomain = (subdomainName ?? "").Trim();
        bool isApex = cleanSubdomain == "" || cleanSubdomain == "@";

        string serverNames = isApex 
            ? $"{domainName} www.{domainName}" 
            : $"{cleanSubdomain}.{domainName}";

        string configFilename = isApex 
            ? $"_apex_.{domainName}.conf" 
            : $"{cleanSubdomain}.{domainName}.conf";

        string compiledConfig;

        if (projectType == ProjectType.StaticSite)
        {
            SystemLogQueue.Log("info", $"[Nginx] Statik Web sitesi yönlendirmesi yapılandırılıyor: {(isApex ? "@" : cleanSubdomain)}.{domainName} -> {staticPath ?? containerName} (PHP: {enablePhp})");

            var resolvedStaticPath = staticPath;
            if (string.IsNullOrEmpty(resolvedStaticPath))
            {
                resolvedStaticPath = ResolvePath(Path.Combine("/opt/dockerpanel/projects", containerName));
            }
            resolvedStaticPath = resolvedStaticPath.Replace('\\', '/');

            var phpBlock = "";
            var indexDirective = "index index.html;";
            if (enablePhp == true)
            {
                indexDirective = "index index.php index.html index.htm;";
                phpBlock = @"
    location ~ \.php$ {
        include snippets/fastcgi-php.conf;
        fastcgi_pass unix:/run/php/php8.3-fpm.sock;
        fastcgi_param SCRIPT_FILENAME $document_root$fastcgi_script_name;
        include fastcgi_params;
    }";
            }

            compiledConfig = $@"server {{
    listen 80;
    server_name {serverNames};

    root {resolvedStaticPath};
    {indexDirective}

    location / {{
        try_files $uri $uri/ /index.html /index.php?$args;
    }}{phpBlock}
}}";
        }
        else
        {
            SystemLogQueue.Log("info", $"[Nginx] Proxy yönlendirmesi yapılandırılıyor: {(isApex ? "@" : cleanSubdomain)}.{domainName} -> {containerName}:{containerPort}");

            await EnsureTemplateExistsAsync();

            // 1. Şablonu oku
            var resolvedTemplate = ResolvePath(TemplatePath);
            var templateContent = await File.ReadAllTextAsync(resolvedTemplate, Utf8WithoutBom);
            templateContent = templateContent.TrimStart('\uFEFF');

            // 2. Tokenları değiştir
            if (isApex)
            {
                // Apex domain (ön eksiz) için şablon kullanmak yerine temiz ve doğrudan konfigürasyon oluşturuyoruz.
                // Bu sayede şablondaki log yollarında boşluk karakterinden kaynaklı Nginx syntax hataları önlenmiş olur.
                compiledConfig = $@"server {{
    listen 80;
    server_name {serverNames};

    location / {{
        proxy_pass http://127.0.0.1:{containerPort};
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;

        # WebSockets desteği (SignalR akışı için)
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection ""upgrade"";
    }}

    # Proje kapalıyken veya çökmüşken panel yönlendirmesini önleyen 502 sayfası
    error_page 502 503 504 @backend_down;
    location @backend_down {{
        return 502 '<html><head><title>Uygulama Calismiyor</title><style>body{{font-family:sans-serif;text-align:center;padding:100px 20px;background:#0e1511;color:#dde4dd}}h1{{font-size:36px;color:#ffb4ab}}p{{font-size:18px;color:#86948a}}</style></head><body><h1>Uygulama Calismiyor (502 Bad Gateway)</h1><p>Bu proje su anda kapali veya baslatilamadi. Lutfen kontrol panelinden loglarini inceleyin.</p></body></html>';
        add_header Content-Type text/html;
    }}
}}";
            }
            else
            {
                compiledConfig = templateContent
                    .Replace("{{Subdomain}}", cleanSubdomain)
                    .Replace("{{Domain}}", domainName)
                    .Replace("{{ContainerName}}", containerName)
                    .Replace("{{ContainerPort}}", containerPort.ToString());
            }
        }

        // 3. Konfigürasyonu sites-available altına yaz
        var availablePath = Path.Combine(SitesAvailableDir, configFilename);
        var resolvedAvailablePath = ResolvePath(availablePath);

        SystemLogQueue.Log("info", $"$ cat << 'EOF' > {availablePath}\n{compiledConfig}\nEOF");

        string? backupContent = null;
        if (File.Exists(resolvedAvailablePath))
        {
            backupContent = await File.ReadAllTextAsync(resolvedAvailablePath, Utf8WithoutBom);
        }

        await File.WriteAllTextAsync(resolvedAvailablePath, compiledConfig, Utf8WithoutBom);
        SystemLogQueue.Log("info", $"[Nginx] Konfigürasyon dosyası sites-available altına yazıldı.");

        // 4. sites-enabled altına symlink oluştur (Windows'ta normal dosya yazar veya simüle eder)
        var enabledPath = Path.Combine(SitesEnabledDir, configFilename);
        var resolvedEnabledPath = ResolvePath(enabledPath);

        SystemLogQueue.Log("info", $"$ ln -s {availablePath} {enabledPath}");

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                // Hata Yakalama (Resilience): Kırık sembolik bağları (broken links) veya mevcut dosyaları tamamen temizle
                var linkInfo = new FileInfo(resolvedEnabledPath);
                if (linkInfo.Exists || linkInfo.LinkTarget != null)
                {
                    linkInfo.Delete();
                }

                // Native Symlink: OS süreç bağımlılığı olmayan .NET 8 yerel sembolik bağ oluşturucu
                File.CreateSymbolicLink(resolvedEnabledPath, resolvedAvailablePath);

                // Validation Log: Sembolik bağın diskte var olduğunu doğrulayan ve hedefi teyit eden çıktı
                bool isCreated = File.Exists(resolvedEnabledPath) || new FileInfo(resolvedEnabledPath).LinkTarget != null;
                Console.WriteLine($"[DevOps Validation] Nginx Sembolik Bağ Doğrulaması: {resolvedEnabledPath} -> {resolvedAvailablePath} | Durum: {isCreated}");
            }
            catch (Exception symEx)
            {
                // Symlink hatası durumunda sites-available dosyasını geri alıp fırlat
                if (backupContent != null) await File.WriteAllTextAsync(resolvedAvailablePath, backupContent, Utf8WithoutBom);
                else File.Delete(resolvedAvailablePath);

                throw new InvalidOperationException($"Nginx sembolik bağ (symlink) oluşturulamadı! Hata: {symEx.Message}");
            }
        }
        else
        {
            // Windows simülasyonu için dosyayı direkt kopyala
            await File.WriteAllTextAsync(resolvedEnabledPath, compiledConfig, Utf8WithoutBom);
            Console.WriteLine($"[DevOps Validation - Windows Simulation] Nginx Konfigürasyon Dosyası Kopyalandı: {resolvedEnabledPath}");
        }

        // 5. Nginx Konfigürasyon Testi (sudo nginx -t)
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                SystemLogQueue.Log("info", $"$ sudo -n /usr/sbin/nginx -t");
                await ExecuteCommandAsync("sudo", "-n /usr/sbin/nginx -t");
                SystemLogQueue.Log("info", $"[Nginx] Konfigürasyon testi başarıyla tamamlandı (nginx -t) | {subdomainName}.{domainName}");
            }
            catch (Exception tEx)
            {
                // Test başarısız olursa symlink ve dosyayı temizle/rollback yap
                if (File.Exists(resolvedEnabledPath)) File.Delete(resolvedEnabledPath);
                
                if (backupContent != null) await File.WriteAllTextAsync(resolvedAvailablePath, backupContent, Utf8WithoutBom);
                else File.Delete(resolvedAvailablePath);

                SystemLogQueue.Log("error", $"[Nginx] Konfigürasyon testi başarısız oldu: {tEx.Message} | {subdomainName}.{domainName}");
                throw new InvalidOperationException($"Nginx konfigürasyon testi başarısız oldu! Değişiklikler geri alındı. Hata: {tEx.Message}");
            }

            // 6. Nginx Reload (sudo systemctl reload nginx)
            try
            {
                await ReloadNginxAsync();
                SystemLogQueue.Log("info", $"[Nginx] Proxy başarıyla reload edildi: {subdomainName}.{domainName}");
            }
            catch (Exception rEx)
            {
                if (File.Exists(resolvedEnabledPath)) File.Delete(resolvedEnabledPath);

                if (backupContent != null) await File.WriteAllTextAsync(resolvedAvailablePath, backupContent, Utf8WithoutBom);
                else if (File.Exists(resolvedAvailablePath)) File.Delete(resolvedAvailablePath);

                SystemLogQueue.Log("error", $"[Nginx] Reload başarısız oldu: {rEx.Message} | {subdomainName}.{domainName}");
                throw new InvalidOperationException($"Nginx reload tetiklenemedi. Yeni vhost dosyasi geri alindi. Hata: {rEx.Message}");
            }
        }
        else
        {
            SystemLogQueue.Log("info", $"[Windows Simülasyonu] Nginx proxy başarıyla kuruldu: {subdomainName}.{domainName}");
        }
    }

    public async Task DeleteSubdomainAsync(string subdomainName, string domainName)
    {
        SystemLogQueue.Log("warning", $"[Nginx] Proxy yönlendirmesi siliniyor: {subdomainName}.{domainName}");
        var configFilename = $"{subdomainName}.{domainName}.conf";
        var availablePath = Path.Combine(SitesAvailableDir, configFilename);
        var enabledPath = Path.Combine(SitesEnabledDir, configFilename);

        var resolvedAvailable = ResolvePath(availablePath);
        var resolvedEnabled = ResolvePath(enabledPath);

        SystemLogQueue.Log("info", $"$ rm -f {enabledPath}");
        SystemLogQueue.Log("info", $"$ rm -f {availablePath}");

        bool deleted = false;

        // Delete the sites-enabled symlink first to avoid broken links
        var enabledInfo = new FileInfo(resolvedEnabled);
        if (enabledInfo.Exists || enabledInfo.LinkTarget != null)
        {
            try
            {
                enabledInfo.Delete();
                deleted = true;
            }
            catch (Exception ex)
            {
                SystemLogQueue.Log("warning", $"[Nginx] sites-enabled symlink silinirken hata: {ex.Message}");
            }
        }

        // Delete the actual sites-available configuration file
        if (File.Exists(resolvedAvailable))
        {
            try
            {
                File.Delete(resolvedAvailable);
                deleted = true;
            }
            catch (Exception ex)
            {
                SystemLogQueue.Log("warning", $"[Nginx] sites-available dosyası silinirken hata: {ex.Message}");
            }
        }

        if (deleted && !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                SystemLogQueue.Log("info", $"$ sudo -n /usr/sbin/nginx -t");
                await ExecuteCommandAsync("sudo", "-n /usr/sbin/nginx -t");
                await ReloadNginxAsync();
                SystemLogQueue.Log("info", $"[Nginx] Proxy başarıyla kaldırıldı ve reload edildi: {subdomainName}.{domainName}");
            }
            catch (Exception ex)
            {
                SystemLogQueue.Log("error", $"[Nginx] Proxy kaldırma sonrası reload hatası: {ex.Message}");
            }
        }
        else if (deleted)
        {
            SystemLogQueue.Log("info", $"[Windows Simülasyonu] Nginx proxy başarıyla kaldırıldı: {subdomainName}.{domainName}");
        }
    }

    public async Task EnableSslWithCertbotAsync(string subdomainName, string domainName)
    {
        if (!InputValidator.IsSubdomainName(subdomainName) || !InputValidator.IsDomainName(domainName))
        {
            throw new ArgumentException("Geçersiz subdomain veya alan adı formatı!");
        }

        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DockerPanelDbContext>();
        var rootDomain = await dbContext.RootDomains.FirstOrDefaultAsync(rd => rd.Name.ToLower() == domainName.ToLower());
        bool hasCloudflareToken = rootDomain != null && !string.IsNullOrWhiteSpace(rootDomain.CloudflareToken);

        if (subdomainName == "*")
        {
            if (!hasCloudflareToken)
            {
                throw new InvalidOperationException("Yaban karakter (*.domain.com) SSL üretimi DNS-01 doğrulaması gerektirir. Lütfen önce Alan Adı sayfasından bu alan adı için geçerli bir Cloudflare API Token tanımlayın!");
            }

            SystemLogQueue.Log("info", $"[Let's Encrypt] *.{domainName} için Cloudflare DNS-01 API challenge üzerinden Wildcard SSL sertifikası üretiliyor...");

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Windows simülasyonu
                SystemLogQueue.Log("info", $"$ [Windows Simülasyonu] certbot certonly --dns-cloudflare --dns-cloudflare-credentials /opt/dockerpanel/cloudflare_{domainName}.ini -d *.{domainName} -d {domainName} --non-interactive --agree-tos");
                await Task.Delay(2000);
                SystemLogQueue.Log("info", $"[Let's Encrypt] *.{domainName} için Wildcard SSL sertifikası başarıyla kuruldu (Simülasyon).");
                return;
            }

            // Linux ortamı - Cloudflare credentials dosyasını oluştur ve kısıtlı yetkilerle kaydet
            var credentialsDir = "/opt/dockerpanel";
            var credentialsPath = Path.Combine(credentialsDir, $"cloudflare_{domainName}.ini");
            try
            {
                if (!Directory.Exists(credentialsDir))
                {
                    Directory.CreateDirectory(credentialsDir);
                }

                await File.WriteAllTextAsync(credentialsPath, $"dns_cloudflare_api_token = {rootDomain!.CloudflareToken!.Trim()}\n", Utf8WithoutBom);
                
                // chmod 600 vererek yetki sınırlarını koru
                try
                {
                    await ExecuteCommandAsync("sudo", $"-n /bin/chmod 600 {credentialsPath}", 5000);
                }
                catch { }

                // Wildcard sertifikasını al (hem *.domain hem domain için)
                string wildcardDomain = $"*.{domainName}";
                await ExecuteCommandAsync("sudo", $"-n /usr/bin/certbot certonly --dns-cloudflare --dns-cloudflare-credentials {credentialsPath} -d {wildcardDomain} -d {domainName} --non-interactive --agree-tos --register-unsafely-without-email", 120000);
                
                SystemLogQueue.Log("info", $"[Let's Encrypt] *.{domainName} için Wildcard SSL sertifikası başarıyla alındı ve kuruldu.");
            }
            catch (Exception ex)
            {
                SystemLogQueue.Log("error", $"[Let's Encrypt] Wildcard SSL sertifikası üretilirken hata oluştu: {ex.Message}");
                var errMsg = ex.Message.ToLowerInvariant();
                if (errMsg.Contains("unrecognized arguments") || errMsg.Contains("dns-cloudflare"))
                {
                    throw new InvalidOperationException("Sunucuda Certbot Cloudflare eklentisi kurulu değil! Lütfen sunucu terminaline bağlanıp 'sudo apt-get update && sudo apt-get install -y python3-certbot-dns-cloudflare' komutunu çalıştırın ve tekrar deneyin.", ex);
                }
                throw;
            }
            finally
            {
                // Güvenlik: Geçici ini dosyasını temizle
                try
                {
                    if (File.Exists(credentialsPath))
                    {
                        File.Delete(credentialsPath);
                    }
                }
                catch { }
            }
            return;
        }

        string cleanSubdomain = (subdomainName ?? "").Trim();
        bool isApex = cleanSubdomain == "" || cleanSubdomain == "@";
        string fullDomain = isApex ? domainName : $"{cleanSubdomain}.{domainName}";
        string domainArgs = isApex ? $"-d {domainName} -d www.{domainName}" : $"-d {fullDomain}";
        SystemLogQueue.Log("info", $"[Let's Encrypt] {fullDomain} için SSL sertifikası üretiliyor...");

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (hasCloudflareToken)
            {
                SystemLogQueue.Log("info", $"$ [Windows Simülasyonu - Cloudflare DNS-01] certbot -a dns-cloudflare --dns-cloudflare-credentials /opt/dockerpanel/cloudflare_{domainName}.ini -i nginx {domainArgs} --non-interactive --agree-tos --register-unsafely-without-email");
            }
            else
            {
                SystemLogQueue.Log("info", $"$ [Windows Simülasyonu - HTTP-01] certbot --nginx {domainArgs} --non-interactive --agree-tos --register-unsafely-without-email");
            }
            await Task.Delay(2000);
            SystemLogQueue.Log("info", $"[Let's Encrypt] {fullDomain} için SSL sertifikası başarıyla kuruldu (Simülasyon).");
            return;
        }

        if (hasCloudflareToken)
        {
            // Cloudflare API Token mevcut ise Cloudflare DNS-01 doğrulamasını Nginx yükleyicisi ile birleştirip çalıştırıyoruz
            var credentialsDir = "/opt/dockerpanel";
            var credentialsPath = Path.Combine(credentialsDir, $"cloudflare_{domainName}.ini");
            try
            {
                if (!Directory.Exists(credentialsDir))
                {
                    Directory.CreateDirectory(credentialsDir);
                }

                await File.WriteAllTextAsync(credentialsPath, $"dns_cloudflare_api_token = {rootDomain!.CloudflareToken!.Trim()}\n", Utf8WithoutBom);

                try
                {
                    await ExecuteCommandAsync("sudo", $"-n /bin/chmod 600 {credentialsPath}", 5000);
                }
                catch { }

                SystemLogQueue.Log("info", $"[Let's Encrypt] {fullDomain} için Cloudflare DNS-01 API ve Nginx yükleyicisi üzerinden SSL sertifikası kuruluyor...");
                await ExecuteCommandAsync("sudo", $"-n /usr/bin/certbot -a dns-cloudflare --dns-cloudflare-credentials {credentialsPath} -i nginx {domainArgs} --non-interactive --agree-tos --register-unsafely-without-email", 120000);
                SystemLogQueue.Log("info", $"[Let's Encrypt] {fullDomain} için Cloudflare DNS-01 tabanlı SSL başarıyla kuruldu ve Nginx konfigüre edildi.");
            }
            catch (Exception ex)
            {
                SystemLogQueue.Log("error", $"[Let's Encrypt] Cloudflare DNS-01 ile SSL sertifikası üretilirken hata oluştu: {ex.Message}");
                var errMsg = ex.Message.ToLowerInvariant();
                if (errMsg.Contains("unrecognized arguments") || errMsg.Contains("dns-cloudflare"))
                {
                    throw new InvalidOperationException("Sunucuda Certbot Cloudflare eklentisi kurulu değil! Lütfen sunucu terminaline bağlanıp 'sudo apt-get update && sudo apt-get install -y python3-certbot-dns-cloudflare' komutunu çalıştırın ve tekrar deneyin.", ex);
                }
                throw;
            }
            finally
            {
                try
                {
                    if (File.Exists(credentialsPath))
                    {
                        File.Delete(credentialsPath);
                    }
                }
                catch { }
            }
        }
        else
        {
            // Klasik HTTP-01 Challenge
            try
            {
                SystemLogQueue.Log("info", $"[Let's Encrypt] {fullDomain} için HTTP-01 (Nginx) üzerinden SSL sertifikası kuruluyor...");
                await ExecuteCommandAsync("sudo", $"-n /usr/bin/certbot --nginx {domainArgs} --non-interactive --agree-tos --register-unsafely-without-email", 60000);
                SystemLogQueue.Log("info", $"[Let's Encrypt] {fullDomain} için HTTP-01 tabanlı SSL başarıyla kuruldu ve Nginx reload edildi.");
            }
            catch (Exception ex)
            {
                SystemLogQueue.Log("error", $"[Let's Encrypt] HTTP-01 ile SSL sertifikası üretilirken hata oluştu: {ex.Message}");
                throw;
            }
        }
    }

    public async Task SyncActiveConfigsWithDbAsync(Guid userId)
    {
        SystemLogQueue.Log("info", "[Nginx Eşitleme] Aktif Nginx yapılandırmaları taranıyor...");
        
        string enabledDir = ResolvePath(SitesEnabledDir);
        if (!Directory.Exists(enabledDir))
        {
            SystemLogQueue.Log("warning", $"[Nginx Eşitleme] {SitesEnabledDir} dizini bulunamadı. Tarama sonlandırıldı.");
            return;
        }

        var confFiles = Directory.GetFiles(enabledDir, "*.conf");
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DockerPanelDbContext>();

        int importedCount = 0;
        foreach (var file in confFiles)
         {
             try
             {
                 string filename = Path.GetFileName(file);
                 // e.g. api.site.com.conf
                 var match = Regex.Match(filename, @"^([a-z0-9_-]+)\.([a-z0-9_.-]+)\.conf$", RegexOptions.IgnoreCase);
                 if (!match.Success) continue;

                 string subdomainName = match.Groups[1].Value.ToLower();
                 string domainName = match.Groups[2].Value.ToLower();

                 // Zaten veritabanında var mı?
                 bool exists = await dbContext.Subdomains.AnyAsync(s => 
                     s.SubdomainName == subdomainName && 
                     s.DomainName == domainName);

                 if (exists) continue;

                 // Dosya içeriğini oku ve portu ayıkla
                 string content = await File.ReadAllTextAsync(file, Utf8WithoutBom);
                 var portMatch = Regex.Match(content, @"proxy_pass\s+http://[^:]+:([0-9]+);");
                 int port = 80;
                 if (portMatch.Success && int.TryParse(portMatch.Groups[1].Value, out int parsedPort))
                 {
                     port = parsedPort;
                 }

                 // SSL aktif mi?
                 bool sslEnabled = content.Contains("listen 443") || content.Contains("ssl_certificate");

                 // Bu portu kullanan bir proje var mı?
                 var project = await dbContext.Projects.FirstOrDefaultAsync(p => p.InternalPort == port);
                 Guid? projectId = project?.Id;

                 var subdomain = new Subdomain
                 {
                     UserId = userId,
                     ProjectId = projectId,
                     SubdomainName = subdomainName,
                     DomainName = domainName,
                     SslEnabled = sslEnabled,
                     CreatedAt = DateTimeOffset.UtcNow
                 };

                 dbContext.Subdomains.Add(subdomain);
                 importedCount++;
                 
                 SystemLogQueue.Log("info", $"[Nginx Eşitleme] Yeni subdomain bulundu ve kaydedildi: {subdomainName}.{domainName} (Port: {port}, Proje: {project?.Name ?? "Bağımsız"})");
             }
             catch (Exception ex)
             {
                 SystemLogQueue.Log("error", $"{file} dosyası taranırken hata oluştu: {ex.Message}");
             }
         }

         if (importedCount > 0)
         {
             await dbContext.SaveChangesAsync();
             SystemLogQueue.Log("info", $"[Nginx Eşitleme] Eşitleme tamamlandı. {importedCount} adet yeni yönlendirme sisteme eklendi.");
         }
         else
         {
             SystemLogQueue.Log("info", "[Nginx Eşitleme] Eşitleme tamamlandı. Yeni bir yönlendirme bulunamadı.");
         }
    }
}
