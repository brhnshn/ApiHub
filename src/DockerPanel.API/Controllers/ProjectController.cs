using System;
using System.IO;
using System.Text.Json;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DockerPanel.API.Helpers;
using DockerPanel.Domain.Entities;
using DockerPanel.Domain.Enums;
using DockerPanel.Domain.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using DockerPanel.Infrastructure.Data;
using DockerPanel.Infrastructure.Services;
using Microsoft.AspNetCore.RateLimiting;

namespace DockerPanel.API.Controllers;

[Authorize]
[ApiController]
[Route("api/projects")]
[EnableRateLimiting("resource-heavy")]
public class ProjectController : ControllerBase
{
    private readonly DockerPanelDbContext _dbContext;
    private readonly IProjectContainerService _containerService;
    private readonly IProcessManagerService _processManagerService;
    private readonly IProjectZipDeployService _zipDeployService;
    private readonly INginxService _nginxService;
    private readonly IDatabaseService _databaseService;
    private readonly ICloudflareService _cloudflareService;
    private readonly IAuditLogService _auditLogService;
    private readonly IServiceScopeFactory _scopeFactory;

    public ProjectController(
        DockerPanelDbContext dbContext,
        IProjectContainerService containerService,
        IProcessManagerService processManagerService,
        IProjectZipDeployService zipDeployService,
        INginxService nginxService,
        IDatabaseService databaseService,
        ICloudflareService cloudflareService,
        IAuditLogService auditLogService,
        IServiceScopeFactory scopeFactory)
    {
        _dbContext = dbContext;
        _containerService = containerService;
        _processManagerService = processManagerService;
        _zipDeployService = zipDeployService;
        _nginxService = nginxService;
        _databaseService = databaseService;
        _cloudflareService = cloudflareService;
        _auditLogService = auditLogService;
        _scopeFactory = scopeFactory;
    }

