using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using PCDoctor.Core;

namespace PCDoctor.Diagnostics;

/// <summary>An online game service we know how to check.</summary>
/// <param name="StatusApi">An Atlassian Statuspage summary.json, when the service publishes one. Null otherwise.</param>
/// <param name="ComponentFilter">Only status components whose name contains this are shown (e.g. "Fortnite" on Epic's page).</param>
/// <param name="StatusPage">The page a person can open to read the status.</param>
/// <param name="Hosts">Servers the game must reach; we test that a connection to port 443 opens.</param>
public sealed record GameService(string Id, string Name, string? StatusApi, string? ComponentFilter, string StatusPage, string[] Hosts);

/// <summary>
/// service_status: is a game's online service down, or is it our own connection?
/// Reads the official status page where one exists and tests whether this PC can reach the service's servers
/// and a neutral site, so the two can be told apart.
/// </summary>
public sealed class ServiceStatusTool : ITool
{
    /// <summary>
    /// Fixed list; the model can only pick from it. Epic and Discord publish Atlassian Statuspage pages (checked 2026-10-14).
    /// Roblox, Minecraft, Steam and Xbox have no such feed, so for them only the connection test and the page link are given.
    /// </summary>
    public static readonly GameService[] Services =
    [
        new("fortnite", "Fortnite", "https://status.epicgames.com/api/v2/summary.json", "Fortnite", "https://status.epicgames.com",
            ["www.epicgames.com", "account-public-service-prod.ol.epicgames.com"]),
        new("rocket-league", "Rocket League", "https://status.epicgames.com/api/v2/summary.json", "Rocket League", "https://status.epicgames.com",
            ["www.epicgames.com", "account-public-service-prod.ol.epicgames.com"]),
        new("fall-guys", "Fall Guys", "https://status.epicgames.com/api/v2/summary.json", "Fall Guys", "https://status.epicgames.com",
            ["www.epicgames.com", "account-public-service-prod.ol.epicgames.com"]),
        new("epic", "Epic Games Store / Launcher", "https://status.epicgames.com/api/v2/summary.json", "Epic Games Store", "https://status.epicgames.com",
            ["store.epicgames.com", "launcher-public-service-prod06.ol.epicgames.com"]),
        new("discord", "Discord", "https://discordstatus.com/api/v2/summary.json", null, "https://discordstatus.com",
            ["discord.com", "gateway.discord.gg"]),
        new("roblox", "Roblox", null, null, "https://status.roblox.com",
            ["www.roblox.com", "apis.roblox.com"]),
        new("minecraft", "Minecraft (login and multiplayer)", null, null, "https://help.minecraft.net",
            ["api.minecraftservices.com", "sessionserver.mojang.com", "login.microsoftonline.com"]),
        new("steam", "Steam", null, null, "https://store.steampowered.com",
            ["store.steampowered.com", "api.steampowered.com"]),
        new("xbox", "Xbox network (also Minecraft Bedrock and Game Pass)", null, null, "https://support.xbox.com/xbox-live-status",
            ["xbox.com", "user.auth.xboxlive.com"]),
    ];

    /// <summary>A site that is up nearly always: if it fails too, the problem is this PC's connection.</summary>
    const string NeutralHost = "www.google.com";

    readonly HttpClient _http;
    readonly Func<string, CancellationToken, Task<(bool Ok, long Ms, string? Error)>> _connect;

    /// <param name="http">For tests. Defaults to a client with a 10-second timeout.</param>
    /// <param name="connect">For tests. Defaults to a real TCP connection to port 443.</param>
    public ServiceStatusTool(HttpClient? http = null, Func<string, CancellationToken, Task<(bool, long, string?)>>? connect = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        if (http is null) _http.DefaultRequestHeaders.UserAgent.ParseAdd("PCDoctor/1.0");
        _connect = connect ?? Connect443;
    }

