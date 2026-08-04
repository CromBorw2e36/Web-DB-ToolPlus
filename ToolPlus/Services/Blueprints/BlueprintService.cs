using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using ToolPlus.Common;
using ToolPlus.Models.Blueprints;
using ToolPlus.Models.Explorer;
using ToolPlus.Models.ToolEntity;
using ToolPlus.Services.Interface;

namespace ToolPlus.Services.Blueprints
{
    public class BlueprintService : IBlueprintService
    {
        private readonly ToolDbContext _db;
        private readonly IMetadataService _meta;
        private readonly IAuditService _audit;
        private readonly IModuleContext _module;

        public BlueprintService(ToolDbContext db, IMetadataService meta, IAuditService audit,
            IModuleContext module)
        {
            _db = db;
            _meta = meta;
            _audit = audit;
            _module = module;
        }

        /// <summary>
        /// Blueprints are always filtered by the module being viewed. Without that filter, opening
        /// another database's blueprint errors on a missing table — or worse, if both databases have
        /// that table it succeeds and edits the wrong one.
        /// </summary>
        private string Mod => _module.Current.Key;

        // ------------------------------------------------------------------- CRUD

        public Task<List<Blueprint>> ListAsync()
            => _db.Blueprint.AsNoTracking().Where(x => x.ModuleKey == Mod)
                .OrderByDescending(x => x.Pinned).ThenBy(x => x.Name)
                .ToListAsync();

        public Task<List<Blueprint>> ListPinnedAsync()
            => _db.Blueprint.AsNoTracking().Where(x => x.ModuleKey == Mod && x.Pinned)
                .OrderBy(x => x.Name).ToListAsync();

        public Task<Blueprint?> GetAsync(Guid id)
            => _db.Blueprint.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.ModuleKey == Mod);

        public async Task<BlueprintGraph> GetGraphAsync(Guid id)
        {
            var row = await GetAsync(id);
            if (row == null) return new BlueprintGraph();

            try
            {
                return JsonConvert.DeserializeObject<BlueprintGraph>(row.GraphJson) ?? new BlueprintGraph();
            }
            catch
            {
                // A corrupt GraphJson yields an empty blueprint instead of breaking the page.
                return new BlueprintGraph();
            }
        }

        public async Task<Blueprint> SaveAsync(Blueprint input, BlueprintGraph graph, string username)
        {
            if (input.Name.IsEmpty()) throw new InvalidOperationException("Blueprint name is required");

            var json = JsonConvert.SerializeObject(graph);
            var row = input.Id == Guid.Empty
                ? null
                : await _db.Blueprint.FirstOrDefaultAsync(x => x.Id == input.Id && x.ModuleKey == Mod);

            if (row == null)
            {
                row = new Blueprint
                {
                    Id = input.Id == Guid.Empty ? Guid.NewGuid() : input.Id,
                    ModuleKey = Mod,
                    Owner = username,
                    CreatedBy = username,
                    CreatedAt = DateTime.Now
                };
                _db.Blueprint.Add(row);
            }
            else
            {
                row.ModifiedBy = username;
                row.ModifiedAt = DateTime.Now;
            }

            row.Name = input.Name.Trim();
            row.Description = input.Description?.Trim() ?? "";
            row.Icon = input.Icon.IsEmpty() ? "i-panel" : input.Icon;
            row.Pinned = input.Pinned;
            row.IsShared = input.IsShared;
            row.GraphJson = json;

            await _db.SaveChangesAsync();

            // Learn the column pairs the user linked, so they can be suggested next time.
            foreach (var edge in graph.Edges.Where(e => !e.IsParam))
            {
                var from = graph.Nodes.FirstOrDefault(n => n.Id == edge.FromNode);
                var to = graph.Nodes.FirstOrDefault(n => n.Id == edge.ToNode);
                if (from == null || to == null || edge.FromColumn.IsEmpty() || edge.ToColumn.IsEmpty()) continue;

                await RememberRelationAsync(from.Table, edge.FromColumn!, to.Table, edge.ToColumn);
            }

            await _audit.WriteAsync("blueprint-save", username, null, row.Name + " (" + graph.Nodes.Count + " tables)");
            return row;
        }

