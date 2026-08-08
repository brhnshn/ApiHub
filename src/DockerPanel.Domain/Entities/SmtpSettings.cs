using System;

namespace DockerPanel.Domain.Entities;

public class SmtpSettings
{
    public Guid Id { get; set; } = Guid.NewGuid();
    
    public Guid? UserId { get; set; }
    
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 587;
    public bool EnableSsl { get; set; } = true;
    public string Username { get; set; } = string.Empty;
    
    public string EncryptedPassword { get; set; } = string.Empty;
    
    public bool IsEnabled { get; set; } = false;
    public bool AcceptSelfSignedCert { get; set; } = false;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
