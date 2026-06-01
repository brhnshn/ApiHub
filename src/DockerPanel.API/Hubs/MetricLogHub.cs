using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;

namespace DockerPanel.API.Hubs;

public class MetricLogHub : Hub
{
    public async Task JoinProjectGroup(string projectId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"project_{projectId}");
    }

    public async Task LeaveProjectGroup(string projectId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"project_{projectId}");
    }

    // Geriye dönük uyumluluk için container metotlarını da tutalım
    public async Task JoinContainerGroup(string containerId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"project_{containerId}");
    }

    public async Task LeaveContainerGroup(string containerId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"project_{containerId}");
    }
}
