using System.Net;
using System.Net.Sockets;

if (args.Length < 2)
{
    Console.Error.WriteLine("Uso: WinKnock.Client <host> <porta1> [porta2 ...] [--delay ms]");
    return 1;
}

var delayMs = 200;
var ports = new List<int>();

for (int i = 1; i < args.Length; i++)
{
    if (args[i] == "--delay" && i + 1 < args.Length && int.TryParse(args[i + 1], out var d) && d >= 0)
    {
        delayMs = d;
        i++;
    }
    else if (int.TryParse(args[i], out var p) && p is >= 1 and <= 65535)
    {
        ports.Add(p);
    }
    else
    {
        Console.Error.WriteLine($"Argumento inválido: {args[i]}");
        return 1;
    }
}

IPAddress[] addresses;
try
{
    addresses = await Dns.GetHostAddressesAsync(args[0]);
}
catch (SocketException ex)
{
    Console.Error.WriteLine($"Não foi possível resolver '{args[0]}': {ex.Message}");
    return 1;
}

var target = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
             ?? addresses.FirstOrDefault();

if (target is null)
{
    Console.Error.WriteLine($"Nenhum endereço encontrado para '{args[0]}'.");
    return 1;
}

using var udp = new UdpClient(target.AddressFamily);
byte[] payload = [0];

foreach (var port in ports)
{
    await udp.SendAsync(payload, payload.Length, new IPEndPoint(target, port));
    Console.WriteLine($"Batida enviada: {target}:{port}/udp");
    await Task.Delay(delayMs);
}

return 0;