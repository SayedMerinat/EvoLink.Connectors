using System.Net;
using EvoLink.DocSeries.API;
using EvoLink.DocSeries.API.Builders;

namespace EvoLink.Connectors.DocSeries
{
    /// <summary>
    /// <see cref="IDocSeriesGateway"/> over the <c>EvoLink.DocSeries.API</c> client (REST, token authentication).
    /// The HTTP errors are classified: refused requests (4xx, 500) become <see cref="DocSeriesRequestException"/> (the
    /// entity fails), the others (network, authentication, timeout, 502/503/504...) stay access failures.
    /// </summary>
    public sealed class DocSeriesGateway : IDocSeriesGateway
    {
        private readonly DocSeriesClient _client;

        public DocSeriesGateway(string host, string username, string password)
        {
            _client = new DocSeriesClient(host, username, password);
        }

        public Task<IReadOnlyList<DocSeriesContentType>> GetContentTypesAsync()
            => CallAsync<IReadOnlyList<DocSeriesContentType>>(async () =>
                (await _client.GetContentTypes())
                    .Where(c => c.Id.HasValue && !string.IsNullOrWhiteSpace(c.Text))
                    .Select(c => new DocSeriesContentType(c.Id!.Value, c.Text))
                    .ToList());

        public Task<IReadOnlyList<DocSeriesFieldDefinition>> GetFieldsAsync(int contentTypeId)
            => CallAsync<IReadOnlyList<DocSeriesFieldDefinition>>(async () =>
                ((await _client.GetContentTypeById(contentTypeId))?.DefFields ?? [])
                    .Select(d => new DocSeriesFieldDefinition(d.Id, d.Name, d.Title, d.FieldType, d.IsRequired))
                    .ToList());

        public Task<IReadOnlyList<string>> GetDynamicListAsync(int defFieldId)
            => CallAsync<IReadOnlyList<string>>(async () =>
                ((await _client.GetDynamicList(defFieldId)) ?? []).Select(i => i.Text).Where(t => t != null).ToList());

        public Task<IReadOnlyList<string>> GetStaticListAsync(int contentTypeId, DocSeriesFieldDefinition field)
            => CallAsync<IReadOnlyList<string>>(async () =>
            {
                var structure = await _client.GetDocumentStructure(contentTypeId);
                var fields = structure?.Fields?.Where(f => f.Type == DocSeriesFieldDefinition.StaticListType).ToList() ?? [];

                var match = fields.FirstOrDefault(f => f.DefFieldId == field.Id)
                    ?? fields.FirstOrDefault(f => string.Equals(f.Title, field.Name, StringComparison.OrdinalIgnoreCase))
                    ?? fields.FirstOrDefault(f => field.Title != null && string.Equals(f.Title, field.Title, StringComparison.OrdinalIgnoreCase))
                    ?? throw new DocSeriesRequestException($"Liste statique '{field.Name}' introuvable dans la structure du type de contenu {contentTypeId}.");

                return (match.ListElements ?? []).Select(e => e.DisplayValue).Where(v => v != null).ToList();
            });

        public Task<string> GetInternalTableColumnAsync(int defFieldId)
            => CallAsync(async () =>
            {
                var items = (await _client.GetDynamicListTableData(defFieldId))?.FirstOrDefault()?.Items ?? [];

                if (items.Count != 1 || string.IsNullOrWhiteSpace(items[0].Column?.Name))
                    throw new DocSeriesRequestException($"La table interne du champ {defFieldId} doit avoir exactement une colonne de données ({items.Count} trouvée(s)) : impossible d'y créer un élément.");

                return items[0].Column!.Name;
            });

        public Task AddDynamicListItemAsync(int defFieldId, string column, string value)
            => CallAsync(async () =>
            {
                await new DynamicListItemBuilder(_client).WithDefFieldId(defFieldId).WithValue(column, value).Create();
                return true;
            });

        public Task<int> CreateDocumentAsync(DocSeriesNewDocument document)
            => CallAsync(async () =>
            {
                var builder = new DocumentBuilder(_client).WithContentType(document.ContentType);

                if (document.AutomaticEmptyFields)
                    builder.WithAutomaticEmptyFields();

                foreach (var (name, value) in document.Fields)
                    builder.WithField(name, value);

                if (!string.IsNullOrWhiteSpace(document.FilePath))
                    builder.WithFile(document.FilePath);

                var created = await builder.Create();
                return created?.ObjectId ?? throw new DocSeriesRequestException("DocSeries n'a pas renvoyé le document créé.");
            });

        private static async Task<T> CallAsync<T>(Func<Task<T>> call)
        {
            try
            {
                return await call();
            }
            catch (HttpRequestException ex) when (IsRefusedRequest(ex))
            {
                throw new DocSeriesRequestException($"Requête refusée par DocSeries ({(int)ex.StatusCode!.Value} {ex.StatusCode}) : {ex.Message}", ex);
            }
        }

        /// <summary>
        /// True when DocSeries answered but refused the request itself (invalid data): the other HTTP errors (no answer,
        /// authentication, throttling, gateway errors) mean the server can't be used for now.
        /// </summary>
        internal static bool IsRefusedRequest(HttpRequestException exception)
            => exception.StatusCode is { } status
               && ((int)status is >= 400 and < 500 || status == HttpStatusCode.InternalServerError)
               && status is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.ProxyAuthenticationRequired
                   or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests);
    }
}
