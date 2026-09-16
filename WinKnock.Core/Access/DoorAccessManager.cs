using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Sockets;
using WinKnock.Core.Configuration;

namespace WinKnock.Core.Access;

public sealed class DoorAccessManager
{
    private readonly IFirewallController _firewall;
    private readonly ILogger _logger;
    private readonly Dictionary<string, DateTimeOffset> _active = new();
    private readonly object _lock = new();

    public DoorAccessManager(IFirewallController firewall, ILogger<DoorAccessManager> logger)
    {
        _firewall = firewall;
        _logger = logger;
    }

    public int ActiveCount
    {
        get { lock (_lock) return _active.Count; }
    }

    public static IPAddress Normalize(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
            return ip.MapToIPv4();

        // O firewall não aceita endereços com zona (ex.: fe80::1%12).
        // ScopeId só existe em IPv6; em IPv4 a propriedade lança exceção.
        if (ip.AddressFamily == AddressFamily.InterNetworkV6 && ip.ScopeId != 0)
            return new IPAddress(ip.GetAddressBytes());

        return ip;
    }

    public static string RuleNameFor(DoorOptions door, IPAddress ip) =>
        $"WinKnock - {door.Name} - {Normalize(ip)}";

    public void Grant(DoorOptions door, IPAddress source, DateTimeOffset now)
    {
        var ip = Normalize(source);
        var name = RuleNameFor(door, ip);
        var expiresAt = now.AddSeconds(door.OpenDurationSeconds);

        lock (_lock)
        {
            if (_active.ContainsKey(name))
            {
                _active[name] = expiresAt;
                _logger.LogInformation("Acesso renovado: {Door} para {Ip} até {Expires:HH:mm:ss}",
                    door.Name, ip, expiresAt.ToLocalTime());
                return;
            }

            _firewall.AddAllowRule(new FirewallRuleSpec(
                Name: name,
                Description: $"Criada pelo WinKnock em {now.ToLocalTime():yyyy-MM-dd HH:mm:ss}",
                Protocol: door.TargetProtocol,
                LocalPorts: [door.TargetPort],
                RemoteAddress: ip));

            _active[name] = expiresAt;
            _logger.LogWarning("Acesso liberado: {Door} ({Protocol} {Port}) para {Ip} até {Expires:HH:mm:ss}",
                door.Name, door.TargetProtocol, door.TargetPort, ip, expiresAt.ToLocalTime());
        }
    }

    public void RevokeExpired(DateTimeOffset now)
    {
        lock (_lock)
        {
            var expired = _active.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList();
            foreach (var name in expired)
                TryRevoke(name);
        }
    }

    public void RevokeAll()
    {
        lock (_lock)
        {
            foreach (var name in _active.Keys.ToList())
                TryRevoke(name);
        }
    }

    // Chamado dentro do lock. Se falhar, mantém a entrada para tentar de novo.
    private void TryRevoke(string name)
    {
        try
        {
            _firewall.RemoveRule(name);
            _active.Remove(name);
            _logger.LogInformation("Acesso revogado: {Rule}", name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao remover a regra {Rule}; nova tentativa em breve", name);
        }
    }
}