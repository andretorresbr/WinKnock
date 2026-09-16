using System.Globalization;
using System.Runtime.Versioning;
using WinKnock.Core.Access;
using WinKnock.Core.Configuration;

namespace WinKnock.Firewall;

[SupportedOSPlatform("windows")]
public sealed class WindowsFirewallController : IFirewallController
{
    public const string RuleGroup = "WinKnock";

    private const int ProtocolTcp = 6;
    private const int ProtocolUdp = 17;
    private const int ProtocolAny = 256;
    private const int DirectionIn = 1;
    private const int ActionBlock = 0;
    private const int ActionAllow = 1;
    private const int AllProfiles = 0x7FFFFFFF;

    private readonly object _lock = new();

    private static dynamic CreateComObject(string progId)
    {
        var type = Type.GetTypeFromProgID(progId, throwOnError: true)!;
        return Activator.CreateInstance(type)!;
    }

    private static int ToFwProtocol(TransportProtocol p) =>
        p == TransportProtocol.Tcp ? ProtocolTcp : ProtocolUdp;

    public void AddAllowRule(FirewallRuleSpec spec)
    {
        lock (_lock)
        {
            dynamic policy = CreateComObject("HNetCfg.FwPolicy2");
            dynamic rule = CreateComObject("HNetCfg.FWRule");

            rule.Name = spec.Name;
            rule.Description = spec.Description;
            rule.Grouping = RuleGroup;
            rule.Direction = DirectionIn;
            rule.Action = ActionAllow;
            rule.Protocol = ToFwProtocol(spec.Protocol); // precisa vir antes de LocalPorts
            rule.LocalPorts = string.Join(",",
                spec.LocalPorts.Select(p => p.ToString(CultureInfo.InvariantCulture)));
            rule.RemoteAddresses = spec.RemoteAddress?.ToString() ?? "*";
            if (spec.ApplicationPath is not null)
                rule.ApplicationName = spec.ApplicationPath;
            rule.Profiles = AllProfiles;
            rule.Enabled = true;

            policy.Rules.Add(rule);
        }
    }

    public void RemoveRule(string name)
    {
        lock (_lock)
        {
            dynamic policy = CreateComObject("HNetCfg.FwPolicy2");
            policy.Rules.Remove(name);
        }
    }

    public int RemoveAllManagedRules()
    {
        lock (_lock)
        {
            dynamic policy = CreateComObject("HNetCfg.FwPolicy2");
            var names = new List<string>();

            foreach (dynamic rule in policy.Rules)
            {
                try
                {
                    if ((string?)rule.Grouping == RuleGroup)
                        names.Add((string)rule.Name);
                }
                catch
                {
                    // Algumas regras do sistema não expõem todas as propriedades
                }
            }

            foreach (var name in names)
                policy.Rules.Remove(name);

            return names.Count;
        }
    }

    public IReadOnlyList<string> FindConflicts(int port, TransportProtocol protocol)
    {
        lock (_lock)
        {
            dynamic policy = CreateComObject("HNetCfg.FwPolicy2");
            var conflicts = new List<string>();
            var fwProtocol = ToFwProtocol(protocol);

            foreach (dynamic rule in policy.Rules)
            {
                try
                {
                    if (!(bool)rule.Enabled) continue;
                    if ((int)rule.Direction != DirectionIn) continue;
                    if ((string?)rule.Grouping == RuleGroup) continue;

                    int ruleProtocol = (int)rule.Protocol;
                    if (ruleProtocol != fwProtocol && ruleProtocol != ProtocolAny) continue;

                    string localPorts = ruleProtocol == ProtocolAny ? "*" : (string?)rule.LocalPorts ?? "*";
                    bool wildcard = localPorts.Trim() == "*";

                    if (!wildcard && !PortMatches(localPorts, port)) continue;

                    string? app = ReadString(rule, "ApplicationName");
                    string? service = ReadString(rule, "ServiceName");
                    string? package = ReadString(rule, "LocalAppPackageId");
                    bool scoped = app is not null || service is not null || package is not null;

                    // Porta "*" restrita a um programa/serviço/app só afeta esse alvo: ignora
                    if (wildcard && scoped) continue;

                    string scope = scoped
                        ? $"programa: {app ?? "-"}, serviço: {service ?? "-"}"
                        : "qualquer programa";
                    string remote = (string?)rule.RemoteAddresses ?? "*";

                    conflicts.Add((int)rule.Action == ActionBlock
                        ? $"BLOQUEIO '{rule.Name}' ({scope}) — bloqueio vence permissão"
                        : $"PERMISSÃO '{rule.Name}' (origem: {remote}, {scope}) — porta pode estar acessível sem batida");
                }
                catch
                {
                }
            }

            return conflicts;
        }
    }

    // Lê uma propriedade COM que pode não existir em todas as versões da interface
    private static string? ReadString(dynamic rule, string property)
    {
        try
        {
            string? value = property switch
            {
                "ApplicationName" => (string?)rule.ApplicationName,
                "ServiceName" => (string?)rule.ServiceName,
                "LocalAppPackageId" => (string?)rule.LocalAppPackageId,
                _ => null
            };
            return string.IsNullOrWhiteSpace(value) || value == "*" ? null : value;
        }
        catch
        {
            return null;
        }
    }

    // Aceita "*", "3389", "80,443", "5000-5010"; ignora palavras-chave como "RPC"
    private static bool PortMatches(string localPorts, int port)
    {
        if (localPorts.Trim() == "*") return true;

        foreach (var part in localPorts.Split(',', StringSplitOptions.TrimEntries))
        {
            var range = part.Split('-');
            if (range.Length == 1 && int.TryParse(range[0], out var single) && single == port)
                return true;
            if (range.Length == 2 &&
                int.TryParse(range[0], out var start) &&
                int.TryParse(range[1], out var end) &&
                port >= start && port <= end)
                return true;
        }
        return false;
    }
}