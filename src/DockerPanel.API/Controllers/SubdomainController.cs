using System;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using DockerPanel.API.Helpers;
using DockerPanel.Domain.Entities;
using DockerPanel.Domain.Enums;
using DockerPanel.Domain.Interfaces;
using DockerPanel.Infrastructure.Data;

namespace DockerPanel.API.Controllers;

[Authorize]
[ApiController]
[Route("api/nginx")]
[EnableRateLimiting("resource-heavy")]
public class SubdomainController : ControllerBase
{
    private readonly DockerPanelDbContext _dbContext;
    private readonly INginxService _nginxService;
    private readonly IAuditLogService _auditLogService;

    public SubdomainController(DockerPanelDbContext dbContext, INginxService nginxService, IAuditLogService auditLogService)
    {
        _dbContext = dbContext;
        _nginxService = nginxService;
        _auditLogService = auditLogService;
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

    [HttpGet]
    [DisableRateLimiting]
    public async Task<IActionResult> GetAll()
    {
        var userId = GetUserId();
        var query = _dbContext.Subdomains.Include(s => s.Project).AsQueryable();

        if (!IsAdmin())
        {
            query = query.Where(s => s.UserId == userId);
        }

        var subdomains = await query.OrderByDescending(s => s.CreatedAt).ToListAsync();
        
        // Frontend'in beklediği DTO formatına map edelim
        var result = subdomains.Select(s => new
        {
            s.Id,
            s.SubdomainName,
            s.DomainName,
            s.SslEnabled,
            s.ProjectId,
            ContainerName = s.Project?.Name ?? "Bağımsız / Dış Servis",
            ContainerPort = s.Project?.HostPort ?? 80, // Eğer proje yoksa varsayılan port
            s.CreatedAt
        });

        return Ok(result);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateSubdomainRequest request)
    {
        // 1. Girdi Güvenlik Regex Denetimleri
        if (!SecurityHelper.IsValidSubdomainName(request.SubdomainName))
        {
            return BadRequest(new { Message = "Alt alan adı ön eki sadece harf, rakam, alt çizgi (_) ve tire (-) içerebilir!" });
        }

        // Domain adını doğrula (e.g. domain.com)
        if (string.IsNullOrWhiteSpace(request.DomainName) || !request.DomainName.Contains('.'))
        {
            return BadRequest(new { Message = "Geçersiz ana alan adı!" });
        }

        var userId = GetUserId();

        // 2. Mükerrerlik Kontrolü (Unique Constraint) - Retry Idempotency
        var existingSubdomain = await _dbContext.Subdomains.FirstOrDefaultAsync(s => 
            s.SubdomainName.ToLower() == request.SubdomainName.ToLower() && 
            s.DomainName.ToLower() == request.DomainName.ToLower());

        if (existingSubdomain != null)
        {
            if (existingSubdomain.UserId != userId)
            {
                return BadRequest(new { Message = "Bu alt alan adı yönlendirmesi başka bir kullanıcı tarafından zaten kullanılıyor!" });
            }

            _dbContext.Subdomains.Remove(existingSubdomain);
            await _dbContext.SaveChangesAsync();
        }

        // 3. Hedef Proje Yükleme (İsteğe bağlı)
        Project? project = null;
        int targetPort = request.ExternalPort ?? 80;

        if (request.ProjectId.HasValue && request.ProjectId.Value != Guid.Empty)
        {
            project = await _dbContext.Projects.FindAsync(request.ProjectId.Value);
            if (project == null)
            {
                return BadRequest(new { Message = "Hedef proje bulunamadı!" });
            }

            if (!IsAdmin() && project.UserId != userId)
            {
                return Forbid();
            }
            targetPort = project.HostPort;
        }

        // 4. Yeni Kayıt Taslağı
        var subdomain = new Subdomain
        {
            UserId = userId,
            ProjectId = request.ProjectId == Guid.Empty ? null : request.ProjectId,
            SubdomainName = request.SubdomainName.ToLower(),
            DomainName = request.DomainName.ToLower(),
            SslEnabled = request.SslEnabled,
            CreatedAt = DateTimeOffset.UtcNow
        };

        // 5. Nginx Proxy Konfigürasyonu Oluştur ve Test Et (Zero-Downtime Reload)
        using var transaction = await _dbContext.Database.BeginTransactionAsync();
        try
        {
            _dbContext.Subdomains.Add(subdomain);
            await _dbContext.SaveChangesAsync();

            // Nginx Gateway ile yönlendirmeyi aktif et
            await _nginxService.ProvisionSubdomainAsync(
                subdomain.SubdomainName,
                subdomain.DomainName,
                project?.Name ?? "external_service",
                targetPort,
                project?.Type ?? ProjectType.DockerContainer,
                project?.ImageOrPath,
                project?.EnablePhp,
                subdomain.SslEnabled
            );

            await transaction.CommitAsync();

            await LogAuditAsync("SubdomainCreated", "Subdomain", subdomain.Id, JsonSerializer.Serialize(new
            {
                subdomain.SubdomainName,
                subdomain.DomainName,
                subdomain.SslEnabled,
                subdomain.ProjectId
            }));

            return Ok(new
            {
                subdomain.Id,
                subdomain.SubdomainName,
                subdomain.DomainName,
                subdomain.SslEnabled,
                subdomain.ProjectId,
                subdomain.CreatedAt
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SubdomainController Error] Nginx Provisioning Failed: {ex.ToString()}");
            try
            {
                await transaction.RollbackAsync();
            }
            catch (Exception rollbackEx)
            {
                Console.WriteLine($"[SubdomainController Error] DB Rollback Failed: {rollbackEx.Message}");
            }
            return StatusCode(500, new { Message = ex.Message });
        }
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateSubdomainRequest request)
    {
        var subdomain = await _dbContext.Subdomains.Include(s => s.Project).FirstOrDefaultAsync(s => s.Id == id);
        if (subdomain == null) return NotFound();

        var userId = GetUserId();
        if (!IsAdmin() && subdomain.UserId != userId) return Forbid();

        Project? project = null;
        int targetPort = request.ExternalPort ?? 80;

        if (request.ProjectId.HasValue && request.ProjectId.Value != Guid.Empty)
        {
            project = await _dbContext.Projects.FindAsync(request.ProjectId.Value);
            if (project == null)
            {
                return BadRequest(new { Message = "Hedef proje bulunamadı!" });
            }

            if (!IsAdmin() && project.UserId != userId)
            {
                return Forbid();
            }
            targetPort = project.HostPort;
        }

        using var transaction = await _dbContext.Database.BeginTransactionAsync();
        try
        {
            // DB güncellemesi
            subdomain.ProjectId = request.ProjectId == Guid.Empty ? null : request.ProjectId;
            subdomain.SslEnabled = request.SslEnabled;
            
            await _dbContext.SaveChangesAsync();

            // Nginx Yapılandırmasını yeniden yazıp güncelle
            await _nginxService.ProvisionSubdomainAsync(
                subdomain.SubdomainName,
                subdomain.DomainName,
                project?.Name ?? "external_service",
                targetPort,
                project?.Type ?? ProjectType.DockerContainer,
                project?.ImageOrPath,
                project?.EnablePhp,
                subdomain.SslEnabled
            );

            await transaction.CommitAsync();
            return Ok(new
            {
                subdomain.Id,
                subdomain.SubdomainName,
                subdomain.DomainName,
                subdomain.SslEnabled,
                subdomain.ProjectId,
                subdomain.CreatedAt
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SubdomainController Error] Nginx Update Failed: {ex.ToString()}");
            try
            {
                await transaction.RollbackAsync();
            }
            catch (Exception rollbackEx)
            {
                Console.WriteLine($"[SubdomainController Error] DB Rollback Failed: {rollbackEx.Message}");
            }
            return StatusCode(500, new { Message = ex.Message });
        }
    }

    [HttpPost("{id}/ssl")]
    public async Task<IActionResult> EnableSsl(Guid id)
    {
        var subdomain = await _dbContext.Subdomains
            .Include(s => s.Project)
            .FirstOrDefaultAsync(s => s.Id == id);
            
        if (subdomain == null) return NotFound();

        if (!IsAdmin() && subdomain.UserId != GetUserId()) return Forbid();

        try
        {
            // Certbot SSL komutunu çalıştır
            await _nginxService.EnableSslWithCertbotAsync(subdomain.SubdomainName, subdomain.DomainName);

            // Veritabanında SSL durumunu güncelle
            subdomain.SslEnabled = true;
            await _dbContext.SaveChangesAsync();

            // Nginx konfigürasyonunu SSL ile yeniden yapılandır
            var project = subdomain.Project;
            int targetPort = project?.HostPort ?? 80;

            await _nginxService.ProvisionSubdomainAsync(
                subdomain.SubdomainName,
                subdomain.DomainName,
                project?.Name ?? "external_service",
                targetPort,
                project?.Type ?? ProjectType.DockerContainer,
                project?.ImageOrPath,
                project?.EnablePhp,
                true // sslEnabled
            );

            return Ok(new
            {
                Message = "Let's Encrypt SSL sertifikası başarıyla kuruldu!",
                Subdomain = new
                {
                    subdomain.Id,
                    subdomain.SubdomainName,
                    subdomain.DomainName,
                    subdomain.SslEnabled,
                    subdomain.ProjectId,
                    subdomain.CreatedAt
                }
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Message = $"Certbot SSL sertifikası üretilemedi: {ex.Message}" });
        }
    }

    [HttpPost("sync")]
    public async Task<IActionResult> SyncConfigs()
    {
        if (!IsAdmin()) return Forbid();

        try
        {
            var adminId = GetUserId();
            await _nginxService.SyncActiveConfigsWithDbAsync(adminId);
            return Ok(new { Message = "Nginx yapılandırmaları başarıyla taranıp sisteme eşitleme yapıldı." });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Message = ex.Message });
        }
    }

    [HttpPost("rebuild")]
    public async Task<IActionResult> RebuildConfigs()
    {
        if (!IsAdmin()) return Forbid();

        try
        {
            var subdomains = await _dbContext.Subdomains
                .Include(s => s.Project)
                .ToListAsync();

            int successCount = 0;
            int failCount = 0;
            var errors = new List<string>();

            foreach (var sub in subdomains)
            {
                try
                {
                    int targetPort = sub.Project?.HostPort ?? 80;
                    
                    await _nginxService.ProvisionSubdomainAsync(
                        sub.SubdomainName,
                        sub.DomainName,
                        sub.Project?.Name ?? "external_service",
                        targetPort,
                        sub.Project?.Type ?? ProjectType.DockerContainer,
                        sub.Project?.ImageOrPath,
                        sub.Project?.EnablePhp,
                        sub.SslEnabled,
                        reloadNginx: false
                    );
                    successCount++;
                }
                catch (Exception ex)
                {
                    failCount++;
                    errors.Add($"{sub.SubdomainName}.{sub.DomainName}: {ex.Message}");
                }
            }

            if (successCount > 0)
            {
                try
                {
                    await _nginxService.ReloadNginxAsync();
                }
                catch (Exception reloadEx)
                {
                    errors.Add($"Toplu reload hatasi: {reloadEx.Message}");
                }
            }

            return Ok(new 
            { 
                Message = $"Yeniden yapılandırma tamamlandı. Başarılı: {successCount}, Başarısız: {failCount}",
                SuccessCount = successCount,
                FailCount = failCount,
                Errors = errors
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Message = ex.Message });
        }
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var subdomain = await _dbContext.Subdomains.Include(s => s.Project).FirstOrDefaultAsync(s => s.Id == id);
        if (subdomain == null) return NotFound();

        if (!IsAdmin() && subdomain.UserId != GetUserId()) return Forbid();

        try
        {
            // Nginx konfigürasyonunu sil ve reload et
            await _nginxService.DeleteSubdomainAsync(subdomain.SubdomainName, subdomain.DomainName);

            _dbContext.Subdomains.Remove(subdomain);
            await _dbContext.SaveChangesAsync();

            await LogAuditAsync("SubdomainDeleted", "Subdomain", subdomain.Id, "{}");

            return Ok(new { Message = "Alt alan adı yönlendirmesi başarıyla kaldırıldı." });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Message = ex.Message });
        }
    }
}

public class CreateSubdomainRequest
{
    public Guid? ProjectId { get; set; }
    public string SubdomainName { get; set; } = string.Empty;
    public string DomainName { get; set; } = string.Empty;
    public bool SslEnabled { get; set; } = true;
    public int? ExternalPort { get; set; }
}

public class UpdateSubdomainRequest
{
    public Guid? ProjectId { get; set; }
    public bool SslEnabled { get; set; }
    public int? ExternalPort { get; set; }
}
