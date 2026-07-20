using DockerPanel.Infrastructure.Services;
using DockerPanel.Domain.Interfaces;
using Xunit;

namespace DockerPanel.Tests;

public sealed class DeploymentTests
{
    [Fact]
    public void ComposeAnalyzer_FindsRequiredAndSecretVariables()
    {
        var yaml = """
services:
  app:
    image: example/app
    ports:
      - "3000:3000"
    environment:
      SECRET_KEY: ${SECRET_KEY:?required}
      APP_URL: ${APP_URL:-http://localhost}
  redis:
    image: redis
""";

        var result = new ComposeAnalyzerService().Analyze(yaml);

        Assert.Contains(result.Services, s => s.Name == "app" && s.Ports.Contains(3000));
        var secret = Assert.Single(result.Variables, v => v.Name == "SECRET_KEY");
        Assert.True(secret.Required);
        Assert.True(secret.IsSecret);
        var appUrl = Assert.Single(result.Variables, v => v.Name == "APP_URL");
        Assert.False(appUrl.Required);
        Assert.Equal("http://localhost", appUrl.DefaultValue);
    }

    [Fact]
    public void ComposeAnalyzer_DeduplicatesVariables()
    {
        var result = new ComposeAnalyzerService().Analyze("services:\n  app:\n    image: app\n    environment:\n      A: ${A}\n      B: ${A}");

        Assert.Single(result.Variables);
        Assert.Equal("A", result.Variables[0].Name);
    }

    [Fact]
    public void ComposeSecurityValidator_RejectsHostAccess()
    {
        IComposeSecurityValidator validator = new ComposeSecurityValidator();
        Assert.Throws<InvalidOperationException>(() => validator.Validate("services:\n  app:\n    privileged: true"));
        Assert.Throws<InvalidOperationException>(() => validator.Validate("services:\n  app:\n    volumes:\n      - /var/run/docker.sock:/var/run/docker.sock"));
    }

    [Fact]
    public void ComposeSecurityValidator_AllowsNamedVolumes()
    {
        new ComposeSecurityValidator().Validate("services:\n  db:\n    image: postgres\n    volumes:\n      - db_data:/var/lib/postgresql/data\nvolumes:\n  db_data:");
    }
}
