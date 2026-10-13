using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using PCDoctor.Core;

namespace PCDoctor.Storage;

/// <summary>What is known about a conversation when it starts.</summary>
public sealed record SessionInfo(string Title, string Model, string Language, string Settings, string Computer);

/// <summary>One row in the history list.</summary>
public sealed record SessionSummary(long Id, DateTimeOffset Started, string Title, int ToolCalls, int ApiCalls, bool HadError);

/// <summary>An activity event as saved, with the mode that was active when it happened.</summary>
public sealed record StoredEvent(AgentEvent Event, PermissionMode Mode);

/// <summary>One API call in the list, without the (large) bodies.</summary>
public sealed record ApiCallSummary(long Id, DateTimeOffset Time, double Seconds, int RequestChars, int ResponseChars, string? Error);

/// <summary>A whole saved conversation.</summary>
public sealed record SessionDetail(
    long Id, DateTimeOffset Started, SessionInfo Info,
    IReadOnlyList<StoredEvent> Events, IReadOnlyList<ApiCallSummary> ApiCalls);

/// <summary>
/// The session history: one SQLite file (%LOCALAPPDATA%\PCDoctor\history.db) holding every conversation,
/// its questions, tool calls, commands, results, answers and the exact API requests and responses.
/// Secrets are masked before anything is written. Writes may come from any thread.
/// </summary>
public sealed class SessionStore
{
    readonly string _connectionString;
    readonly Func<string?> _secret;

    /// <param name="file">The database file. Created if missing.</param>
    /// <param name="secret">Returns the current API key, so it can be masked if it ever turns up in text.</param>
    public SessionStore(string file, Func<string?>? secret = null)
    {
        // No pooling: every call opens and closes the file, so nothing keeps it locked.
        _connectionString = new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString();
        _secret = secret ?? (() => null);
        Execute("""
            CREATE TABLE IF NOT EXISTS sessions (
                id INTEGER PRIMARY KEY, started TEXT NOT NULL, title TEXT NOT NULL,
                model TEXT NOT NULL, language TEXT NOT NULL, settings TEXT NOT NULL, computer TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS events (
                id INTEGER PRIMARY KEY, session_id INTEGER NOT NULL, time TEXT NOT NULL,
                kind TEXT NOT NULL, mode TEXT NOT NULL, title TEXT NOT NULL, detail TEXT);
            CREATE TABLE IF NOT EXISTS api_calls (
                id INTEGER PRIMARY KEY, session_id INTEGER NOT NULL, time TEXT NOT NULL, seconds REAL NOT NULL,
                request TEXT NOT NULL, response TEXT, error TEXT);
            CREATE INDEX IF NOT EXISTS events_session ON events(session_id);
            CREATE INDEX IF NOT EXISTS api_calls_session ON api_calls(session_id);
            """);
    }

    public static string DefaultFile => Path.Combine(AppPaths.DataDir, "history.db");