        public async Task DeleteAsync(Guid id, string username)
        {
            var row = await _db.Blueprint.FirstOrDefaultAsync(x => x.Id == id && x.ModuleKey == Mod)
                      ?? throw new InvalidOperationException("Blueprint not found");

            _db.Blueprint.Remove(row);
            await _db.SaveChangesAsync();
            await _audit.WriteAsync("blueprint-delete", username, null, row.Name);
        }

        public async Task<Blueprint> DuplicateAsync(Guid id, string username)
        {
            var src = await GetAsync(id) ?? throw new InvalidOperationException("Blueprint not found");

            var copy = new Blueprint
            {
                Id = Guid.NewGuid(),
                ModuleKey = Mod,
                Name = src.Name + " (copy)",
                Description = src.Description,
                Icon = src.Icon,
                GraphJson = src.GraphJson,
                Pinned = false,
                IsShared = src.IsShared,
                Owner = username,
                CreatedBy = username,
                CreatedAt = DateTime.Now
            };

            _db.Blueprint.Add(copy);
            await _db.SaveChangesAsync();
            return copy;
        }

        public async Task SetPinnedAsync(Guid id, bool pinned)
        {
            var row = await _db.Blueprint.FirstOrDefaultAsync(x => x.Id == id && x.ModuleKey == Mod)
                      ?? throw new InvalidOperationException("Blueprint not found");

            row.Pinned = pinned;
            await _db.SaveChangesAsync();
        }

        // ------------------------------------------------------- link suggestions

        /// <summary>
        /// Three sources, ranked by confidence:
        ///   100 hint — you linked this column pair before
        ///    90 fk   — a real foreign key in the database
        ///  50-70 ten — inferred from the column name (see MatchByName)
        /// Databases in the wild often declare almost no foreign keys, which makes the name-inference
        /// branch the one that actually runs most of the time.
        /// </summary>
        public async Task<List<EdgeSuggestion>> SuggestAsync(string table, List<string> others)
        {
            var self = await _meta.ResolveTableAsync(table);
            if (self == null) return new List<EdgeSuggestion>();

            var targets = new List<TableInfo>();
            foreach (var name in others.Where(o => !o.IsEmpty()))
            {
                var t = await _meta.ResolveTableAsync(name);
                if (t != null && !t.Full.Equals(self.Full, StringComparison.OrdinalIgnoreCase)) targets.Add(t);
            }
            if (targets.Count == 0) return new List<EdgeSuggestion>();

            var result = new List<EdgeSuggestion>();
            var selfCols = await _meta.GetColumnsAsync(self);

            // 1. Real foreign keys.
            var fks = await _meta.GetForeignKeysAsync();
            foreach (var fk in fks)
            {
                var childIsSelf = fk.ChildTable.Equals(self.Full, StringComparison.OrdinalIgnoreCase);
                var parentIsSelf = fk.ParentTable.Equals(self.Full, StringComparison.OrdinalIgnoreCase);

                if (childIsSelf && targets.Any(t => t.Full.Equals(fk.ParentTable, StringComparison.OrdinalIgnoreCase)))
                {
                    Add(result, fk.ChildTable, fk.ChildColumn, fk.ParentTable, fk.ParentColumn, "fk", 90);
                }
                else if (parentIsSelf && targets.Any(t => t.Full.Equals(fk.ChildTable, StringComparison.OrdinalIgnoreCase)))
                {
                    Add(result, fk.ChildTable, fk.ChildColumn, fk.ParentTable, fk.ParentColumn, "fk", 90);
                }
            }

            // 2. Relationships you linked before — this module's only.
            var hints = await _db.RelationHint.AsNoTracking().Where(x => x.ModuleKey == Mod).ToListAsync();
            foreach (var h in hints)
            {
                var involvesSelf = h.FromTable.Equals(self.Full, StringComparison.OrdinalIgnoreCase)
                                   || h.ToTable.Equals(self.Full, StringComparison.OrdinalIgnoreCase);
                var involvesTarget = targets.Any(t => t.Full.Equals(h.FromTable, StringComparison.OrdinalIgnoreCase)
                                                      || t.Full.Equals(h.ToTable, StringComparison.OrdinalIgnoreCase));
                if (involvesSelf && involvesTarget)
                {
                    Add(result, h.FromTable, h.FromColumn, h.ToTable, h.ToColumn, "hint", 100);
                }
            }

            // 3. Name inference, in both directions.
            foreach (var target in targets)
            {
                var targetCols = await _meta.GetColumnsAsync(target);
                MatchByName(result, self, selfCols, target, targetCols);
                MatchByName(result, target, targetCols, self, selfCols);
            }

            return result.OrderByDescending(x => x.Score).ToList();
        }

