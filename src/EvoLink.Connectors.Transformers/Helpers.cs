using System.Text.RegularExpressions;
using EvoLink.Connectors.Common;
using Ortolan.Core.Models;

namespace EvoLink.Connectors.Transformers
{
    /// <summary>
    /// Helpers shared by the transformers and the conditions of the plugin.
    /// </summary>
    internal static class Helpers
    {
        /// <summary>Default timeout of the regular expressions (protection against catastrophic backtracking).</summary>
        public const int DefaultRegexTimeoutMs = 1000;

        /// <summary>Returns the value of a property, or throws when the entity doesn't have it.</summary>
        public static object? GetRequired(Entity entity, string property)
            => entity.TryGetValue(property, out var value)
                ? value
                : throw new InvalidOperationException($"L'entité ne contient pas la propriété '{property}'.");

        /// <summary>Culture-invariant text of a value, null being an empty string.</summary>
        public static string Text(object? value)
            => ValueConverter.ToText(value) ?? string.Empty;

        /// <summary>
        /// Builds a regular expression with a timeout; an invalid pattern is a configuration error.
        /// </summary>
        public static Regex CreateRegex(string module, string pattern, bool ignoreCase, int timeoutMs)
        {
            ConnectorGuard.Required(module, ("pattern", pattern));

            var options = RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
            var timeout = TimeSpan.FromMilliseconds(timeoutMs > 0 ? timeoutMs : DefaultRegexTimeoutMs);

            try
            {
                return new Regex(pattern, options, timeout);
            }
            catch (ArgumentException ex)
            {
                throw ConnectorGuard.Invalid(module, $"Expression régulière '{pattern}' invalide : {ex.Message}");
            }
        }

        /// <summary>
        /// Removes <paramref name="root"/> from the start of <paramref name="path"/> (separators and case ignored) and the
        /// leading separators. Throws when the path is not located under the root.
        /// </summary>
        public static string RelativeTo(string path, string root)
        {
            var normalizedRoot = NormalizeSeparators(root).TrimEnd(Path.DirectorySeparatorChar);
            var normalizedPath = NormalizeSeparators(path);

            if (!normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
                || (normalizedPath.Length > normalizedRoot.Length && normalizedPath[normalizedRoot.Length] != Path.DirectorySeparatorChar))
                throw new InvalidOperationException($"Le chemin '{path}' n'est pas situé sous la racine '{root}'.");

            return normalizedPath[normalizedRoot.Length..].TrimStart(Path.DirectorySeparatorChar);
        }

        /// <summary>Uses the separator of the platform ('\' on Windows) everywhere in a path.</summary>
        public static string NormalizeSeparators(string path)
            => path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

        /// <summary>Splits a path into its non-empty segments ('\' and '/' are both separators).</summary>
        public static string[] Segments(string path)
            => path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
    }
}
