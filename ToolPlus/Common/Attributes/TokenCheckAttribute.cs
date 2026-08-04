using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using ToolPlus.Controllers;
using ToolPlus.Models.APIModels;
using ToolPlus.Services.Interface;

namespace ToolPlus.Common.Attributes
{
    /// <summary>
    /// Authenticates against ToolPlus's own session store (SQLite). A module database is only ever
    /// a source of data here — it never grants identity.
    ///
    /// Three layers, in order: valid session -> (where required) must be admin -> must have
    /// permission on the module being opened. The module's write permission is assigned onto
    /// IModuleContext, so every write path has a single place to ask.
    /// </summary>
    public class TokenCheckAttribute : ActionFilterAttribute
    {
        /// <summary>true = admins only (the user administration page).</summary>
        public bool AdminOnly { get; set; }

        /// <summary>
        /// true = skip the forced password change. Set this on the password change page itself and
        /// nowhere else, or it redirects to itself forever.
        /// </summary>
        public bool SkipPasswordChange { get; set; }

        /// <summary>
        /// true = the page belongs to the account rather than to a module (home, session info,
        /// password change), so module permissions are not checked.
        ///
        /// Without this flag a user who has not been granted any module is blocked on EVERY page —
        /// including the password change page they are being forced onto, leaving them stuck with
        /// no way out.
        /// </summary>
        public bool SkipModuleCheck { get; set; }

        public override void OnActionExecuting(ActionExecutingContext context)
        {
            if (context.Controller is not BaseController controller) return;

            var services = context.HttpContext.RequestServices;
            var options = services.GetRequiredService<IOptions<ToolPlusOptions>>().Value;
            var users = services.GetRequiredService<IUserService>();
            var module = services.GetRequiredService<IModuleContext>();
            var request = context.HttpContext.Request;

            // fetch() sends this header so it gets JSON back instead of a redirect to an HTML page.
            if (request.Headers["X-Requested-With"] == "fetch")
            {
                controller.isAPI = true;
            }

            string? token;
            if (!request.Headers[ToolConst.TokenHeader].IsEmpty())
            {
                token = request.Headers[ToolConst.TokenHeader]!;
                controller.isAPI = true;
            }
            else
            {
                token = request.Cookies[options.CookieName];
            }

            if (token.IsEmpty())
            {
                context.Result = Reject(context, controller, options);
                return;
            }

            var session = users.ResolveSession(token!);
            if (session == null)
            {
                context.Result = Reject(context, controller, options);
                return;
            }

            controller.currentUser = session.User;
            controller.sessionToken = token!;

            if (session.User.MustChangePassword && !SkipPasswordChange)
            {
                context.Result = controller.isAPI
                    ? new JsonResult(new ResponseModel(ToolConst.PerrmissionDenied,
                        "Password change required before continuing"))
                    : new RedirectResult("/login/doimatkhau");
                return;
            }

            // The URL named no module and the default one is not granted to this user: send them to
            // a module they can use rather than blocking them. They did nothing wrong — they just
            // opened a path that does not say which database it means.
            if (!module.FromRoute && !controller.isAPI
                && !request.Path.StartsWithSegments("/login")
                && !users.CanUseModule(session, module.Current.Key, out _))
            {
                var registry = services.GetRequiredService<ModuleRegistry>();
                var fallback = registry.All.FirstOrDefault(m => users.CanUseModule(session, m.Key, out _));

                if (fallback != null)
                {
                    context.Result = new RedirectResult("/" + fallback.Key + request.Path + request.QueryString);
                    return;
                }
            }

            if (AdminOnly && !session.User.IsAdmin)
            {
                context.Result = Denied(controller, "This page is for administrators only");
                return;
            }

            if (SkipModuleCheck)
            {
                // No permission checked means no write granted — pages of this kind change nothing anyway.
                module.UserCanEdit = false;
            }
            else
            {
                if (!users.CanUseModule(session, module.Current.Key, out var canEdit))
                {
                    context.Result = Denied(controller,
                        $"Account has no permission on module \"{module.Current.Name}\"");
                    return;
                }

                module.UserCanEdit = canEdit;
            }

            controller.ViewData["CurrentUser"] = session.User;
            controller.ViewData["Session"] = session;
            controller.ViewData["Options"] = options;
        }

        /// <summary>API requests get JSON; browser requests are sent to the login page.</summary>
        private static IActionResult Reject(ActionExecutingContext context, BaseController controller, ToolPlusOptions options)
        {
            if (controller.isAPI)
            {
                return new JsonResult(new ResponseModel(ToolConst.UserNotFound, ""));
            }

            context.HttpContext.Response.Cookies.Delete(options.CookieName);

            var back = context.HttpContext.Request.Path + context.HttpContext.Request.QueryString;
            return new RedirectResult("/login/index?returnUrl=" + Uri.EscapeDataString(back));
        }

        /// <summary>Signed in but not authorised — a different case from an expired session, so do not bounce to login.</summary>
        private static IActionResult Denied(BaseController controller, string message)
        {
            if (controller.isAPI)
            {
                return new JsonResult(new ResponseModel(ToolConst.PerrmissionDenied, message));
            }

            controller.TempData["DeniedMessage"] = message;
            return new RedirectResult("/login/denied");
        }
    }
}
