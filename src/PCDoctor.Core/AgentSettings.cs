namespace PCDoctor.Core;

/// <summary>Adjustable limits that stop runaway sessions without blocking deep troubleshooting.</summary>
public sealed class AgentSettings
{
    /// <summary>Max model round-trips per question.</summary>
    public int MaxIterations { get; set; } = 25;

    /// <summary>Default time limit for one shell command.</summary>
    public int ShellTimeoutSeconds { get; set; } = 60;

    /// <summary>The model may ask for a longer limit, up to this.</summary>
    public int MaxShellTimeoutSeconds { get; set; } = 600;

    /// <summary>Tool output longer than this is cut; the full output is saved to a file.</summary>
    public int MaxOutputChars { get; set; } = 20_000;

    /// <summary>Max tokens per model reply.</summary>
    public int MaxTokens { get; set; } = 16_000;
}
