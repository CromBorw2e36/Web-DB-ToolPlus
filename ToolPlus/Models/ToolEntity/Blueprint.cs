namespace ToolPlus.Models.ToolEntity;

/// <summary>
/// One blueprint: the tables placed on the canvas and how they are linked.
/// The graph stays raw JSON — its shape is still changing, and forcing it into relational tables
/// would only buy migrations.
/// </summary>
public class Blueprint
{
    public Guid Id { get; set; }

    /// <summary>
    /// The module (database) this blueprint belongs to. A blueprint names specific tables and
    /// columns, so running it against another database errors on a missing table — and worse, if
    /// both databases happen to have that table it succeeds and edits the wrong place.
    /// </summary>
    public string ModuleKey { get; set; } = "";

    public string Name { get; set; } = "";

    public string Description { get; set; } = "";

    /// <summary>Symbol id in _Icons.cshtml (without the leading #).</summary>
    public string Icon { get; set; } = "i-panel";

    public string GraphJson { get; set; } = "{}";

    /// <summary>Pinned blueprints appear in the sidebar as if they were pages of their own.</summary>
    public bool Pinned { get; set; }

    public string Owner { get; set; } = "";

    /// <summary>Defaults to true — this is a team tool, everyone sees and edits the same blueprints.</summary>
    public bool IsShared { get; set; } = true;

    public DateTime CreatedAt { get; set; }

    public string CreatedBy { get; set; } = "";

    public DateTime? ModifiedAt { get; set; }

    public string? ModifiedBy { get; set; }
}
