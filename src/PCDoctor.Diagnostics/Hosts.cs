using System.Net;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;

namespace PCDoctor.Diagnostics;

static partial class Hosts
{
    [GeneratedRegex(@"^(?=.{1,253}$)([a-zA-Z0-9]([a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?)(\.[a-zA-Z0-9]([a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?)*\.?$")]
    private static partial Regex HostnameRegex();

    /// <summary>Accepts only a plain hostname or IP address, nothing else.</summary>
    public static bool IsValid(string? host) =>
        !string.IsNullOrWhiteSpace(host) && (IPAddress.TryParse(host, out _) || HostnameRegex().IsMatch(host));

    /// <summary>IPv4/IPv6 default gateways of network adapters that are up.</summary>
    public static List<string> DefaultGateways() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().GatewayAddresses)
            .Select(g => g.Address)
            .Where(a => !a.Equals(IPAddress.Any) && !a.Equals(IPAddress.IPv6Any))
            .Select(a => a.ToString())
            .Distinct()
            .ToList();
}
