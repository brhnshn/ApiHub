using System;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using DockerPanel.Domain.Interfaces;
using DockerPanel.Domain.Enums;

namespace DockerPanel.API.Controllers;

[Authorize]
[ApiController]
[Route("api/firewall")]
public class FirewallController : ControllerBase
{
    private readonly IFirewallService _firewallService;
    private readonly IAuditLogService _auditLogService;

    public FirewallController(IFirewallService firewallService, IAuditLogService auditLogService)
    {
        _firewallService = firewallService;
        _auditLogService = auditLogService;
    }

    private Guid GetUserId()
    {
        var idStr = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(idStr, out var id) ? id : Guid.Empty;
    }

    private async Task LogAuditAsync(string action, string entity, Guid? targetId, string details)
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var ua = Request.Headers["User-Agent"].ToString() ?? "unknown";
        await _auditLogService.LogAsync(GetUserId(), action, entity, targetId, details, ip, ua);
    }

    private bool IsAdmin()
    {
        return User.IsInRole(UserRole.Administrator.ToString());
    }

    [HttpGet]
    public async Task<IActionResult> GetStatusAndRules()
    {
        if (!IsAdmin()) return Forbid();

        try
        {
            var active = await _firewallService.IsFirewallActiveAsync();
            var rules = await _firewallService.GetRulesAsync();

            return Ok(new
            {
                Active = active,
                Rules = rules
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Message = ex.Message });
        }
    }

    [HttpPost]
    public async Task<IActionResult> AddRule([FromBody] AddRuleRequest request)
    {
        if (!IsAdmin()) return Forbid();

        try
        {
            await _firewallService.AddRuleAsync(request.Port, request.Protocol, request.Action);

            await LogAuditAsync("FirewallRuleAdded", "FirewallRule", null, JsonSerializer.Serialize(new
            {
                request.Port,
                request.Protocol,
                request.Action
            }));

            return Ok(new { Message = "Güvenlik duvarı kuralı başarıyla eklendi." });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { Message = ex.Message });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Message = ex.Message });
        }
    }

    [HttpDelete("{number}")]
    public async Task<IActionResult> DeleteRule(int number)
    {
        if (!IsAdmin()) return Forbid();

        try
        {
            await _firewallService.DeleteRuleAsync(number);

            await LogAuditAsync("FirewallRuleRemoved", "FirewallRule", null, JsonSerializer.Serialize(new
            {
                RuleNumber = number
            }));

            return Ok(new { Message = $"Güvenlik duvarı kuralı #{number} başarıyla kaldırıldı." });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Message = ex.Message });
        }
    }

    [HttpPost("toggle")]
    public async Task<IActionResult> ToggleStatus([FromBody] ToggleStatusRequest request)
    {
        if (!IsAdmin()) return Forbid();

        try
        {
            await _firewallService.SetFirewallStatusAsync(request.Active);
            return Ok(new { Message = $"Güvenlik duvarı başarıyla {(request.Active ? "aktif edildi" : "devre dışı bırakıldı")}." });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Message = ex.Message });
        }
    }
}

public class AddRuleRequest
{
    public string Port { get; set; } = string.Empty;
    public string Protocol { get; set; } = "tcp"; // tcp, udp, any
    public string Action { get; set; } = "ALLOW"; // ALLOW, DENY
}

public class ToggleStatusRequest
{
    public bool Active { get; set; }
}
