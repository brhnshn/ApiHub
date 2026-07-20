namespace DockerPanel.Domain.Enums;

public enum DeploymentStatus
{
    Queued,
    Provisioning,
    HealthChecking,
    ProxyConfiguring,
    SslConfiguring,
    Succeeded,
    RollbackPending,
    RollingBack,
    RolledBack,
    RollbackFailed
}
