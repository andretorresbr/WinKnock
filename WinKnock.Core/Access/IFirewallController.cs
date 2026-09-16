using WinKnock.Core.Configuration;

namespace WinKnock.Core.Access;

public interface IFirewallController
{
    void AddAllowRule(FirewallRuleSpec spec);
    void RemoveRule(string name);
    int RemoveAllManagedRules();
    IReadOnlyList<string> FindConflicts(int port, TransportProtocol protocol);
}