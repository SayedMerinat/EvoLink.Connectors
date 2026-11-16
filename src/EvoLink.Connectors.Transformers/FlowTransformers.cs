using System.ComponentModel;
using EvoLink.Connectors.Common;
using Ortolan.Core.Conditions;
using Ortolan.Core.Exceptions;
using Ortolan.Core.Models;
using Ortolan.Core.Modules;
using Ortolan.Core.Pipeline;
using Ortolan.Core.Serialization;

namespace EvoLink.Connectors.Transformers
{
    /// <summary>
    /// Execution of the sub-transformers of the flow transformers. A sub-transformer is any Ortolan <see cref="ITransformer"/>
    /// (polymorphic through "$type" in the JSON configuration).
    /// <list type="bullet">
    /// <item>In a job (<see cref="IModule.ActAsync"/>), each sub-transformer is run as a module on the entities it applies to,
    /// with a nested context: it gets its whole life cycle (connection opened before the entities and closed after them,
    /// cache shared by the entities, its own per-entity error handling and parallelism). Modules preparing a connection
    /// (odoo-id-lookup, sql-lookup...) only work this way.</item>
    /// <item>Called entity by entity (<see cref="ITransformer.TransformAsync"/>, a flow transformer inside a component that
    /// doesn't run it as a module), its exceptions fail the entity in the parent module and returning null removes the entity.</item>
    /// </list>
    /// </summary>
    internal static class SubTransformers
    {
        /// <summary>Runs a sub-transformer as a module on the given entities (nothing is called for an empty list).</summary>
        public static async Task<List<Entity>> ActAsync(ITransformer transformer, List<Entity> entities, JobContext nested)
            => entities.Count == 0 ? entities : await transformer.ActAsync(entities, nested);

        /// <summary>
        /// Puts the results of the entities given to a sub-transformer back in the flow, in place of them: the order of the
        /// flow is kept. A result is the given entity itself (modified), or a new entity replacing it; a given entity
        /// without result was removed by the sub-transformer.
        /// </summary>
        public static List<Entity> Merge(List<Entity> input, List<Entity> given, List<Entity> results)
        {
            var givenSet = new HashSet<Entity>(given, ReferenceEqualityComparer.Instance);
            var replacements = new Dictionary<Entity, Entity>(ReferenceEqualityComparer.Instance);
            var next = 0;

            // The results keep the order of the given entities, without the removed ones.
            foreach (var entity in given)
            {
                if (next >= results.Count)
                    break;

                if (ReferenceEquals(results[next], entity) || !givenSet.Contains(results[next]))
                    replacements[entity] = results[next++];
            }

            var output = new List<Entity>(input.Count);
            Entity? lastGiven = null;

            foreach (var entity in input)
            {
                if (!givenSet.Contains(entity))
                {
                    output.Add(entity);
                    continue;
                }

                lastGiven = entity;
                if (replacements.TryGetValue(entity, out var result))
                    output.Add(result);
            }

            // Extra entities created by the sub-transformer (more results than given entities) follow the last one.
            if (next < results.Count)
            {
                var index = lastGiven != null && replacements.TryGetValue(lastGiven, out var last) ? output.IndexOf(last) + 1 : output.Count;
                output.InsertRange(index <= 0 ? output.Count : index, results.Skip(next));
            }

            return output;
        }

        /// <summary>
        /// Evaluates the condition of a flow transformer on an entity. An error of the condition fails the entity (the job
        /// stops instead when <paramref name="continueOnError"/> is false); entities already in failure never match.
        /// </summary>
        public static bool Matches(ICondition condition, Entity entity, JobContext context, string displayName, bool continueOnError)
        {
            if (entity.IsFailed)
                return false;

            try
            {
                return condition.Match(entity);
            }
            catch (Exception ex) when (!ExceptionClassifier.IsFatalForJob(ex) && continueOnError)
            {
                entity.SyncResult = SyncResult.Failed;
                entity.Error = ex.Message;
                context.Report.Increment(SyncResult.Failed);
                context.LogError($"[{displayName}] Échec de l'évaluation de la condition pour {entity.ToFlatString(200)} : {ex.Message}");
                return false;
            }
        }

