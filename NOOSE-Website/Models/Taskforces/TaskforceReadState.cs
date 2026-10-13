namespace NOOSE_Website.Models.Taskforces;

/// <summary>Where the caller's unread taskforce messages start, and how many there are.</summary>
/// <param name="SinceUtc">Read mark, or the join time before the first read; null when the caller keeps no mark here.</param>
public sealed record TaskforceReadState(DateTime? SinceUtc, int Unread)
{
    public static readonly TaskforceReadState None = new(null, 0);
}
