using Xunit;
using DockerPanel.Domain.Security;
using System;

namespace DockerPanel.Tests;

public class InputValidatorTests
{
    [Theory]
    [InlineData("valid-project-name")]
    [InlineData("project_123")]
    [InlineData("test")]
    public void IsProjectName_ShouldReturnTrue_ForValidProjectNames(string value)
    {
        Assert.True(InputValidator.IsProjectName(value));
    }

    [Theory]
    [InlineData("Invalid Project")]
    [InlineData("project!")]
    [InlineData("")]
    [InlineData(null)]
    public void IsProjectName_ShouldReturnFalse_ForInvalidProjectNames(string value)
    {
        Assert.False(InputValidator.IsProjectName(value!));
    }

    [Theory]
    [InlineData("db_name")]
    [InlineData("DbName123")]
    public void IsDatabaseIdentifier_ShouldReturnTrue_ForValidIdentifiers(string value)
    {
        Assert.True(InputValidator.IsDatabaseIdentifier(value));
    }

    [Theory]
    [InlineData("db-name")]
    [InlineData("db; drop table;")]
    public void IsDatabaseIdentifier_ShouldReturnFalse_ForInvalidIdentifiers(string value)
    {
        Assert.False(InputValidator.IsDatabaseIdentifier(value));
    }

    [Theory]
    [InlineData("subdomain")]
    [InlineData("*")]
    public void IsSubdomainName_ShouldReturnTrue_ForValidSubdomains(string value)
    {
        Assert.True(InputValidator.IsSubdomainName(value));
    }

    [Theory]
    [InlineData("subdomain.com")]
    [InlineData("sub_domain!")]
    public void IsSubdomainName_ShouldReturnFalse_ForInvalidSubdomains(string value)
    {
        Assert.False(InputValidator.IsSubdomainName(value));
    }

    [Theory]
    [InlineData("src/DockerPanel.API/Program.cs")]
    [InlineData("Program.cs")]
    public void IsSafePathOrFile_ShouldReturnTrue_ForSafePaths(string value)
    {
        Assert.True(InputValidator.IsSafePathOrFile(value));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("dir\\file.txt")]
    [InlineData("dir//file.txt")]
    public void IsSafePathOrFile_ShouldReturnFalse_ForUnsafePaths(string value)
    {
        Assert.False(InputValidator.IsSafePathOrFile(value));
    }
}
