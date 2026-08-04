using ToolPlus.Models.Blueprints;
using ToolPlus.Models.ToolEntity;

namespace ToolPlus.Services.Interface
{
    public interface IBlueprintService
    {
        Task<List<Blueprint>> ListAsync();

        /// <summary>Pinned blueprints, used to build the sidebar.</summary>
        Task<List<Blueprint>> ListPinnedAsync();

        Task<Blueprint?> GetAsync(Guid id);

        Task<BlueprintGraph> GetGraphAsync(Guid id);

        /// <summary>Creates when Id is empty, otherwise updates. Returns the saved record.</summary>
        Task<Blueprint> SaveAsync(Blueprint input, BlueprintGraph graph, string username);

        Task DeleteAsync(Guid id, string username);

        Task<Blueprint> DuplicateAsync(Guid id, string username);

        Task SetPinnedAsync(Guid id, bool pinned);

        /// <summary>Suggests how to link <paramref name="table"/> to the tables already on the canvas.</summary>
        Task<List<EdgeSuggestion>> SuggestAsync(string table, List<string> others);

        /// <summary>Records a column pair a user linked by hand, so it can be suggested next time.</summary>
        Task RememberRelationAsync(string fromTable, string fromColumn, string toTable, string toColumn);
    }
}
