using System;

namespace DockerPanel.Domain.Entities;

public class AttachmentDto
{
    public string FileName { get; set; } = string.Empty;
    public string Base64Data { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
}
