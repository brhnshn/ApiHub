using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DockerPanel.Domain.Entities;

namespace DockerPanel.Domain.Interfaces;

public interface IMailService
{
    Task CreateMailAccountAsync(string emailAddress, string password);
    Task DeleteMailAccountAsync(string emailAddress);
    Task<List<MailItemDto>> GetMailsAsync(string emailAddress, string folder = "inbox", int take = 75);
    Task SendMailAsync(string from, string to, string subject, string body, List<AttachmentDto>? attachments = null);
    Task DeleteMailAsync(string emailAddress, string folder, string fileName);
    Task MoveMailAsync(string emailAddress, string sourceFolder, string destFolder, string fileName);
    Task MarkMailAsReadAsync(string emailAddress, string folder, string fileName);
    Task<List<string>> GetCustomLabelsAsync(string emailAddress);
    Task CreateCustomLabelAsync(string emailAddress, string labelName);
    Task DeleteCustomLabelAsync(string emailAddress, string labelName);
    Task<long> GetMailboxUsageBytesAsync(string emailAddress);
}
