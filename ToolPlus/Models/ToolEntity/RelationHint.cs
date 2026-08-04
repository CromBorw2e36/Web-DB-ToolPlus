namespace ToolPlus.Models.ToolEntity;

/// <summary>
/// A relationship between two columns that a user linked by hand on the canvas. Databases in the
/// wild often declare almost no real foreign keys, so the tool learns from what people actually
/// link and suggests that next time instead of guessing from names again.
/// </summary>
public class RelationHint
{
    public long Id { get; set; }

    /// <summary>
    /// The module this relationship was learned in. Two databases can have identically named tables
    /// that are linked differently — sharing hints across them would suggest the wrong join.
    /// </summary>
    public string ModuleKey { get; set; } = "";

    public string FromTable { get; set; } = "";

    public string FromColumn { get; set; } = "";

    public string ToTable { get; set; } = "";

    public string ToColumn { get; set; } = "";

    public int UseCount { get; set; }

    public DateTime LastUsed { get; set; }
}
