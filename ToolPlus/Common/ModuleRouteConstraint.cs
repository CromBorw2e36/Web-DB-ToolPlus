namespace ToolPlus.Common
{
    /// <summary>
    /// Lets the "{module}/..." route match only when the first segment really is a module.
    ///
    /// Without this constraint, "/explorer/index" would be read as module = "explorer",
    /// controller = "index", breaking every URL that omits the module.
    /// </summary>
    public class ModuleRouteConstraint : IRouteConstraint
    {
        private readonly ModuleRegistry _registry;

        public ModuleRouteConstraint(ModuleRegistry registry)
        {
            _registry = registry;
        }

        public bool Match(HttpContext? httpContext, IRouter? route, string routeKey,
            RouteValueDictionary values, RouteDirection routeDirection)
            => values.TryGetValue(routeKey, out var raw)
               && _registry.Find(raw?.ToString()) != null;
    }
}
