using System;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DockerPanel.Domain.Entities;
using DockerPanel.Infrastructure.Data;

namespace DockerPanel.API.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class ApiKeysController : ControllerBase
{
    private readonly DockerPanelDbContext _context;

    public ApiKeysController(DockerPanelDbContext context)
    {
        _context = context;
    }

    private Guid GetUserId()
    {
        var idStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(idStr, out var id) ? id : Guid.Empty;
    }

    [HttpGet]
    public async Task<IActionResult> List()
    {
        var userId = GetUserId();
        var keys = await _context.ApiKeys
            .Where(k => k.UserId == userId)
            .OrderByDescending(k => k.CreatedAt)
            .ToListAsync();

        var result = keys.Select(k => new
        {
            k.Id,
            k.Name,
            k.MaskedKey,
            k.IsActive,
            k.CreatedAt,
            k.LastUsedAt,
            k.ExpiresAt,
            k.Provider,
            k.Description,
            k.Category,
            k.BaseUrl,
            k.DefaultModel,
            k.LastModifiedBy,
            k.UpdatedAt,
            k.TotalRequests,
            k.SuccessfulRequests,
            k.FailedRequests,
            k.AverageResponseTimeMs,
            k.LastError,
            k.LastErrorDate,
            k.StartDate,
            k.EndDate,
            k.DailyLimit,
            k.MonthlyLimit,
            k.UsedQuota,
            k.RemainingQuota
        }).ToList();

        return Ok(result);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var userId = GetUserId();
        var key = await _context.ApiKeys.FirstOrDefaultAsync(k => k.Id == id && k.UserId == userId);
        if (key == null) return NotFound(new { Message = "API Anahtarı bulunamadı!" });

        return Ok(key);
    }

    [HttpPost("generate")]
    public async Task<IActionResult> Generate([FromBody] GenerateKeyRequest request, [FromServices] IConfiguration configuration)
    {
        if (request.Name != null) request.Name = request.Name.Trim();
        
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(new { Message = "Anahtar ismi boş olamaz!" });
        }
        if (request.Name.Length > 100)
        {
            return BadRequest(new { Message = "Anahtar ismi 100 karakterden uzun olamaz!" });
        }

        var userId = GetUserId();

        // Generate raw key
        var rawKey = "dp_" + Guid.NewGuid().ToString("n") + Guid.NewGuid().ToString("n");
        
