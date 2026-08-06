using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DockerPanel.Domain.Entities;
using DockerPanel.Infrastructure.Data;

namespace DockerPanel.API.Controllers;

[Authorize]
[ApiController]
[Route("api/maintenance-pages")]
public class MaintenancePagesController : ControllerBase
{
    private readonly DockerPanelDbContext _dbContext;

    public MaintenancePagesController(DockerPanelDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    private Guid GetUserId() =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private bool IsAdmin() =>
        User.FindFirstValue(ClaimTypes.Role) == "Administrator";

    // GET /api/maintenance-pages
    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var pages = await _dbContext.MaintenancePages
            .Where(p => IsAdmin() || p.UserId == GetUserId())
            .OrderByDescending(p => p.CreatedAt)
            .Select(p => new
            {
                p.Id,
                p.Name,
                p.HtmlContent,
                p.CreatedAt,
                p.UpdatedAt,
                ActiveProjectCount = _dbContext.Projects.Count(proj => proj.ActiveMaintenancePageId == p.Id)
            })
            .ToListAsync();

        return Ok(pages);
    }

    // GET /api/maintenance-pages/{id}
    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var page = await _dbContext.MaintenancePages.FindAsync(id);
        if (page == null) return NotFound();
        if (!IsAdmin() && page.UserId != GetUserId()) return Forbid();

        return Ok(new
        {
            page.Id,
            page.Name,
            page.HtmlContent,
            page.CreatedAt,
            page.UpdatedAt
        });
    }

    // POST /api/maintenance-pages
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateMaintenancePageRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { Message = "Sayfa adı boş olamaz." });

        if (string.IsNullOrWhiteSpace(request.HtmlContent))
            return BadRequest(new { Message = "HTML içeriği boş olamaz." });

        var page = new MaintenancePage
        {
            Id = Guid.NewGuid(),
            UserId = GetUserId(),
            Name = request.Name.Trim(),
            HtmlContent = request.HtmlContent,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        _dbContext.MaintenancePages.Add(page);
        await _dbContext.SaveChangesAsync();

        return Ok(new { page.Id, page.Name, Message = "Bakım sayfası oluşturuldu." });
    }

    // PUT /api/maintenance-pages/{id}
    [HttpPut("{id}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateMaintenancePageRequest request)
    {
        var page = await _dbContext.MaintenancePages.FindAsync(id);
        if (page == null) return NotFound();
        if (!IsAdmin() && page.UserId != GetUserId()) return Forbid();

        if (!string.IsNullOrWhiteSpace(request.Name))
            page.Name = request.Name.Trim();

        if (!string.IsNullOrWhiteSpace(request.HtmlContent))
            page.HtmlContent = request.HtmlContent;

        page.UpdatedAt = DateTimeOffset.UtcNow;

        await _dbContext.SaveChangesAsync();

        return Ok(new { page.Id, page.Name, Message = "Bakım sayfası güncellendi." });
    }

    // DELETE /api/maintenance-pages/{id}
    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var page = await _dbContext.MaintenancePages.FindAsync(id);
        if (page == null) return NotFound();
        if (!IsAdmin() && page.UserId != GetUserId()) return Forbid();

        // Aktif olarak kullanılan projeleri kontrol et
        var activeProjects = await _dbContext.Projects
            .Where(p => p.ActiveMaintenancePageId == id)
            .Select(p => p.Name)
            .ToListAsync();

        if (activeProjects.Any())
            return BadRequest(new { Message = $"Bu bakım sayfası şu an aktif projelerde kullanılmaktadır: {string.Join(", ", activeProjects)}. Önce bu projelerin bakım modunu kapatın." });

        _dbContext.MaintenancePages.Remove(page);
        await _dbContext.SaveChangesAsync();

        return Ok(new { Message = "Bakım sayfası silindi." });
    }

    public record CreateMaintenancePageRequest(string Name, string HtmlContent);
    public record UpdateMaintenancePageRequest(string? Name, string? HtmlContent);
}
