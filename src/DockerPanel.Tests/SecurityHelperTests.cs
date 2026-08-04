using System;
using System.IO;
using System.IO.Compression;
using DockerPanel.API.Helpers;
using Xunit;

namespace DockerPanel.Tests;

public class SecurityHelperTests
{
    [Fact]
    public void SafeExtractZip_ShouldThrowException_OnPartialPathTraversal()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ZipTest_" + Guid.NewGuid().ToString("N"));
        var destDir = Path.Combine(tempDir, "target");
        var siblingDir = Path.Combine(tempDir, "target_evil");
        var zipPath = Path.Combine(tempDir, "test.zip");

        Directory.CreateDirectory(destDir);
        Directory.CreateDirectory(siblingDir);

        try
        {
            using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("../target_evil/malicious.txt");
                using var writer = new StreamWriter(entry.Open());
                writer.Write("evil content");
            }

            Assert.Throws<InvalidOperationException>(() => SecurityHelper.SafeExtractZip(zipPath, destDir));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }
}
