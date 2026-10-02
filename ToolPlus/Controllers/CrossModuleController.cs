using ToolPlus.Common;
using ToolPlus.Services.Interface;

namespace ToolPlus.Controllers
{
    /// <summary>
    /// Base of the pages that read a SECOND module besides the one in the URL (field sync, table
    /// copy). Kept in one place because <see cref="SourceOrThrow"/> is a security check: a copy of it
    /// that drifts is a way to read a database the account was never granted.
    /// </summary>
    public abstract class CrossModuleController : BaseController
    {
        private readonly ModuleRegistry _registry;
        private readonly IUserService _users;

        protected CrossModuleController(MemoryCacheManager memoryCache, ModuleRegistry registry, IUserService users)
            : base(memoryCache)
        {
            _registry = registry;
            _users = users;
        }

        /// <summary>
        /// Resolves the source module AND checks this account may read it.
        ///
        /// TokenCheckAttribute only authorises the module in the URL, so without this an account
        /// granted one database could read any of the others just by naming it in the request body.
        /// Every path that reaches a second database goes through here.
        /// </summary>
        protected ModuleDefinition SourceOrThrow(string? key)
        {
            var m = _registry.Find(key)
                    ?? throw new InvalidOperationException($"Không có module \"{key}\"");

            var session = ViewData["Session"] as SessionInfo;
            if (session == null || !_users.CanUseModule(session, m.Key, out _))
            {
                throw new InvalidOperationException($"Tài khoản không có quyền trên module \"{m.Name}\"");
            }

            return m;
        }

        /// <summary>Modules this account may read — the candidates for the source dropdown.</summary>
        protected List<ModuleDefinition> ReadableModules()
        {
            var session = ViewData["Session"] as SessionInfo;
            if (session == null) return new List<ModuleDefinition>();

            return _registry.All.Where(m => _users.CanUseModule(session, m.Key, out _)).ToList();
        }

        protected static string Message(Exception ex)
            => ex is InvalidOperationException ? ex.Message : ex.GetBaseException().Message;

        protected string? ClientIp()
        {
            var real = Request.Headers["X-Real-IP"].ToString();
            return real.IsEmpty() ? HttpContext.Connection.RemoteIpAddress?.ToString() : real;
        }
    }
}
