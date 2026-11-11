using EvoLink.Connectors.Common;
using EvoLink.Odoo.Client;
using Ortolan.Core.Exceptions;
using Ortolan.Core.Pipeline;

namespace EvoLink.Connectors.Odoo
{
    /// <summary>
    /// Configuration checks and failure classification shared by the Odoo modules.
    /// </summary>
    internal static class OdooModuleSupport
    {
        public static OdooConnection CreateConnection(string module, string url, string database, string username, string apiKey, int timeoutSeconds, string model)
        {
            ConnectorGuard.Required(module, ("url", url), ("database", database), ("username", username), ("apiKey", apiKey), ("model", model));

            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                throw ConnectorGuard.Invalid(module, $"L'URL Odoo '{url}' n'est pas une adresse http(s) absolue, ex. https://societe.odoo.com.");

            if (timeoutSeconds <= 0)
                throw ConnectorGuard.Invalid(module, "Le délai d'attente (timeoutSeconds) doit être supérieur à 0.");

            return new OdooConnection
            {
                Url = url.Trim(),
                Database = database.Trim(),
                Username = username.Trim(),
                ApiKey = apiKey,
                Timeout = TimeSpan.FromSeconds(timeoutSeconds),
            };
        }

        public static TimeZoneInfo ResolveTimeZone(string module, string? timeZone)
        {
            if (string.IsNullOrWhiteSpace(timeZone))
                return TimeZoneInfo.Local;

            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(timeZone.Trim());
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                throw ConnectorGuard.Invalid(module, $"Fuseau horaire '{timeZone}' inconnu (ex. Europe/Zurich).");
            }
        }

        /// <summary>Retries of the paged reads (none when <paramref name="attempts"/> is 0), logged as warnings.</summary>
        public static OdooReadRetry CreateReadRetry(string module, int attempts, int delaySeconds, bool shrinkPage, JobContext context)
        {
            if (attempts < 0 || delaySeconds < 0)
                throw ConnectorGuard.Invalid(module, "Le nombre de nouvelles tentatives (readRetries) et leur délai (readRetryDelaySeconds) ne peuvent pas être négatifs.");

            if (attempts == 0)
                return OdooReadRetry.None;

            return new OdooReadRetry
            {
                Attempts = attempts,
                Delay = TimeSpan.FromSeconds(delaySeconds),
                ShrinkPage = shrinkPage,
                OnRetry = info => context.LogWarning(
                    $"[{module}] Odoo indisponible pendant la lecture ({info.Error.Message}) : nouvelle tentative {info.Attempt}/{info.MaxAttempts}"
                    + (shrinkPage ? $", pages de {info.PageSize} enregistrement(s)." : ".")),
            };
        }

        public static OdooContext CreateContext(string? language, bool includeArchived)
            => new() { Language = string.IsNullOrWhiteSpace(language) ? null : language.Trim(), ActiveTest = includeArchived ? false : null };

        /// <summary>
        /// Runs a call to Odoo, an unavailability of Odoo (or a technical access failure) being converted by <paramref name="toUnavailable"/>.
        /// </summary>
        public static async Task<T> CallAsync<T>(Func<Task<T>> call, Func<Exception, OrtolanException> toUnavailable)
        {
            try
            {
                return await ConnectorGuard.CallAsync(call, ex => ex is OdooUnavailableException, toUnavailable);
            }
            catch (OdooConfigurationException ex)
            {
                // Wrong address of the instance: retrying can't succeed, the job stops without retry.
                throw new OrtolanConfigurationException(ex.Message, ex);
            }
        }

        public static Task CallAsync(Func<Task> call, Func<Exception, OrtolanException> toUnavailable)
            => CallAsync(async () =>
            {
                await call();
                return true;
            }, toUnavailable);

        public static bool IsUnavailability(Exception exception)
            => exception is OdooUnavailableException || ExceptionClassifier.IsAccessFailure(exception);
    }
}