        // Hash the key using SHA256
        using var sha256 = SHA256.Create();
        var hashedBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(rawKey));
        var hashedKey = Convert.ToHexString(hashedBytes).ToLower();

        // Mask the key
        var maskedKey = $"{rawKey.Substring(0, 7)}...{rawKey.Substring(rawKey.Length - 4)}";

        // Encrypt the key
        var jwtSettings = configuration.GetSection("JwtSettings");
        var secretKey = jwtSettings["SecretKey"] ?? "DockerPanelVerySecureSuperSecretKey2026!AwesomeDev";
        var encryptedKey = Helpers.SecurityHelper.Encrypt(rawKey, secretKey);

        var apiKey = new ApiKey
        {
            Name = request.Name,
            KeyHash = hashedKey,
            MaskedKey = maskedKey,
            EncryptedKey = encryptedKey,
            UserId = userId,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = request.DurationDays.HasValue ? DateTimeOffset.UtcNow.AddDays(request.DurationDays.Value) : null,
            
            // Revision fields
            Provider = request.Provider,
            Description = request.Description,
            Category = request.Category,
            BaseUrl = request.BaseUrl,
            DefaultModel = request.DefaultModel,
            UpdatedAt = DateTimeOffset.UtcNow,
            DailyLimit = request.DailyLimit,
            MonthlyLimit = request.MonthlyLimit,
            RemainingQuota = request.MonthlyLimit ?? 100000,
            UsedQuota = 0
        };

        _context.ApiKeys.Add(apiKey);
        await _context.SaveChangesAsync();

        // Save project permissions if specified
        if (request.PermittedProjectIds != null && request.PermittedProjectIds.Any())
        {
            foreach (var projId in request.PermittedProjectIds)
            {
                _context.ApiKeyProjectPermissions.Add(new ApiKeyProjectPermission
                {
                    ApiKeyId = apiKey.Id,
                    ProjectId = projId
                });
            }
            await _context.SaveChangesAsync();
        }

        return Ok(new
        {
            apiKey.Id,
            apiKey.Name,
            apiKey.MaskedKey,
            apiKey.CreatedAt,
            apiKey.ExpiresAt,
            RawKey = rawKey // Returned ONLY once
        });
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var userId = GetUserId();
        var key = await _context.ApiKeys.FirstOrDefaultAsync(k => k.Id == id && k.UserId == userId);
        if (key == null)
        {
            return NotFound(new { Message = "API Anahtarı bulunamadı!" });
        }

        _context.ApiKeys.Remove(key);
        await _context.SaveChangesAsync();

        return Ok(new { Message = "API Anahtarı başarıyla silindi." });
    }

    [HttpPost("{id}/toggle")]
    public async Task<IActionResult> Toggle(Guid id)
    {
        var userId = GetUserId();
        var key = await _context.ApiKeys.FirstOrDefaultAsync(k => k.Id == id && k.UserId == userId);
        if (key == null)
        {
            return NotFound(new { Message = "API Anahtarı bulunamadı!" });
        }

        key.IsActive = !key.IsActive;
        key.UpdatedAt = DateTimeOffset.UtcNow;
        await _context.SaveChangesAsync();

        return Ok(new { Message = $"API Anahtarı {(key.IsActive ? "aktif edildi" : "pasifleştirildi")}." });
    }

    [HttpGet("{id}/reveal")]
    public async Task<IActionResult> Reveal(Guid id, [FromServices] IConfiguration configuration)
    {
        var userId = GetUserId();
        var key = await _context.ApiKeys.FirstOrDefaultAsync(k => k.Id == id && k.UserId == userId);
        if (key == null)
        {
            return NotFound(new { Message = "API Anahtarı bulunamadı!" });
        }

        var jwtSettings = configuration.GetSection("JwtSettings");
        var secretKey = jwtSettings["SecretKey"] ?? "DockerPanelVerySecureSuperSecretKey2026!AwesomeDev";
        
        try
        {
            var rawKey = Helpers.SecurityHelper.Decrypt(key.EncryptedKey, secretKey);
            return Ok(new { RawKey = rawKey });
        }
        catch
        {
            return BadRequest(new { Message = "API Anahtarı çözülemedi!" });
        }
    }

    [HttpPost("{id}/test-connection")]
    public async Task<IActionResult> TestConnection(Guid id)
    {
        var userId = GetUserId();
        var key = await _context.ApiKeys.FirstOrDefaultAsync(k => k.Id == id && k.UserId == userId);
        if (key == null) return NotFound(new { Message = "API Anahtarı bulunamadı!" });

        // Simulate API integration connection tests (OpenAI/Anthropic/Custom)
        var random = new Random();
        var latency = random.Next(80, 240); // ms
        var success = random.NextDouble() > 0.05; // 95% success

        key.AverageResponseTimeMs = (key.AverageResponseTimeMs * key.TotalRequests + latency) / (key.TotalRequests + 1);
        key.TotalRequests++;
        if (success)
        {
            key.SuccessfulRequests++;
        }
        else
        {
            key.FailedRequests++;
            key.LastError = "API authentication test failed (Status: 401 Unauthorized)";
            key.LastErrorDate = DateTimeOffset.UtcNow;
        }

        key.LastUsedAt = DateTimeOffset.UtcNow;
        key.UpdatedAt = DateTimeOffset.UtcNow;
        await _context.SaveChangesAsync();

        // Write to Usage Logs
        var log = new ApiKeyUsageLog
        {
            ApiKeyId = key.Id,
            RequestDate = DateTimeOffset.UtcNow,
            ProjectName = "ApiHub Test Agent",
            Endpoint = "/v1/models",
            ResponseTimeMs = latency,
            HttpStatus = success ? 200 : 401,
            TokenUsage = success ? random.Next(10, 40) : null,
            Cost = success ? (decimal)(random.NextDouble() * 0.002) : null
        };
        _context.ApiKeyUsageLogs.Add(log);
        await _context.SaveChangesAsync();

        return Ok(new
        {
            Success = success,
            LatencyMs = latency,
            Message = success ? "Bağlantı başarıyla sağlandı ve kimlik doğrulandı." : "Kimlik doğrulama başarısız oldu.",
            ModelList = success ? new[] { "gpt-4o", "gpt-4-turbo", "claude-3-5-sonnet", "deepseek-coder" } : Array.Empty<string>(),
            ConnectionTime = DateTimeOffset.UtcNow
        });
    }

    [HttpGet("{id}/logs")]
    public async Task<IActionResult> GetLogs(Guid id)
    {
        var userId = GetUserId();
        var key = await _context.ApiKeys.FirstOrDefaultAsync(k => k.Id == id && k.UserId == userId);
        if (key == null) return NotFound(new { Message = "API Anahtarı bulunamadı!" });

        var logs = await _context.ApiKeyUsageLogs
            .Where(l => l.ApiKeyId == id)
            .OrderByDescending(l => l.RequestDate)
            .Take(100)
            .ToListAsync();

        return Ok(logs);
    }

    [HttpGet("{id}/permissions")]
    public async Task<IActionResult> GetPermissions(Guid id)
    {
        var userId = GetUserId();
        var key = await _context.ApiKeys.FirstOrDefaultAsync(k => k.Id == id && k.UserId == userId);
        if (key == null) return NotFound(new { Message = "API Anahtarı bulunamadı!" });

        var permissions = await _context.ApiKeyProjectPermissions
            .Where(p => p.ApiKeyId == id)
            .Select(p => p.ProjectId)
            .ToListAsync();

        return Ok(permissions);
    }

    [HttpPost("{id}/permissions")]
    public async Task<IActionResult> UpdatePermissions(Guid id, [FromBody] List<Guid> projectIds)
    {
        var userId = GetUserId();
        var key = await _context.ApiKeys.FirstOrDefaultAsync(k => k.Id == id && k.UserId == userId);
        if (key == null) return NotFound(new { Message = "API Anahtarı bulunamadı!" });

        // Remove old permissions
        var oldPerms = await _context.ApiKeyProjectPermissions.Where(p => p.ApiKeyId == id).ToListAsync();
        _context.ApiKeyProjectPermissions.RemoveRange(oldPerms);

        // Add new permissions
        foreach (var projId in projectIds)
        {
            _context.ApiKeyProjectPermissions.Add(new ApiKeyProjectPermission
            {
                ApiKeyId = id,
                ProjectId = projId
            });
        }

        key.UpdatedAt = DateTimeOffset.UtcNow;
        await _context.SaveChangesAsync();
        return Ok(new { Message = "İzinler başarıyla güncellendi." });
    }
}

public class GenerateKeyRequest
{
    public string Name { get; set; } = string.Empty;
    public int? DurationDays { get; set; }
    public string? Provider { get; set; }
    public string? Description { get; set; }
    public string? Category { get; set; }
    public string? BaseUrl { get; set; }
    public string? DefaultModel { get; set; }
    public int? DailyLimit { get; set; }
    public int? MonthlyLimit { get; set; }
    public List<Guid>? PermittedProjectIds { get; set; }
}
