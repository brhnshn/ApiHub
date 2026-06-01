using System.Collections.Generic;
using System.Threading.Tasks;

namespace DockerPanel.Domain.Interfaces;

public class FirewallRuleDto
{
    public int Number { get; set; }
    public string Port { get; set; } = string.Empty;
    public string Protocol { get; set; } = "tcp"; // tcp, udp, any
    public string Action { get; set; } = "ALLOW"; // ALLOW, DENY
    public string Direction { get; set; } = "IN"; // IN, OUT
    public string From { get; set; } = "Anywhere";
}

public interface IFirewallService
{
    Task<bool> IsFirewallActiveAsync();
    Task<IEnumerable<FirewallRuleDto>> GetRulesAsync();
    Task AddRuleAsync(string port, string protocol, string action);
    Task DeleteRuleAsync(int ruleNumber);
    Task SetFirewallStatusAsync(bool active);
}