    private async Task LogAuditAsync(string action, string entity, Guid? targetId, string details)
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var ua = Request.Headers["User-Agent"].ToString() ?? "unknown";
        await _auditLogService.LogAsync(GetUserId(), action, entity, targetId, details, ip, ua);
    }

    private Guid GetUserId()
    {
        var idStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(idStr, out var id) ? id : Guid.Empty;
    }

    private bool IsAdmin()
    {
        return User.IsInRole(UserRole.Administrator.ToString());
    }

    private static void MarkRunning(Project project)
    {
        project.Status = ProjectStatus.Running;
        project.StartedAt = DateTimeOffset.UtcNow;
    }

    private static void MarkStopped(Project project)
    {
        project.Status = ProjectStatus.Stopped;
        project.StartedAt = null;
    }

    private static void MarkError(Project project)
    {
        project.Status = ProjectStatus.Error;
        project.StartedAt = null;
    }

    private async Task UpdateLinkedSubdomainsNginxConfigAsync(Project project)
    {
        var linkedSubdomains = await _dbContext.Subdomains
            .Where(s => s.ProjectId == project.Id)
            .ToListAsync();

        foreach (var sub in linkedSubdomains)
        {
            try
            {
                await _nginxService.ProvisionSubdomainAsync(
                    sub.SubdomainName,
                    sub.DomainName,
                    project.Name,
                    project.HostPort,
                    project.Type,
                    project.ImageOrPath,
                    project.EnablePhp,
                    sub.SslEnabled
                );
            }
            catch (Exception nginxEx)
            {
                SystemLogQueue.Log("warning", $"[Nginx Sync] {sub.SubdomainName}.{sub.DomainName} Nginx yapılandırması güncellenemedi: {nginxEx.Message}");
            }
        }
    }

    private async Task ActivateMaintenanceModeForProjectAsync(Project project, StopProjectRequest? request)
    {
        var linkedSubdomains = await _dbContext.Subdomains
            .Where(s => s.ProjectId == project.Id)
            .ToListAsync();

        if (!linkedSubdomains.Any())
        {
            var dnsRecords = await _dbContext.DnsRecords
                .Where(d => d.ProjectId == project.Id)
                .ToListAsync();

            foreach (var dns in dnsRecords)
            {
                linkedSubdomains.Add(new Subdomain
                {
                    Id = Guid.NewGuid(),
                    ProjectId = project.Id,
                    SubdomainName = "@",
                    DomainName = dns.Name,
                    SslEnabled = true
                });
            }
        }

        if (!linkedSubdomains.Any()) return;

        // Varsayılan genel şablonu (ilk sıradaki veya 'Sistem Bakımda') bul
        var defaultTemplate = await _dbContext.MaintenancePages
            .OrderBy(p => p.CreatedAt)
            .FirstOrDefaultAsync();

        // 1. Dosya kaydedici helper (Null-safe)
        async Task<string> EnsureHtmlFileExistsAsync(DockerPanel.Domain.Entities.MaintenancePage? page)
        {
            const string maintenancePagesDir = "/opt/dockerpanel/maintenance-pages";
            var pageId = page?.Id.ToString() ?? "default-offline";
            var htmlFileName = $"{pageId}.html";
            string htmlContent = page?.HtmlContent ?? @"<!DOCTYPE html><html lang=""tr""><head><meta charset=""UTF-8""><meta name=""viewport"" content=""width=device-width, initial-scale=1.0""><title>Sistem Bakımda</title><style>*{box-sizing:border-box;margin:0;padding:0}body{font-family:system-ui,-apple-system,sans-serif;background-color:#0b0f17;color:#e2e8f0;display:flex;align-items:center;justify-content:center;min-height:100vh;padding:24px}.card{background:rgba(23,32,48,0.75);border:1px solid rgba(255,255,255,0.08);border-radius:20px;padding:48px 36px;max-width:500px;width:100%;text-align:center;box-shadow:0 20px 40px rgba(0,0,0,0.6)}.badge{display:inline-flex;align-items:center;gap:8px;background:rgba(245,158,11,0.1);color:#fbbf24;border:1px solid rgba(245,158,11,0.2);padding:6px 16px;border-radius:20px;font-size:13px;font-weight:600;margin-bottom:20px}.dot{width:8px;height:8px;background-color:#f59e0b;border-radius:50%;animation:pulse 2s infinite}@keyframes pulse{0%,100%{opacity:1;transform:scale(1)}50%{opacity:0.4;transform:scale(0.8);}}h1{font-size:24px;font-weight:700;color:#fff;margin-bottom:12px}p{font-size:15px;color:#94a3b8;line-height:1.6;margin-bottom:28px}.footer{font-size:12px;color:#64748b;border-top:1px solid rgba(255,255,255,0.06);padding-top:20px}</style></head><body><div class=""card""><div class=""badge""><span class=""dot""></span> Servis Çevrimdışı</div><h1>Bu Sayfa Şu Anlık Kapalıdır</h1><p>Aradığınız proje veya web sitesi şu anda kapalıdır ya da bakım modundadır. Lütfen daha sonra tekrar ziyaret ediniz.</p><div class=""footer"">Burhan Şahin | info@burhansahin.com.tr</div></div></body></html>";
            
            var resolvedDir = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows)
                ? System.IO.Path.Combine(AppContext.BaseDirectory, "opt_dockerpanel", "maintenance-pages")
                : maintenancePagesDir;
            
            if (!System.IO.Directory.Exists(resolvedDir)) 
                System.IO.Directory.CreateDirectory(resolvedDir);

            var fileName = $"maintenance-{pageId}.html";
            var resolvedFilePath = System.IO.Path.Combine(resolvedDir, fileName);

            await System.IO.File.WriteAllTextAsync(resolvedFilePath, htmlContent, new System.Text.UTF8Encoding(false));

            if (!System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
            {
                try
                {
                    System.IO.File.SetUnixFileMode(resolvedFilePath,
                        System.IO.UnixFileMode.UserRead | System.IO.UnixFileMode.UserWrite |
                        System.IO.UnixFileMode.GroupRead | System.IO.UnixFileMode.OtherRead);
                }
                catch { }
            }

            return resolvedFilePath;
        }

        // Genel seçilen sayfa
        DockerPanel.Domain.Entities.MaintenancePage? generalPage = null;
        if (request?.MaintenancePageId != null)
        {
            generalPage = await _dbContext.MaintenancePages.FindAsync(request.MaintenancePageId);
        }
        generalPage ??= defaultTemplate;

        foreach (var sub in linkedSubdomains)
        {
            try
            {
                DockerPanel.Domain.Entities.MaintenancePage? subPage = null;

                // Subdomain özelinde sayfa seçilmiş mi?
                if (request?.SubdomainPages != null && request.SubdomainPages.TryGetValue(sub.Id, out var subPageId) && subPageId.HasValue)
                {
                    subPage = await _dbContext.MaintenancePages.FindAsync(subPageId.Value);
                }

                subPage ??= generalPage;

                var htmlFilePath = await EnsureHtmlFileExistsAsync(subPage);
                await _nginxService.ActivateMaintenanceModeAsync(
                    sub.SubdomainName,
                    sub.DomainName,
                    htmlFilePath,
                    sub.SslEnabled
                );
                if (subPage != null)
                {
                    sub.ActiveMaintenancePageId = subPage.Id;
                }
            }
            catch (Exception nginxEx)
            {
                SystemLogQueue.Log("warning", $"[Bakım Modu] {sub.SubdomainName}.{sub.DomainName} bakım modu aktif edilemedi: {nginxEx.Message}");
            }
        }

        project.ActiveMaintenancePageId = generalPage?.Id;
    }

    private async Task DeactivateMaintenanceModeForProjectAsync(Project project)
    {
        var linkedSubdomains = await _dbContext.Subdomains
            .Where(s => s.ProjectId == project.Id)
            .ToListAsync();

        foreach (var sub in linkedSubdomains)
        {
            try
            {
                await _nginxService.DeactivateMaintenanceModeAsync(
                    sub.SubdomainName,
                    sub.DomainName,
                    project.Name,
                    project.HostPort,
                    project.Type,
                    sub.SslEnabled
                );
                sub.ActiveMaintenancePageId = null;
            }
            catch (Exception nginxEx)
            {
                SystemLogQueue.Log("warning", $"[Bakım Modu] {sub.SubdomainName}.{sub.DomainName} bakım modu devre dışı bırakılamadı: {nginxEx.Message}");
            }
        }
        project.ActiveMaintenancePageId = null;
    }

    private async Task DeleteLinkedDnsRecordAsync(DnsRecord record)
    {
        var rootDomain = await _dbContext.RootDomains
            .FirstOrDefaultAsync(rd => rd.UserId == record.UserId && (record.Name == rd.Name || record.Name.EndsWith("." + rd.Name)));

        if (!string.IsNullOrEmpty(record.CloudflareRecordId) &&
            rootDomain != null &&
            !string.IsNullOrEmpty(rootDomain.CloudflareToken) &&
            !string.IsNullOrEmpty(rootDomain.CloudflareZoneId))
        {
            try
            {
                await _cloudflareService.DeleteDnsRecordAsync(rootDomain.CloudflareToken, rootDomain.CloudflareZoneId, record.CloudflareRecordId);
            }
            catch (Exception cfEx)
            {
                var errMsg = cfEx.Message.ToLowerInvariant();
                if (!errMsg.Contains("81044") && !errMsg.Contains("notfound") && !errMsg.Contains("not found") && !errMsg.Contains("exist"))
                {
                    throw;
                }
            }
        }

        _dbContext.DnsRecords.Remove(record);
    }

    [HttpGet]
    [DisableRateLimiting]
    public async Task<IActionResult> GetAll()
    {
        var userId = GetUserId();
        var query = _dbContext.Projects.Include(p => p.Subdomains).AsQueryable();

        if (!IsAdmin())
        {
            query = query.Where(p => p.UserId == userId);
        }

        var projects = await query.OrderByDescending(p => p.CreatedAt).ToListAsync();
        return Ok(projects);
    }

    [HttpGet("{id}")]
    [DisableRateLimiting]
    public async Task<IActionResult> GetById(Guid id)
    {
        var project = await _dbContext.Projects.FindAsync(id);
        if (project == null) return NotFound(new { Message = "Proje bulunamadı!" });

        if (!IsAdmin() && project.UserId != GetUserId())
        {
            return Forbid();
        }

        return Ok(project);
    }

    // 1. Docker Container Oluşturma Endpointi
    [HttpPost("container")]
    public async Task<IActionResult> CreateContainer([FromBody] CreateContainerRequest request)
    {
        if (!SecurityHelper.IsValidAppName(request.Name))
        {
            return BadRequest(new { Message = "Proje adı sadece küçük harf, rakam, tire (-) ve alt çizgi (_) içerebilir!" });
        }

        var userId = GetUserId();
        var existingProject = await _dbContext.Projects.FirstOrDefaultAsync(p => p.Name == request.Name);
        Project project;
        if (existingProject != null)
        {
            if (existingProject.UserId != userId)
            {
                return BadRequest(new { Message = "Bu isimde bir proje başka bir kullanıcı tarafından zaten kullanılıyor!" });
            }

            // Safe clean up: remove old container if it exists
            if (existingProject.Type == ProjectType.DockerContainer && !string.IsNullOrEmpty(existingProject.DockerContainerId))
            {
                try
                {
                    await _containerService.DeleteContainerAsync(existingProject.DockerContainerId);
                }
                catch (Exception ex)
                {
                    SystemLogQueue.Log("warning", $"[Deployment Retry] Eski konteyner temizlenirken hata oluştu: {ex.Message}");
                }
            }
            else if (existingProject.Type == ProjectType.NativeProject)
            {
                try
                {
                    await _processManagerService.StopProcessAsync(existingProject.Name);
                    await _processManagerService.DeleteProcessConfigAsync(existingProject.Name);
                }
                catch (Exception ex)
                {
                    SystemLogQueue.Log("warning", $"[Deployment Retry] Eski native süreç temizlenirken hata oluştu: {ex.Message}");
                }
            }

            project = existingProject;
            project.Type = ProjectType.DockerContainer;
            project.ImageOrPath = request.ImageName;
            project.MemoryLimitBytes = request.MemoryLimitBytes;
            project.CpuCount = request.CpuCount;
            project.HostPort = request.HostPort;
            project.ContainerPort = request.ContainerPort;
            project.Status = ProjectStatus.Provisioning;
            
            _dbContext.Entry(project).State = EntityState.Modified;
        }
        else
        {
            project = new Project
            {
                UserId = userId,
                Name = request.Name,
                Type = ProjectType.DockerContainer,
                ImageOrPath = request.ImageName,
                MemoryLimitBytes = request.MemoryLimitBytes,
                CpuCount = request.CpuCount,
                HostPort = request.HostPort,
                ContainerPort = request.ContainerPort,
                Status = ProjectStatus.Provisioning,
                CreatedAt = DateTimeOffset.UtcNow
            };
            _dbContext.Projects.Add(project);
        }
        await _dbContext.SaveChangesAsync();

        try
        {
            // Akıllı containerPort çözümleme:
            // 1. Kullanıcının girdiği ContainerPort
            // 2. Image'ın EXPOSE ettiği port (pull + inspect)
            // 3. Son çare: HostPort
            int effectiveContainerPort = request.ContainerPort
                ?? await _containerService.GetImageExposedPortAsync(request.ImageName)
                ?? request.HostPort;

            // Çözümlenen containerPort'u DB'ye yansıt
            project.ContainerPort = effectiveContainerPort;

            var dockerId = await _containerService.ProvisionContainerAsync(
                request.Name,
                request.ImageName,
                request.MemoryLimitBytes,
                request.CpuCount,
                request.HostPort,
                effectiveContainerPort
            );

            project.DockerContainerId = dockerId;
            MarkRunning(project);
            await _dbContext.SaveChangesAsync();

            await UpdateLinkedSubdomainsNginxConfigAsync(project);

            await LogAuditAsync("ContainerCreated", "Project", project.Id, JsonSerializer.Serialize(new { name = project.Name, image = project.ImageOrPath, memory = project.MemoryLimitBytes, cpu = project.CpuCount }));

            return Ok(project);
        }
        catch (Exception ex)
        {
            MarkError(project);
            await _dbContext.SaveChangesAsync();
            return StatusCode(500, new { Message = $"Docker orkestrasyon hatası: {ex.Message}" });
        }
    }

    // 2. Native ZIP Deployment Endpointi
    [HttpPost("native-deploy")]
    [DisableRequestSizeLimit]
    public async Task<IActionResult> DeployNativeProject([FromForm] DeployNativeRequest request)
    {
        if (!SecurityHelper.IsValidAppName(request.Name))
        {
            return BadRequest(new { Message = "Proje adı sadece küçük harf, rakam, tire (-) ve alt çizgi (_) içerebilir!" });
        }

        var userId = GetUserId();
        var existingProject = await _dbContext.Projects.FirstOrDefaultAsync(p => p.Name == request.Name);
        Project project;
        if (existingProject != null)
        {
            if (existingProject.UserId != userId)
            {
                return BadRequest(new { Message = "Bu isimde bir proje başka bir kullanıcı tarafından zaten kullanılıyor!" });
            }

            // Safe clean up: remove old container if it exists
            if (existingProject.Type == ProjectType.DockerContainer && !string.IsNullOrEmpty(existingProject.DockerContainerId))
            {
                try
                {
                    await _containerService.DeleteContainerAsync(existingProject.DockerContainerId);
                }
                catch (Exception ex)
                {
                    SystemLogQueue.Log("warning", $"[Deployment Retry] Eski konteyner temizlenirken hata oluştu: {ex.Message}");
                }
            }
            else if (existingProject.Type == ProjectType.NativeProject)
            {
                try
                {
                    await _processManagerService.StopProcessAsync(existingProject.Name);
                    // Sürecin tamamen durması ve kilitlediği dosyaları bırakması için 2 saniye bekle
                    await Task.Delay(2000);
                    // Do NOT delete process config to preserve previous StartCommand configuration
                }
                catch (Exception ex)
                {
                    SystemLogQueue.Log("warning", $"[Deployment Retry] Eski native süreç durdurulurken hata oluştu: {ex.Message}");
                }
            }

            project = existingProject;
            project.Type = ProjectType.NativeProject;
            project.MemoryLimitBytes = request.MemoryLimitBytes;
            project.CpuCount = request.CpuCount;
            project.HostPort = request.HostPort;
            project.ContainerPort = null; // Native projeler için ContainerPort anlamsız
            project.Status = ProjectStatus.Provisioning;

            _dbContext.Entry(project).State = EntityState.Modified;
        }
        else
        {
            project = new Project
            {
                UserId = userId,
                Name = request.Name,
                Type = ProjectType.NativeProject,
                MemoryLimitBytes = request.MemoryLimitBytes,
                CpuCount = request.CpuCount,
                HostPort = request.HostPort,
                ContainerPort = null, // Native projeler için ContainerPort anlamsız
                Status = ProjectStatus.Provisioning,
                CreatedAt = DateTimeOffset.UtcNow
            };
            _dbContext.Projects.Add(project);
        }
        await _dbContext.SaveChangesAsync();

        if (request.ZipFile == null || request.ZipFile.Length == 0)
        {
            return BadRequest(new { Message = "Lütfen geçerli bir ZIP deploy dosyası yükleyin!" });
        }

        try
        {
            // ZIP Dosyasını Güvenle Çıkar (Zip Slip Traversal Korumalı)
            using var stream = request.ZipFile.OpenReadStream();
            var extractPath = await _zipDeployService.DeployZipAsync(request.Name, stream);

            project.ImageOrPath = extractPath;

            // Bağımlılıkları Yükle (npm install, pip install, dotnet restore vb.)
            var runtimeType = request.RuntimeType;
            if (string.IsNullOrWhiteSpace(runtimeType))
            {
                if (System.IO.File.Exists(System.IO.Path.Combine(extractPath, "package.json")))
                {
                    runtimeType = "node";
                }
                else if (System.IO.Directory.GetFiles(extractPath, "*.csproj").Any() || 
                         System.IO.Directory.GetFiles(extractPath, "*.runtimeconfig.json").Any())
                {
                    runtimeType = "dotnet";
                }
                else if (System.IO.File.Exists(System.IO.Path.Combine(extractPath, "requirements.txt")) ||
                         System.IO.File.Exists(System.IO.Path.Combine(extractPath, "manage.py")) ||
                         System.IO.File.Exists(System.IO.Path.Combine(extractPath, "app.py")) ||
                         System.IO.File.Exists(System.IO.Path.Combine(extractPath, "main.py")))
                {
                    runtimeType = "python";
                }
            }

            await _processManagerService.RestoreDependenciesAsync(request.Name, extractPath, runtimeType);

            // Kritik: Otomatik tespit edilen runtimeType'ı request nesnesine aktararak config güncellemesinde ezilmesini önlüyoruz.
            if (string.IsNullOrWhiteSpace(request.RuntimeType))
            {
                request.RuntimeType = runtimeType;
            }

            // Config Kayıt Et
            await _processManagerService.AddOrUpdateProcessConfigAsync(request.Name, request.HostPort, request.RuntimeType, request.EntryFile, request.CustomCommand);

            // Native Süreci Başlat
            await _processManagerService.StartProcessAsync(request.Name);

            MarkRunning(project);
            await _dbContext.SaveChangesAsync();

            await UpdateLinkedSubdomainsNginxConfigAsync(project);

            await LogAuditAsync("NativeProjectDeployed", "Project", project.Id, JsonSerializer.Serialize(new { name = project.Name, path = project.ImageOrPath, port = project.HostPort }));

            return Ok(project);
        }
        catch (Exception ex)
        {
            try
            {
                if (!string.IsNullOrEmpty(project.ImageOrPath))
                {
                    SafeDeleteDirectory(project.ImageOrPath);
                }
                await _processManagerService.DeleteProcessConfigAsync(project.Name);
            }
            catch (Exception cleanEx)
            {
                SystemLogQueue.Log("warning", $"[Deploy Cleanup] Hata sonrası temizlik yapılamadı: {cleanEx.Message}");
            }

            if (existingProject == null)
            {
                _dbContext.Projects.Remove(project);
            }
            else
            {
                MarkError(project);
                project.ImageOrPath = string.Empty;
            }
            await _dbContext.SaveChangesAsync();
            return StatusCode(500, new { Message = $"Native deploy hatası: {ex.Message}" });
        }
    }

    // 4. Statik ZIP Deployment Endpointi
    [HttpPost("static-deploy")]
    [DisableRequestSizeLimit]
    public async Task<IActionResult> DeployStaticProject([FromForm] DeployStaticRequest request)
    {
        if (!SecurityHelper.IsValidAppName(request.Name))
        {
            return BadRequest(new { Message = "Proje adı sadece küçük harf, rakam, tire (-) ve alt çizgi (_) içerebilir!" });
        }

        var userId = GetUserId();
        var existingProject = await _dbContext.Projects.FirstOrDefaultAsync(p => p.Name == request.Name);
        Project project;
        if (existingProject != null)
        {
            if (existingProject.UserId != userId)
            {
                return BadRequest(new { Message = "Bu isimde bir proje başka bir kullanıcı tarafından zaten kullanılıyor!" });
            }

            // Safe clean up: remove old container if it exists
            if (existingProject.Type == ProjectType.DockerContainer && !string.IsNullOrEmpty(existingProject.DockerContainerId))
            {
                try
                {
                    await _containerService.DeleteContainerAsync(existingProject.DockerContainerId);
                }
                catch (Exception ex)
                {
                    SystemLogQueue.Log("warning", $"[Deployment Retry] Eski konteyner temizlenirken hata oluştu: {ex.Message}");
                }
            }
            else if (existingProject.Type == ProjectType.NativeProject)
            {
                try
                {
                    await _processManagerService.StopProcessAsync(existingProject.Name);
                    await _processManagerService.DeleteProcessConfigAsync(existingProject.Name);
                }
                catch (Exception ex)
                {
                    SystemLogQueue.Log("warning", $"[Deployment Retry] Eski native süreç temizlenirken hata oluştu: {ex.Message}");
                }
            }
            else if (existingProject.Type == ProjectType.StaticSite)
            {
                SafeDeleteDirectory(existingProject.ImageOrPath);
            }

            project = existingProject;
            project.Type = ProjectType.StaticSite;
            project.MemoryLimitBytes = 0;
            project.CpuCount = 0;
            project.HostPort = 80;
            project.ContainerPort = null;
            project.EnablePhp = request.EnablePhp;
            project.Status = ProjectStatus.Provisioning;

            _dbContext.Entry(project).State = EntityState.Modified;
        }
        else
        {
            project = new Project
            {
                UserId = userId,
                Name = request.Name,
                Type = ProjectType.StaticSite,
                MemoryLimitBytes = 0,
                CpuCount = 0,
                HostPort = 80,
                ContainerPort = null,
                EnablePhp = request.EnablePhp,
                Status = ProjectStatus.Provisioning,
                CreatedAt = DateTimeOffset.UtcNow
            };
            _dbContext.Projects.Add(project);
        }
        await _dbContext.SaveChangesAsync();

        if (request.ZipFile == null || request.ZipFile.Length == 0)
        {
            return BadRequest(new { Message = "Lütfen geçerli bir ZIP deploy dosyası yükleyin!" });
        }

        try
        {
            // ZIP Dosyasını Güvenle Çıkar (Zip Slip Traversal Korumalı)
            using var stream = request.ZipFile.OpenReadStream();
            var extractPath = await _zipDeployService.DeployZipAsync(request.Name, stream);

            project.ImageOrPath = extractPath;
            MarkRunning(project);
            await _dbContext.SaveChangesAsync();

            await UpdateLinkedSubdomainsNginxConfigAsync(project);

            await LogAuditAsync("StaticSiteDeployed", "Project", project.Id, JsonSerializer.Serialize(new { name = project.Name, path = project.ImageOrPath, php = project.EnablePhp }));

            return Ok(project);
        }
        catch (Exception ex)
        {
            try
            {
                if (!string.IsNullOrEmpty(project.ImageOrPath))
                {
                    SafeDeleteDirectory(project.ImageOrPath);
                }
            }
            catch (Exception cleanEx)
            {
                SystemLogQueue.Log("warning", $"[Static Deploy Cleanup] Hata sonrası temizlik yapılamadı: {cleanEx.Message}");
            }

            if (existingProject == null)
            {
                _dbContext.Projects.Remove(project);
            }
            else
            {
                MarkError(project);
                project.ImageOrPath = string.Empty;
            }
            await _dbContext.SaveChangesAsync();
            return StatusCode(500, new { Message = $"Statik deploy hatası: {ex.Message}" });
        }
    }


    [HttpPost("{id}/start")]
    public async Task<IActionResult> Start(Guid id)
    {
        var project = await _dbContext.Projects.FindAsync(id);
        if (project == null) return NotFound();

        if (!IsAdmin() && project.UserId != GetUserId()) return Forbid();

        ProcessTransitionTracker.StartTransition(project.Name);
        try
        {
            // Bakım modu aktifse subdomain konfigürasyonlarını önce eski haline döndür
            if (project.ActiveMaintenancePageId != null)
            {
                await DeactivateMaintenanceModeForProjectAsync(project);
                project.ActiveMaintenancePageId = null;
            }

            if (project.Type == ProjectType.DockerContainer)
            {
                if (string.IsNullOrEmpty(project.DockerContainerId))
                    return BadRequest(new { Message = "Konteynerin gerçek Docker ID'si bulunmuyor!" });

                await _containerService.StartContainerAsync(project.DockerContainerId);
            }
            else if (project.Type == ProjectType.NativeProject)
            {
                await _processManagerService.StartProcessAsync(project.Name);
            }

            MarkRunning(project);
            await _dbContext.SaveChangesAsync();
            await LogAuditAsync("ContainerStarted", "Project", project.Id, "{}");
            return Ok(new { Message = "Proje başarıyla başlatıldı." });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Message = ex.Message });
        }
        finally
        {
            ProcessTransitionTracker.EndTransition(project.Name);
        }
    }

    [HttpPost("{id}/stop")]
    public async Task<IActionResult> Stop(Guid id, [FromBody] StopProjectRequest? request = null)
    {
        var project = await _dbContext.Projects.FindAsync(id);
        if (project == null) return NotFound();

        if (!IsAdmin() && project.UserId != GetUserId()) return Forbid();

        ProcessTransitionTracker.StartTransition(project.Name);
        try
        {
            if (project.Type == ProjectType.DockerContainer)
            {
                if (string.IsNullOrEmpty(project.DockerContainerId))
                    return BadRequest(new { Message = "Konteynerin gerçek Docker ID'si bulunmuyor!" });

                await _containerService.StopContainerAsync(project.DockerContainerId);
            }
            else if (project.Type == ProjectType.NativeProject)
            {
                await _processManagerService.StopProcessAsync(project.Name);
            }

            MarkStopped(project);

            // Proje durdurulduğunda tüm bağlı subdomain'leri Nginx üzerinde bakım moduna al (Cloudflare 502 önlenir)
            await ActivateMaintenanceModeForProjectAsync(project, request);

            await _dbContext.SaveChangesAsync();
            await LogAuditAsync("ContainerStopped", "Project", project.Id, "{}");
            return Ok(new { Message = "Proje durduruldu ve Nginx bakım sayfaları yayına alındı." });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Message = ex.Message });
        }
        finally
        {
            ProcessTransitionTracker.EndTransition(project.Name);
        }
    }

    public record StopProjectRequest(Guid? MaintenancePageId, Dictionary<Guid, Guid?>? SubdomainPages = null);

    [HttpPost("bulk-maintenance")]
    public async Task<IActionResult> BulkMaintenance([FromBody] BulkMaintenanceRequest request)
    {
        var userId = GetUserId();
        var query = _dbContext.Projects.AsQueryable();
        if (!IsAdmin())
        {
            query = query.Where(p => p.UserId == userId);
        }

        var projects = await query.ToListAsync();
        var defaultTemplate = await _dbContext.MaintenancePages.OrderBy(p => p.CreatedAt).FirstOrDefaultAsync();

        DockerPanel.Domain.Entities.MaintenancePage? selectedPage = null;
        if (request.MaintenancePageId.HasValue)
        {
            selectedPage = await _dbContext.MaintenancePages.FindAsync(request.MaintenancePageId.Value);
        }
        selectedPage ??= defaultTemplate;

        int processedCount = 0;

        if (string.Equals(request.Action, "activate", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var proj in projects)
            {
                try
                {
                    if (proj.Status == ProjectStatus.Running)
                    {
                        if (proj.Type == ProjectType.DockerContainer && !string.IsNullOrEmpty(proj.DockerContainerId))
                        {
                            await _containerService.StopContainerAsync(proj.DockerContainerId);
                        }
                        else if (proj.Type == ProjectType.NativeProject)
                        {
                            await _processManagerService.StopProcessAsync(proj.Name);
                        }
                        MarkStopped(proj);
                    }

                    await ActivateMaintenanceModeForProjectAsync(proj, new StopProjectRequest(selectedPage?.Id));
                    processedCount++;
                }
                catch (Exception ex)
                {
                    SystemLogQueue.Log("error", $"[Bulk Maintenance] {proj.Name} bakıma alınırken hata: {ex.Message}");
                }
            }

            await _dbContext.SaveChangesAsync();
            await LogAuditAsync("BulkMaintenanceActivated", "Project", null, JsonSerializer.Serialize(new { count = processedCount }));
            return Ok(new { Message = $"{processedCount} proje durduruldu ve Nginx bakım sayfaları yayına alındı." });
        }
        else if (string.Equals(request.Action, "deactivate", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var proj in projects)
            {
                try
                {
                    await DeactivateMaintenanceModeForProjectAsync(proj);

                    if (proj.Status == ProjectStatus.Stopped)
                    {
                        if (proj.Type == ProjectType.DockerContainer && !string.IsNullOrEmpty(proj.DockerContainerId))
                        {
                            await _containerService.StartContainerAsync(proj.DockerContainerId);
                        }
                        else if (proj.Type == ProjectType.NativeProject)
                        {
                            await _processManagerService.StartProcessAsync(proj.Name);
                        }
                        MarkRunning(proj);
                    }
                    processedCount++;
                }
                catch (Exception ex)
                {
                    SystemLogQueue.Log("error", $"[Bulk Maintenance] {proj.Name} bakımdan çıkarılırken hata: {ex.Message}");
                }
            }

            await _dbContext.SaveChangesAsync();
            await LogAuditAsync("BulkMaintenanceDeactivated", "Project", null, JsonSerializer.Serialize(new { count = processedCount }));
            return Ok(new { Message = $"{processedCount} projenin bakım modu kaldırıldı ve yeniden başlatıldı." });
        }

        return BadRequest(new { Message = "Geçersiz eylem. 'activate' veya 'deactivate' kullanın." });
    }

    public record BulkMaintenanceRequest(Guid? MaintenancePageId, string Action);

    [HttpPost("panic-stop")]
    public async Task<IActionResult> PanicStop()
    {
        var userId = GetUserId();
        var query = _dbContext.Projects.Where(p => p.Status == ProjectStatus.Running);

        if (!IsAdmin())
        {
            query = query.Where(p => p.UserId == userId);
        }

        var runningProjects = await query.ToListAsync();
        int stoppedCount = 0;
        int failedCount = 0;

        foreach (var project in runningProjects)
        {
            ProcessTransitionTracker.StartTransition(project.Name);
            try
            {
                if (project.Type == ProjectType.DockerContainer && !string.IsNullOrEmpty(project.DockerContainerId))
                {
                    await _containerService.StopContainerAsync(project.DockerContainerId);
                }
                else if (project.Type == ProjectType.NativeProject)
                {
                    await _processManagerService.StopProcessAsync(project.Name);
                }
                MarkStopped(project);
                stoppedCount++;
            }
            catch (Exception ex)
            {
                SystemLogQueue.Log("error", $"[Panic-Stop] {project.Name} durdurulamadı: {ex.Message}");
                failedCount++;
            }
            finally
            {
                ProcessTransitionTracker.EndTransition(project.Name);
            }
        }

        await _dbContext.SaveChangesAsync();
        await LogAuditAsync("PanicStopTriggered", "System", null, $"{{\"StoppedCount\":{stoppedCount},\"FailedCount\":{failedCount}}}");

        return Ok(new { Message = $"Acil durum kilidi tetiklendi. {stoppedCount} adet proje başarıyla durduruldu, {failedCount} adet başarısız." });
    }

    [HttpPost("{id}/restart")]
    public async Task<IActionResult> Restart(Guid id)
    {
        var project = await _dbContext.Projects.FindAsync(id);
        if (project == null) return NotFound();

        if (!IsAdmin() && project.UserId != GetUserId()) return Forbid();

        ProcessTransitionTracker.StartTransition(project.Name);
        try
        {
            if (project.Type == ProjectType.DockerContainer)
            {
                if (string.IsNullOrEmpty(project.DockerContainerId))
                    return BadRequest(new { Message = "Konteynerin gerçek Docker ID'si bulunmuyor!" });

                await _containerService.StopContainerAsync(project.DockerContainerId);
                await _containerService.StartContainerAsync(project.DockerContainerId);
            }
            else if (project.Type == ProjectType.NativeProject)
            {
                await _processManagerService.AddOrUpdateProcessConfigAsync(project.Name, project.HostPort);
                await _processManagerService.RestartProcessAsync(project.Name);
            }

            MarkRunning(project);
            await _dbContext.SaveChangesAsync();
            await LogAuditAsync("ContainerRestarted", "Project", project.Id, "{}");
            return Ok(new { Message = "Proje başarıyla yeniden başlatıldı." });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Message = ex.Message });
        }
        finally
        {
            ProcessTransitionTracker.EndTransition(project.Name);
        }
    }

    // Image'ın EXPOSE ettiği portu dönen endpoint
    [HttpPost("image-exposed-port")]
    [DisableRateLimiting]
    public async Task<IActionResult> GetImageExposedPort([FromBody] ImageExposedPortRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ImageName))
            return BadRequest(new { Message = "Image adı boş olamaz." });

        var port = await _containerService.GetImageExposedPortAsync(request.ImageName);
        return Ok(new { ExposedPort = port });
    }

    [HttpPut("{id}/limits")]
    public async Task<IActionResult> UpdateLimits(Guid id, [FromBody] UpdateProjectLimitsRequest request)
    {
        if (request.MemoryLimitBytes < 64L * 1024 * 1024)
        {
            return BadRequest(new { Message = "RAM limiti en az 64 MB olmalıdır." });
        }

        if (request.CpuCount < 0.1 || request.CpuCount > 64)
        {
            return BadRequest(new { Message = "CPU limiti 0.1 ile 64 çekirdek arasında olmalıdır." });
        }

        var project = await _dbContext.Projects.FindAsync(id);
        if (project == null) return NotFound();

        if (!IsAdmin() && project.UserId != GetUserId()) return Forbid();

        try
        {
            if (project.Type == ProjectType.DockerContainer)
            {
                if (string.IsNullOrEmpty(project.DockerContainerId))
                    return BadRequest(new { Message = "Konteynerin gerçek Docker ID'si bulunmuyor!" });

                await _containerService.UpdateContainerLimitsAsync(project.DockerContainerId, request.MemoryLimitBytes, request.CpuCount);
            }

            project.MemoryLimitBytes = request.MemoryLimitBytes;
            project.CpuCount = request.CpuCount;
            await _dbContext.SaveChangesAsync();

            await LogAuditAsync("LimitsUpdated", "Project", project.Id, JsonSerializer.Serialize(new { memory = project.MemoryLimitBytes, cpu = project.CpuCount }));

            return Ok(new { Message = "Proje kaynak limitleri güncellendi." });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Message = ex.Message });
        }
    }

    // 3. Tüm Projeleri Yeniden Başlatma Endpointi (Restart All)
    [HttpPost("restart-all")]
    public async Task<IActionResult> RestartAll()
    {
        var userId = GetUserId();
        var query = _dbContext.Projects.AsQueryable();

        if (!IsAdmin())
        {
            query = query.Where(p => p.UserId == userId);
        }

        var activeProjects = await query.ToListAsync();
        if (!activeProjects.Any())
        {
            return Ok(new { Message = "Yeniden başlatılacak aktif proje bulunmuyor." });
        }

        // Run entire restart logic asynchronously to avoid Cloudflare/Nginx 504 Gateway Timeout
        _ = Task.Run(async () =>
        {
            try
            {
                using (var scope = _scopeFactory.CreateScope())
                {
                    var dbContext = scope.ServiceProvider.GetRequiredService<DockerPanelDbContext>();
                    var containerService = scope.ServiceProvider.GetRequiredService<IProjectContainerService>();
                    var processManagerService = scope.ServiceProvider.GetRequiredService<IProcessManagerService>();

                    // Fetch actual tracking records within scope to prevent EF core concurrency issues
                    var projectIds = activeProjects.Select(p => p.Id).ToList();
                    var bgProjects = await dbContext.Projects.Where(p => projectIds.Contains(p.Id)).ToListAsync();

                    int successCount = 0;
                    var failedProjects = new System.Collections.Generic.List<string>();

                    // 1. Native projects orchestration (if any)
                    bool nativeRestartSuccessful = false;
                    bool hasNativeProjects = bgProjects.Any(p => p.Type == ProjectType.NativeProject);
                    bool nativeRestartAttempted = false;

                    if (hasNativeProjects)
                    {
                        SystemLogQueue.Log("info", "[Bulk Restart] Native projelerin yapılandırmaları güncelleniyor ve toplu restart tetikleniyor...");
                        try
                        {
                            foreach (var project in bgProjects.Where(p => p.Type == ProjectType.NativeProject))
                            {
                                await processManagerService.AddOrUpdateProcessConfigAsync(project.Name, project.HostPort);
                            }

                            nativeRestartAttempted = true;
                            await processManagerService.RestartAllProcessesAsync();
                            nativeRestartSuccessful = true;
                            SystemLogQueue.Log("info", "[Bulk Restart] Native projelerin toplu restart script tetiklemesi başarılı.");
                        }
                        catch (Exception ex)
                        {
                            SystemLogQueue.Log("error", $"[Bulk Restart] Native projeler toplu yeniden başlatılırken hata oluştu: {ex.Message}");
                        }
                    }

                    // 2. Sequential start status checks and Docker container restarts
                    foreach (var project in bgProjects)
                    {
                        try
                        {
                            var localProj = await dbContext.Projects.FindAsync(project.Id);
                            if (localProj == null) continue;

                            if (project.Type == ProjectType.DockerContainer && !string.IsNullOrEmpty(project.DockerContainerId))
                            {
                                SystemLogQueue.Log("info", $"[Bulk Restart] '{project.Name}' Docker projesi güvenli şekilde yeniden başlatılıyor...");
                                await containerService.StopContainerAsync(project.DockerContainerId);
                                await containerService.StartContainerAsync(project.DockerContainerId);
                                MarkRunning(localProj);
                                successCount++;
                            }
                            else if (project.Type == ProjectType.NativeProject)
                            {
                                if (nativeRestartAttempted && nativeRestartSuccessful)
                                {
                                    var isRunning = await processManagerService.IsProcessRunningAsync(project.Name);
                                    if (isRunning)
                                    {
                                        MarkRunning(localProj);
                                        successCount++;
                                    }
                                    else
                                    {
                                        try
                                        {
                                            await processManagerService.StartProcessAsync(project.Name);
                                            await Task.Delay(500);
                                            isRunning = await processManagerService.IsProcessRunningAsync(project.Name);
                                        }
                                        catch (Exception ex)
                                        {
                                            SystemLogQueue.Log("error", $"[Bulk Restart] '{project.Name}' tekil start denemesi başarısız: {ex.Message}");
                                        }

                                        if (isRunning)
                                        {
                                            MarkRunning(localProj);
                                            successCount++;
                                        }
                                        else
                                        {
                                            MarkStopped(localProj);
                                            failedProjects.Add(project.Name);
                                        }
                                    }
                                }
                                else
                                {
                                    MarkError(localProj);
                                    failedProjects.Add(project.Name);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            SystemLogQueue.Log("error", $"[Bulk Restart] '{project.Name}' yeniden başlatılırken hata: {ex.Message}");
                            var localProj = await dbContext.Projects.FindAsync(project.Id);
                            if (localProj != null) MarkError(localProj);
                            failedProjects.Add(project.Name);
                        }
                    }

                    await dbContext.SaveChangesAsync();
                    
                    if (failedProjects.Count > 0)
                    {
                        SystemLogQueue.Log("warning", $"[Bulk Restart] Toplu yeniden başlatma tamamlandı fakat {failedProjects.Count} adet proje başlatılamadı: {string.Join(", ", failedProjects)}");
                    }
                    else
                    {
                        SystemLogQueue.Log("info", $"[Bulk Restart] Toplu yeniden başlatma başarıyla tamamlandı. Yeniden başlatılan proje sayısı: {successCount}");
                    }
                }
            }
            catch (Exception ex)
            {
                SystemLogQueue.Log("error", $"[Bulk Restart] Toplu yeniden başlatma arka plan görevi çöktü: {ex.Message}");
            }
        });

        return Ok(new { Message = "Toplu yeniden başlatma orkestrasyonu arka planda başlatıldı. Sunucu loglarını terminal konsolundan canlı izleyebilirsiniz." });
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(Guid id, [FromQuery] bool deleteDb = true)
    {
        var project = await _dbContext.Projects.FindAsync(id);
        if (project == null) return NotFound();

        if (!IsAdmin() && project.UserId != GetUserId()) return Forbid();

        try
        {
            // Projeye ait tüm subdomain yönlendirmelerini bul ve Nginx fiziksel konfigürasyonlarını sil
            var linkedSubdomains = await _dbContext.Subdomains
                .Where(s => s.ProjectId == id)
                .ToListAsync();

            foreach (var sub in linkedSubdomains)
            {
                try
                {
                    await _nginxService.DeleteSubdomainAsync(sub.SubdomainName, sub.DomainName);
                }
                catch (Exception nginxEx)
                {
                    SystemLogQueue.Log("warning", $"[Delete Project] Projeye bağlı {sub.SubdomainName}.{sub.DomainName} subdomain Nginx konfigürasyonu silinemedi: {nginxEx.Message}");
                }
            }

            var linkedDnsRecords = await _dbContext.DnsRecords
                .Where(d => d.ProjectId == id)
                .ToListAsync();

            foreach (var dnsRecord in linkedDnsRecords)
            {
                await DeleteLinkedDnsRecordAsync(dnsRecord);
            }

            var linkedDatabases = await _dbContext.DatabaseSchemas
                .Where(d => d.ProjectId == id)
                .ToListAsync();

            foreach (var database in linkedDatabases)
            {
                if (deleteDb)
                {
                    await _databaseService.DeleteDatabaseAsync(database.DbName, database.DbUser);
                    _dbContext.DatabaseSchemas.Remove(database);
                }
                else
                {
                    database.ProjectId = null;
                    _dbContext.Entry(database).State = EntityState.Modified;
                }
            }

            if (project.Type == ProjectType.DockerContainer)
            {
                if (!string.IsNullOrEmpty(project.DockerContainerId))
                {
                    await _containerService.DeleteContainerAsync(project.DockerContainerId);
                }
            }
            else if (project.Type == ProjectType.NativeProject)
            {
                try
                {
                    await _processManagerService.StopProcessAsync(project.Name);
                }
                catch (Exception stopEx)
                {
                    SystemLogQueue.Log("warning", $"[Delete Project] Proje durdurulurken hata oluştu (muhtemelen zaten durmuştu): {stopEx.Message}");
                }

                await _processManagerService.DeleteProcessConfigAsync(project.Name);

                // Fiziksel dizini sil
                SafeDeleteDirectory(project.ImageOrPath);
            }
            else if (project.Type == ProjectType.StaticSite)
            {
                // Fiziksel dizini sil
                SafeDeleteDirectory(project.ImageOrPath);
            }

            _dbContext.Projects.Remove(project);
            await _dbContext.SaveChangesAsync();
            await LogAuditAsync("ContainerDeleted", "Project", project.Id, "{}");
            return Ok(new { Message = "Proje başarıyla yok edildi." });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Message = ex.Message });
        }
    }

    [HttpGet("{id}/logs")]
    [DisableRateLimiting]
    public async Task<IActionResult> GetLogs(Guid id, [FromQuery] int tail = 100)
    {
        var project = await _dbContext.Projects.FindAsync(id);
        if (project == null) return NotFound();

        if (!IsAdmin() && project.UserId != GetUserId()) return Forbid();

        try
        {
            if (project.Type == ProjectType.DockerContainer)
            {
                if (string.IsNullOrEmpty(project.DockerContainerId))
                    return BadRequest(new { Message = "Konteynerin gerçek Docker ID'si yok!" });

                var logs = await _containerService.GetContainerLogsAsync(project.DockerContainerId, tail);
                return Ok(logs);
            }
            else if (project.Type == ProjectType.NativeProject)
            {
                var logs = await _processManagerService.GetProcessLogsAsync(project.Name, tail);
                return Ok(logs);
            }
            else
            {
                return Ok(new[] { "[Statik Web Sitesi] Bu proje doğrudan Nginx tarafından sunulmaktadır. Aktif bir arka plan süreci bulunmadığı için çalışma zamanı logu üretilmez." });
            }
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Message = ex.Message });
        }
    }

    private void SafeDeleteDirectory(string path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;

        try
        {
            var directory = new DirectoryInfo(path);
            
            // Clear Read-Only attribute on all files recursively
            foreach (var file in directory.GetFiles("*", SearchOption.AllDirectories))
            {
                try
                {
                    if (file.IsReadOnly)
                    {
                        file.IsReadOnly = false;
                    }
                }
                catch { /* Ignore attribute clear failures */ }
            }

            // Clear Read-Only attribute on all directories recursively
            foreach (var dir in directory.GetDirectories("*", SearchOption.AllDirectories))
            {
                try
                {
                    dir.Attributes &= ~FileAttributes.ReadOnly;
                }
                catch { /* Ignore attribute clear failures */ }
            }

            Directory.Delete(path, true);
        }
        catch (Exception ex)
        {
            if (Path.DirectorySeparatorChar == '/')
            {
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "sudo",
                        Arguments = $"/usr/local/bin/project-manager.sh clean-path \"{path}\"",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using var process = System.Diagnostics.Process.Start(psi);
                    process?.WaitForExit();

                    if (!Directory.Exists(path))
                    {
                        return; // Successfully deleted via fallback
                    }
                }
                catch { /* Ignore fallback errors */ }
            }

            SystemLogQueue.Log("warning", $"[Safe Delete Directory] Klasör tamamen silinemedi: {path}. Hata: {ex.Message}");
        }
    }
}

