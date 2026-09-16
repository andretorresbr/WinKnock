using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using WinKnock.Core.Access;
using WinKnock.Core.Configuration;

namespace WinKnock.Tests;

public class DoorAccessManagerTests
{
    private sealed class FakeFirewall : IFirewallController
    {
        public Dictionary<string, FirewallRuleSpec> Rules { get; } = new();
        public bool FailOnRemove { get; set; }

        public void AddAllowRule(FirewallRuleSpec spec) => Rules.Add(spec.Name, spec);

        public void RemoveRule(string name)
        {
            if (FailOnRemove) throw new InvalidOperationException("falha simulada");
            Rules.Remove(name);
        }

        public int RemoveAllManagedRules() { var n = Rules.Count; Rules.Clear(); return n; }
        public IReadOnlyList<string> FindConflicts(int port, TransportProtocol protocol) => [];
    }

    private static readonly DoorOptions Door = new()
    {
        Name = "RDP",
        Sequence = [7000, 8000],
        TargetPort = 3389,
        OpenDurationSeconds = 30
    };
    private static readonly IPAddress Client = IPAddress.Parse("203.0.113.10");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static (DoorAccessManager, FakeFirewall) Create()
    {
        var fw = new FakeFirewall();
        return (new DoorAccessManager(fw, NullLogger<DoorAccessManager>.Instance), fw);
    }

    [Fact]
    public void Grant_CriaRegraRestritaAoIp()
    {
        var (manager, fw) = Create();
        manager.Grant(Door, Client, T0);

        var rule = Assert.Single(fw.Rules.Values);
        Assert.Equal(Client, rule.RemoteAddress);
        Assert.Equal([3389], rule.LocalPorts);
        Assert.Equal(TransportProtocol.Tcp, rule.Protocol);
    }

    [Fact]
    public void GrantRepetido_RenovaSemDuplicar()
    {
        var (manager, fw) = Create();
        manager.Grant(Door, Client, T0);
        manager.Grant(Door, Client, T0.AddSeconds(20));

        Assert.Single(fw.Rules);

        manager.RevokeExpired(T0.AddSeconds(35)); // venceria sem a renovação
        Assert.Single(fw.Rules);

        manager.RevokeExpired(T0.AddSeconds(51));
        Assert.Empty(fw.Rules);
    }

    [Fact]
    public void RevokeAll_RemoveTudo()
    {
        var (manager, fw) = Create();
        manager.Grant(Door, Client, T0);
        manager.Grant(Door, IPAddress.Parse("203.0.113.20"), T0);

        manager.RevokeAll();

        Assert.Empty(fw.Rules);
        Assert.Equal(0, manager.ActiveCount);
    }

    [Fact]
    public void FalhaAoRemover_TentaNovamente()
    {
        var (manager, fw) = Create();
        manager.Grant(Door, Client, T0);

        fw.FailOnRemove = true;
        manager.RevokeExpired(T0.AddSeconds(31));
        Assert.Equal(1, manager.ActiveCount);

        fw.FailOnRemove = false;
        manager.RevokeExpired(T0.AddSeconds(32));
        Assert.Equal(0, manager.ActiveCount);
        Assert.Empty(fw.Rules);
    }

    [Fact]
    public void Ipv4Mapeado_EhNormalizado()
    {
        var (manager, fw) = Create();
        manager.Grant(Door, Client.MapToIPv6(), T0);

        Assert.Equal(Client, Assert.Single(fw.Rules.Values).RemoteAddress);
    }

    [Fact]
    public void Ipv6ComZona_TemZonaRemovida()
    {
        var (manager, fw) = Create();
        manager.Grant(Door, IPAddress.Parse("fe80::1%12"), T0);

        var rule = Assert.Single(fw.Rules.Values);
        Assert.Equal(0, rule.RemoteAddress!.ScopeId);
        Assert.Equal("fe80::1", rule.RemoteAddress.ToString());
    }
}