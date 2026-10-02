using Microsoft.AspNetCore.Mvc;
using ToolPlus.Common;
using ToolPlus.Common.Attributes;
using ToolPlus.Models.Copy;
using ToolPlus.Services.Interface;

namespace ToolPlus.Controllers
{
    /// <summary>
    /// Compares tables between another database and this one, then copies the differences the user
    /// chose — a refresh of a backup or test copy without a full backup and restore.
    ///
    /// The database written to is always the module in the URL, and only when that module is
    /// configured with CopyTarget = true. The other one is only ever read.
    /// </summary>
    [TokenCheck]
    public class CopyController : CrossModuleController
    {
        private readonly ITableCopyService _copy;
        private readonly IModuleContext _module;

        public CopyController(MemoryCacheManager memoryCache, ITableCopyService copy, IModuleContext module,
            ModuleRegistry registry, IUserService users)
            : base(memoryCache, registry, users)
        {
            _copy = copy;
            _module = module;
        }

        public IActionResult Index()
        {
            ViewData["Title"] = "Chép bảng từ DB khác";
            ViewData["Fill"] = true;
            ViewData["Sources"] = ReadableModules()
                .Where(m => m.Key != _module.Current.Key)
                .Select(m => new { m.Key, m.Name })
                .ToList();
            return View();
        }

        // -------------------------------------------------------------------- api

        // The query parameter must NOT be called "module": model binding reads route values before
        // the query string, so {module} in the URL (the target) would silently win over it.
        public async Task<IActionResult> Tables(string source)
        {
            try
            {
                return Success(await _copy.TablesAsync(SourceOrThrow(source)));
            }
            catch (Exception ex)
            {
                return Error(Message(ex));
            }
        }

        [HttpPost]
        public async Task<IActionResult> Order([FromBody] CopyOrderRequest req)
        {
            try
            {
                return Success(await _copy.OrderAsync(req.Tables));
            }
            catch (Exception ex)
            {
                return Error(Message(ex));
            }
        }

        [HttpPost]
        public async Task<IActionResult> Compare([FromBody] CopyCompareRequest req)
        {
            try
            {
                return Success(await _copy.CompareAsync(req, SourceOrThrow(req.SourceModule),
                    HttpContext.RequestAborted));
            }
            catch (Exception ex)
            {
                return Error(Message(ex));
            }
        }

        [HttpPost]
        public async Task<IActionResult> Apply([FromBody] CopyApplyRequest req)
        {
            try
            {
                var result = await _copy.ApplyAsync(req, SourceOrThrow(req.SourceModule),
                    currentUser?.Username ?? "?", ClientIp(), HttpContext.RequestAborted);

                return Success(result);
            }
            catch (Exception ex)
            {
                return Error(Message(ex));
            }
        }
    }
}
