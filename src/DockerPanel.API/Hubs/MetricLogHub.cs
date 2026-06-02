using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using DockerPanel.Infrastructure.Data;

namespace DockerPanel.API.Hubs;

[Authorize]
public class MetricLogHub : Hub
{
    private readonly DockerPanelDbContext _dbContext;

    public MetricLogHub(DockerPanelDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task JoinProjectGroup(string projectId)
    {
        if (Guid.TryParse(projectId, out var pId) && await IsUserAuthorizedForProjectAsync(pId))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"project_{projectId}");
        }
    }

    public async Task LeaveProjectGroup(string projectId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"project_{projectId}");
    }

    // Geriye dönük uyumluluk için container metotlarını da tutalım
    public async Task JoinContainerGroup(string containerId)
    {
        if (Guid.TryParse(containerId, out var cId) && await IsUserAuthorizedForProjectAsync(cId))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"project_{containerId}");
        }
    }

    public async Task LeaveContainerGroup(string containerId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"project_{containerId}");
    }

    private async Task<bool> IsUserAuthorizedForProjectAsync(Guid projectId)
    {
        var userIdStr = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(userIdStr, out var userId))
        {
            return false;
        }

        return await _dbContext.Projects
            .AnyAsync(p => p.Id == projectId && p.UserId == userId);
    }
}
