namespace NOOSE_Website.Models.People;

/// <summary>Person list filter on the record keeper.</summary>
public enum KeeperFilter
{
    /// <summary>Records the viewer keeps or stands in for.</summary>
    Mine = 0,

    /// <summary>Records nobody keeps.</summary>
    Unkept = 1,
}
