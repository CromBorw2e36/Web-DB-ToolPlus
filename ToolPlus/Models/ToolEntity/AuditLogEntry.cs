namespace ToolPlus.Models.ToolEntity;

/// <summary>
/// One recorded action. Kept in SQLite rather than a log file so it can be filtered and searched.
/// </summary>
public class AuditLogEntry
{
    public long Id { get; set; }

    public DateTime Time { get; set; }

    /// <summary>
    /// update | login | login-fail | query | blueprint-save | blueprint-delete | blueprint-run | sync
    /// </summary>
    public string Type { get; set; } = "";

    /// <summary>
    /// The module (database) that was touched. Empty for account-level events (login, user edits).
    /// With several databases in play, a log that does not say WHERE something was changed is
    /// useless.
    /// </summary>
    public string Module { get; set; } = "";

    public string? User { get; set; }

    public string? Ip { get; set; }

    /// <summary>The table that was touched, as schema.name.</summary>
    public string? TableName { get; set; }

    /// <summary>Primary key of the edited row, as JSON.</summary>
    public string? KeyJson { get; set; }

    /// <summary>
    /// Something a person recognises the row by — a username, a document number, a name.
    ///
    /// Captured when the row is written, never looked up afterwards: by the time anybody reads the
    /// log the row may have been deleted or that very value changed. Without it an entry is keyed
    /// only by a primary key, which in most of these databases is a GUID and tells a reader nothing.
    /// </summary>
    public string? Label { get; set; }

    /// <summary>List of {Column, From, To}, as JSON.</summary>
    public string? ChangesJson { get; set; }

    public string? Sql { get; set; }

    public string? Note { get; set; }

    /// <summary>
    /// Ties every row written by one action together — a field sync stamps the same value on each
    /// of its per-row "update" entries and on its own "sync" summary. Without it those rows are
    /// indistinguishable from any other edit, and "undo that run" has nothing to select on.
    ///
    /// Null for anything that touched a single row on its own.
    /// </summary>
    public Guid? RunId { get; set; }
}
