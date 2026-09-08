namespace ToolPlus.Common
{
    public class MenuEntry
    {
        public string Text { get; init; } = "";
        public string Url { get; init; } = "";

        /// <summary>Symbol id in Views/Shared/_Icons.cshtml (without the leading #).</summary>
        public string Icon { get; init; } = "i-table";

        /// <summary>false = planned only; shown dimmed in the menu and not clickable.</summary>
        public bool Ready { get; init; } = true;

        /// <summary>
        /// true = highlight only on this exact path. Needed for a parent entry such as /blueprint,
        /// which would otherwise light up while /blueprint/ve is open.
        /// </summary>
        public bool Exact { get; init; }

        /// <summary>true = admin-only. Must match the page's own [TokenCheck(AdminOnly = true)].</summary>
        public bool AdminOnly { get; init; }

        public string Note { get; init; } = "";
    }

    public class MenuGroup
    {
        public string Text { get; init; } = "";
        public List<MenuEntry> Items { get; init; } = new();
    }

    /// <summary>
    /// The whole menu declared in one place; the sidebar and the home page both read from here.
    ///
    /// Lookup / tracing / data-fixing pages are deliberately NOT declared here — users draw those
    /// themselves as blueprints instead of waiting for code. This is only for what a blueprint
    /// cannot express: actions with logic of their own.
    /// </summary>
    public static class MenuDefinition
    {
        public static readonly List<MenuGroup> Groups = new()
        {
            new MenuGroup
            {
                Text = "Bản vẽ",
                Items =
                {
                    new MenuEntry
                    {
                        Text = "Danh sách bản vẽ", Url = "/blueprint", Icon = "i-panel", Exact = true,
                        Note = "Gắn bảng vào canvas, nối trường với trường, lưu lại để lần sau chỉ nhập tham số và chạy."
                    },
                    new MenuEntry
                    {
                        Text = "Vẽ bản mới", Url = "/blueprint/ve", Icon = "i-plus",
                        Note = "Bắt đầu một bản vẽ trống."
                    }
                }
            },

            // Buttons that have to be hand-coded because they carry real logic rather than just
            // editing a cell: regenerating a document number from a sequence, recomputing a summary
            // table after its source rows were fixed, and so on.
            new MenuGroup
            {
                Text = "Hành động",
                Items =
                {
                    new MenuEntry
                    {
                        Text = "Đồng bộ trường từ DB khác", Url = "/sync/index", Icon = "i-refresh",
                        Note = "So một cột với database khác rồi điền lại những dòng đang trống hoặc sai."
                    }
                }
            },

            new MenuGroup
            {
                Text = "Công cụ",
                Items =
                {
                    new MenuEntry
                    {
                        Text = "Tra cứu mọi bảng", Url = "/explorer/index", Icon = "i-database",
                        Note = "Truy vết nhanh trên bảng bất kỳ, chạy câu SELECT tự do, xem lịch sử thao tác."
                    },
                    new MenuEntry
                    {
                        Text = "Người dùng", Url = "/user/index", Icon = "i-users", AdminOnly = true,
                        Note = "Tài khoản dùng ToolPlus và quyền của họ trên từng module."
                    },
                    new MenuEntry
                    {
                        Text = "Thông tin phiên", Url = "/home/thongtinphien", Icon = "i-key",
                        Note = "Tài khoản đang dùng, quyền theo module và cấu hình ToolPlus."
                    }
                }
            }
        };

        /// <summary>
        /// The menu this user may see. Empty groups are dropped so no bare heading is rendered —
        /// which really happens when a non-admin filters out every entry of a group.
        /// </summary>
        public static IEnumerable<MenuGroup> VisibleFor(bool isAdmin)
            => Groups
                .Select(g => new MenuGroup
                {
                    Text = g.Text,
                    Items = g.Items.Where(i => isAdmin || !i.AdminOnly).ToList()
                })
                .Where(g => g.Items.Count > 0);

        /// <summary>
        /// Whether this entry is the page being viewed. Matching is prefix-based, so
        /// /blueprint/ve/123 still highlights the "new blueprint" entry.
        ///
        /// <paramref name="path"/> is the real path (possibly carrying a module segment) while
        /// <see cref="MenuEntry.Url"/> is always bare — so the module segment has to be stripped
        /// before comparing, otherwise nothing highlights at all on /billing/explorer.
        /// </summary>
        public static bool IsActive(MenuEntry entry, string path, string moduleKey)
        {
            var prefix = "/" + moduleKey;
            if (path.Equals(prefix, StringComparison.OrdinalIgnoreCase))
            {
                path = "/";
            }
            else if (path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
            {
                path = path.Substring(prefix.Length);
            }

            var url = entry.Url.TrimEnd('/');

            if (path.Equals(url, StringComparison.OrdinalIgnoreCase)
                || path.Equals(url + "/", StringComparison.OrdinalIgnoreCase)
                || path.Equals(url + "/index", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return !entry.Exact && path.StartsWith(url + "/", StringComparison.OrdinalIgnoreCase);
        }
    }
}
