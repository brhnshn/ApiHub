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
[Route("api/databases")]
[EnableRateLimiting("resource-heavy")]
public class DatabaseController : ControllerBase
{
    private readonly DockerPanelDbContext _dbContext;
    private readonly IDatabaseService _databaseService;
    private readonly IAuditLogService _auditLogService;

    public DatabaseController(DockerPanelDbContext dbContext, IDatabaseService databaseService, IAuditLogService auditLogService)
    {
        _dbContext = dbContext;
        _databaseService = databaseService;
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
        var query = _dbContext.DatabaseSchemas.AsQueryable();

        if (!IsAdmin())
        {
            query = query.Where(d => d.UserId == userId);
        }

        var schemas = await query.OrderByDescending(d => d.CreatedAt).ToListAsync();
        
        // Dinamik olarak her veritabanının fiziksel boyutunu sorgula ve DTO olarak dön
        var result = await Task.WhenAll(schemas.Select(async s => new
        {
            s.Id,
            s.UserId,
            s.DbName,
            s.DbUser,
            s.CreatedAt,
            SizeBytes = await _databaseService.GetDatabaseSizeAsync(s.DbName)
        }));

        return Ok(result);
    }

    [HttpGet("stats")]
    [DisableRateLimiting]
    public async Task<IActionResult> GetStats()
    {
        try
        {
            var activeConns = await _databaseService.GetActiveConnectionsCountAsync();
            
            var userId = GetUserId();
            var query = _dbContext.DatabaseSchemas.AsQueryable();
            if (!IsAdmin())
            {
                query = query.Where(d => d.UserId == userId);
            }
            var schemas = await query.ToListAsync();
            
            long totalBytes = 0;
            foreach (var s in schemas)
            {
                totalBytes += await _databaseService.GetDatabaseSizeAsync(s.DbName);
            }

            return Ok(new
            {
                ActiveConnections = activeConns,
                MaxConnections = 100,
                TotalSizeBytes = totalBytes
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Message = ex.Message });
        }
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateDatabaseRequest request)
    {
        // 1. Girdi Güvenliği Karakter Denetimleri
        if (!SecurityHelper.IsValidDatabaseIdentifier(request.DbName))
        {
            return BadRequest(new { Message = "Veritabanı adı sadece harf, rakam ve alt çizgi (_) içerebilir!" });
        }

        if (!SecurityHelper.IsValidDatabaseIdentifier(request.DbUser))
        {
            return BadRequest(new { Message = "Veritabanı kullanıcı adı sadece harf, rakam ve alt çizgi (_) içerebilir!" });
        }

        var userId = GetUserId();
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

        // 2. Mükerrerlik ve Idempotency (Kaldığı Yerden Devam Edebilme) Denetimi
        var existingSchemaByName = await _dbContext.DatabaseSchemas.FirstOrDefaultAsync(d => d.DbName == request.DbName);
        if (existingSchemaByName != null)
        {
            if (existingSchemaByName.UserId != userId)
            {
                return BadRequest(new { Message = "Bu veritabanı ismi başka bir kullanıcı tarafından kullanımda!" });
            }
            if (existingSchemaByName.DbUser != request.DbUser)
            {
                return BadRequest(new { Message = "Bu veritabanı ismi zaten bu kullanıcı adına farklı bir veri tabanı kullanıcısı ile tanımlanmış!" });
            }

            // Aynı kullanıcı, aynı isim ve aynı veri tabanı kullanıcısı: Idempotency (Kaldığı yerden güvenle devam etsin)
            try
            {
                await _databaseService.ProvisionDatabaseAsync(request.DbName, request.DbUser, request.DbPassword);
                if (project != null && existingSchemaByName.ProjectId == null)
                {
                    existingSchemaByName.ProjectId = project.Id;
                    await _dbContext.SaveChangesAsync();
                }
                return Ok(existingSchemaByName);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = $"PostgreSQL Şema Sağlama Hatası (Idempotent Yeniden Deneme): {ex.Message}" });
            }
        }

        var existingSchemaByUser = await _dbContext.DatabaseSchemas.FirstOrDefaultAsync(d => d.DbUser == request.DbUser);
        if (existingSchemaByUser != null)
        {
            if (existingSchemaByUser.UserId != userId)
            {
                return BadRequest(new { Message = "Bu veritabanı kullanıcısı zaten başka bir kullanıcı tarafından kullanımda!" });
            }
            return BadRequest(new { Message = "Bu veritabanı kullanıcısı zaten farklı bir veritabanı ile tanımlı!" });
        }

        // 3. Veritabanını PostgreSQL üzerinde yarat
        try
        {
            await _databaseService.ProvisionDatabaseAsync(request.DbName, request.DbUser, request.DbPassword);

            // 4. Başarılı ise veri tabanına kaydet
            var schema = new DatabaseSchema
            {
                UserId = userId,
                ProjectId = project?.Id,
                DbName = request.DbName,
                DbUser = request.DbUser,
                CreatedAt = DateTimeOffset.UtcNow
            };

            _dbContext.DatabaseSchemas.Add(schema);
            await _dbContext.SaveChangesAsync();

            await LogAuditAsync("DatabaseCreated", "Database", schema.Id, JsonSerializer.Serialize(new
            {
                schema.DbName,
                schema.DbUser
            }));

            return Ok(schema);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Message = $"PostgreSQL Şema Sağlama Hatası: {ex.Message}" });
        }
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var schema = await _dbContext.DatabaseSchemas.FindAsync(id);
        if (schema == null) return NotFound();

        if (!IsAdmin() && schema.UserId != GetUserId()) return Forbid();

        try
        {
            // PostgreSQL üzerinden şemayı ve kullanıcıyı kaldır
            await _databaseService.DeleteDatabaseAsync(schema.DbName, schema.DbUser);

            // DB kaydını sil
            _dbContext.DatabaseSchemas.Remove(schema);
            await _dbContext.SaveChangesAsync();

            await LogAuditAsync("DatabaseDeleted", "Database", schema.Id, "{}");

            return Ok(new { Message = "Veritabanı ve yetkili kullanıcısı başarıyla silindi." });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Message = ex.Message });
        }
    }

    [HttpGet("discover")]
    [DisableRateLimiting]
    public async Task<IActionResult> Discover()
    {
        if (!IsAdmin()) return Forbid();

        try
        {
            var discovered = await _databaseService.DiscoverExistingDatabasesAsync();
            var registeredDbNames = await _dbContext.DatabaseSchemas.Select(d => d.DbName).ToListAsync();

            var result = discovered.Select(d => new
            {
                d.DbName,
                d.DbUser,
                d.SizeInBytes,
                IsImported = registeredDbNames.Contains(d.DbName)
            }).ToList();

            return Ok(result);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Message = ex.Message });
        }
    }

    [HttpPost("import")]
    public async Task<IActionResult> Import([FromBody] ImportDatabaseRequest request)
    {
        if (!IsAdmin()) return Forbid();

        if (string.IsNullOrWhiteSpace(request.DbName) || string.IsNullOrWhiteSpace(request.DbUser))
        {
            return BadRequest(new { Message = "Veritabanı adı ve kullanıcı adı gereklidir!" });
        }

        var existing = await _dbContext.DatabaseSchemas.FirstOrDefaultAsync(d => d.DbName == request.DbName);
        if (existing != null)
        {
            return BadRequest(new { Message = "Bu veritabanı zaten panelde kayıtlı!" });
        }

        var userId = GetUserId();

        var schema = new DatabaseSchema
        {
            UserId = userId,
            DbName = request.DbName,
            DbUser = request.DbUser,
            CreatedAt = DateTimeOffset.UtcNow
        };

        _dbContext.DatabaseSchemas.Add(schema);
        await _dbContext.SaveChangesAsync();

        return Ok(schema);
    }
}

public class CreateDatabaseRequest
{
    public Guid? ProjectId { get; set; }
    public string DbName { get; set; } = string.Empty;
    public string DbUser { get; set; } = string.Empty;
    public string DbPassword { get; set; } = string.Empty;
}

public class ImportDatabaseRequest
{
    public string DbName { get; set; } = string.Empty;
    public string DbUser { get; set; } = string.Empty;
}
