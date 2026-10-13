namespace PCDoctor.Core;

/// <summary>Where the API key lives: Windows Credential Manager today, an OS keyring on Linux later.</summary>
public interface ISecretStore
{
    string? Read();
    void Write(string secret);
    void Delete();
}
