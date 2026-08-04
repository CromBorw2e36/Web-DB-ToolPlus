namespace ToolPlus.Models.ToolEntity;

/// <summary>
/// A login session.
///
/// The cookie holds a random token; only its SHA256 is stored here. App_Data/toolplus.db sits right
/// next to the app, so whoever gets hold of the file still cannot rebuild a live cookie from it.
/// A random token is also why expiry is a real date column rather than something derived from the
/// token's own contents.
/// </summary>
public class ToolSession
{
    public long Id { get; set; }

    public Guid UserId { get; set; }

    public string TokenHash { get; set; } = "";

    public string? Ip { get; set; }

    /// <summary>One session per machine, so signing in here does not sign the other machine out.</summary>
    public string? DeviceId { get; set; }

    public string? UserAgent { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime ExpiresAt { get; set; }

    public DateTime LastSeen { get; set; }

    public bool Revoked { get; set; }
}
