namespace ToolPlus.Models.ToolEntity;

/// <summary>
/// One recorded action. Kept in SQLite rather than a log file so it can be filtered and searched.
/// </summary>
public class AuditLogEntry
{
    public long Id { get; set; }

    public DateTime Time { get; set; }

    /// <summary>update | login | login-fail | query | blueprint-save | blueprint-delete | blueprint-run</summary>
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

    /// <summary>List of {Column, From, To}, as JSON.</summary>
    public string? ChangesJson { get; set; }

    public string? Sql { get; set; }

    public string? Note { get; set; }
}
