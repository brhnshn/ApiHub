using System.Text.RegularExpressions;
using DockerPanel.Domain.Interfaces;

namespace DockerPanel.Infrastructure.Services;

public sealed class ComposeSecurityValidator : IComposeSecurityValidator
{
    private static readonly Regex HostBind = new("(?m)^\\s*-\\s*[\\\"']?(?<source>(?:/|[A-Za-z]:\\\\)[^\\\"']*):", RegexOptions.Compiled);

    public void Validate(string composeContent)
    {
        if (Regex.IsMatch(composeContent, @"(?im)^\s*privileged\s*:\s*true\b") ||
            Regex.IsMatch(composeContent, @"(?im)^\s*(network_mode|pid)\s*:\s*host\b") ||
            composeContent.Contains("/var/run/docker.sock", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(composeContent, @"(?im)^\s*cap_add\s*:") ||
            Regex.IsMatch(composeContent, @"(?im)^\s*devices\s*:") || HostBind.IsMatch(composeContent))
        {
            throw new InvalidOperationException("Compose güvenlik politikası ihlali: host erişimi, privileged, capability veya host bind mount kullanılamaz.");
        }
    }
}
