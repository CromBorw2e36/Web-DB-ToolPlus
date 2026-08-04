using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using ToolPlus.Common;
using ToolPlus.Models.ToolEntity;
using ToolPlus.Models.Users;
using ToolPlus.Services.Interface;

namespace ToolPlus.Services.Users
{
    /// <summary>
    /// ToolPlus's identity layer: accounts, login sessions and per-module permissions — all of it in
    /// the tool's own SQLite file, never borrowed from the databases it browses.
    /// </summary>
    public class UserService : IUserService
    {
        /// <summary>Short cache so a request need not re-read the session; short enough that locking an account takes effect almost at once.</summary>
        private static readonly MemoryCacheEntryOptions ShortCache = new MemoryCacheEntryOptions()
            .SetAbsoluteExpiration(TimeSpan.FromSeconds(5));

        /// <summary>LastSeen is written sparsely — there is no reason to UPDATE on every click.</summary>
        private static readonly TimeSpan LastSeenGap = TimeSpan.FromMinutes(5);

        private static readonly Regex UsernamePattern = new("^[A-Za-z0-9._-]{3,50}$", RegexOptions.Compiled);

        public const int MinPasswordLength = 8;

        private readonly ToolDbContext _db;
        private readonly MemoryCacheManager _cache;
        private readonly IAuditService _audit;
        private readonly ToolPlusOptions _opt;

        public UserService(ToolDbContext db, MemoryCacheManager cache, IAuditService audit,
            IOptions<ToolPlusOptions> opt)
        {
            _db = db;
            _cache = cache;
            _audit = audit;
            _opt = opt.Value;
        }

        // --------------------------------------------------------------- sign-in

        public (LoginStatus Status, ToolUser? User) Authenticate(string username, string password)
        {
            var name = (username ?? "").Trim().ToLowerInvariant();

            var user = _db.ToolUser.AsNoTracking().FirstOrDefault(x => x.Username.ToLower() == name);

            if (user == null || !Verify(password, user.PasswordHash))
            {
                return (LoginStatus.WrongCredentials, null);
            }

            // "Locked" / "expired" is only reported AFTER the password checks out, so an outsider
            // cannot probe which accounts exist.
            if (!user.IsActive) return (LoginStatus.Locked, user);
            if (user.ExpiresAt != null && user.ExpiresAt <= DateTime.Now) return (LoginStatus.Expired, user);

            return (LoginStatus.Ok, user);
        }

        public string CreateSession(ToolUser user, string? ip, string? deviceId, string? userAgent)
        {
            // Dead sessions are swept on sign-in: nothing else prunes this table, it only grows.
            _db.ToolSession.Where(x => x.ExpiresAt <= DateTime.Now).ExecuteDelete();

            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

            _db.ToolSession.Add(new ToolSession
            {
                UserId = user.Id,
                TokenHash = Utility.GetSHA256(token),
                Ip = ip,
                DeviceId = deviceId,
                UserAgent = Truncate(userAgent, 256),
                CreatedAt = DateTime.Now,
                ExpiresAt = DateTime.Now.AddDays(Math.Max(1, _opt.SessionDays)),
                LastSeen = DateTime.Now,
                Revoked = false
            });

            _db.ToolUser.Where(x => x.Id == user.Id)
                .ExecuteUpdate(s => s.SetProperty(x => x.LastLoginAt, DateTime.Now));

            _db.SaveChanges();

            return token;
        }

        public SessionInfo? ResolveSession(string token)
        {
            if (token.IsEmpty()) return null;

            var hash = Utility.GetSHA256(token);
            var info = _cache.GetValue(SessionKey(hash), () => Load(hash), ShortCache);
            if (info == null) return null;

            if (info.Session.LastSeen < DateTime.Now - LastSeenGap)
            {
                var now = DateTime.Now;
                _db.ToolSession.Where(x => x.Id == info.Session.Id)
                    .ExecuteUpdate(s => s.SetProperty(x => x.LastSeen, now));
                info.Session.LastSeen = now;
            }

            return info;
        }

        private SessionInfo? Load(string hash)
        {
            var session = _db.ToolSession.AsNoTracking()
                .FirstOrDefault(x => x.TokenHash == hash && !x.Revoked && x.ExpiresAt > DateTime.Now);

            if (session == null) return null;

            var user = _db.ToolUser.AsNoTracking().FirstOrDefault(x => x.Id == session.UserId);
            if (user == null || !user.IsUsable) return null;

            var modules = _db.ToolUserModule.AsNoTracking()
                .Where(x => x.UserId == user.Id)
                .ToList();

            return new SessionInfo(session, user, modules);
        }

