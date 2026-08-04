namespace ToolPlus.Models.Users;

/// <summary>Permission on one module — shared by both the read and the save path.</summary>
public class ModuleAccess
{
    public string ModuleKey { get; set; } = "";

    public bool CanEdit { get; set; }
}

/// <summary>One row of the user list.</summary>
public class UserRow
{
    public Guid Id { get; set; }

    public string Username { get; set; } = "";

    public string FullName { get; set; } = "";

    public bool IsAdmin { get; set; }

    public bool IsActive { get; set; }

    public DateTime? ExpiresAt { get; set; }

    public bool MustChangePassword { get; set; }

    public DateTime? LastLoginAt { get; set; }

    /// <summary>Manual lock and expiry combined — the UI only has to look at one flag.</summary>
    public bool IsUsable { get; set; }

    public List<ModuleAccess> Modules { get; set; } = new();
}

public class SaveUserRequest
{
    /// <summary>Null = create a new account.</summary>
    public Guid? Id { get; set; }

    public string Username { get; set; } = "";

    public string FullName { get; set; } = "";

    /// <summary>Left empty on an edit = keep the existing password.</summary>
    public string? Password { get; set; }

    public bool IsAdmin { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime? ExpiresAt { get; set; }

    public List<ModuleAccess> Modules { get; set; } = new();
}

public class ChangePasswordRequest
{
    public string OldPassword { get; set; } = "";

    public string NewPassword { get; set; } = "";
}
