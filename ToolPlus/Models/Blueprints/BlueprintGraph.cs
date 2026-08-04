using System.Text.RegularExpressions;
using ToolPlus.Models.Explorer;

namespace ToolPlus.Models.Blueprints
{
    /// <summary>
    /// The contents of one blueprint, stored as raw JSON in Blueprint.GraphJson.
    ///
    /// The graph is a TREE: the root node takes its values from the parameters, each child takes
    /// them from its parent. Deliberately not translated into one big JOIN — see BlueprintRunner
    /// for why.
    /// </summary>
    public class BlueprintGraph
    {
        public List<GraphParam> Params { get; set; } = new();
        public List<GraphNode> Nodes { get; set; } = new();
        public List<GraphEdge> Edges { get; set; } = new();

        /// <summary>Canvas viewport state, so reopening a blueprint shows the same part of it.</summary>
        public double Zoom { get; set; } = 1;
        public double PanX { get; set; }
        public double PanY { get; set; }

        /// <summary>Position of the PARAMETER node on the canvas.</summary>
        public double ParamX { get; set; } = 40;
        public double ParamY { get; set; } = 40;

        /// <summary>{paramName} · {paramName:tu} · {paramName:den} inside a filter value (from / to).</summary>
        public static readonly Regex ParamToken = new(@"\{(\w+)(?::(tu|den))?\}", RegexOptions.Compiled);

        /// <summary>
        /// Parameters that are ACTUALLY used: either linked straight into a column, or referenced by
        /// a {token} in a filter value.
        ///
        /// Looking only at the links used to be the bug here: a parameter used solely as {fromDate}
        /// or {month:tu} was never asked for at run time -> the token expanded to nothing -> the
        /// date filter was silently dropped. The run page and the runner must share this method.
        /// </summary>
        public HashSet<string> UsedParamKeys()
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var e in Edges.Where(e => e.IsParam)) keys.Add(e.FromParam!);

            foreach (var f in Nodes.SelectMany(n => n.Filters))
            {
                if (string.IsNullOrEmpty(f.Value)) continue;
                foreach (Match m in ParamToken.Matches(f.Value)) keys.Add(m.Groups[1].Value);
            }

            return keys;
        }

        /// <summary>Parameters to ask for at run time, in declaration order.</summary>
        public List<GraphParam> UsedParams()
        {
            var keys = UsedParamKeys();
            return Params.Where(p => keys.Contains(p.Key)).ToList();
        }
    }

    public class GraphParam
    {
        /// <summary>Variable name used in the URL and at run time, e.g. "userId".</summary>
        public string Key { get; set; } = "";

        /// <summary>Label shown to the user, e.g. "Mã tài khoản".</summary>
        public string Label { get; set; } = "";

        /// <summary>text | guid | int | date | month</summary>
        public string Type { get; set; } = "text";

        public bool Required { get; set; } = true;

        public string? Default { get; set; }
    }

    public class GraphNode
    {
        public string Id { get; set; } = "";

        /// <summary>As schema.name; must match a real table.</summary>
        public string Table { get; set; } = "";

        /// <summary>Name shown in the UI instead of the table name, so end users can read it.</summary>
        public string Alias { get; set; } = "";

        public double X { get; set; }
        public double Y { get; set; }

        /// <summary>Ticked columns to show in the results. Empty = pick defaults at run time.</summary>
        public List<string> Columns { get; set; } = new();

        /// <summary>The node's own filters, reusing Explorer's operator set.</summary>
        public List<FilterItem> Filters { get; set; } = new();

        public int Limit { get; set; } = 50;

        public string? SortColumn { get; set; }
        public bool SortDesc { get; set; }

        /// <summary>Collapse the node on the canvas to reduce clutter.</summary>
        public bool Collapsed { get; set; }
    }

    /// <summary>
    /// One link. Two forms:
    ///   parameter -> column  (FromParam is set)
    ///   column    -> column  (FromNode + FromColumn are set)
    /// </summary>
    public class GraphEdge
    {
        public string Id { get; set; } = "";

        public string? FromParam { get; set; }

        public string? FromNode { get; set; }
        public string? FromColumn { get; set; }

        public string ToNode { get; set; } = "";
        public string ToColumn { get; set; } = "";

        public bool IsParam => !string.IsNullOrWhiteSpace(FromParam);
    }

    /// <summary>A suggested link, offered when a new table is added to the canvas.</summary>
    public class EdgeSuggestion
    {
        public string FromTable { get; set; } = "";
        public string FromColumn { get; set; } = "";
        public string ToTable { get; set; } = "";
        public string ToColumn { get; set; } = "";

        /// <summary>fk = real foreign key · hint = you linked it before · ten = inferred from the name</summary>
        public string Reason { get; set; } = "";

        public int Score { get; set; }
    }
}
