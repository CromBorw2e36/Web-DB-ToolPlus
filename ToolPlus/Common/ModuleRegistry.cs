using System.Text.RegularExpressions;

namespace ToolPlus.Common
{
    /// <summary>
    /// One module = one connection string in ConnectionStrings. Adding a module takes no code:
    /// add the connection string and restart, then optionally tune name/icon/write permission
    /// under ToolPlus:Modules:&lt;key&gt;.
    /// </summary>
    public class ModuleDefinition
    {
        /// <summary>Lowercased, used as the first URL segment. E.g. "billing" -&gt; /billing/explorer.</summary>
        public string Key { get; init; } = "";

        /// <summary>Original name in ConnectionStrings, kept for display on the session info page.</summary>
        public string ConnectionKey { get; init; } = "";

        public string ConnectionString { get; init; } = "";

        public string Name { get; init; } = "";

        public string Icon { get; init; } = "i-database";

        /// <summary>
        /// Whether this module's data may be edited. ANDed with the global ToolPlus:AllowEdit —
        /// switching it off anywhere makes the module read-only.
        /// </summary>
        public bool AllowEdit { get; init; } = true;
    }

    /// <summary>Optional extras for one module, bound from ToolPlus:Modules:&lt;key&gt;.</summary>
    public class ModuleMeta
    {
        public string Name { get; set; } = "";
        public string Icon { get; set; } = "";
        public bool AllowEdit { get; set; } = true;

        /// <summary>false = the connection string exists but is not turned into a module.</summary>
        public bool Enabled { get; set; } = true;
    }

    /// <summary>
    /// The module list, built once at startup. Bad configuration fails right here with a clear
    /// message instead of surfacing as an ADO.NET error the first time a user clicks something.
    /// </summary>
    public class ModuleRegistry
    {
        /// <summary>The key has to be clean because it ends up in the URL.</summary>
        private static readonly Regex SafeKey = new("^[a-z0-9][a-z0-9-]*$", RegexOptions.Compiled);

        /// <summary>
        /// Controller names cannot be used as module keys: "/explorer/index" would be ambiguous
        /// between "module explorer" and "controller Explorer". Add new controllers here.
        /// </summary>
        private static readonly string[] Reserved = { "home", "login", "explorer", "blueprint", "user" };

        public IReadOnlyList<ModuleDefinition> All { get; }

        public ModuleDefinition Default { get; }

        public ModuleRegistry(IConfiguration config, ToolPlusOptions options)
        {
            var raw = config.GetSection("ConnectionStrings").GetChildren().ToList();
            if (raw.Count == 0)
            {
                throw new InvalidOperationException(
                    "No ConnectionStrings configured. Copy appsettings.local.example.json to "
                    + "appsettings.local.json and fill in at least one connection string.");
            }

            var list = new List<ModuleDefinition>();

            foreach (var entry in raw)
            {
                var key = entry.Key.ToLowerInvariant();
                var meta = options.Modules.GetValueOrDefault(entry.Key)
                           ?? options.Modules.GetValueOrDefault(key)
                           ?? new ModuleMeta();

                if (!meta.Enabled) continue;

                if (entry.Value.IsEmpty())
                {
                    throw new InvalidOperationException(
                        $"ConnectionStrings:{entry.Key} is empty. Fill it in, or set "
                        + $"ToolPlus:Modules:{key}:Enabled = false to skip it.");
                }

                if (!SafeKey.IsMatch(key))
                {
                    throw new InvalidOperationException(
                        $"Connection string name \"{entry.Key}\" cannot be used as a URL segment. "
                        + "Only lowercase letters, digits and hyphens are allowed.");
                }

                if (list.Any(m => m.Key == key))
                {
                    throw new InvalidOperationException(
                        $"Two connection strings resolve to module \"{key}\" — rename one of them.");
                }

                if (Reserved.Contains(key))
                {
                    throw new InvalidOperationException(
                        $"Connection string name \"{entry.Key}\" collides with one of the tool's own "
                        + $"pages (/{key}/...), so it cannot be a module — rename the connection string.");
                }

                list.Add(new ModuleDefinition
                {
                    Key = key,
                    ConnectionKey = entry.Key,
                    ConnectionString = entry.Value!,
                    Name = meta.Name.IsEmpty() ? entry.Key : meta.Name,
                    Icon = meta.Icon.IsEmpty() ? "i-database" : meta.Icon,
                    AllowEdit = meta.AllowEdit
                });
            }

            if (list.Count == 0)
            {
                throw new InvalidOperationException("Every module is Enabled = false, so there is nothing to run.");
            }

            All = list;

            var wanted = options.DefaultModule.IsEmpty() ? null : options.DefaultModule!.ToLowerInvariant();
            Default = (wanted == null ? null : list.FirstOrDefault(m => m.Key == wanted)) ?? list[0];

            if (wanted != null && Default.Key != wanted)
            {
                throw new InvalidOperationException(
                    $"ToolPlus:DefaultModule = \"{options.DefaultModule}\" matches no connection string.");
            }
        }

        public ModuleDefinition? Find(string? key)
            => key.IsEmpty() ? null : All.FirstOrDefault(m => m.Key == key!.ToLowerInvariant());

        /// <summary>No match falls back to the default module — used by routes with no module segment.</summary>
        public ModuleDefinition Resolve(string? key) => Find(key) ?? Default;
    }
}
