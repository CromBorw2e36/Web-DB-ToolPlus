using System.ComponentModel.DataAnnotations.Schema;

namespace ToolPlus.Models.ToolEntity;

/// <summary>
/// A ToolPlus account. Identity lives entirely in SQLite; a module database is only ever a source
/// of data and never grants identity.
///
/// The trade-off to remember: when someone leaves and their account is disabled in the application
/// that owns the data, ToolPlus does NOT find out. Hence <see cref="ExpiresAt"/> — accounts expire
/// on their own to force a review, so do not leave it null for non-admins.
/// </summary>
public class ToolUser
{
    public Guid Id { get; set; }

    /// <summary>Matched case-insensitively, but stored exactly as it was typed.</summary>
    public string Username { get; set; } = "";

    /// <summary>BCrypt — the salt is part of the string.</summary>
    public string PasswordHash { get; set; } = "";

    public string FullName { get; set; } = "";

    /// <summary>Full access: every module, plus user administration.</summary>
    public bool IsAdmin { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>Past this point the account cannot sign in. Null = never expires.</summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>Password was set by an admin — force a change on the next sign-in.</summary>
    public bool MustChangePassword { get; set; }

    public DateTime CreatedAt { get; set; }

    public string CreatedBy { get; set; } = "";

    public DateTime? LastLoginAt { get; set; }

    /// <summary>Still usable — covers both a manual lock and expiry.</summary>
    [NotMapped]
    public bool IsUsable => IsActive && (ExpiresAt == null || ExpiresAt > DateTime.Now);
}
