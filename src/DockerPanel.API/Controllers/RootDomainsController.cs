using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DockerPanel.Domain.Entities;
using DockerPanel.Domain.Enums;
using DockerPanel.Infrastructure.Data;

namespace DockerPanel.API.Controllers;

[Authorize]
[ApiController]
[Route("api/domains/roots")]
public class RootDomainsController : ControllerBase
{
    private readonly DockerPanelDbContext _dbContext;

    public RootDomainsController(DockerPanelDbContext dbContext)
    {
        _dbContext = dbContext;
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
        var query = _dbContext.RootDomains.AsQueryable();

        if (!IsAdmin())
        {
            query = query.Where(d => d.UserId == userId);
        }

        var domains = await query.OrderByDescending(d => d.CreatedAt).ToListAsync();
        return Ok(domains);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateRootDomainRequest request)
    {
        var name = request.Name?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(name) || !name.Contains('.'))
        {
            return BadRequest(new { Message = "Geçerli bir alan adı giriniz (örn: domain.com)." });
        }

        var userId = GetUserId();

        // Mükerrerlik Kontrolü
        var existing = await _dbContext.RootDomains.FirstOrDefaultAsync(d => d.Name == name);
        if (existing != null)
        {
            return BadRequest(new { Message = "Bu alan adı sisteme zaten kayıtlı!" });
        }

        var rootDomain = new RootDomain
        {
            UserId = userId,
            Name = name,
            CloudflareToken = request.CloudflareToken?.Trim(),
            CloudflareZoneId = request.CloudflareZoneId?.Trim(),
            CreatedAt = DateTimeOffset.UtcNow
        };

        _dbContext.RootDomains.Add(rootDomain);
        await _dbContext.SaveChangesAsync();

        return Ok(rootDomain);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var domain = await _dbContext.RootDomains.FindAsync(id);
        if (domain == null) return NotFound();

        var userId = GetUserId();
        if (!IsAdmin() && domain.UserId != userId) return Forbid();

        _dbContext.RootDomains.Remove(domain);
        await _dbContext.SaveChangesAsync();

        return Ok(new { Message = "Ana alan adı başarıyla silindi." });
    }
}

public class CreateRootDomainRequest
{
    public string Name { get; set; } = string.Empty;
    public string? CloudflareToken { get; set; }
    public string? CloudflareZoneId { get; set; }
}
