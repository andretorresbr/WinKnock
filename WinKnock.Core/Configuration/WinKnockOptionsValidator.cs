using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace WinKnock.Core.Configuration;

public sealed partial class WinKnockOptionsValidator : IValidateOptions<WinKnockOptions>
{
    [GeneratedRegex("^[A-Za-z0-9_-]{1,40}$")]
    private static partial Regex ValidName();

    public ValidateOptionsResult Validate(string? name, WinKnockOptions options)
    {
        var errors = new List<string>();

        if (options.Doors.Count == 0)
            errors.Add("Nenhuma Door configurada.");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sequences = new HashSet<string>();

        foreach (var d in options.Doors)
        {
            var id = string.IsNullOrEmpty(d.Name) ? "(sem nome)" : d.Name;

            if (!ValidName().IsMatch(d.Name))
                errors.Add($"Door '{id}': Name deve ter 1-40 caracteres (letras, números, _ ou -).");
            else if (!names.Add(d.Name))
                errors.Add($"Door '{id}': Name duplicado.");

            if (d.Sequence.Count < 2)
                errors.Add($"Door '{id}': a sequência deve ter pelo menos 2 portas.");

            foreach (var p in d.Sequence)
                if (p is < 1 or > 65535)
                    errors.Add($"Door '{id}': porta de batida inválida {p}.");

            if (!sequences.Add(string.Join(",", d.Sequence)))
                errors.Add($"Door '{id}': sequência idêntica à de outra Door.");

            if (d.TargetPort is < 1 or > 65535)
                errors.Add($"Door '{id}': TargetPort inválida.");

            if (d.TargetProtocol == TransportProtocol.Udp && d.Sequence.Contains(d.TargetPort))
                errors.Add($"Door '{id}': TargetPort não pode ser uma porta de batida.");

            if (d.SequenceTimeoutSeconds is < 1 or > 300)
                errors.Add($"Door '{id}': SequenceTimeoutSeconds deve estar entre 1 e 300.");

            if (d.OpenDurationSeconds is < 1 or > 86400)
                errors.Add($"Door '{id}': OpenDurationSeconds deve estar entre 1 e 86400.");
        }

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }
}