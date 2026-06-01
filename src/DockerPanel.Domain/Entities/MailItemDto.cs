using System;

namespace DockerPanel.Domain.Entities;

/// <summary>
/// Webmail arayüzünde listelenecek e-posta mesajlarını temsil eden veri transfer nesnesi (DTO).
/// MailService tarafından Maildir formatındaki ham dosyalardan ayrıştırılarak üretilir.
/// </summary>
public class MailItemDto
{
    /// <summary>Blazor bileşenlerinde seçim takibi için rastgele üretilen benzersiz ID</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Disk üzerindeki e-posta dosyasının kısa adı (silinmede kullanılır)</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>Gönderenin görünen adı (örn: "DockerPanel Team")</summary>
    public string SenderName { get; set; } = string.Empty;

    /// <summary>Gönderenin e-posta adresi (örn: "support@dockerpanel.dev")</summary>
    public string SenderEmail { get; set; } = string.Empty;

    /// <summary>Alıcı e-posta adresi</summary>
    public string To { get; set; } = string.Empty;

    /// <summary>E-posta konusu</summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>E-posta gönderim tarihi ve saati</summary>
    public DateTimeOffset Date { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>E-posta gövdesi (HTML veya düz metin)</summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>Önizleme için kısa içerik özeti (maksimum 100 karakter)</summary>
    public string Snippet { get; set; } = string.Empty;

    /// <summary>Mailin okunmuş olup olmadığı (new/ dizinindeyse false)</summary>
    public bool IsRead { get; set; } = false;

    /// <summary>E-posta ile birlikte gelen gerçek dosya eklerinin listesi</summary>
    public System.Collections.Generic.List<AttachmentDto> Attachments { get; set; } = new();
}
