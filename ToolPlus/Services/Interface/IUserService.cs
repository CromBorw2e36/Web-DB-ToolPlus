using ToolPlus.Models.ToolEntity;
using ToolPlus.Models.Users;

namespace ToolPlus.Services.Interface
{
    /// <summary>Outcome of an account check — separated out so the reason can be reported instead of a blanket "wrong password".</summary>
    public enum LoginStatus
    {
        Ok,
        WrongCredentials,

        /// <summary>Password was right, but the account is locked by hand.</summary>
        Locked,

        /// <summary>Password was right, but ExpiresAt has passed.</summary>
        Expired
    }

    /// <summary>A live session, carrying its module permissions so no request has to look them up twice.</summary>
    public record SessionInfo(ToolSession Session, ToolUser User, IReadOnlyList<ToolUserModule> Modules);

    public interface IUserService
    {
        (LoginStatus Status, ToolUser? User) Authenticate(string username, string password);

        /// <summary>Creates a session and returns the raw token for the cookie (only its hash is stored).</summary>
        string CreateSession(ToolUser user, string? ip, string? deviceId, string? userAgent);

        /// <summary>Null = wrong token, expired, revoked, or the account is no longer usable.</summary>
        SessionInfo? ResolveSession(string token);

        void RevokeSession(string token);

        /// <summary>Whether this module may be opened; <paramref name="canEdit"/> adds whether it may be edited.</summary>
        bool CanUseModule(SessionInfo session, string moduleKey, out bool canEdit);

        Task<List<UserRow>> ListAsync();

        /// <summary>Creates or updates. Throws InvalidOperationException with a user-facing message on bad input.</summary>
        Task<Guid> SaveAsync(SaveUserRequest req, string actor);

        Task DeleteAsync(Guid id, string actor);

        /// <summary>Self-service password change. Revokes the other sessions and keeps the current one.</summary>
        Task ChangePasswordAsync(Guid userId, string oldPassword, string newPassword, string? keepToken);
    }
}