        public void RevokeSession(string token)
        {
            if (token.IsEmpty()) return;

            var hash = Utility.GetSHA256(token);
            _db.ToolSession.Where(x => x.TokenHash == hash).ExecuteUpdate(s => s.SetProperty(x => x.Revoked, true));
            _cache.Remove(SessionKey(hash));
        }

        public bool CanUseModule(SessionInfo session, string moduleKey, out bool canEdit)
        {
            if (session.User.IsAdmin)
            {
                canEdit = true;
                return true;
            }

            var row = session.Modules.FirstOrDefault(
                x => string.Equals(x.ModuleKey, moduleKey, StringComparison.OrdinalIgnoreCase));

            canEdit = row?.CanEdit ?? false;
            return row != null;
        }

        // --------------------------------------------------------- administration

        public async Task<List<UserRow>> ListAsync()
        {
            var users = await _db.ToolUser.AsNoTracking().OrderBy(x => x.Username).ToListAsync();
            var modules = await _db.ToolUserModule.AsNoTracking().ToListAsync();

            return users.Select(u => new UserRow
            {
                Id = u.Id,
                Username = u.Username,
                FullName = u.FullName,
                IsAdmin = u.IsAdmin,
                IsActive = u.IsActive,
                ExpiresAt = u.ExpiresAt,
                MustChangePassword = u.MustChangePassword,
                LastLoginAt = u.LastLoginAt,
                IsUsable = u.IsUsable,
                Modules = modules.Where(m => m.UserId == u.Id)
                    .Select(m => new ModuleAccess { ModuleKey = m.ModuleKey, CanEdit = m.CanEdit })
                    .ToList()
            }).ToList();
        }

        public async Task<Guid> SaveAsync(SaveUserRequest req, string actor)
        {
            var username = (req.Username ?? "").Trim();

            if (!UsernamePattern.IsMatch(username))
            {
                throw new InvalidOperationException(
                    "Username must be 3–50 characters containing only letters, digits, dots, dashes and underscores");
            }

            var lower = username.ToLowerInvariant();
            var taken = await _db.ToolUser.AsNoTracking()
                .AnyAsync(x => x.Username.ToLower() == lower && x.Id != (req.Id ?? Guid.Empty));

            if (taken) throw new InvalidOperationException($"Username \"{username}\" is already taken");

            ToolUser user;

            if (req.Id == null)
            {
                if (req.Password.IsEmpty())
                {
                    throw new InvalidOperationException("A password is required for a new account");
                }

                user = new ToolUser { Id = Guid.NewGuid(), CreatedAt = DateTime.Now, CreatedBy = actor };
                _db.ToolUser.Add(user);
            }
            else
            {
                user = await _db.ToolUser.FirstOrDefaultAsync(x => x.Id == req.Id)
                       ?? throw new InvalidOperationException("Không tìm thấy tài khoản");
            }

            // Captured before the overwrite below, otherwise there is no way to tell whether this
            // account used to be a live administrator.
            var wasAdmin = req.Id != null && user.IsAdmin && user.IsUsable;

            user.Username = username;
            user.FullName = (req.FullName ?? "").Trim();
            user.IsAdmin = req.IsAdmin;
            user.IsActive = req.IsActive;
            user.ExpiresAt = req.ExpiresAt;

            if (!req.Password.IsEmpty())
            {
                CheckPasswordStrength(req.Password!);
                user.PasswordHash = Hash(req.Password!);

                // A password set by an admin must be changed by its owner; changing your own goes
                // through ChangePasswordAsync and never lands here.
                user.MustChangePassword = !string.Equals(username, actor, StringComparison.OrdinalIgnoreCase);
            }

            await GuardLastAdminAsync(user.Id, wasAdmin, user.IsAdmin && user.IsUsable);

            // Module permissions are rewritten wholesale — there are few rows, and diffing them
            // would only add code.
            var existing = await _db.ToolUserModule.Where(x => x.UserId == user.Id).ToListAsync();
            _db.ToolUserModule.RemoveRange(existing);

            foreach (var m in req.Modules.Where(m => !m.ModuleKey.IsEmpty())
                         .GroupBy(m => m.ModuleKey.ToLowerInvariant())
                         .Select(g => g.First()))
            {
                _db.ToolUserModule.Add(new ToolUserModule
                {
                    UserId = user.Id,
                    ModuleKey = m.ModuleKey.ToLowerInvariant(),
                    CanEdit = m.CanEdit
                });
            }

            // Locking an account while its existing sessions keep working would lock nothing.
            if (!user.IsUsable)
            {
                await _db.ToolSession.Where(x => x.UserId == user.Id && !x.Revoked)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.Revoked, true));
            }

