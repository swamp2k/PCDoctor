using System.Text.Json;
using PCDoctor.Core;

namespace PCDoctor.App;

/// <summary>
/// Appends every activity event (questions, tool calls, commands, output, answers) to a
/// JSON-lines file per session in %LOCALAPPDATA%\PCDoctor\sessions. Never contains the API key.
/// </summary>
sealed class SessionLog
{
    readonly string _file;

    public SessionLog()
    {
        _file = Path.Combine(AppPaths.SubDir("sessions"), $"session-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl");
    }

    public string FilePath => _file;

    public void Write(AgentEvent e, PermissionMode mode)
    {
        try
        {
            string line = JsonSerializer.Serialize(new { time = e.Time, kind = e.Kind.ToString(), mode = mode.ToString(), title = e.Title, detail = e.Detail });
            File.AppendAllText(_file, line + Environment.NewLine);
        }
        catch (IOException) { /* logging must never break a diagnosis */ }
    }
}
