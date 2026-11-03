using System.ComponentModel;
using EvoLink.Connectors.Common;
using Ortolan.Core.Conditions;
using Ortolan.Core.Models;
using Ortolan.Core.Modules;
using Ortolan.Core.Pipeline;
using Ortolan.Core.Serialization;

namespace EvoLink.Connectors.Transformers
{
    /// <summary>
    /// Execution of the sub-transformers of the flow transformers. A sub-transformer is any Ortolan <see cref="ITransformer"/>
    /// (polymorphic through "$type" in the JSON configuration); it is called directly through
    /// <see cref="ITransformer.TransformAsync"/>, so its exceptions fail the entity in the parent module (never swallowed)
    /// and returning null removes the entity from the flow.
    /// </summary>
    internal static class SubTransformers
    {
        public static async Task<Entity?> RunAsync(IEnumerable<ITransformer> transformers, Entity entity, JobContext context)
        {
            Entity? current = entity;

            foreach (var transformer in transformers)
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                current = await transformer.TransformAsync(current, context);

                if (current == null)
                    return null;
            }

            return current;
        }
    }

    /// <summary>
    /// Port of the EvoBus <c>CompositeTransformer</c>: plays a list of transformers one after the other on each entity.
    /// Differences: an error of a sub-transformer fails the entity (EvoBus logged it and went on with the next one), and a
    /// sub-transformer returning null (filter) removes the entity.
    /// </summary>
    [OrtolanType("composite")]
    [Description("Applique une liste de transformers l'un après l'autre sur chaque entité.")]
    public class CompositeTransformer : TransformerModule
    {
        [Description("Transformers appliqués dans l'ordre (chacun avec son \"$type\").")]
        public List<ITransformer> Transformers { get; set; } = [];

        protected override Task BeforeEntitiesAsync(List<Entity> input, JobContext context)
        {
            if (Transformers.Count == 0)
                context.LogWarning($"[{DisplayName}] Aucun sous-transformer n'est configuré : les entités sont transmises sans modification.");

            return Task.CompletedTask;
        }

        public override Task<Entity?> TransformAsync(Entity entity, JobContext context)
        {
            if (Transformers.Any(t => t == null))
                throw ConnectorGuard.Invalid(DisplayName, "La liste 'transformers' contient un élément vide.");

            return SubTransformers.RunAsync(Transformers, entity, context);
        }
    }

    /// <summary>
    /// Port of the EvoBus <c>ConditionalTransformer</c>: applies a transformer only to the entities matching a condition.
    /// Differences: an error of the condition or of the transformer fails the entity instead of being logged.
    /// </summary>
    [OrtolanType("conditional")]
    [Description("Applique un transformer uniquement aux entités qui respectent la condition ; les autres sont transmises sans modification.")]
    public class ConditionalTransformer : TransformerModule
    {
        [Description("Condition évaluée sur chaque entité.")]
        public ICondition? Condition { get; set; }

        [Description("Transformer appliqué lorsque la condition est vraie.")]
        public ITransformer? Transformer { get; set; }

        public override async Task<Entity?> TransformAsync(Entity entity, JobContext context)
        {
            ConnectorGuard.Required(DisplayName, ("condition", Condition), ("transformer", Transformer));

            if (Condition!.Match(entity))
                return await Transformer!.TransformAsync(entity, context);

            context.LogDebug($"[{DisplayName}] Condition non remplie, entité transmise sans modification : {entity.ToFlatString(200)}");
            return entity;
        }
    }

    /// <summary>
    /// Port of the EvoBus <c>IfThenElseTransformer</c>: applies one transformer or the other depending on a condition.
    /// Both branches are optional (an empty branch leaves the entity untouched), at least one is required.
    /// </summary>
    [OrtolanType("if-then-else")]
    [Description("Applique le transformer 'then' aux entités qui respectent la condition et le transformer 'else' aux autres.")]
    public class IfThenElseTransformer : TransformerModule
    {
        [Description("Condition évaluée sur chaque entité.")]
        public ICondition? Condition { get; set; }

        [Description("Transformer appliqué lorsque la condition est vraie (vide = entité inchangée).")]
        public ITransformer? Then { get; set; }

        [Description("Transformer appliqué lorsque la condition est fausse (vide = entité inchangée).")]
        public ITransformer? Else { get; set; }

        public override async Task<Entity?> TransformAsync(Entity entity, JobContext context)
        {
            ConnectorGuard.Required(DisplayName, ("condition", Condition));

            if (Then == null && Else == null)
                throw ConnectorGuard.Invalid(DisplayName, "Au moins une des branches 'then' ou 'else' doit être configurée.");

            var branch = Condition!.Match(entity) ? Then : Else;

            return branch == null ? entity : await branch.TransformAsync(entity, context);
        }
    }
}
