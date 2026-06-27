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
            .Select(k => new
            {
                k.Id,
                k.Name,
                k.MaskedKey,
                k.IsActive,
                k.CreatedAt,
                k.LastUsedAt,
                k.ExpiresAt
            })
            .ToListAsync();

        return Ok(keys);
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
            ExpiresAt = request.DurationDays.HasValue ? DateTimeOffset.UtcNow.AddDays(request.DurationDays.Value) : null
        };

        _context.ApiKeys.Add(apiKey);
        await _context.SaveChangesAsync();

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
}

public class GenerateKeyRequest
{
    public string Name { get; set; } = string.Empty;
    public int? DurationDays { get; set; }
}
