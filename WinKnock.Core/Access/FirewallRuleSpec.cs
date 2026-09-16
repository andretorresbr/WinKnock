using System.Net;
using WinKnock.Core.Configuration;

namespace WinKnock.Core.Access;

public sealed record FirewallRuleSpec(
    string Name,
    string Description,
    TransportProtocol Protocol,
    IReadOnlyList<int> LocalPorts,
    IPAddress? RemoteAddress,      // null = qualquer origem
    string? ApplicationPath = null // null = qualquer programa
);