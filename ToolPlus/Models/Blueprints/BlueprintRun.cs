using ToolPlus.Models.Explorer;

namespace ToolPlus.Models.Blueprints
{
    public class BlueprintRunRequest
    {
        public Guid Id { get; set; }

        /// <summary>What the user typed for each parameter, keyed by Key.</summary>
        public Dictionary<string, string?> Params { get; set; } = new();
    }

    public class BlueprintRunResult
    {
        public List<NodeResult> Nodes { get; set; } = new();

        /// <summary>Worth knowing but not an error: a node with no inbound link, parent keys that got trimmed…</summary>
        public List<string> Warnings { get; set; } = new();

        public long ElapsedMs { get; set; }
    }

    public class NodeResult
    {
        public string NodeId { get; set; } = "";
        public string Table { get; set; } = "";
        public string Alias { get; set; } = "";
        public bool IsView { get; set; }

        /// <summary>Has a primary key, is not a view, and writes are configured -> show the edit button.</summary>
        public bool CanEdit { get; set; }

        /// <summary>Columns actually SELECTed (always including the primary key, so rows stay editable).</summary>
        public List<ColumnInfo> Columns { get; set; } = new();

        /// <summary>Columns the user ticked in the blueprint — the rest are for internal use only.</summary>
        public List<string> Shown { get; set; } = new();

        public List<Dictionary<string, object?>> Rows { get; set; } = new();

        /// <summary>Parent node in the tree; null when this is the root.</summary>
        public string? ParentNodeId { get; set; }

        /// <summary>The parent node's column that supplies the values.</summary>
        public string? LinkFromColumn { get; set; }

        /// <summary>This node's column that receives them and is filtered by them.</summary>
        public string? LinkToColumn { get; set; }

        public bool Truncated { get; set; }

        public string Sql { get; set; } = "";

        public long ElapsedMs { get; set; }

        /// <summary>This node's own error, if any; the other nodes still run normally.</summary>
        public string? Error { get; set; }
    }
}