// ── Image ExposedPort Endpoint ──────────────────────────────────────────────
public class ImageExposedPortRequest
{
    public string ImageName { get; set; } = string.Empty;
}

public class CreateContainerRequest
{
    public string Name { get; set; } = string.Empty;
    public string ImageName { get; set; } = string.Empty;
    public long MemoryLimitBytes { get; set; }
    public double CpuCount { get; set; }
    /// <summary>Dışarıya/Nginx'e açılan port (örn. 8080)</summary>
    public int HostPort { get; set; }
    /// <summary>Image içinde dinlenen port. Boşsa sistem EXPOSE'u okur, o da yoksa HostPort kullanılır.</summary>
    public int? ContainerPort { get; set; }
}

public class UpdateProjectLimitsRequest
{
    public long MemoryLimitBytes { get; set; }
    public double CpuCount { get; set; }
}

public class DeployNativeRequest
{
    public string Name { get; set; } = string.Empty;
    public long MemoryLimitBytes { get; set; }
    public double CpuCount { get; set; }
    /// <summary>Native projenin dinleyeceği port</summary>
    public int HostPort { get; set; }
    public IFormFile ZipFile { get; set; } = null!;
    public string? RuntimeType { get; set; }
    public string? EntryFile { get; set; }
    public string? CustomCommand { get; set; }
}

public class DeployStaticRequest
{
    public string Name { get; set; } = string.Empty;
    public IFormFile ZipFile { get; set; } = null!;
    public bool EnablePhp { get; set; }
}
