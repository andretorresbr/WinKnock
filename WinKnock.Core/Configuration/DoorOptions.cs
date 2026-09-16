namespace WinKnock.Core.Configuration;

public sealed class DoorOptions
{
    // Identificador usado nos logs e no nome da regra de firewall
    public string Name { get; set; } = string.Empty;

    // Portas UDP que devem ser "batidas", na ordem
    public List<int> Sequence { get; set; } = new();

    // Tempo máximo para completar a sequência (seq_timeout do knockd)
    public int SequenceTimeoutSeconds { get; set; } = 10;

    // Porta do serviço protegido que será liberada
    public int TargetPort { get; set; }

    public TransportProtocol TargetProtocol { get; set; } = TransportProtocol.Tcp;

    // Quanto tempo a regra fica ativa (cmd_timeout do knockd)
    public int OpenDurationSeconds { get; set; } = 30;
}