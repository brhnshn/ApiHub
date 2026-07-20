namespace DockerPanel.Domain.Interfaces;

public interface IComposeSecurityValidator
{
    void Validate(string composeContent);
}
