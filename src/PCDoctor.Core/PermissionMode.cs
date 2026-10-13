namespace PCDoctor.Core;

/// <summary>How much the agent may do on its own. The user picks this explicitly.</summary>
public enum PermissionMode
{
    /// <summary>Only built-in, non-destructive tools. No free shell at all. The default.</summary>
    ReadOnly,

    /// <summary>Free shell allowed, but the user must approve every single shell command first.</summary>
    Assisted,

    /// <summary>Free shell without per-command approval, as the current Windows user (never elevated).</summary>
    Yolo,
}
