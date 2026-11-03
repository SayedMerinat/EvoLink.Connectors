using Ortolan.Core.Exceptions;

namespace EvoLink.Connectors.Common
{
    /// <summary>
    /// Validation of the configurations and classification of the failures, so that every connector reports them the same way:
    /// an invalid configuration fails the job without retry, an unreachable system allows the restart policy to act.
    /// </summary>
    public static class ConnectorGuard
    {
        /// <summary>Throws an <see cref="OrtolanConfigurationException"/> listing the empty required properties.</summary>
        public static void Required(string module, params (string Name, object? Value)[] properties)
        {
            var missing = properties.Where(p => p.Value switch
            {
                null => true,
                string s => string.IsNullOrWhiteSpace(s),
                int i => i <= 0,
                System.Collections.ICollection c => c.Count == 0,
                _ => false,
            }).Select(p => p.Name).ToList();

            if (missing.Count > 0)
                throw new OrtolanConfigurationException($"[{module}] Propriété(s) obligatoire(s) manquante(s) : {string.Join(", ", missing)}.");
        }

        public static OrtolanConfigurationException Invalid(string module, string message)
            => new($"[{module}] {message}");

        public static SourceUnavailableException SourceUnavailable(string module, string system, Exception inner)
            => new(module, $"[{module}] Source '{system}' inaccessible : {inner.Message}", inner);

        public static TargetUnavailableException TargetUnavailable(string module, string system, Exception inner)
            => new(module, $"[{module}] Cible '{system}' inaccessible : {inner.Message}", inner);

        /// <summary>
        /// Runs a call to a remote system and converts its access failures (network, authentication, timeout) into a
        /// source / target unavailability, the other exceptions being left untouched.
        /// </summary>
        public static async Task<T> CallAsync<T>(Func<Task<T>> call, Func<Exception, bool> isAccessFailure, Func<Exception, Exception> toUnavailable)
        {
            try
            {
                return await call();
            }
            catch (Exception ex) when (ex is not OrtolanException && (ExceptionClassifier.IsAccessFailure(ex) || isAccessFailure(ex)))
            {
                throw toUnavailable(ex);
            }
        }
    }
}