    public string Name => "service_status";
    public string Description =>
        "Check whether a game's online service is down, or whether the problem is this PC's internet. " +
        "Reads the official status page where one exists (Epic Games: Fortnite, Rocket League, Fall Guys, Epic Store; Discord) and " +
        "tests whether this PC can reach the service's servers and a neutral site. Services: " +
        string.Join(", ", Services.Select(s => s.Id)) + ". Without 'service' it checks all of them.";
    public ToolKind Kind => ToolKind.ReadOnly;
    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["service"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(Services.Select(s => (JsonNode)s.Id).ToArray()), ["description"] = "Which service. Omit to check all." },
        },
    };

    public async Task<ToolResult> RunAsync(JsonElement input, CancellationToken ct)
    {
        string? id = input.TryGetProperty("service", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
        var chosen = id is null ? Services : Services.Where(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (chosen.Length == 0)
            return ToolResult.Error($"Unknown service '{id}'. Known: {string.Join(", ", Services.Select(x => x.Id))}.");

        var neutral = _connect(NeutralHost, ct);
        // One download per status page, shared by the services on it (Epic's page covers four).
        var pages = chosen.Where(x => x.StatusApi is not null).Select(x => x.StatusApi!).Distinct()
            .ToDictionary(u => u, u => Fetch(u, ct));
        var checks = chosen.Select(async svc => new
        {
            service = svc.Id,
            name = svc.Name,
            status_page = svc.StatusPage,
            official_status = svc.StatusApi is null ? null : Statuspage.Summarise(await pages[svc.StatusApi], svc.ComponentFilter),
            connections = await Task.WhenAll(svc.Hosts.Select(async h =>
            {
                var (ok, ms, error) = await _connect(h, ct);
                return new { host = h, reachable = ok, ms = ok ? ms : (long?)null, error };
            })),
        }).ToList();

        var results = await Task.WhenAll(checks);
        var (nOk, nMs, nErr) = await neutral;
        return ToolResult.Json(new
        {
            neutral_site = new { host = NeutralHost, reachable = nOk, ms = nOk ? nMs : (long?)null, error = nErr },
            services = results,
            note = "official_status comes from the service's own status page; null means it publishes no machine-readable status, so open status_page. " +
                   "If the neutral site is unreachable, the problem is this PC's internet, not the game. If the neutral site works but the game's " +
                   "servers do not, the service may be down or blocked (firewall, DNS, router). Reachable servers do not prove that logins or matches work.",
        });
    }

    async Task<StatuspageResult> Fetch(string url, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
                return new StatuspageResult(null, $"The status page answered {(int)resp.StatusCode}.");
            return new StatuspageResult(await resp.Content.ReadAsStringAsync(ct), null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new StatuspageResult(null, "The status page could not be read: " + e.Message);
        }
    }

    static async Task<(bool, long, string?)> Connect443(string host, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await client.ConnectAsync(host, 443, timeout.Token);
            return (true, sw.ElapsedMilliseconds, null);
        }
        catch (SocketException ex) { return (false, 0, ex.SocketErrorCode == SocketError.HostNotFound ? "Name not found (DNS)." : ex.SocketErrorCode.ToString()); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return (false, 0, "No answer within 5 seconds."); }
    }
}

public sealed record StatuspageResult(string? Json, string? Error);

/// <summary>Reads an Atlassian Statuspage summary.json (the format Epic Games and Discord use).</summary>
public static class Statuspage
{
    public static object Summarise(StatuspageResult page, string? componentFilter)
    {
        if (page.Json is null)
            return new { error = page.Error };
        try
        {
            using var doc = JsonDocument.Parse(page.Json);
            var r = doc.RootElement;
            string? overall = r.TryGetProperty("status", out var st) ? Str(st, "description") : null;
            string? indicator = r.TryGetProperty("status", out st) ? Str(st, "indicator") : null;

            // Components can be groups with children; match the filter on the component or its group.
            var all = Arr(r, "components").ToList();
            var groupNames = all.Where(c => Str(c, "id") is not null).ToDictionary(c => Str(c, "id")!, c => Str(c, "name") ?? "");
            bool Matches(JsonElement c) =>
                componentFilter is null
                || (Str(c, "name") ?? "").Contains(componentFilter, StringComparison.OrdinalIgnoreCase)
                || (Str(c, "group_id") is { } g && groupNames.GetValueOrDefault(g, "").Contains(componentFilter, StringComparison.OrdinalIgnoreCase));
            var mine = all.Where(Matches).ToList();
            var problems = mine.Where(c => Str(c, "status") is { } s && s != "operational")
                .Select(c => new { component = Str(c, "name"), status = Plain(Str(c, "status")) })
                .Take(20).ToList();

            var incidents = Arr(r, "incidents")
                .Where(i => componentFilter is null || Arr(i, "components").Any(Matches) || (Str(i, "name") ?? "").Contains(componentFilter, StringComparison.OrdinalIgnoreCase)
                            || !Arr(i, "components").Any())
                .Select(i => new
                {
                    title = Str(i, "name"),
                    status = Str(i, "status"),
                    impact = Str(i, "impact"),
                    updated = Str(i, "updated_at"),
                    latest_update = Arr(i, "incident_updates").Select(u => Str(u, "body")).FirstOrDefault() is { } body
                        ? (body.Length > 300 ? body[..300] + " […]" : body) : null,
                })
                .Take(5).ToList();

            var maintenance = Arr(r, "scheduled_maintenances")
                .Where(m => Str(m, "status") == "in_progress")
                .Select(m => new { title = Str(m, "name"), until = Str(m, "scheduled_until") })
                .Take(3).ToList();

            return new
            {
                whole_page = overall,
                whole_page_indicator = indicator,
                checked_components = componentFilter is null ? all.Count : mine.Count,
                components_with_problems = problems,
                all_checked_components_ok = mine.Count > 0 && problems.Count == 0,
                open_incidents = incidents,
                maintenance_now = maintenance,
            };
        }
        catch (JsonException)
        {
            return new { error = "The status page did not return the expected data." };
        }
    }

    static string Plain(string? s) => s switch
    {
        "degraded_performance" => "degraded performance (slow or partly failing)",
        "partial_outage" => "partial outage",
        "major_outage" => "major outage (down)",
        "under_maintenance" => "under maintenance",
        _ => s ?? "?",
    };

    static IEnumerable<JsonElement> Arr(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];

    static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
