namespace WinKnock.Core.Configuration;

public sealed class WinKnockOptions
{
    public const string SectionName = "WinKnock";

    public List<DoorOptions> Doors { get; set; } = new();
}