        /// <summary>
        /// Assumes the "Ma&lt;Entity&gt;" naming convention ("ma" being Vietnamese for code/id): a child
        /// column named Ma&lt;Entity&gt; references the primary key of the parent table whose name ends in
        /// that entity, e.g. MaCustomer -&gt; sales_Customer.Ma.
        ///
        /// This is a convention-based guess, not a rule — a schema naming its keys differently simply
        /// gets no suggestions from this branch and the user links the columns by hand once, after
        /// which the RelationHint branch takes over.
        /// </summary>
        private static void MatchByName(List<EdgeSuggestion> result,
            TableInfo child, List<ColumnInfo> childCols,
            TableInfo parent, List<ColumnInfo> parentCols)
        {
            var parentPk = parentCols.FirstOrDefault(c => c.IsPrimaryKey);
            if (parentPk == null) return;

            // Strip a schema-ish prefix: sales_Customer -> "Customer"
            var bare = parent.Name;
            var underscore = bare.IndexOf('_');
            if (underscore > 0 && underscore < bare.Length - 1) bare = bare.Substring(underscore + 1);

            foreach (var col in childCols)
            {
                if (!col.Name.StartsWith("Ma", StringComparison.Ordinal) || col.Name.Length <= 2) continue;

                var suffix = col.Name.Substring(2);
                if (!suffix.Equals(bare, StringComparison.OrdinalIgnoreCase)) continue;

                // Types must match, otherwise running the link fails on a cast.
                if (!col.DataType.Equals(parentPk.DataType, StringComparison.OrdinalIgnoreCase)) continue;

                Add(result, child.Full, col.Name, parent.Full, parentPk.Name, "ten", 70);
            }

            // Same column name in both tables, one side being a key: MaCustomer <-> MaCustomer.
            foreach (var col in childCols)
            {
                var twin = parentCols.FirstOrDefault(c =>
                    c.Name.Equals(col.Name, StringComparison.OrdinalIgnoreCase)
                    && c.DataType.Equals(col.DataType, StringComparison.OrdinalIgnoreCase)
                    && col.Name.StartsWith("Ma", StringComparison.Ordinal)
                    && col.Name.Length > 2);

                if (twin != null) Add(result, child.Full, col.Name, parent.Full, twin.Name, "ten", 50);
            }
        }

        private static void Add(List<EdgeSuggestion> list, string fromTable, string fromColumn,
            string toTable, string toColumn, string reason, int score)
        {
            var existed = list.FirstOrDefault(x =>
                x.FromTable.Equals(fromTable, StringComparison.OrdinalIgnoreCase)
                && x.FromColumn.Equals(fromColumn, StringComparison.OrdinalIgnoreCase)
                && x.ToTable.Equals(toTable, StringComparison.OrdinalIgnoreCase)
                && x.ToColumn.Equals(toColumn, StringComparison.OrdinalIgnoreCase));

            if (existed != null)
            {
                if (score > existed.Score) { existed.Score = score; existed.Reason = reason; }
                return;
            }

            list.Add(new EdgeSuggestion
            {
                FromTable = fromTable, FromColumn = fromColumn,
                ToTable = toTable, ToColumn = toColumn,
                Reason = reason, Score = score
            });
        }

        public async Task RememberRelationAsync(string fromTable, string fromColumn, string toTable, string toColumn)
        {
            var row = await _db.RelationHint.FirstOrDefaultAsync(x =>
                x.ModuleKey == Mod
                && x.FromTable == fromTable && x.FromColumn == fromColumn
                && x.ToTable == toTable && x.ToColumn == toColumn);

            if (row == null)
            {
                _db.RelationHint.Add(new RelationHint
                {
                    ModuleKey = Mod,
                    FromTable = fromTable, FromColumn = fromColumn,
                    ToTable = toTable, ToColumn = toColumn,
                    UseCount = 1, LastUsed = DateTime.Now
                });
            }
            else
            {
                row.UseCount++;
                row.LastUsed = DateTime.Now;
            }

            await _db.SaveChangesAsync();
        }
    }
}
