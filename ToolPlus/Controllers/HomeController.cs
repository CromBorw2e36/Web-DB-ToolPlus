using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using ToolPlus.Common;
using ToolPlus.Common.Attributes;
using ToolPlus.Models;

namespace ToolPlus.Controllers;

public class HomeController : BaseController
{
    public HomeController(MemoryCacheManager memoryCache) : base(memoryCache) { }

    /// <summary>
    /// Home — the list of task pages, taken from MenuDefinition.
    /// SkipModuleCheck: this page reads no business database, and a freshly created account with no
    /// module granted yet still has to get in to see what it is missing.
    /// </summary>
    [TokenCheck(SkipModuleCheck = true)]
    public IActionResult Index()
    {
        ViewData["Title"] = "Home";
        return View();
    }

    [TokenCheck(SkipModuleCheck = true)]
    public IActionResult ThongTinPhien()
    {
        ViewData["Title"] = "Session info";
        return View();
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}
