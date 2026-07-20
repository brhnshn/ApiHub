using System.Text.RegularExpressions;
using DockerPanel.Domain.Interfaces;

namespace DockerPanel.Infrastructure.Services;

public sealed class ComposeAnalyzerService : IComposeAnalyzerService
{
    private static readonly Regex VariableRegex = new(@"\$\{(?<name>[A-Za-z_][A-Za-z0-9_]*)(?:(?<separator>:-|:\?| -)(?<default>[^}]*))?\}", RegexOptions.Compiled | RegexOptions.IgnorePatternWhitespace);
    private static readonly Regex ServiceRegex = new(@"^\s{2}(?<name>[A-Za-z0-9_-]+):\s*$", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex ImageRegex = new(@"^\s{4}image:\s*(?<image>[^\s#]+)", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex PortRegex = new("^\\s{6}-\\s*[\\\"']?(?:127\\.0\\.0\\.1:)?(?<host>\\d+):(?<container>\\d+)", RegexOptions.Multiline | RegexOptions.Compiled);

    public ComposeAnalysis Analyze(string composeContent)
    {
        if (string.IsNullOrWhiteSpace(composeContent)) throw new ArgumentException("Compose içeriği boş olamaz.");
        var variables = VariableRegex.Matches(composeContent).Select(m => new ComposeEnvironmentVariable(
            m.Groups["name"].Value,
            string.IsNullOrEmpty(m.Groups["separator"].Value) || m.Groups["separator"].Value == ":?",
            m.Groups["default"].Value.StartsWith("?", StringComparison.Ordinal) ? null : (string.IsNullOrEmpty(m.Groups["default"].Value) ? null : m.Groups["default"].Value),
            m.Groups["name"].Value.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase) || m.Groups["name"].Value.Contains("SECRET", StringComparison.OrdinalIgnoreCase) || m.Groups["name"].Value.Contains("TOKEN", StringComparison.OrdinalIgnoreCase)))
            .GroupBy(v => v.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();

        var services = new List<ComposeServiceInfo>();
        var matches = ServiceRegex.Matches(composeContent);
        foreach (Match match in matches)
        {
            var start = match.Index + match.Length;
            var next = matches.Cast<Match>().FirstOrDefault(m => m.Index > match.Index)?.Index ?? composeContent.Length;
            var block = composeContent[start..next];
            var image = ImageRegex.Match(block).Groups["image"].Value;
            var ports = PortRegex.Matches(block).Select(p => int.Parse(p.Groups["container"].Value)).Distinct().ToList();
            services.Add(new ComposeServiceInfo(match.Groups["name"].Value, string.IsNullOrEmpty(image) ? null : image, ports, block.Contains("healthcheck:", StringComparison.OrdinalIgnoreCase)));
        }
        return new ComposeAnalysis(variables, services);
    }
}
