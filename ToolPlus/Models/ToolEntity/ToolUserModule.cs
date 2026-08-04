namespace ToolPlus.Models.ToolEntity;

/// <summary>
/// One account's permission on one module (= one connection string).
/// A row means the module can be opened; <see cref="CanEdit"/> means its data can be changed.
///
/// This is the whole reason identity moved into SQLite: a flat list of allowed usernames in
/// configuration cannot express it, so anyone who could open the tool could touch EVERY database.
/// Admins need no row here — they have full access by default.
/// </summary>
public class ToolUserModule
{
    public long Id { get; set; }

    public Guid UserId { get; set; }

    /// <summary>Matches ModuleDefinition.Key (lowercase).</summary>
    public string ModuleKey { get; set; } = "";

    public bool CanEdit { get; set; }
}
