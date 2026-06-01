using System.Text.RegularExpressions;

namespace DockerPanel.Domain.Security;

public static partial class InputValidator
{
    public static bool IsProjectName(string value)
    {
        return !string.IsNullOrWhiteSpace(value) && ProjectNameRegex().IsMatch(value);
    }

    public static bool IsDatabaseIdentifier(string value)
    {
        return !string.IsNullOrWhiteSpace(value) && DatabaseIdentifierRegex().IsMatch(value);
    }

    public static bool IsSubdomainName(string value)
    {
        return !string.IsNullOrWhiteSpace(value) && (value == "*" || SubdomainNameRegex().IsMatch(value));
    }

    public static bool IsDomainName(string value)
    {
        return !string.IsNullOrWhiteSpace(value) && DomainNameRegex().IsMatch(value);
    }

    public static void ThrowIfInvalidProjectName(string value, string message)
    {
        if (!IsProjectName(value))
        {
            throw new ArgumentException(message);
        }
    }

    [GeneratedRegex("^[a-z0-9_-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ProjectNameRegex();

    [GeneratedRegex("^[a-zA-Z0-9_]+$", RegexOptions.CultureInvariant)]
    private static partial Regex DatabaseIdentifierRegex();

    [GeneratedRegex("^[a-z0-9_-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SubdomainNameRegex();

    [GeneratedRegex("^[a-z0-9_.-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex DomainNameRegex();
}
