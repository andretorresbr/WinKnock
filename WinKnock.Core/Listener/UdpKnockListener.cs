using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using WinKnock.Core.Configuration;
using WinKnock.Core.Engine;

namespace WinKnock.Core.Listener;

public sealed class UdpKnockListener : IDisposable
{
    private readonly KnockSequenceEngine _engine;
    private readonly ILogger _logger;
    private readonly Func<DoorOptions, IPAddress, Task> _onDoorOpened;
    private readonly List<(int Port, Socket Socket)> _sockets = new();

    public UdpKnockListener(
        KnockSequenceEngine engine,
        ILogger<UdpKnockListener> logger,
        Func<DoorOptions, IPAddress, Task> onDoorOpened)
    {
        _engine = engine;
        _logger = logger;
        _onDoorOpened = onDoorOpened;
    }

    // Abre todos os sockets. Síncrono de propósito: se uma porta
    // estiver ocupada, a exceção sobe na hora e o serviço não inicia.
    public void Start()
    {
        try
        {
            foreach (var port in _engine.KnockPorts)
            {
                var socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);

                // Impede que outro processo "sequestre" a porta
                socket.ExclusiveAddressUse = true;

                // Um único socket atende IPv4 e IPv6
                socket.DualMode = true;

                socket.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
                _sockets.Add((port, socket));
                _logger.LogInformation("Escutando batidas em UDP {Port}", port);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public Task RunAsync(CancellationToken ct) =>
        Task.WhenAll(_sockets.Select(s => ReceiveLoopAsync(s.Port, s.Socket, ct)));

    private async Task ReceiveLoopAsync(int port, Socket socket, CancellationToken ct)
    {
        var buffer = new byte[2048];
        EndPoint anyEndPoint = new IPEndPoint(IPAddress.IPv6Any, 0);

        while (!ct.IsCancellationRequested)
        {
            SocketReceiveFromResult result;
            try
            {
                result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, anyEndPoint, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException ex) when (ex.SocketErrorCode is
                SocketError.MessageSize or SocketError.ConnectionReset)
            {
                // Datagrama maior que o buffer ou ICMP residual: ignora
                continue;
            }

            var source = ((IPEndPoint)result.RemoteEndPoint).Address;
            if (source.IsIPv4MappedToIPv6)
                source = source.MapToIPv4();

            _logger.LogDebug("Batida de {Source} em UDP {Port}", source, port);

            // Nunca respondemos nada ao remetente
            var opened = _engine.OnKnock(source, port, DateTimeOffset.UtcNow);

            foreach (var door in opened)
            {
                try
                {
                    await _onDoorOpened(door, source);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Falha ao abrir a Door {Door} para {Source}", door.Name, source);
                }
            }
        }
    }

    public void Dispose()
    {
        foreach (var (_, socket) in _sockets)
            socket.Dispose();
        _sockets.Clear();
    }
}