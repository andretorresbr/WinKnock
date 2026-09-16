using System.Net;
using WinKnock.Core.Configuration;
using WinKnock.Core.Engine;

namespace WinKnock.Tests;

public class KnockSequenceEngineTests
{
    private static readonly IPAddress ClientA = IPAddress.Parse("203.0.113.10");
    private static readonly IPAddress ClientB = IPAddress.Parse("203.0.113.20");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static KnockSequenceEngine CreateEngine() => new(new[]
    {
        new DoorOptions
        {
            Name = "RDP",
            Sequence = [7000, 8000, 9000],
            SequenceTimeoutSeconds = 10,
            TargetPort = 3389
        }
    });

    private static IReadOnlyList<DoorOptions> Knock(
        KnockSequenceEngine engine, IPAddress ip, int port, double seconds)
        => engine.OnKnock(ip, port, T0.AddSeconds(seconds));

    [Fact]
    public void SequenciaCorreta_AbreSomenteNaUltimaBatida()
    {
        var engine = CreateEngine();

        Assert.Empty(Knock(engine, ClientA, 7000, 0));
        Assert.Empty(Knock(engine, ClientA, 8000, 1));
        var opened = Knock(engine, ClientA, 9000, 2);

        Assert.Single(opened);
        Assert.Equal("RDP", opened[0].Name);
    }

    [Fact]
    public void OrdemErrada_NaoAbre()
    {
        var engine = CreateEngine();

        Knock(engine, ClientA, 8000, 0);
        Knock(engine, ClientA, 7000, 1);
        Assert.Empty(Knock(engine, ClientA, 9000, 2));
    }

    [Fact]
    public void ForaDoTempo_NaoAbre()
    {
        var engine = CreateEngine();

        Knock(engine, ClientA, 7000, 0);
        Knock(engine, ClientA, 8000, 5);
        Assert.Empty(Knock(engine, ClientA, 9000, 11));
    }

    [Fact]
    public void PortaErradaNoMeio_ZeraMasPermiteRecomecar()
    {
        var engine = CreateEngine();

        Knock(engine, ClientA, 7000, 0);
        Knock(engine, ClientA, 8000, 1);
        Knock(engine, ClientA, 7000, 2); // errada, mas é o início: recomeça
        Knock(engine, ClientA, 8000, 3);
        Assert.Single(Knock(engine, ClientA, 9000, 4));
    }

    [Fact]
    public void ClientesDiferentes_SaoIndependentes()
    {
        var engine = CreateEngine();

        Knock(engine, ClientA, 7000, 0);
        Knock(engine, ClientB, 7000, 0);
        Knock(engine, ClientA, 8000, 1);
        Knock(engine, ClientB, 8000, 1);

        Assert.Single(Knock(engine, ClientA, 9000, 2));
        Assert.Single(Knock(engine, ClientB, 9000, 2));
    }

    [Fact]
    public void BatidasDeOutroIp_NaoCompletamSequencia()
    {
        var engine = CreateEngine();

        Knock(engine, ClientA, 7000, 0);
        Knock(engine, ClientB, 8000, 1);
        Assert.Empty(Knock(engine, ClientA, 9000, 2));
    }

    [Fact]
    public void Ipv4MapeadoEmIpv6_EhTratadoComoOMesmoCliente()
    {
        var engine = CreateEngine();
        var mapped = ClientA.MapToIPv6();

        Knock(engine, ClientA, 7000, 0);
        Knock(engine, mapped, 8000, 1);
        Assert.Single(Knock(engine, ClientA, 9000, 2));
    }

    [Fact]
    public void AposAbrir_EstadoEhLimpo()
    {
        var engine = CreateEngine();

        Knock(engine, ClientA, 7000, 0);
        Knock(engine, ClientA, 8000, 1);
        Knock(engine, ClientA, 9000, 2);

        Assert.Equal(0, engine.TrackedEntries);
    }

    [Fact]
    public void Purge_RemoveProgressosExpirados()
    {
        var engine = CreateEngine();

        Knock(engine, ClientA, 7000, 0);
        Assert.Equal(1, engine.TrackedEntries);

        engine.Purge(T0.AddSeconds(11));
        Assert.Equal(0, engine.TrackedEntries);
    }

    [Fact]
    public void LimiteDeEntradas_IgnoraNovosIps()
    {
        var engine = new KnockSequenceEngine(
            new[] { new DoorOptions { Name = "X", Sequence = [7000, 8000], TargetPort = 22 } },
            maxTrackedEntries: 1);

        Knock(engine, ClientA, 7000, 0);
        Knock(engine, ClientB, 7000, 0);

        Assert.Equal(1, engine.TrackedEntries);
    }
}