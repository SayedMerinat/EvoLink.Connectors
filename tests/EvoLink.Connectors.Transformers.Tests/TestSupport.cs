using Newtonsoft.Json.Linq;
using Ortolan.Core.Models;
using Ortolan.Core.Modules;
using Ortolan.Core.Pipeline;
using Ortolan.Core.Serialization;

namespace EvoLink.Connectors.Transformers.Tests
{
    internal static class TestSupport
    {
        public static JobContext Context(string? workingDirectory = null)
            => new()
            {
                JobId = Guid.NewGuid().ToString("N"),
                ServiceId = "service",
                WorkingDirectory = workingDirectory ?? Path.GetTempPath(),
            };

        public static TypeRegistry Registry()
            => new TypeRegistry().RegisterAssembly(typeof(CompositeTransformer).Assembly);

        public static T FromJson<T>(string json)
            => OrtolanJson.ToConfiguration<T>(JToken.Parse(json), Registry(), "de test");

        /// <summary>Transforms one entity directly (exceptions propagate).</summary>
        public static async Task<Entity?> TransformAsync(this ITransformer transformer, Entity entity)
            => await transformer.TransformAsync(entity, Context());

        /// <summary>Runs the module like the pipeline does (per-entity error handling).</summary>
        public static async Task<Entity> ActOneAsync(this IModule module, Entity entity, JobContext? context = null)
            => Assert.Single(await module.ActAsync([entity], context ?? Context()));

        public static Entity Entity(params (string Key, object? Value)[] properties)
        {
            var entity = new Entity();
            foreach (var (key, value) in properties)
                entity[key] = value;
            return entity;
        }
    }

    /// <summary>Temporary folder deleted after the test.</summary>
    internal sealed class TempFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "evolink-connectors-tests", Guid.NewGuid().ToString("N"));

        public TempFolder()
        {
            Directory.CreateDirectory(Path);
        }

        public string File(string relativePath, string content = "")
        {
            var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(Path, relativePath));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            System.IO.File.WriteAllText(full, content);
            return full;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