            await _db.SaveChangesAsync();

            await _audit.WriteAsync("user-save", actor, null,
                $"{(req.Id == null ? "Add" : "Edit")} account {username}"
                + (user.IsAdmin ? " (admin)" : "")
                + (user.IsUsable ? "" : " — locked"));

            return user.Id;
        }

        public async Task DeleteAsync(Guid id, string actor)
        {
            var user = await _db.ToolUser.FirstOrDefaultAsync(x => x.Id == id)
                       ?? throw new InvalidOperationException("Account not found");

            if (string.Equals(user.Username, actor, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Cannot delete the account you are currently signed in with");
            }

            await GuardLastAdminAsync(user.Id, user.IsAdmin && user.IsUsable, false);

            await _db.ToolUserModule.Where(x => x.UserId == id).ExecuteDeleteAsync();
            await _db.ToolSession.Where(x => x.UserId == id).ExecuteDeleteAsync();

            _db.ToolUser.Remove(user);
            await _db.SaveChangesAsync();

            await _audit.WriteAsync("user-delete", actor, null, $"Delete account {user.Username}");
        }

        public async Task ChangePasswordAsync(Guid userId, string oldPassword, string newPassword, string? keepToken)
        {
            var user = await _db.ToolUser.FirstOrDefaultAsync(x => x.Id == userId)
                       ?? throw new InvalidOperationException("Account not found");

            if (!Verify(oldPassword, user.PasswordHash))
            {
                throw new InvalidOperationException("Current password is incorrect");
            }

            if (Verify(newPassword, user.PasswordHash))
            {
                throw new InvalidOperationException("New password must differ from the current one");
            }

            CheckPasswordStrength(newPassword);

            user.PasswordHash = Hash(newPassword);
            user.MustChangePassword = false;

            // The point of a password change is to cut off whoever else is riding a session ->
            // revoke every other one and keep only the one doing the typing.
            var keepHash = keepToken.IsEmpty() ? "" : Utility.GetSHA256(keepToken!);
            await _db.ToolSession.Where(x => x.UserId == userId && !x.Revoked && x.TokenHash != keepHash)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Revoked, true));

            await _db.SaveChangesAsync();

            // The surviving session still has a cached copy with MustChangePassword = true; without
            // dropping it, the user keeps being bounced back to this page for a few more seconds.
            _cache.Remove(SessionKey(keepHash));

            await _audit.WriteAsync("password-change", user.Username, null, "User changed their own password");
        }

        // ------------------------------------------------------------ helper

        /// <summary>
        /// Losing every administrator means nobody can reach the administration page again, and only
        /// editing the SQLite file by hand gets you out — so it is blocked here. Only checked while
        /// REMOVING the rights of a live admin, so creating an ordinary account never trips it.
        /// </summary>
        private async Task GuardLastAdminAsync(Guid userId, bool wasAdmin, bool stillAdmin)
        {
            if (!wasAdmin || stillAdmin) return;

            var anotherAdminExists = await _db.ToolUser.AsNoTracking()
                .AnyAsync(x => x.Id != userId && x.IsAdmin && x.IsActive
                               && (x.ExpiresAt == null || x.ExpiresAt > DateTime.Now));

            if (!anotherAdminExists)
            {
                throw new InvalidOperationException(
                    "This is the last active administrator — removing/locking/deleting them would leave no one to manage the tool");
            }
        }

        private static string SessionKey(string tokenHash) => "tp-session-" + tokenHash;

        private static void CheckPasswordStrength(string password)
        {
            if (password.Length < MinPasswordLength)
            {
                throw new InvalidOperationException($"Password must be at least {MinPasswordLength} characters");
            }
        }

        public static string Hash(string password) => BCrypt.Net.BCrypt.HashPassword(password);

        private static bool Verify(string? password, string hash)
        {
            if (password.IsEmpty() || hash.IsEmpty()) return false;

            try
            {
                return BCrypt.Net.BCrypt.Verify(password, hash);
            }
            catch
            {
                // A corrupt or malformed hash counts as a wrong password; never let it escape as an error.
                return false;
            }
        }

        private static string? Truncate(string? value, int max)
            => value == null || value.Length <= max ? value : value[..max];
    }
}
