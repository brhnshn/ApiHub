using System;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DockerPanel.Domain.Entities;
using DockerPanel.Domain.Enums;
using DockerPanel.Domain.Interfaces;
using DockerPanel.Infrastructure.Data;

namespace DockerPanel.API.Controllers;

[Authorize]
[ApiController]
[Route("api/dns")]
public class DnsController : ControllerBase
{
    private readonly DockerPanelDbContext _dbContext;
    private readonly ICloudflareService _cloudflareService;
    private readonly IAuditLogService _auditLogService;

    public DnsController(DockerPanelDbContext dbContext, ICloudflareService cloudflareService, IAuditLogService auditLogService)
    {
        _dbContext = dbContext;
        _cloudflareService = cloudflareService;
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
    public async Task<IActionResult> GetAll()
    {
        var userId = GetUserId();

        // Her bir Cloudflare entegrasyonu olan RootDomain için gerçek zamanlı iki yönlü eşitleme yap
        var rootDomains = await _dbContext.RootDomains
            .Where(d => d.UserId == userId && !string.IsNullOrEmpty(d.CloudflareToken) && !string.IsNullOrEmpty(d.CloudflareZoneId))
            .ToListAsync();

        foreach (var domain in rootDomains)
        {
            try
            {
                var cfRecords = await _cloudflareService.ListDnsRecordsAsync(domain.CloudflareToken!, domain.CloudflareZoneId!);
                var localRecords = await _dbContext.DnsRecords.Where(d => d.UserId == userId && (d.Name == domain.Name || d.Name.EndsWith("." + domain.Name))).ToListAsync();

                // 1. Cloudflare'de olan ama lokalde eksik/farklı olanları güncelle veya ekle
                foreach (var cfRec in cfRecords)
                {
                    var existing = localRecords.FirstOrDefault(r => r.CloudflareRecordId == cfRec.Id);
                    if (existing != null)
                    {
                        // Alanları güncelle
                        existing.Type = cfRec.Type;
                        existing.Name = cfRec.Name;
                        existing.Value = cfRec.Content;
                        existing.Ttl = cfRec.Ttl;
                        existing.Proxied = cfRec.Proxied;
                    }
                    else
                    {
                        // CloudflareRecordId eşleşmeyen ama Name/Type/Value eşleşen var mı bak (kayıp bağları yakalamak için)
                        var unlinked = localRecords.FirstOrDefault(r => 
                            string.IsNullOrEmpty(r.CloudflareRecordId) && 
                            r.Type.Equals(cfRec.Type, StringComparison.OrdinalIgnoreCase) && 
                            r.Name.Equals(cfRec.Name, StringComparison.OrdinalIgnoreCase));
                        
                        if (unlinked != null)
                        {
                            unlinked.CloudflareRecordId = cfRec.Id;
                            unlinked.Value = cfRec.Content;
                            unlinked.Ttl = cfRec.Ttl;
                            unlinked.Proxied = cfRec.Proxied;
                        }
                        else
                        {
                            // Tamamen yeni
                            var newRecord = new DnsRecord
                            {
                                UserId = userId,
                                Type = cfRec.Type,
                                Name = cfRec.Name,
                                Value = cfRec.Content,
                                Ttl = cfRec.Ttl,
                                Proxied = cfRec.Proxied,
                                CloudflareRecordId = cfRec.Id
                            };
                            _dbContext.DnsRecords.Add(newRecord);
                        }
                    }
                }

                // 2. Lokalde Cloudflare Record ID'ye sahip olan ama Cloudflare'den silinmiş olanları temizle
                foreach (var localRec in localRecords)
                {
                    if (!string.IsNullOrEmpty(localRec.CloudflareRecordId))
                    {
                        var existsInCf = cfRecords.Any(r => r.Id == localRec.CloudflareRecordId);
                        if (!existsInCf)
                        {
                            _dbContext.DnsRecords.Remove(localRec);
                        }
                    }
                }

                await _dbContext.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                // Eşitleme başarısız olsa bile yerel verileri göstermeye devam et (arayüz çökmesin diye logla geç)
                Console.WriteLine($"[Cloudflare Sync Hatası] {domain.Name} için DNS eşitleme yapılamadı: {ex.Message}");
            }
        }

        var query = _dbContext.DnsRecords.AsQueryable();
        if (!IsAdmin())
        {
            query = query.Where(d => d.UserId == userId);
        }

        var records = await query.ToListAsync();
        return Ok(records);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateDnsRequest request)
    {
        var userId = GetUserId();
        var recordType = string.IsNullOrWhiteSpace(request.Type) ? "A" : request.Type.Trim().ToUpperInvariant();
        var recordName = request.Name.Trim().ToLowerInvariant();
        var recordValue = request.Value.Trim();

        if (string.IsNullOrWhiteSpace(recordName) || string.IsNullOrWhiteSpace(recordValue))
        {
            return BadRequest(new { Message = "DNS kayit adi ve hedef degeri zorunludur." });
        }

        Project? project = null;
        if (request.ProjectId.HasValue && request.ProjectId.Value != Guid.Empty)
        {
            project = await _dbContext.Projects.FindAsync(request.ProjectId.Value);
            if (project == null)
            {
                return BadRequest(new { Message = "Bağlanacak proje bulunamadı!" });
            }

            if (!IsAdmin() && project.UserId != userId)
            {
                return Forbid();
            }
        }

        var matchingLocalRecords = await _dbContext.DnsRecords
            .Where(d =>
                d.UserId == userId &&
                d.Type.ToLower() == recordType.ToLower() &&
                d.Name.ToLower() == recordName)
            .OrderByDescending(d => !string.IsNullOrEmpty(d.CloudflareRecordId))
            .ThenBy(d => d.Id)
            .ToListAsync();
        var existingLocalRecord = matchingLocalRecords.FirstOrDefault();

        string? cloudflareRecordId = null;

        // Eşleşen RootDomain bulup Cloudflare bilgilerini çek
        var rootDomain = await _dbContext.RootDomains
            .FirstOrDefaultAsync(rd => rd.UserId == userId && (recordName == rd.Name || recordName.EndsWith("." + rd.Name)));

        // Eğer Cloudflare token ve zone ID mevcutsa, API ile Cloudflare üzerinde kaydı aç
        if (request.UseCloudflare && rootDomain != null && !string.IsNullOrEmpty(rootDomain.CloudflareToken) && !string.IsNullOrEmpty(rootDomain.CloudflareZoneId))
        {
            try
            {
                var cfRecords = await _cloudflareService.ListDnsRecordsAsync(rootDomain.CloudflareToken, rootDomain.CloudflareZoneId);
                var existingCloudflareRecord = cfRecords.FirstOrDefault(r =>
                    r.Type.Equals(recordType, StringComparison.OrdinalIgnoreCase) &&
                    r.Name.Equals(recordName, StringComparison.OrdinalIgnoreCase));

                cloudflareRecordId = existingCloudflareRecord?.Id;

                if (string.IsNullOrWhiteSpace(cloudflareRecordId))
                {
                    cloudflareRecordId = await _cloudflareService.CreateDnsRecordAsync(
                        rootDomain.CloudflareToken,
                        rootDomain.CloudflareZoneId,
                        recordType,
                        recordName,
                        recordValue,
                        request.Proxied
                    );
                }
                else
                {
                    recordValue = existingCloudflareRecord!.Content;
                    request.Ttl = existingCloudflareRecord.Ttl;
                    request.Proxied = existingCloudflareRecord.Proxied;
                }
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = $"Cloudflare DNS oluşturma hatası: {ex.Message}" });
            }
        }

