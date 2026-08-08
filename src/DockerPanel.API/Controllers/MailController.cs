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
[Route("api/mail")]
[EnableRateLimiting("resource-heavy")]
public class MailController : ControllerBase
{
    private readonly DockerPanelDbContext _dbContext;
    private readonly IMailService _mailService;
    private readonly IAuditLogService _auditLogService;
    private readonly IPushNotificationService _pushNotificationService;

    public MailController(DockerPanelDbContext dbContext, IMailService mailService, IAuditLogService auditLogService, IPushNotificationService pushNotificationService)
    {
        _dbContext = dbContext;
        _mailService = mailService;
        _auditLogService = auditLogService;
        _pushNotificationService = pushNotificationService;
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
        var query = _dbContext.MailAccounts.AsQueryable();

        if (!IsAdmin())
        {
            query = query.Where(m => m.UserId == userId);
        }

        var accounts = await query.OrderByDescending(m => m.CreatedAt).ToListAsync();
        return Ok(accounts);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateMailRequest request)
    {
        // 1. Email Format Regex Kontrolü
        if (!SecurityHelper.IsValidEmail(request.EmailAddress))
        {
            return BadRequest(new { Message = "Geçersiz e-posta adresi biçimi!" });
        }

        // 2. Mükerrerlik Denetimi
        if (await _dbContext.MailAccounts.AnyAsync(m => m.EmailAddress.ToLower() == request.EmailAddress.ToLower()))
        {
            return BadRequest(new { Message = "Bu e-posta adresi zaten mevcut!" });
        }

        var userId = GetUserId();

        try
        {
            // 3. docker-mailserver CLI üzerinden hesabı ekle
            await _mailService.CreateMailAccountAsync(request.EmailAddress, request.Password);

            // 4. Veri tabanına kaydet
            var mailAccount = new MailAccount
            {
                UserId = userId,
                EmailAddress = request.EmailAddress.ToLower(),
                DisplayName = request.DisplayName,
                QuotaBytes = request.QuotaBytes,
                CreatedAt = DateTimeOffset.UtcNow
            };

            _dbContext.MailAccounts.Add(mailAccount);
            await _dbContext.SaveChangesAsync();

            await LogAuditAsync("MailAccountCreated", "MailAccount", mailAccount.Id, JsonSerializer.Serialize(new
            {
                mailAccount.EmailAddress,
                mailAccount.QuotaBytes
            }));

            return Ok(mailAccount);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Message = $"Mail Server Entegrasyon Hatası: {ex.Message}" });
        }
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var account = await _dbContext.MailAccounts.FindAsync(id);
        if (account == null) return NotFound();

        if (!IsAdmin() && account.UserId != GetUserId()) return Forbid();

        try
        {
            // Mail sunucusundan kaldır ve posta kutusu dizinlerini sil
            await _mailService.DeleteMailAccountAsync(account.EmailAddress);

            // DB kaydını sil
            _dbContext.MailAccounts.Remove(account);
            await _dbContext.SaveChangesAsync();

            await LogAuditAsync("MailAccountDeleted", "MailAccount", account.Id, "{}");

            return Ok(new { Message = "E-posta hesabı ve tüm fiziksel verileri başarıyla temizlendi." });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Message = ex.Message });
        }
    }

    [HttpGet("{emailAddress}/emails")]
    [DisableRateLimiting]
    public async Task<IActionResult> GetEmails(string emailAddress, [FromQuery] string folder = "inbox", [FromQuery] int take = 75)
    {
        if (!SecurityHelper.IsValidEmail(emailAddress))
        {
            return BadRequest(new { Message = "Geçersiz e-posta adresi!" });
        }

        var account = await _dbContext.MailAccounts.FirstOrDefaultAsync(m => m.EmailAddress.ToLower() == emailAddress.ToLower());
        if (account == null) return NotFound("E-posta hesabı sistemde bulunamadı.");

        if (!IsAdmin() && account.UserId != GetUserId()) return Forbid();

        try
        {
            var emails = await _mailService.GetMailsAsync(emailAddress, folder, Math.Clamp(take, 1, 200));
            return Ok(emails);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Message = ex.Message });
        }
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateMailRequest request)
    {
        var account = await _dbContext.MailAccounts.FindAsync(id);
        if (account == null) return NotFound("E-posta hesabı bulunamadı.");

        if (!IsAdmin() && account.UserId != GetUserId()) return Forbid();

        try
        {
            account.DisplayName = request.DisplayName ?? "";
            account.QuotaBytes = request.QuotaBytes;
            
            if (request.ForwardingEnabled)
            {
                if (!string.IsNullOrEmpty(request.ForwardingAddress) && !SecurityHelper.IsValidEmail(request.ForwardingAddress))
                {
                    return BadRequest(new { Message = "Geçersiz yönlendirme e-posta adresi biçimi!" });
                }
            }

            account.ForwardingAddress = request.ForwardingAddress;
            account.ForwardingEnabled = request.ForwardingEnabled;

            // Docker mailserver alias güncellemesi
            await _mailService.UpdateForwardingAsync(account.EmailAddress, account.ForwardingAddress ?? "", account.ForwardingEnabled);

            if (!string.IsNullOrEmpty(request.NewPassword))
            {
                await _mailService.UpdateMailPasswordAsync(account.EmailAddress, request.NewPassword);
            }

            await _dbContext.SaveChangesAsync();
            await LogAuditAsync("UpdateMailAccount", "MailAccounts", account.Id, $"Hesap güncellendi: {account.EmailAddress}");

            return Ok(account);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Message = ex.Message });
        }
    }

    [HttpPost("{emailAddress}/send")]
    public async Task<IActionResult> SendEmail(string emailAddress, [FromBody] SendEmailRequest request)
    {
        if (!SecurityHelper.IsValidEmail(emailAddress))
        {
            return BadRequest(new { Message = "Geçersiz gönderen e-posta adresi!" });
        }

        var account = await _dbContext.MailAccounts.FirstOrDefaultAsync(m => m.EmailAddress.ToLower() == emailAddress.ToLower());
        if (account == null) return NotFound("Gönderen e-posta hesabı bulunamadı.");

        if (!IsAdmin() && account.UserId != GetUserId()) return Forbid();

        try
        {
            var domainAttachments = request.Attachments?.Select(a => new AttachmentDto
            {
                FileName = a.FileName,
                Base64Data = a.Base64Data,
                ContentType = a.ContentType
            }).ToList();

            await _mailService.SendMailAsync(emailAddress, account.DisplayName, request.To, request.Subject, request.Body, domainAttachments);
            
            // Eğer alıcı da bizim sistemdeyse FCM push at
            var receiverAccount = await _dbContext.MailAccounts.FirstOrDefaultAsync(m => m.EmailAddress.ToLower() == request.To.ToLower());
            if (receiverAccount != null)
            {
                await _pushNotificationService.SendNotificationToUserAsync(
                    receiverAccount.UserId, 
                    $"📧 Yeni E-posta: {request.Subject}", 
                    $"{account.DisplayName} ({account.EmailAddress})", 
                    "apihub://navigate?path=/webmail");
            }

            return Ok(new { Message = "E-posta başarıyla gönderildi." });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Message = ex.Message });
        }
    }

    [HttpDelete("{emailAddress}/emails/{folder}/{fileName}")]
    public async Task<IActionResult> DeleteEmail(string emailAddress, string folder, string fileName)
    {
        if (!SecurityHelper.IsValidEmail(emailAddress))
        {
            return BadRequest(new { Message = "Geçersiz e-posta adresi!" });
        }

        var account = await _dbContext.MailAccounts.FirstOrDefaultAsync(m => m.EmailAddress.ToLower() == emailAddress.ToLower());
        if (account == null) return NotFound("E-posta hesabı bulunamadı.");

        if (!IsAdmin() && account.UserId != GetUserId()) return Forbid();

        try
        {
            await _mailService.DeleteMailAsync(emailAddress, folder, fileName);
            return Ok(new { Message = "E-posta başarıyla silindi." });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Message = ex.Message });
        }
    }

    [HttpPost("{emailAddress}/emails/move")]
    public async Task<IActionResult> MoveEmail(string emailAddress, [FromBody] MoveEmailRequest request)
    {
        if (!SecurityHelper.IsValidEmail(emailAddress))
        {
            return BadRequest(new { Message = "Geçersiz e-posta adresi!" });
        }

        var account = await _dbContext.MailAccounts.FirstOrDefaultAsync(m => m.EmailAddress.ToLower() == emailAddress.ToLower());
        if (account == null) return NotFound("E-posta hesabı bulunamadı.");

        if (!IsAdmin() && account.UserId != GetUserId()) return Forbid();

        try
        {
            await _mailService.MoveMailAsync(emailAddress, request.SourceFolder, request.DestFolder, request.FileName);
            return Ok(new { Message = "E-posta başarıyla taşındı." });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Message = ex.Message });
        }
    }

    [HttpPost("{emailAddress}/emails/read")]
    public async Task<IActionResult> MarkEmailAsRead(string emailAddress, [FromBody] MarkEmailReadRequest request)
    {
        if (!SecurityHelper.IsValidEmail(emailAddress))
        {
            return BadRequest(new { Message = "Geçersiz e-posta adresi!" });
        }

        var account = await _dbContext.MailAccounts.FirstOrDefaultAsync(m => m.EmailAddress.ToLower() == emailAddress.ToLower());
        if (account == null) return NotFound("E-posta hesabı bulunamadı.");

        if (!IsAdmin() && account.UserId != GetUserId()) return Forbid();

        try
        {
            await _mailService.MarkMailAsReadAsync(emailAddress, request.Folder, request.FileName);
            return Ok(new { Message = "E-posta okundu olarak işaretlendi." });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Message = ex.Message });
        }
    }

    [HttpGet("{emailAddress}/labels")]
    [DisableRateLimiting]
    public async Task<IActionResult> GetCustomLabels(string emailAddress)
    {
        if (!SecurityHelper.IsValidEmail(emailAddress))
        {
            return BadRequest(new { Message = "Geçersiz e-posta adresi!" });
        }

        var account = await _dbContext.MailAccounts.FirstOrDefaultAsync(m => m.EmailAddress.ToLower() == emailAddress.ToLower());
        if (account == null) return NotFound("E-posta hesabı bulunamadı.");

        if (!IsAdmin() && account.UserId != GetUserId()) return Forbid();

        try
        {
            var labels = await _mailService.GetCustomLabelsAsync(emailAddress);
            return Ok(labels);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Message = ex.Message });
        }
    }

    [HttpPost("{emailAddress}/labels")]
    public async Task<IActionResult> CreateCustomLabel(string emailAddress, [FromBody] CreateLabelRequest request)
    {
        if (!SecurityHelper.IsValidEmail(emailAddress))
        {
            return BadRequest(new { Message = "Geçersiz e-posta adresi!" });
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(new { Message = "Etiket ismi boş olamaz!" });
        }

        var account = await _dbContext.MailAccounts.FirstOrDefaultAsync(m => m.EmailAddress.ToLower() == emailAddress.ToLower());
        if (account == null) return NotFound("E-posta hesabı bulunamadı.");

        if (!IsAdmin() && account.UserId != GetUserId()) return Forbid();

        try
        {
            await _mailService.CreateCustomLabelAsync(emailAddress, request.Name);
            return Ok(new { Message = "Yeni etiket başarıyla oluşturuldu." });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Message = ex.Message });
        }
    }

    [HttpDelete("{emailAddress}/labels/{labelName}")]
    public async Task<IActionResult> DeleteCustomLabel(string emailAddress, string labelName)
    {
        if (!SecurityHelper.IsValidEmail(emailAddress))
        {
            return BadRequest(new { Message = "Geçersiz e-posta adresi!" });
        }

        if (string.IsNullOrWhiteSpace(labelName))
        {
            return BadRequest(new { Message = "Etiket adı boş olamaz!" });
        }

        var account = await _dbContext.MailAccounts.FirstOrDefaultAsync(m => m.EmailAddress.ToLower() == emailAddress.ToLower());
        if (account == null) return NotFound("E-posta hesabı bulunamadı.");

        if (!IsAdmin() && account.UserId != GetUserId()) return Forbid();

        try
        {
            await _mailService.DeleteCustomLabelAsync(emailAddress, labelName);
            return Ok(new { Message = $"'{labelName}' etiketi başarıyla silindi." });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Message = ex.Message });
        }
    }

    [HttpGet("{emailAddress}/quota")]
    [DisableRateLimiting]
    public async Task<IActionResult> GetQuota(string emailAddress)
    {
        if (!SecurityHelper.IsValidEmail(emailAddress))
        {
            return BadRequest(new { Message = "Geçersiz e-posta adresi!" });
        }

        var account = await _dbContext.MailAccounts.FirstOrDefaultAsync(m => m.EmailAddress.ToLower() == emailAddress.ToLower());
        if (account == null) return NotFound("E-posta hesabı bulunamadı.");

        if (!IsAdmin() && account.UserId != GetUserId()) return Forbid();

        try
        {
            var usedBytes = await _mailService.GetMailboxUsageBytesAsync(emailAddress);
            return Ok(new
            {
                UsedBytes = usedBytes,
                QuotaBytes = account.QuotaBytes,
                Percentage = account.QuotaBytes > 0
                    ? (int)Math.Min(100, Math.Round((double)usedBytes / account.QuotaBytes * 100))
                    : 0
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Message = ex.Message });
        }
    }
}

public class SendEmailRequest
{
    public string To { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public System.Collections.Generic.List<AttachmentRequest>? Attachments { get; set; }
}

public class AttachmentRequest
{
    public string FileName { get; set; } = string.Empty;
    public string Base64Data { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
}

public class CreateMailRequest
{
    public string EmailAddress { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public long QuotaBytes { get; set; } = 1073741824; // Varsayılan 1 GB
}

public class UpdateMailRequest
{
    public string DisplayName { get; set; } = string.Empty;
    public long QuotaBytes { get; set; }
    public string? NewPassword { get; set; }
    public string? ForwardingAddress { get; set; }
    public bool ForwardingEnabled { get; set; }
}

public class MoveEmailRequest
{
    public string SourceFolder { get; set; } = string.Empty;
    public string DestFolder { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
}

public class MarkEmailReadRequest
{
    public string Folder { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
}

public class CreateLabelRequest
{
    public string Name { get; set; } = string.Empty;
}
