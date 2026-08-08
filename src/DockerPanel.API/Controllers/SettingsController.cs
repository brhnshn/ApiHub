using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DockerPanel.Domain.Entities;
using DockerPanel.Domain.Enums;
using DockerPanel.Infrastructure.Data;
using DockerPanel.Infrastructure.Services;

namespace DockerPanel.API.Controllers;

[Authorize]
[ApiController]
[Route("api/settings/smtp")]
public class SettingsController : ControllerBase
{
    private readonly DockerPanelDbContext _dbContext;
    private readonly EncryptionService _encryptionService;

    public SettingsController(DockerPanelDbContext dbContext, EncryptionService encryptionService)
    {
        _dbContext = dbContext;
        _encryptionService = encryptionService;
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
    public async Task<IActionResult> GetSmtpSettings()
    {
        if (!IsAdmin()) return Forbid();

        var settings = await _dbContext.SmtpSettings.FirstOrDefaultAsync();
        if (settings == null)
        {
            return Ok(new SmtpSettingsDto()); // Varsayılan boş döndür
        }

        return Ok(new SmtpSettingsDto
        {
            Host = settings.Host,
            Port = settings.Port,
            EnableSsl = settings.EnableSsl,
            Username = settings.Username,
            IsEnabled = settings.IsEnabled,
            HasPassword = !string.IsNullOrEmpty(settings.EncryptedPassword),
            AcceptSelfSignedCert = settings.AcceptSelfSignedCert
        });
    }

    [HttpPost]
    public async Task<IActionResult> SaveSmtpSettings([FromBody] SaveSmtpSettingsRequest request)
    {
        if (!IsAdmin()) return Forbid();

        var settings = await _dbContext.SmtpSettings.FirstOrDefaultAsync();
        
        if (settings == null)
        {
            settings = new SmtpSettings { Id = Guid.NewGuid() };
            _dbContext.SmtpSettings.Add(settings);
        }

        settings.Host = request.Host;
        settings.Port = request.Port;
        settings.EnableSsl = request.EnableSsl;
        settings.Username = request.Username;
        settings.IsEnabled = request.IsEnabled;
        settings.AcceptSelfSignedCert = request.AcceptSelfSignedCert;
        settings.UpdatedAt = DateTimeOffset.UtcNow;

        if (!string.IsNullOrEmpty(request.Password))
        {
            settings.EncryptedPassword = _encryptionService.Encrypt(request.Password);
        }

        await _dbContext.SaveChangesAsync();

        return Ok(new { Message = "SMTP ayarları başarıyla kaydedildi." });
    }

    [HttpPost("test")]
    public async Task<IActionResult> TestSmtpConnection([FromBody] SaveSmtpSettingsRequest request)
    {
        if (!IsAdmin()) return Forbid();

        try
        {
            using var smtpClient = new MailKit.Net.Smtp.SmtpClient();
            
            // Self-signed veya kurumsal CA sertifikalarını kabul et
            smtpClient.ServerCertificateValidationCallback = (sender, cert, chain, errors) =>
                errors == System.Net.Security.SslPolicyErrors.None || request.AcceptSelfSignedCert;

            var secureSocketOptions = request.EnableSsl
                ? MailKit.Security.SecureSocketOptions.StartTls
                : MailKit.Security.SecureSocketOptions.Auto;
            
            await smtpClient.ConnectAsync(request.Host, request.Port, secureSocketOptions);

            var password = request.Password;
            if (string.IsNullOrEmpty(password))
            {
                var settings = await _dbContext.SmtpSettings.FirstOrDefaultAsync();
                if (settings != null && !string.IsNullOrEmpty(settings.EncryptedPassword))
                {
                    password = _encryptionService.Decrypt(settings.EncryptedPassword);
                }
            }

            if (!string.IsNullOrEmpty(request.Username) && !string.IsNullOrEmpty(password))
            {
                await smtpClient.AuthenticateAsync(request.Username, password);
            }

            await smtpClient.DisconnectAsync(true);
            return Ok(new { Message = "Bağlantı testi başarılı!" });
        }
        catch (Exception ex)
        {
            return BadRequest(new { Message = $"Bağlantı hatası: {ex.Message}" });
        }
    }
}

public class SmtpSettingsDto
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 587;
    public bool EnableSsl { get; set; } = true;
    public string Username { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = false;
    public bool HasPassword { get; set; } = false;
    public bool AcceptSelfSignedCert { get; set; } = false;
}

public class SaveSmtpSettingsRequest
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 587;
    public bool EnableSsl { get; set; } = true;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = false;
    public bool AcceptSelfSignedCert { get; set; } = false;
}
