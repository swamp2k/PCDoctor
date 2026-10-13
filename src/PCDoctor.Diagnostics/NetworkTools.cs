using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using PCDoctor.Core;

namespace PCDoctor.Diagnostics;

/// <summary>network_ping: ICMP ping to one or more hosts. "gateway" means the router.</summary>
public sealed class PingTool : ITool
{
    public string Name => "network_ping";
    public string Description =>
        "Ping hosts to check reachability and latency. Use \"gateway\" to ping the local router. " +
        "Default when no hosts are given: gateway, 1.1.1.1 and 8.8.8.8 (local network vs internet).";
    public ToolKind Kind => ToolKind.ReadOnly;
    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["hosts"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "string" },
                ["description"] = "Hostnames or IP addresses, or \"gateway\". Max 6.",
            },
            ["count"] = new JsonObject { ["type"] = "integer", ["description"] = "Pings per host, 1-10. Default 4." },
        },
    };

    public async Task<ToolResult> RunAsync(JsonElement input, CancellationToken ct)
    {
        var hosts = new List<string>();
        if (input.TryGetProperty("hosts", out var h) && h.ValueKind == JsonValueKind.Array)
            hosts.AddRange(h.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!));
        if (hosts.Count == 0)
            hosts = ["gateway", "1.1.1.1", "8.8.8.8"];
        int count = input.TryGetProperty("count", out var c) && c.TryGetInt32(out int n) ? Math.Clamp(n, 1, 10) : 4;

        var expanded = new List<string>();
        foreach (string host in hosts.Take(6))
        {
            if (host.Equals("gateway", StringComparison.OrdinalIgnoreCase))
            {
                var gws = Hosts.DefaultGateways();
                if (gws.Count == 0) expanded.Add("gateway:none-found");
                expanded.AddRange(gws);
            }
            else expanded.Add(host);
        }

        var results = new List<object>();
        foreach (string host in expanded)
        {
            ct.ThrowIfCancellationRequested();
            if (host == "gateway:none-found")
            {
                results.Add(new { host = "gateway", error = "No default gateway found; the PC may not be connected to any network." });
                continue;
            }
            if (!Hosts.IsValid(host))
            {
                results.Add(new { host, error = "Not a valid hostname or IP address." });
                continue;
            }
            results.Add(await PingHost(host, count, ct));
        }
        return ToolResult.Json(new { results });
    }

    static async Task<object> PingHost(string host, int count, CancellationToken ct)
    {
        using var ping = new Ping();
        var times = new List<long>();
        string? lastStatus = null;
        string? address = null;
        for (int i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                PingReply reply = await ping.SendPingAsync(host, TimeSpan.FromSeconds(2), cancellationToken: ct);
                lastStatus = reply.Status.ToString();
                if (reply.Status == IPStatus.Success)
                {
                    times.Add(reply.RoundtripTime);
                    address = reply.Address.ToString();
                }
            }
            catch (PingException ex)
            {
                lastStatus = ex.InnerException?.Message ?? ex.Message;
            }
        }
        return new
        {
            host,
            address,
            sent = count,
            received = times.Count,
            loss_percent = 100 * (count - times.Count) / count,
            min_ms = times.Count > 0 ? times.Min() : (long?)null,
            avg_ms = times.Count > 0 ? Math.Round(times.Average(), 1) : (double?)null,
            max_ms = times.Count > 0 ? times.Max() : (long?)null,
            last_status = lastStatus,
        };
    }
}

/// <summary>network_dns: resolve names and show which DNS servers the PC uses.</summary>
public sealed class DnsTool : ITool
{
    public string Name => "network_dns";
    public string Description =>
        "Resolve hostnames with the PC's normal DNS settings and list the configured DNS servers. " +
        "Default when no names are given: www.google.com and www.microsoft.com.";
    public ToolKind Kind => ToolKind.ReadOnly;
    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["names"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "string" },
                ["description"] = "Hostnames to resolve. Max 6.",
            },
        },
    };

    public async Task<ToolResult> RunAsync(JsonElement input, CancellationToken ct)
    {
        var names = new List<string>();
        if (input.TryGetProperty("names", out var n) && n.ValueKind == JsonValueKind.Array)
            names.AddRange(n.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!));
        if (names.Count == 0)
            names = ["www.google.com", "www.microsoft.com"];

        var servers = NetworkInterface.GetAllNetworkInterfaces()
            .Where(i => i.OperationalStatus == OperationalStatus.Up)
            .Select(i => new
            {
                adapter = i.Name,
                dns_servers = i.GetIPProperties().DnsAddresses.Select(a => a.ToString()).ToList(),
            })
            .Where(x => x.dns_servers.Count > 0)
            .ToList();

        var lookups = new List<object>();
        foreach (string name in names.Take(6))
        {
            if (!Hosts.IsValid(name))
            {
                lookups.Add(new { name, error = "Not a valid hostname." });
                continue;
            }
            var sw = Stopwatch.StartNew();
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                IPAddress[] addrs = await Dns.GetHostAddressesAsync(name, timeout.Token);
                lookups.Add(new { name, addresses = addrs.Select(a => a.ToString()).ToList(), ms = sw.ElapsedMilliseconds });
            }
            catch (SocketException ex)
            {
                lookups.Add(new { name, error = ex.SocketErrorCode.ToString(), message = ex.Message, ms = sw.ElapsedMilliseconds });
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                lookups.Add(new { name, error = "Timeout after 10 seconds." });
            }
        }
        return ToolResult.Json(new { configured_dns = servers, lookups });
    }
}

/// <summary>network_tcp: can we open a TCP connection to host:port?</summary>
public sealed class TcpTool : ITool
{
    public string Name => "network_tcp";
    public string Description =>
        "Try to open a TCP connection to host:port (e.g. a game server or www.google.com:443) and report whether it succeeds and how fast.";
    public ToolKind Kind => ToolKind.ReadOnly;
    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["host"] = new JsonObject { ["type"] = "string", ["description"] = "Hostname or IP address." },
            ["port"] = new JsonObject { ["type"] = "integer", ["description"] = "TCP port, 1-65535." },
        },
        ["required"] = new JsonArray("host", "port"),
    };

    public async Task<ToolResult> RunAsync(JsonElement input, CancellationToken ct)
    {
        string? host = input.TryGetProperty("host", out var h) ? h.GetString() : null;
        if (!Hosts.IsValid(host))
            return ToolResult.Error("Not a valid hostname or IP address.");
        if (!input.TryGetProperty("port", out var p) || !p.TryGetInt32(out int port) || port is < 1 or > 65535)
            return ToolResult.Error("Port must be 1-65535.");

        var sw = Stopwatch.StartNew();
        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await client.ConnectAsync(host!, port, timeout.Token);
            return ToolResult.Json(new { host, port, connected = true, ms = sw.ElapsedMilliseconds, remote = client.Client.RemoteEndPoint?.ToString() });
        }
        catch (SocketException ex)
        {
            return ToolResult.Json(new { host, port, connected = false, error = ex.SocketErrorCode.ToString(), message = ex.Message });
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ToolResult.Json(new { host, port, connected = false, error = "Timeout after 5 seconds." });
        }
    }
}
