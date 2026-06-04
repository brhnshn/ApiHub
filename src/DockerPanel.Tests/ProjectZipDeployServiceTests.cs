using Xunit;
using DockerPanel.Infrastructure.Services;
using System;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;

namespace DockerPanel.Tests;

public class ProjectZipDeployServiceTests
{
    private readonly ProjectZipDeployService _deployService;

    public ProjectZipDeployServiceTests()
    {
        _deployService = new ProjectZipDeployService();
    }

    [Fact]
    public async Task DeployZipAsync_ShouldThrowArgumentException_ForInvalidProjectName()
    {
        using var ms = new MemoryStream();
        await Assert.ThrowsAsync<ArgumentException>(() => 
            _deployService.DeployZipAsync("invalid project name!", ms));
    }

    [Fact]
    public async Task DeployZipAsync_ShouldThrowInvalidOperationException_WhenZipSlipAttempted()
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            // Zip Slip entry attempting to traverse outside target directory
            var entry = archive.CreateEntry("../escaped_file.txt");
            using var writer = new StreamWriter(entry.Open());
            await writer.WriteAsync("unsafe content");
        }
        ms.Position = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() => 
            _deployService.DeployZipAsync("testproject", ms));
    }

    [Fact]
    public async Task DeployZipAsync_ShouldExtractSuccessfully_WhenZipIsSafe()
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            var entry1 = archive.CreateEntry("subfolder/file1.txt");
            using (var writer = new StreamWriter(entry1.Open()))
            {
                await writer.WriteAsync("safe content 1");
            }

            var entry2 = archive.CreateEntry("file2.txt");
            using (var writer = new StreamWriter(entry2.Open()))
            {
                await writer.WriteAsync("safe content 2");
            }
        }
        ms.Position = 0;

        var targetDir = await _deployService.DeployZipAsync("testproject", ms);

        try
        {
            Assert.True(Directory.Exists(targetDir));
            Assert.True(File.Exists(Path.Combine(targetDir, "file2.txt")));
            Assert.True(File.Exists(Path.Combine(targetDir, "subfolder", "file1.txt")));
        }
        finally
        {
            if (Directory.Exists(targetDir))
            {
                Directory.Delete(targetDir, true);
            }
        }
    }
}
