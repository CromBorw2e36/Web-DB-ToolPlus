using Microsoft.AspNetCore.Mvc;
using ToolPlus.Common;
using ToolPlus.Common.Attributes;
using ToolPlus.Models.Sync;
using ToolPlus.Services.Interface;

namespace ToolPlus.Controllers
{
    /// <summary>
    /// Repairs a column from another database: pair the rows by a match column, look at what would
    /// change, then write the ones the user ticked.
    ///
    /// The database written to is always the module in the URL. The other one is only ever read.
    /// </summary>
    [TokenCheck]
    public class SyncController : BaseController
    {
        private readonly ISyncService _sync;
        private readonly IModuleContext _module;
        private readonly ModuleRegistry _registry;
        private readonly IUserService _users;

        public SyncController(MemoryCacheManager memoryCache, ISyncService sync, IModuleContext module,
            ModuleRegistry registry, IUserService users)
            : base(memoryCache)
        {
            _sync = sync;
            _module = module;
            _registry = registry;
            _users = users;
        }

        public IActionResult Index()
        {
            ViewData["Title"] = "Đồng bộ trường từ DB khác";
            ViewData["Fill"] = true;
            ViewData["Sources"] = ReadableModules()
                .Select(m => new { m.Key, m.Name, Current = m.Key == _module.Current.Key })
                .ToList();
            return View();
        }

        // -------------------------------------------------------------------- api

        /// <summary>Tables of a module. No module named = the one being written to.</summary>
        public async Task<IActionResult> Tables(string? module)
        {
            try
            {
                var m = module.IsEmpty() ? null : SourceOrThrow(module);
                var tables = await _sync.TablesAsync(m);
                return Success(tables.Select(t => new { t.Full, t.Name, t.IsView }));
            }
            catch (Exception ex)
            {
                return Error(Message(ex));
            }
        }

        public async Task<IActionResult> Columns(string? module, string table)
        {
            try
            {
                var m = module.IsEmpty() ? null : SourceOrThrow(module);
                return Success(new { Table = table, Columns = await _sync.ColumnsAsync(m, table) });
            }
            catch (Exception ex)
            {
                return Error(Message(ex));
            }
        }

        [HttpPost]
        public async Task<IActionResult> Plan([FromBody] SyncPlanRequest req)
        {
            try
            {
                return Success(await _sync.PlanAsync(req, SourceOrThrow(req.SourceModule)));
            }
            catch (Exception ex)
            {
                return Error(Message(ex));
            }
        }

        [HttpPost]
        public async Task<IActionResult> Apply([FromBody] SyncApplyRequest req)
        {
            try
            {
                var result = await _sync.ApplyAsync(req, SourceOrThrow(req.SourceModule),
                    currentUser?.Username ?? "?", ClientIp());

                return Success(result, $"Đã ghi {result.Updated} dòng");
            }
            catch (Exception ex)
            {
                return Error(Message(ex));
            }
        }

        // ----------------------------------------------------------------- shared

        /// <summary>
        /// Resolves the source module AND checks this account may read it.
        ///
        /// TokenCheckAttribute only authorises the module in the URL, so without this an account
        /// granted one database could read any of the others just by naming it in the request body.
        /// Every path that reaches a second database goes through here.
        /// </summary>
        private ModuleDefinition SourceOrThrow(string? key)
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
        private List<ModuleDefinition> ReadableModules()
        {
            var session = ViewData["Session"] as SessionInfo;
            if (session == null) return new List<ModuleDefinition>();

            return _registry.All.Where(m => _users.CanUseModule(session, m.Key, out _)).ToList();
        }

        private static string Message(Exception ex)
            => ex is InvalidOperationException ? ex.Message : ex.GetBaseException().Message;

        private string? ClientIp()
        {
            var real = Request.Headers["X-Real-IP"].ToString();
            return real.IsEmpty() ? HttpContext.Connection.RemoteIpAddress?.ToString() : real;
        }
    }
}
