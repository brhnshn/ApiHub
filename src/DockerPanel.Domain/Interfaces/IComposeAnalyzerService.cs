namespace DockerPanel.Domain.Interfaces;

public sealed record ComposeEnvironmentVariable(string Name, bool Required, string? DefaultValue, bool IsSecret);
public sealed record ComposeServiceInfo(string Name, string? Image, IReadOnlyList<int> Ports, bool HasHealthCheck);
public sealed record ComposeAnalysis(IReadOnlyList<ComposeEnvironmentVariable> Variables, IReadOnlyList<ComposeServiceInfo> Services);

public interface IComposeAnalyzerService
{
    ComposeAnalysis Analyze(string composeContent);
}
