using System.Net;
using WinKnock.Core.Configuration;

namespace WinKnock.Core.Engine;

public sealed class KnockSequenceEngine
{
    private sealed class Progress
    {
        public int NextIndex;
        public DateTimeOffset StartedAt;
    }

    private readonly IReadOnlyList<DoorOptions> _doors;
    private readonly Dictionary<(IPAddress Ip, int Door), Progress> _state = new();
    private readonly object _lock = new();
    private readonly int _maxTrackedEntries;

    public KnockSequenceEngine(IEnumerable<DoorOptions> doors, int maxTrackedEntries = 10_000)
    {
        _doors = doors.ToList();

        if (_doors.Any(d => d.Sequence.Count < 2))
            throw new ArgumentException("Todas as Doors precisam de pelo menos 2 portas na sequência.");

        _maxTrackedEntries = maxTrackedEntries;
        KnockPorts = _doors.SelectMany(d => d.Sequence).Distinct().Order().ToList();
    }

    // Portas UDP que o listener precisará abrir
    public IReadOnlyList<int> KnockPorts { get; }

    public int TrackedEntries
    {
        get { lock (_lock) return _state.Count; }
    }

    // Retorna as Doors cuja sequência foi completada por esta batida
    public IReadOnlyList<DoorOptions> OnKnock(IPAddress source, int port, DateTimeOffset now)
    {
        // Sockets dual-mode entregam IPv4 como ::ffff:1.2.3.4
        if (source.IsIPv4MappedToIPv6)
            source = source.MapToIPv4();

        var completed = new List<DoorOptions>();

        lock (_lock)
        {
            for (int i = 0; i < _doors.Count; i++)
            {
                var door = _doors[i];
                var key = (source, i);
                _state.TryGetValue(key, out var progress);

                // Expirou: descarta
                if (progress != null &&
                    now - progress.StartedAt > TimeSpan.FromSeconds(door.SequenceTimeoutSeconds))
                {
                    _state.Remove(key);
                    progress = null;
                }

                // Porta esperada: avança
                if (progress != null && door.Sequence[progress.NextIndex] == port)
                {
                    progress.NextIndex++;

                    if (progress.NextIndex == door.Sequence.Count)
                    {
                        _state.Remove(key);
                        completed.Add(door);
                    }
                    continue;
                }

                // Porta errada: zera o progresso
                if (progress != null)
                    _state.Remove(key);

                // Se for a primeira porta da sequência, começa (ou recomeça)
                if (door.Sequence[0] == port && _state.Count < _maxTrackedEntries)
                {
                    _state[key] = new Progress { NextIndex = 1, StartedAt = now };
                }
            }
        }

        return completed;
    }

    // Remove progressos expirados; o serviço chamará isso periodicamente
    public void Purge(DateTimeOffset now)
    {
        lock (_lock)
        {
            var expired = _state
                .Where(kv => now - kv.Value.StartedAt >
                             TimeSpan.FromSeconds(_doors[kv.Key.Door].SequenceTimeoutSeconds))
                .Select(kv => kv.Key)
                .ToList();

            foreach (var key in expired)
                _state.Remove(key);
        }
    }
}