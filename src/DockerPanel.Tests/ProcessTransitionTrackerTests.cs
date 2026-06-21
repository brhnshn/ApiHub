using Xunit;
using DockerPanel.Infrastructure.Services;
using System;
using System.Threading.Tasks;

namespace DockerPanel.Tests;

public class ProcessTransitionTrackerTests
{
    [Fact]
    public void Tracker_ShouldCorrectlyTrackTransitions()
    {
        string projectName = "test-project-transition";

        // Assert initially false
        Assert.False(ProcessTransitionTracker.IsTransitioning(projectName));

        // Start transition
        ProcessTransitionTracker.StartTransition(projectName);
        Assert.True(ProcessTransitionTracker.IsTransitioning(projectName));

        // End transition
        ProcessTransitionTracker.EndTransition(projectName);
        Assert.False(ProcessTransitionTracker.IsTransitioning(projectName));
    }

    [Fact]
    public void Tracker_ShouldBeCaseInsensitive()
    {
        string projectNameLower = "myproject";
        string projectNameUpper = "MYPROJECT";

        ProcessTransitionTracker.StartTransition(projectNameLower);
        try
        {
            Assert.True(ProcessTransitionTracker.IsTransitioning(projectNameUpper));
        }
        finally
        {
            ProcessTransitionTracker.EndTransition(projectNameUpper);
        }
        Assert.False(ProcessTransitionTracker.IsTransitioning(projectNameLower));
    }

    [Fact]
    public async Task Tracker_ShouldBeThreadSafe()
    {
        int tasksCount = 100;
        var tasks = new Task[tasksCount];

        for (int i = 0; i < tasksCount; i++)
        {
            int index = i;
            tasks[index] = Task.Run(() =>
            {
                string name = $"thread-project-{index}";
                ProcessTransitionTracker.StartTransition(name);
                Assert.True(ProcessTransitionTracker.IsTransitioning(name));
                ProcessTransitionTracker.EndTransition(name);
                Assert.False(ProcessTransitionTracker.IsTransitioning(name));
            });
        }

        await Task.WhenAll(tasks);
    }
}
