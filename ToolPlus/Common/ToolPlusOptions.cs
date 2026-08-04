namespace ToolPlus.Common
{
    /// <summary>Bound from the "ToolPlus" section of appsettings.json.</summary>
    public class ToolPlusOptions
    {
        public const string SectionName = "ToolPlus";

        /// <summary>
        /// Per-module extras (display name, icon, whether editing is allowed). The key is the
        /// connection string name in ConnectionStrings. A module with no entry still works, and
        /// falls back to the connection string name as its display name.
        /// </summary>
        public Dictionary<string, ModuleMeta> Modules { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Module opened when the URL does not name one. Empty = first connection string.</summary>
        public string DefaultModule { get; set; } = "";

        /// <summary>
        /// Optional link to the application that owns the data, shown as a convenience on the
        /// login page. Left empty on purpose: ToolPlus does not need to know about that app.
        /// </summary>
        public HostAppLink HostApp { get; set; } = new();

        /// <summary>
        /// Name of ToolPlus's session cookie. Keep it different from the cookie of any other app
        /// on the same host: cookies are not scoped by port, so two apps sharing a cookie name
        /// on localhost will keep signing each other out.
        /// </summary>
        public string CookieName { get; set; } = "tp_token";

        /// <summary>How many days a session lives before requiring a fresh login.</summary>
        public int SessionDays { get; set; } = 30;

        /// <summary>
        /// Administrator created automatically while the ToolUser table is empty — otherwise a
        /// fresh install would have no way in. Clear this password once you can sign in.
        /// </summary>
        public BootstrapAccount Bootstrap { get; set; } = new();

        /// <summary>Accent-insensitive collation for the search box. Empty = accents are significant.</summary>
        public string SearchCollation { get; set; } = "Vietnamese_CI_AI";

        /// <summary>Cap on rows returned per request, so nobody drags a whole table into the browser.</summary>
        public int MaxRows { get; set; } = 500;

        public int QueryTimeoutSeconds { get; set; } = 60;

        /// <summary>Whether data can be edited at all. Off = the whole instance is read-only.</summary>
        public bool AllowEdit { get; set; } = true;
    }

    /// <summary>Convenience link back to the application that owns the data being browsed.</summary>
    public class HostAppLink
    {
        /// <summary>Label for the link. Falls back to the URL host when empty.</summary>
        public string Name { get; set; } = "";

        public string Url { get; set; } = "";

        public bool IsValid => !Url.IsEmpty();
    }

    public class BootstrapAccount
    {
        public string Username { get; set; } = "";

        public string Password { get; set; } = "";

        public string FullName { get; set; } = "";

        public bool IsValid => !Username.IsEmpty() && !Password.IsEmpty();
    }
}
