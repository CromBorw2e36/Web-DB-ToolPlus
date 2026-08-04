using Microsoft.AspNetCore.Mvc;
using ToolPlus.Common;
using ToolPlus.Models.ToolEntity;

namespace ToolPlus.Controllers
{
    /// <summary>
    /// Base class of every ToolPlus controller. TokenCheckAttribute reads and writes the
    /// properties below directly, which is why controllers must derive from this type.
    ///
    /// It deliberately holds no business DbContext: identity comes from SQLite and business data
    /// goes through IModuleContext, injected only by the controllers that need it.
    /// </summary>
    public abstract class BaseController : Controller
    {
        public MemoryCacheManager memoryCache { get; }

        /// <summary>Signed-in account, populated by TokenCheckAttribute.</summary>
        public ToolUser? currentUser { get; set; }

        /// <summary>Current session token — needed to keep this session alive across a password change, and to sign out.</summary>
        public string sessionToken { get; set; } = "";

        /// <summary>Request carried its token in a header instead of a cookie -> answer with JSON, not a redirect.</summary>
        public bool isAPI { get; set; }

        protected BaseController(MemoryCacheManager memoryCache)
        {
            this.memoryCache = memoryCache;
        }

        protected IActionResult Success(string message = "Success")
            => Ok(new { Status = ToolConst.OK, Message = message });

        protected IActionResult Success<T>(T data, string message = "Success")
            => Ok(new { Status = ToolConst.OK, Message = message, Data = data });

        protected IActionResult Error(string message = "Error")
            => Ok(new { Status = ToolConst.Error, Message = message });

        protected IActionResult Error(object errors, string message = "Error")
            => Ok(new { Status = ToolConst.Error, Message = message, Errors = errors });
    }
}