        var record = existingLocalRecord ?? new DnsRecord
        {
            UserId = userId,
            Type = recordType,
            Name = recordName
        };

        record.Type = recordType;
        record.Name = recordName;
        record.Value = recordValue;
        record.Ttl = request.Ttl;
        record.Proxied = request.Proxied;
        record.CloudflareRecordId = cloudflareRecordId ?? record.CloudflareRecordId;
        if (project != null && record.ProjectId == null)
        {
            record.ProjectId = project.Id;
        }

        if (existingLocalRecord == null)
        {
            _dbContext.DnsRecords.Add(record);
        }
        else if (matchingLocalRecords.Count > 1)
        {
            _dbContext.DnsRecords.RemoveRange(matchingLocalRecords.Skip(1));
        }

        await _dbContext.SaveChangesAsync();

        await LogAuditAsync("DnsRecordCreated", "DnsRecord", record.Id, JsonSerializer.Serialize(new
        {
            record.Type,
            record.Name,
            record.Value,
            record.Ttl,
            record.Proxied,
            record.ProjectId,
            record.CloudflareRecordId
        }));

        return Ok(record);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var record = await _dbContext.DnsRecords.FindAsync(id);
        if (record == null) return NotFound();

        if (!IsAdmin() && record.UserId != GetUserId()) return Forbid();

        try
        {
            var rootDomain = await _dbContext.RootDomains
                .FirstOrDefaultAsync(rd => rd.UserId == record.UserId && (record.Name == rd.Name || record.Name.EndsWith("." + rd.Name)));

            // Eğer Cloudflare kaydıysa ve token/zone geçilmişse Cloudflare'den kaldır
            if (!string.IsNullOrEmpty(record.CloudflareRecordId) && rootDomain != null && !string.IsNullOrEmpty(rootDomain.CloudflareToken) && !string.IsNullOrEmpty(rootDomain.CloudflareZoneId))
            {
                try
                {
                    await _cloudflareService.DeleteDnsRecordAsync(rootDomain.CloudflareToken, rootDomain.CloudflareZoneId, record.CloudflareRecordId);
                }
                catch (Exception cfEx)
                {
                    string errMsg = cfEx.Message.ToLower();
                    // Zaten silinmişse (404 NotFound veya 81044 hatası) veya silme başarısız olsa bile
                    // yerel veritabanımızdan güvenle kaldırmak için bu hatayı tolere et
                    if (errMsg.Contains("81044") || errMsg.Contains("notfound") || errMsg.Contains("not found") || errMsg.Contains("exist"))
                    {
                        Console.WriteLine($"[Cloudflare Sync] Kayıt zaten Cloudflare üzerinden silinmiş, lokal temizlik yapılıyor: {record.Name}");
                    }
                    else
                    {
                        // Farklı bir kritik hata (örneğin auth veya internet kaybı) durumunda hata fırlat
                        throw;
                    }
                }
            }

            _dbContext.DnsRecords.Remove(record);
            await _dbContext.SaveChangesAsync();

            await LogAuditAsync("DnsRecordDeleted", "DnsRecord", record.Id, "{}");

            return Ok(new { Message = "DNS kaydı başarıyla silindi." });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Message = ex.Message });
        }
    }
}

public class CreateDnsRequest
{
    public Guid? ProjectId { get; set; }
    public string Type { get; set; } = "A";
    public string Name { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public int Ttl { get; set; } = 3600;
    public bool Proxied { get; set; } = false;
    public bool UseCloudflare { get; set; } = false;
    public string? CloudflareToken { get; set; }
    public string? CloudflareZoneId { get; set; }
}