        /// <summary>The flow transformer handled all its entities at once: its progress is complete.</summary>
        public static void Complete(JobContext context, int count)
        {
            context.Progress.SetTotal(count);
            for (var i = 0; i < count; i++)
                context.Progress.Increment();
        }

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

        /// <summary>The entities are tracked by the sub-transformers, run as modules with a nested context: no resume entity by entity.</summary>
        [Newtonsoft.Json.JsonIgnore]
        public override bool SupportsEntityResume => false;

        /// <summary>Each sub-transformer is run as a module on the whole flow, one after the other.</summary>
        public override async Task<List<Entity>> ActAsync(List<Entity> input, JobContext context)
        {
            CheckTransformers();
            await context.CheckpointAsync();

            if (Transformers.Count == 0)
            {
                context.LogWarning($"[{DisplayName}] Aucun sous-transformer n'est configuré : les entités sont transmises sans modification.");
                return input;
            }

            var nested = context.CreateNested();
            var current = input;

            foreach (var transformer in Transformers)
                current = await SubTransformers.ActAsync(transformer, current, nested);

            SubTransformers.Complete(context, input.Count);
            return current;
        }

        public override Task<Entity?> TransformAsync(Entity entity, JobContext context)
        {
            CheckTransformers();
            return SubTransformers.RunAsync(Transformers, entity, context);
        }

        private void CheckTransformers()
        {
            if (Transformers.Any(t => t == null))
                throw ConnectorGuard.Invalid(DisplayName, "La liste 'transformers' contient un élément vide.");
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

        [Newtonsoft.Json.JsonIgnore]
        public override bool SupportsEntityResume => false;

        /// <summary>The transformer is run as a module on the entities matching the condition, the others pass through.</summary>
        public override async Task<List<Entity>> ActAsync(List<Entity> input, JobContext context)
        {
            ConnectorGuard.Required(DisplayName, ("condition", Condition), ("transformer", Transformer));
            await context.CheckpointAsync();

            var matching = input.Where(e => SubTransformers.Matches(Condition!, e, context, DisplayName, ContinueOnError)).ToList();
            context.LogDebug($"[{DisplayName}] Condition remplie par {matching.Count} entité(s) sur {input.Count}.");

            var results = await SubTransformers.ActAsync(Transformer!, matching, context.CreateNested());

            SubTransformers.Complete(context, input.Count);
            return SubTransformers.Merge(input, matching, results);
        }

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

        [Newtonsoft.Json.JsonIgnore]
        public override bool SupportsEntityResume => false;

        /// <summary>Each branch is run as a module on its entities; the entities in failure pass through.</summary>
        public override async Task<List<Entity>> ActAsync(List<Entity> input, JobContext context)
        {
            CheckConfiguration();
            await context.CheckpointAsync();

            List<Entity> then = [], @else = [];

            foreach (var entity in input)
            {
                if (entity.IsFailed)
                    continue;

                if (SubTransformers.Matches(Condition!, entity, context, DisplayName, ContinueOnError))
                    then.Add(entity);
                else if (!entity.IsFailed)
                    @else.Add(entity);
            }

            var nested = context.CreateNested();
            var output = input;

            if (Then != null)
                output = SubTransformers.Merge(output, then, await SubTransformers.ActAsync(Then, then, nested));

            if (Else != null)
                output = SubTransformers.Merge(output, @else, await SubTransformers.ActAsync(Else, @else, nested));

            SubTransformers.Complete(context, input.Count);
            return output;
        }

        public override async Task<Entity?> TransformAsync(Entity entity, JobContext context)
        {
            CheckConfiguration();

            var branch = Condition!.Match(entity) ? Then : Else;

            return branch == null ? entity : await branch.TransformAsync(entity, context);
        }

        private void CheckConfiguration()
        {
            ConnectorGuard.Required(DisplayName, ("condition", Condition));

            if (Then == null && Else == null)
                throw ConnectorGuard.Invalid(DisplayName, "Au moins une des branches 'then' ou 'else' doit être configurée.");
        }
    }
}
