using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using DockerPanel.Domain.Interfaces;
using DockerPanel.Domain.Entities;

namespace DockerPanel.Infrastructure.Services;

public class MailService : IMailService
{
    private readonly DockerClient _dockerClient;
    private const string MailContainerName = "dockerpanel-mailserver";
    private const string PhysicalMailDataDir = "/opt/dockerpanel/mail/data";

    public MailService()
    {
        Uri dockerUri;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            dockerUri = new Uri("npipe://./pipe/docker_engine");
        }
        else
        {
            dockerUri = new Uri("unix:///var/run/docker.sock");
        }

        _dockerClient = new DockerClientConfiguration(dockerUri).CreateClient();
    }

    private string ResolveMailPath(string domain, string username)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return Path.Combine(AppContext.BaseDirectory, "opt_dockerpanel", "mail", "data", domain, username);
        }
        return Path.Combine(PhysicalMailDataDir, domain, username);
    }

    private string GetActualMaildirPath(string domain, string username)
    {
        var mailFolder = ResolveMailPath(domain, username);
        
        // Eğer doğrudan mailFolder altında cur veya new varsa, dovecot doğrudan burayı kullanıyor demektir (Maildir klasörü olmadan).
        if (Directory.Exists(Path.Combine(mailFolder, "new")) || Directory.Exists(Path.Combine(mailFolder, "cur")))
        {
            return mailFolder;
        }
        
        // Eğer Maildir alt klasörü varsa VE onun altında cur veya new varsa, o zaman Maildir alt klasörünü kullanıyoruzdur.
        var maildirPath = Path.Combine(mailFolder, "Maildir");
        if (Directory.Exists(Path.Combine(maildirPath, "new")) || Directory.Exists(Path.Combine(maildirPath, "cur")))
        {
            return maildirPath;
        }
        
        // Eğer hiçbiri yoksa (yeni açılan hesap), varsayılan olarak doğrudan mailFolder'ı kullanalım.
        // Çünkü docker-mailserver varsayılanı doğrudan bu dizindir (Maildir olmadan).
        return mailFolder;
    }

    public async Task CreateMailAccountAsync(string emailAddress, string password)
    {
        // Girdi regex kontrolü
        if (!emailAddress.Contains('@') || emailAddress.Split('@').Length != 2)
        {
            throw new ArgumentException("Geçersiz e-posta adresi biçimi!");
        }

        SystemLogQueue.Log("info", $"[Mail] Yeni e-posta hesabı ekleniyor: {emailAddress}");
        SystemLogQueue.Log("info", $"$ docker exec -it {MailContainerName} setup email add {emailAddress} ********");

        // Arka planda güvenli docker exec komutunu başlat
        var command = new List<string> { "setup", "email", "add", emailAddress, password };
        
        var (success, errorOutput) = await RunMailserverExecAsync(command);

        if (!success)
        {
            SystemLogQueue.Log("error", $"[Mail] E-posta hesabı oluşturulamadı! Hata: {errorOutput}");
            throw new InvalidOperationException($"E-posta hesabı mail sunucusunda oluşturulamadı! Hata: {errorOutput}");
        }

        SystemLogQueue.Log("info", $"[Mail] Dovecot/Postfix posta kutusu başarıyla yapılandırıldı.");
        
        // Hesaba hoş geldin maili seed et
        SystemLogQueue.Log("info", $"[Mail] Posta kutusu ({emailAddress}) için ilk hoş geldiniz (welcome) e-postası dizine yerleştiriliyor...");
        SeedWelcomeEmail(emailAddress);
        SystemLogQueue.Log("info", $"[Mail] E-posta hesabı ({emailAddress}) başarıyla oluşturuldu.");
    }

    private void SeedWelcomeEmail(string emailAddress)
    {
        try
        {
            var parts = emailAddress.Split('@');
            var username = parts[0];
            var domain = parts[1];
            var maildirPath = GetActualMaildirPath(domain, username);
            var newDir = Path.Combine(maildirPath, "new");
            Directory.CreateDirectory(newDir);

            var fileName = $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.welcome.eml";
            var welcomeFilePath = Path.Combine(newDir, fileName);

            var emailContent = $@"From: DockerPanel Team <support@dockerpanel.dev>
To: {emailAddress}
Subject: Hoş Geldiniz! DockerPanel Altyapısı Aktif Edildi.
Date: {DateTimeOffset.UtcNow:r}
MIME-Version: 1.0
Content-Type: text/html; charset=utf-8

<html>
<body style='font-family: sans-serif; background-color: #0f172a; color: #e2e8f0; padding: 20px;'>
<h2 style='color: #10b981;'>DockerPanel Mail Sunucusuna Hoş Geldiniz!</h2>
<p>Merhaba,</p>
<p>E-posta hesabınız <strong>docker-mailserver</strong> üzerinde başarıyla aktif edilmiştir.</p>
<p>Bu e-posta, sistemimiz tarafından Dovecot/Postfix Maildir dizininizde gerçek bir dosya olarak oluşturulmuştur.</p>
<p>Aşağıdaki bilgilerle dış istemcilerinizi (Outlook, Thunderbird vb.) yapılandırabilirsiniz:</p>
<ul>
    <li><strong>IMAP Sunucusu:</strong> sunucu_ip_adresiniz (Port: 993 SSL)</li>
    <li><strong>SMTP Sunucusu:</strong> sunucu_ip_adresiniz (Port: 587 TLS)</li>
</ul>
<p>Herhangi bir sorunuz olursa destek ekibimizle iletişime geçebilirsiniz.</p>
<p>Saygılarımızla,<br/>DockerPanel Geliştirici Ekibi</p>
</body>
</html>";

            File.WriteAllText(welcomeFilePath, emailContent, Encoding.UTF8);
        }
        catch
        {
            // Seed işlemini yut
        }
    }

    public async Task DeleteMailAccountAsync(string emailAddress)
    {
        if (!emailAddress.Contains('@')) return;

        var parts = emailAddress.Split('@');
        var username = parts[0];
        var domain = parts[1];

        SystemLogQueue.Log("warning", $"[Mail] E-posta hesabı kaldırılıyor: {emailAddress}");
        SystemLogQueue.Log("info", $"$ docker exec -it {MailContainerName} setup email del {emailAddress}");

        // 1. Mail sunucusundan hesabı kaldır
        var command = new List<string> { "setup", "email", "del", emailAddress };
        await RunMailserverExecAsync(command);

        // 2. Fiziksel posta kutusu dizinlerinin silindiğinden emin ol
        var mailFolder = ResolveMailPath(domain, username);
        SystemLogQueue.Log("info", $"[Mail] Posta kutusu dizini kaldırılıyor: {mailFolder}");
        try
        {
            if (Directory.Exists(mailFolder))
            {
                Directory.Delete(mailFolder, true);
            }
        }
        catch (Exception ex)
        {
            SystemLogQueue.Log("warning", $"[Mail] Posta kutusu dizini silinirken hata (Windows kilidi veya zaten silinmiş olabilir): {ex.Message}");
        }
        SystemLogQueue.Log("info", $"[Mail] E-posta hesabı ({emailAddress}) sistemden başarıyla silindi.");
    }

    public async Task<List<MailItemDto>> GetMailsAsync(string emailAddress, string folder = "inbox", int take = 75)
    {
        var result = new List<MailItemDto>();
        if (!emailAddress.Contains('@')) return result;

        var parts = emailAddress.Split('@');
        var username = parts[0];
        var domain = parts[1];

        var maildirPath = GetActualMaildirPath(domain, username);

        string subFolder = folder.ToLower() switch
        {
            "sent" => ".Sent",
            "draft" => ".Drafts",
            "spam" => ".Spam",
            "trash" => ".Trash",
            "archive" => ".Archive",
            "label" => ".Labels",
            _ => folder.StartsWith("label-", StringComparison.OrdinalIgnoreCase) ? "." + folder : ""
        };

        var targetMaildir = string.IsNullOrEmpty(subFolder) ? maildirPath : Path.Combine(maildirPath, subFolder);
        
        var curDir = Path.Combine(targetMaildir, "cur");
        var newDir = Path.Combine(targetMaildir, "new");

        // Dizinleri otomatik oluştur (hata almamak için)
        try
        {
            Directory.CreateDirectory(curDir);
            Directory.CreateDirectory(newDir);
        }
        catch
        {
            // Windows test ortamı yetki hatası vb. durumlar için yut
        }

        var mailFiles = new List<(string Path, bool IsNew, DateTime LastWriteTimeUtc)>();
        if (Directory.Exists(curDir))
        {
            mailFiles.AddRange(Directory.GetFiles(curDir).Select(f => (f, false, File.GetLastWriteTimeUtc(f))));
        }
        if (Directory.Exists(newDir))
        {
            mailFiles.AddRange(Directory.GetFiles(newDir).Select(f => (f, true, File.GetLastWriteTimeUtc(f))));
        }

        foreach (var (filePath, isNew, _) in mailFiles
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Take(Math.Clamp(take, 1, 200)))
        {
            try
            {
                var mailItem = await ParseRawEmailFromFileAsync(filePath);
                mailItem.IsRead = !isNew;
                mailItem.FilePath = Path.GetFileName(filePath);
                mailItem.Id = Guid.NewGuid(); // Random ID for Blazor selection
                result.Add(mailItem);
            }
            catch
            {
                // Corrupt mailleri yut
            }
        }

        return result.OrderByDescending(m => m.Date).ToList();
    }

    public async Task SendMailAsync(string from, string to, string subject, string body, List<AttachmentDto>? attachments = null)
    {
        // Construct standard compliant MIME message via MimeKit
        var mimeMessage = new MimeKit.MimeMessage();
        mimeMessage.From.Add(MimeKit.MailboxAddress.Parse(from));
        mimeMessage.To.Add(MimeKit.MailboxAddress.Parse(to));
        mimeMessage.Subject = subject;
        mimeMessage.Date = DateTimeOffset.UtcNow;

        var bodyBuilder = new MimeKit.BodyBuilder { HtmlBody = body };

        if (attachments != null)
        {
            foreach (var att in attachments)
            {
                try
                {
                    var bytes = Convert.FromBase64String(att.Base64Data);
                    bodyBuilder.Attachments.Add(att.FileName, bytes, MimeKit.ContentType.Parse(att.ContentType));
                }
                catch (Exception ex)
                {
                    SystemLogQueue.Log("warning", $"[Mail] Eklenti eklenirken hata: {att.FileName}, {ex.Message}");
                }
            }
        }

        mimeMessage.Body = bodyBuilder.ToMessageBody();

        var fileName = $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.eml";

        // 1. Gönderenin Sent dizinine kaydet
        if (from.Contains('@'))
        {
            var fromParts = from.Split('@');
            var fromUsername = fromParts[0];
            var fromDomain = fromParts[1];
            var maildirPath = GetActualMaildirPath(fromDomain, fromUsername);
            var fromSentDir = Path.Combine(maildirPath, ".Sent", "cur");
            Directory.CreateDirectory(fromSentDir);
            await mimeMessage.WriteToAsync(Path.Combine(fromSentDir, fileName));
        }

        // 2. Alıcı bizim sunucumuzdaysa, onun Inbox/new dizinine kaydet (Yerel Anlık Teslimat)
        if (to.Contains('@'))
        {
            var toParts = to.Split('@');
            var toUsername = toParts[0];
            var toDomain = toParts[1];
            var toFolder = ResolveMailPath(toDomain, toUsername);
            
            if (Directory.Exists(toFolder))
            {
                var maildirPath = GetActualMaildirPath(toDomain, toUsername);
                var toInboxNewDir = Path.Combine(maildirPath, "new");
                Directory.CreateDirectory(toInboxNewDir);
                await mimeMessage.WriteToAsync(Path.Combine(toInboxNewDir, fileName));
            }
        }

        // 3. SMTP Relay aracılığıyla dış dünyaya gönder (docker-mailserver SMTP port 25 localhost üzerinden)
        try
        {
            using (var smtpClient = new System.Net.Mail.SmtpClient("127.0.0.1", 25))
            {
                var mailMessage = new System.Net.Mail.MailMessage
                {
                    From = new System.Net.Mail.MailAddress(from),
                    Subject = subject,
                    Body = body,
                    IsBodyHtml = true
                };
                mailMessage.To.Add(to);
                
                if (attachments != null)
                {
                    foreach (var att in attachments)
                    {
                        var bytes = Convert.FromBase64String(att.Base64Data);
                        var ms = new System.IO.MemoryStream(bytes);
                        var attachment = new System.Net.Mail.Attachment(ms, att.FileName, att.ContentType);
                        mailMessage.Attachments.Add(attachment);
                    }
                }
                
                // Localhost üzerinden bağlandığı için mynetworks kapsamında şifresiz gönderim yapılır.
                await smtpClient.SendMailAsync(mailMessage);
                SystemLogQueue.Log("info", $"[Mail] E-posta SMTP üzerinden dış dünyaya başarıyla iletildi: {from} -> {to}");
            }
        }
        catch (Exception ex)
        {
            // Windows test ortamında veya 25 portu kapalı olduğunda programın çökmesini önler, log yazıp devam eder
            SystemLogQueue.Log("warning", $"[Mail] E-posta SMTP üzerinden dış dünyaya gönderilemedi: {ex.Message}");
        }
    }

    public Task DeleteMailAsync(string emailAddress, string folder, string fileName)
    {
        if (!emailAddress.Contains('@')) return Task.CompletedTask;

        var parts = emailAddress.Split('@');
        var username = parts[0];
        var domain = parts[1];

        var maildirPath = GetActualMaildirPath(domain, username);

        string subFolder = folder.ToLower() switch
        {
            "sent" => ".Sent",
            "draft" => ".Drafts",
            "spam" => ".Spam",
            "trash" => ".Trash",
            "archive" => ".Archive",
            "label" => ".Labels",
            _ => folder.StartsWith("label-", StringComparison.OrdinalIgnoreCase) ? "." + folder : ""
        };

        var targetMaildir = string.IsNullOrEmpty(subFolder) ? maildirPath : Path.Combine(maildirPath, subFolder);
        
        var curPath = Path.Combine(targetMaildir, "cur", fileName);
        var newPath = Path.Combine(targetMaildir, "new", fileName);

        var filePath = File.Exists(curPath) ? curPath : (File.Exists(newPath) ? newPath : null);

        if (filePath != null)
        {
            if (folder.ToLower() == "trash")
            {
                // Kalıcı olarak sil
                File.Delete(filePath);
            }
            else
            {
                // Çöp kutusu (.Trash) dizinine taşı
                var trashCurDir = Path.Combine(maildirPath, ".Trash", "cur");
                try
                {
                    Directory.CreateDirectory(trashCurDir);
                    var destPath = Path.Combine(trashCurDir, fileName);
                    if (File.Exists(destPath))
                    {
                        destPath = Path.Combine(trashCurDir, $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.{fileName}");
                    }
                    File.Move(filePath, destPath);
                }
                catch (Exception ex)
                {
                    SystemLogQueue.Log("warning", $"[Mail] E-posta çöp kutusuna taşınamadı, kalıcı siliniyor: {ex.Message}");
                    throw;
                }
            }
        }

        return Task.CompletedTask;
    }

    private async Task<MailItemDto> ParseRawEmailFromFileAsync(string filePath)
    {
        var dto = new MailItemDto();
        try
        {
            var message = await MimeKit.MimeMessage.LoadAsync(filePath);

            dto.Subject = message.Subject ?? "";
            
            var fromMailbox = message.From.Mailboxes.FirstOrDefault();
            if (fromMailbox != null)
            {
                dto.SenderName = !string.IsNullOrEmpty(fromMailbox.Name) ? fromMailbox.Name : fromMailbox.Address;
                dto.SenderEmail = fromMailbox.Address ?? "";
            }
            else
            {
                dto.SenderName = "Bilinmeyen Gönderici";
                dto.SenderEmail = "";
            }

            dto.To = string.Join(", ", message.To.Mailboxes.Select(m => m.Address));
            dto.Date = message.Date;

            if (!string.IsNullOrEmpty(message.HtmlBody))
            {
                dto.Body = message.HtmlBody;
            }
            else if (!string.IsNullOrEmpty(message.TextBody))
            {
                dto.Body = WebUtility.HtmlEncode(message.TextBody).Replace("\n", "<br/>");
            }
            else
            {
                dto.Body = "";
            }

            // Gelen maildeki dosya eklerini ayrıştır
            foreach (var attachment in message.Attachments)
            {
                if (attachment is MimeKit.MimePart part)
                {
                    try
                    {
                        if (part.Content == null)
                        {
                            continue;
                        }

                        using (var ms = new System.IO.MemoryStream())
                        {
                            part.Content.DecodeTo(ms);
                            var base64 = Convert.ToBase64String(ms.ToArray());
                            dto.Attachments.Add(new DockerPanel.Domain.Entities.AttachmentDto
                            {
                                FileName = part.FileName ?? "unnamed_attachment",
                                Base64Data = base64,
                                ContentType = part.ContentType?.MimeType ?? "application/octet-stream"
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        SystemLogQueue.Log("warning", $"[Mail] Eklenti ayrıştırılırken hata: {part.FileName}, {ex.Message}");
                    }
                }
            }

            var cleanText = message.TextBody ?? StripHtmlTags(message.HtmlBody ?? "");
            // Satır sonlarını boşluğa çevirip temizleyelim
            var snippetText = cleanText.Replace("\r", "").Replace("\n", " ").Trim();
            dto.Snippet = snippetText.Length > 100 ? snippetText.Substring(0, 100) + "..." : snippetText;
        }
        catch (Exception ex)
        {
            SystemLogQueue.Log("warning", $"[Mail] E-posta ayrıştırılırken hata oluştu ({Path.GetFileName(filePath)}): {ex.Message}");
            dto.Subject = "Ayrıştırma Hatası";
            dto.Body = "Bu e-posta mesajı düzgün ayrıştırılamadı.";
            dto.Snippet = "Hata...";
        }

        return dto;
    }

    private string StripHtmlTags(string html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;
        var array = new char[html.Length];
        var arrayIndex = 0;
        var inside = false;

        for (int i = 0; i < html.Length; i++)
        {
            char let = html[i];
            if (let == '<')
            {
                inside = true;
                continue;
            }
            if (let == '>')
            {
                inside = false;
                continue;
            }
            if (!inside)
            {
                array[arrayIndex] = let;
                arrayIndex++;
            }
        }
        return new string(array, 0, arrayIndex).Trim();
    }

    public async Task MoveMailAsync(string emailAddress, string sourceFolder, string destFolder, string fileName)
    {
        if (!emailAddress.Contains('@')) return;

        var parts = emailAddress.Split('@');
        var username = parts[0];
        var domain = parts[1];

        var maildirPath = GetActualMaildirPath(domain, username);

        string sourceSub = sourceFolder.ToLower() switch
        {
            "sent" => ".Sent",
            "draft" => ".Drafts",
            "spam" => ".Spam",
            "trash" => ".Trash",
            "archive" => ".Archive",
            "label" => ".Labels",
            _ => sourceFolder.StartsWith("label-", StringComparison.OrdinalIgnoreCase) ? "." + sourceFolder : ""
        };

        string destSub = destFolder.ToLower() switch
        {
            "sent" => ".Sent",
            "draft" => ".Drafts",
            "spam" => ".Spam",
            "trash" => ".Trash",
            "archive" => ".Archive",
            "label" => ".Labels",
            _ => destFolder.StartsWith("label-", StringComparison.OrdinalIgnoreCase) ? "." + destFolder : ""
        };

        var sourceDir = string.IsNullOrEmpty(sourceSub) ? maildirPath : Path.Combine(maildirPath, sourceSub);
        var destDir = string.IsNullOrEmpty(destSub) ? maildirPath : Path.Combine(maildirPath, destSub);

        var sourceCurPath = Path.Combine(sourceDir, "cur", fileName);
        var sourceNewPath = Path.Combine(sourceDir, "new", fileName);

        var sourceFilePath = File.Exists(sourceCurPath) ? sourceCurPath : (File.Exists(sourceNewPath) ? sourceNewPath : null);

        if (sourceFilePath != null)
        {
            var destCurDir = Path.Combine(destDir, "cur");
            Directory.CreateDirectory(destCurDir);
            var destFilePath = Path.Combine(destCurDir, fileName);

            if (File.Exists(destFilePath))
            {
                destFilePath = Path.Combine(destCurDir, $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.{fileName}");
            }
            File.Move(sourceFilePath, destFilePath);
            SystemLogQueue.Log("info", $"[Mail] E-posta başarıyla taşındı: {sourceFolder} -> {destFolder} ({fileName})");
        }
        await Task.CompletedTask;
    }

    public Task MarkMailAsReadAsync(string emailAddress, string folder, string fileName)
    {
        if (!emailAddress.Contains('@')) return Task.CompletedTask;

        var parts = emailAddress.Split('@');
        var username = parts[0];
        var domain = parts[1];
        var maildirPath = GetActualMaildirPath(domain, username);

        string subFolder = folder.ToLower() switch
        {
            "sent" => ".Sent",
            "draft" => ".Drafts",
            "spam" => ".Spam",
            "trash" => ".Trash",
            "archive" => ".Archive",
            "label" => ".Labels",
            _ => folder.StartsWith("label-", StringComparison.OrdinalIgnoreCase) ? "." + folder : ""
        };

        var targetMaildir = string.IsNullOrEmpty(subFolder) ? maildirPath : Path.Combine(maildirPath, subFolder);
        var sourcePath = Path.Combine(targetMaildir, "new", fileName);
        if (!File.Exists(sourcePath))
        {
            return Task.CompletedTask;
        }

        var curDir = Path.Combine(targetMaildir, "cur");
        Directory.CreateDirectory(curDir);

        var destPath = Path.Combine(curDir, fileName);
        if (File.Exists(destPath))
        {
            destPath = Path.Combine(curDir, $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.{fileName}");
        }

        File.Move(sourcePath, destPath);
        return Task.CompletedTask;
    }

    public async Task<List<string>> GetCustomLabelsAsync(string emailAddress)
    {
        var result = new List<string>();
        if (!emailAddress.Contains('@')) return result;

        var parts = emailAddress.Split('@');
        var username = parts[0];
        var domain = parts[1];
        var maildirPath = GetActualMaildirPath(domain, username);

        if (Directory.Exists(maildirPath))
        {
            var dirs = Directory.GetDirectories(maildirPath, ".*");
            foreach (var dir in dirs)
            {
                var folderName = Path.GetFileName(dir);
                // Standart sistem klasörlerini filtrele
                if (folderName.Equals(".Sent", StringComparison.OrdinalIgnoreCase) ||
                    folderName.Equals(".Drafts", StringComparison.OrdinalIgnoreCase) ||
                    folderName.Equals(".Spam", StringComparison.OrdinalIgnoreCase) ||
                    folderName.Equals(".Trash", StringComparison.OrdinalIgnoreCase) ||
                    folderName.Equals(".Archive", StringComparison.OrdinalIgnoreCase) ||
                    folderName.Equals(".Labels", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (folderName.StartsWith(".Label-", StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(folderName.Substring(7));
                }
                else
                {
                    result.Add(folderName.Substring(1)); // Noktayı kaldır (.Fatura -> Fatura)
                }
            }
        }

        return await Task.FromResult(result);
    }

    public async Task CreateCustomLabelAsync(string emailAddress, string labelName)
    {
        if (!emailAddress.Contains('@')) return;

        var parts = emailAddress.Split('@');
        var username = parts[0];
        var domain = parts[1];
        var maildirPath = GetActualMaildirPath(domain, username);

        var cleanLabel = labelName.Replace(" ", "_").Replace(".", "_");
        var labelDir = Path.Combine(maildirPath, $".Label-{cleanLabel}");

        Directory.CreateDirectory(Path.Combine(labelDir, "cur"));
        Directory.CreateDirectory(Path.Combine(labelDir, "new"));
        Directory.CreateDirectory(Path.Combine(labelDir, "tmp"));

        SystemLogQueue.Log("info", $"[Mail] Yeni özel etiket klasörü oluşturuldu: .Label-{cleanLabel} ({emailAddress})");
        await Task.CompletedTask;
    }

    public async Task DeleteCustomLabelAsync(string emailAddress, string labelName)
    {
        if (!emailAddress.Contains('@')) return;

        var parts = emailAddress.Split('@');
        var username = parts[0];
        var domain = parts[1];
        var maildirPath = GetActualMaildirPath(domain, username);

        var cleanLabel = labelName.Replace(" ", "_").Replace(".", "_");
        var labelDir = Path.Combine(maildirPath, $".Label-{cleanLabel}");

        if (Directory.Exists(labelDir))
        {
            Directory.Delete(labelDir, recursive: true);
            SystemLogQueue.Log("info", $"[Mail] Özel etiket klasörü silindi: .Label-{cleanLabel} ({emailAddress})");
        }

        await Task.CompletedTask;
    }

    public async Task<long> GetMailboxUsageBytesAsync(string emailAddress)
    {
        if (!emailAddress.Contains('@')) return 0;

        var parts = emailAddress.Split('@');
        var username = parts[0];
        var domain = parts[1];
        var maildirPath = GetActualMaildirPath(domain, username);

        if (!Directory.Exists(maildirPath)) return 0;

        try
        {
            var totalBytes = Directory
                .EnumerateFiles(maildirPath, "*", SearchOption.AllDirectories)
                .Sum(f =>
                {
                    try { return new FileInfo(f).Length; }
                    catch { return 0L; }
                });
            return await Task.FromResult(totalBytes);
        }
        catch
        {
            return 0;
        }
    }

    private async Task<(bool Success, string ErrorOutput)> RunMailserverExecAsync(List<string> command)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return (true, string.Empty); // Windows local development testing simulation
        }

        try
        {
            var containers = await _dockerClient.Containers.ListContainersAsync(new ContainersListParameters { All = true });
            var mailContainer = containers.FirstOrDefault(c => c.Names.Contains($"/{MailContainerName}"));

            if (mailContainer == null)
            {
                return (false, "Mail sunucusu konteyneri (dockerpanel-mailserver) aktif bulunamadı!");
            }

            var execCreate = await _dockerClient.Exec.ExecCreateContainerAsync(mailContainer.ID, new ContainerExecCreateParameters
            {
                AttachStdout = true,
                AttachStderr = true,
                Cmd = command
            });

            using var execStream = await _dockerClient.Exec.StartAndAttachContainerExecAsync(execCreate.ID, false);
            var result = await execStream.ReadOutputToEndAsync(CancellationToken.None);

            var inspect = await _dockerClient.Exec.InspectContainerExecAsync(execCreate.ID);

            if (inspect.ExitCode != 0)
            {
                var errorOutput = string.IsNullOrEmpty(result.stderr) ? result.stdout : result.stderr;
                return (false, errorOutput);
            }

            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
