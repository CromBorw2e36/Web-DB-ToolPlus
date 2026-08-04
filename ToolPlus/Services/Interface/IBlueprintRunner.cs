using ToolPlus.Models.Blueprints;

namespace ToolPlus.Services.Interface
{
    public interface IBlueprintRunner
    {
        /// <summary>
        /// Runs a blueprint level by level: the root node takes its values from the parameters, each
        /// child filters by the values its parent produced.
        /// </summary>
        Task<BlueprintRunResult> RunAsync(BlueprintGraph graph, Dictionary<string, string?> values, bool allowEdit);
    }
}