    /// <summary>A short, local description of this computer for the session record.</summary>
    public static string DescribeComputer() =>
        $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}), " +
        $"{Environment.ProcessorCount} logiske CPU-kerner, " +
        $"{GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024.0 * 1024 * 1024):0.#} GB RAM, " +
        $".NET {Environment.Version}";

    public long StartSession(SessionInfo info, DateTimeOffset? started = null)
    {
        string title = Mask(info.Title);
        if (title.Length > 200) title = title[..200] + "…";
        return Scalar<long>("""
            INSERT INTO sessions (started, title, model, language, settings, computer)
            VALUES ($started, $title, $model, $language, $settings, $computer);
            SELECT last_insert_rowid();
            """,
            ("$started", Time(started ?? DateTimeOffset.Now)), ("$title", title), ("$model", info.Model),
            ("$language", info.Language), ("$settings", Mask(info.Settings)), ("$computer", info.Computer));
    }

    public void AddEvent(long sessionId, AgentEvent e, PermissionMode mode) =>
        Execute("INSERT INTO events (session_id, time, kind, mode, title, detail) VALUES ($s, $time, $kind, $mode, $title, $detail)",
            ("$s", sessionId), ("$time", Time(e.Time)), ("$kind", e.Kind.ToString()), ("$mode", mode.ToString()),
            ("$title", Mask(e.Title)), ("$detail", e.Detail is null ? null : Mask(e.Detail)));

    public void AddApiCall(long sessionId, ApiExchange x) =>
        Execute("INSERT INTO api_calls (session_id, time, seconds, request, response, error) VALUES ($s, $time, $secs, $req, $resp, $err)",
            ("$s", sessionId), ("$time", Time(x.Time)), ("$secs", x.Seconds), ("$req", Mask(x.Request)),
            ("$resp", x.Response is null ? null : Mask(x.Response)), ("$err", x.Error is null ? null : Mask(x.Error)));

    /// <summary>Newest first.</summary>
    public IReadOnlyList<SessionSummary> ListSessions(int limit = 500)
    {
        var list = new List<SessionSummary>();
        Query("""
            SELECT s.id, s.started, s.title,
                (SELECT COUNT(*) FROM events e WHERE e.session_id = s.id AND e.kind = 'ToolStarted'),
                (SELECT COUNT(*) FROM api_calls a WHERE a.session_id = s.id),
                EXISTS (SELECT 1 FROM events e WHERE e.session_id = s.id AND e.kind = 'Error')
            FROM sessions s ORDER BY s.id DESC LIMIT $limit
            """,
            r => list.Add(new SessionSummary(r.GetInt64(0), ParseTime(r.GetString(1)), r.GetString(2), r.GetInt32(3), r.GetInt32(4), r.GetBoolean(5))),
            ("$limit", limit));
        return list;
    }

    public SessionDetail? Load(long id)
    {
        SessionDetail? detail = null;
        Query("SELECT started, title, model, language, settings, computer FROM sessions WHERE id = $id",
            r => detail = new SessionDetail(id, ParseTime(r.GetString(0)),
                new SessionInfo(r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5)), [], []),
            ("$id", id));
        if (detail is null) return null;

        var events = new List<StoredEvent>();
        Query("SELECT time, kind, mode, title, detail FROM events WHERE session_id = $id ORDER BY id",
            r => events.Add(new StoredEvent(
                new AgentEvent(Enum.TryParse(r.GetString(1), out AgentEventKind k) ? k : AgentEventKind.Info, r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4))
                { Time = ParseTime(r.GetString(0)) },
                Enum.TryParse(r.GetString(2), out PermissionMode m) ? m : PermissionMode.ReadOnly)),
            ("$id", id));

        var calls = new List<ApiCallSummary>();
        Query("SELECT id, time, seconds, length(request), coalesce(length(response), 0), error FROM api_calls WHERE session_id = $id ORDER BY id",
            r => calls.Add(new ApiCallSummary(r.GetInt64(0), ParseTime(r.GetString(1)), r.GetDouble(2), r.GetInt32(3), r.GetInt32(4), r.IsDBNull(5) ? null : r.GetString(5))),
            ("$id", id));

        return detail with { Events = events, ApiCalls = calls };
    }

    /// <summary>The full request and response of one API call.</summary>
    public ApiExchange? LoadApiCall(long callId)
    {
        ApiExchange? x = null;
        Query("SELECT time, request, response, seconds, error FROM api_calls WHERE id = $id",
            r => x = new ApiExchange(ParseTime(r.GetString(0)), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetDouble(3), r.IsDBNull(4) ? null : r.GetString(4)),
            ("$id", callId));
        return x;
    }

    public void Delete(long id) =>
        Execute("DELETE FROM events WHERE session_id = $id; DELETE FROM api_calls WHERE session_id = $id; DELETE FROM sessions WHERE id = $id;", ("$id", id));

    /// <summary>Keeps the newest <paramref name="keep"/> conversations and deletes the rest.</summary>
    public void Prune(int keep)
    {
        long removed = Scalar<long>("""
            DELETE FROM sessions WHERE id NOT IN (SELECT id FROM sessions ORDER BY id DESC LIMIT $keep);
            DELETE FROM events WHERE session_id NOT IN (SELECT id FROM sessions);
            DELETE FROM api_calls WHERE session_id NOT IN (SELECT id FROM sessions);
            SELECT total_changes();
            """, ("$keep", keep));
        if (removed > 0)
            Execute("VACUUM"); // give the space back to the disk
    }

    string Mask(string text) => SecretMasker.Apply(text, _secret());

    static string Time(DateTimeOffset t) => t.ToString("o", CultureInfo.InvariantCulture);

    static DateTimeOffset ParseTime(string s) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }

    static SqliteCommand Command(SqliteConnection c, string sql, (string, object?)[] args)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    void Execute(string sql, params (string, object?)[] args)
    {
        using var c = Open();
        using var cmd = Command(c, sql, args);
        cmd.ExecuteNonQuery();
    }

    T Scalar<T>(string sql, params (string, object?)[] args)
    {
        using var c = Open();
        using var cmd = Command(c, sql, args);
        return (T)Convert.ChangeType(cmd.ExecuteScalar()!, typeof(T), CultureInfo.InvariantCulture);
    }

    void Query(string sql, Action<SqliteDataReader> row, params (string, object?)[] args)
    {
        using var c = Open();
        using var cmd = Command(c, sql, args);
        using var r = cmd.ExecuteReader();
        while (r.Read()) row(r);
    }
}
