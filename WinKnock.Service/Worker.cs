using Microsoft.Extensions.Options;
using WinKnock.Core.Access;
using WinKnock.Core.Configuration;
using WinKnock.Core.Engine;
using WinKnock.Core.Listener;

namespace WinKnock.Service;

public sealed class Worker(
    IOptions<WinKnockOptions> options,
    IFirewallController firewall,
    ILogger<Worker> logger,
    ILoggerFactory loggerFactory) : BackgroundService
{
    private const string KnockPortsRuleName = "WinKnock - Portas de batida";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Garante que tudo rode de forma assíncrona, sem travar a inicialização do host
        await Task.Yield();

        try
        {
            await RunAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Falha fatal no WinKnock; o serviço será encerrado");
            Environment.Exit(1);
        }
    }
    private async Task RunAsync(CancellationToken stoppingToken)
    {
        var doors = options.Value.Doors;

        var removed = firewall.RemoveAllManagedRules();
        if (removed > 0)
            logger.LogWarning("{Count} regra(s) antigas do WinKnock removidas", removed);

        foreach (var door in doors)
            foreach (var conflict in firewall.FindConflicts(door.TargetPort, door.TargetProtocol))
                logger.LogWarning("Door {Door}: regra conflitante: {Conflict}", door.Name, conflict);

        var engine = new KnockSequenceEngine(doors);
        var access = new DoorAccessManager(firewall, loggerFactory.CreateLogger<DoorAccessManager>());

        // Libera as portas de batida apenas para este executável
        firewall.AddAllowRule(new FirewallRuleSpec(
            Name: KnockPortsRuleName,
            Description: "Portas UDP escutadas pelo WinKnock",
            Protocol: TransportProtocol.Udp,
            LocalPorts: engine.KnockPorts,
            RemoteAddress: null,
            ApplicationPath: Environment.ProcessPath));

        try
        {
            using var listener = new UdpKnockListener(
                engine,
                loggerFactory.CreateLogger<UdpKnockListener>(),
                (door, ip) =>
                {
                    access.Grant(door, ip, DateTimeOffset.UtcNow);
                    return Task.CompletedTask;
                });

            listener.Start();
            var listenTask = listener.RunAsync(stoppingToken);

            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            var lastPurge = DateTimeOffset.UtcNow;

            try
            {
                while (await timer.WaitForNextTickAsync(stoppingToken))
                {
                    var now = DateTimeOffset.UtcNow;
                    access.RevokeExpired(now);

                    if (now - lastPurge >= TimeSpan.FromSeconds(30))
                    {
                        engine.Purge(now);
                        lastPurge = now;
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }

            await listenTask;
        }
        finally
        {
            access.RevokeAll();
            try { firewall.RemoveRule(KnockPortsRuleName); }
            catch (Exception ex) { logger.LogError(ex, "Falha ao remover a regra das portas de batida"); }
        }
    }
}