using System.Collections.Concurrent;

namespace EvoLink.Connectors.DocSeries.Tests
{
    /// <summary>In-memory DocSeries: content types, list fields and created documents.</summary>
    internal sealed class FakeDocSeriesGateway : IDocSeriesGateway
    {
        public List<DocSeriesContentType> ContentTypes { get; } = [];
        public Dictionary<int, List<DocSeriesFieldDefinition>> FieldsByContentType { get; } = [];
        public Dictionary<int, List<string>> Lists { get; } = [];
        public Dictionary<int, string> InternalColumns { get; } = [];
        public ConcurrentQueue<(int DefFieldId, string Column, string Value)> CreatedItems { get; } = new();
        public ConcurrentQueue<DocSeriesNewDocument> Documents { get; } = new();

        /// <summary>Exception thrown by every call (simulates an unreachable server or a refused request).</summary>
        public Exception? Failure { get; set; }

        /// <summary>Exception thrown only when a document is created.</summary>
        public Exception? CreationFailure { get; set; }

        public int ListReads;
        private int _nextId = 1000;

        public FakeDocSeriesGateway()
        {
            ContentTypes.Add(new DocSeriesContentType(1, "Facture"));
            FieldsByContentType[1] =
            [
                new DocSeriesFieldDefinition(10, "Numero", "Numéro", 1, IsRequired: true),
                new DocSeriesFieldDefinition(11, "DateFacture", "Date de facture", 2),
                new DocSeriesFieldDefinition(12, "Fournisseur", "Fournisseur", DocSeriesFieldDefinition.InternalTableType),
                new DocSeriesFieldDefinition(13, "Pays", "Pays", DocSeriesFieldDefinition.ExternalTableType),
                new DocSeriesFieldDefinition(14, "Statut", "Statut", DocSeriesFieldDefinition.StaticListType),
                new DocSeriesFieldDefinition(15, "Montant", "Montant", 5),
            ];
            Lists[12] = ["ACME SA", "Globex Sàrl"];
            Lists[13] = ["Suisse", "France"];
            Lists[14] = ["Ouvert", "Payé"];
            InternalColumns[12] = "Nom";
        }

        public Task<IReadOnlyList<DocSeriesContentType>> GetContentTypesAsync()
            => Run<IReadOnlyList<DocSeriesContentType>>(() => [.. ContentTypes]);

        public Task<IReadOnlyList<DocSeriesFieldDefinition>> GetFieldsAsync(int contentTypeId)
            => Run<IReadOnlyList<DocSeriesFieldDefinition>>(() => [.. FieldsByContentType[contentTypeId]]);

        public Task<IReadOnlyList<string>> GetDynamicListAsync(int defFieldId)
            => Run<IReadOnlyList<string>>(() =>
            {
                Interlocked.Increment(ref ListReads);
                lock (Lists)
                    return [.. Lists[defFieldId]];
            });

        public Task<IReadOnlyList<string>> GetStaticListAsync(int contentTypeId, DocSeriesFieldDefinition field)
            => GetDynamicListAsync(field.Id);

        public Task<string> GetInternalTableColumnAsync(int defFieldId)
            => Run(() => InternalColumns[defFieldId]);

        public Task AddDynamicListItemAsync(int defFieldId, string column, string value)
            => Run(() =>
            {
                lock (Lists)
                    Lists[defFieldId].Add(value);

                CreatedItems.Enqueue((defFieldId, column, value));
                return true;
            });

        public Task<int> CreateDocumentAsync(DocSeriesNewDocument document)
            => Run(() =>
            {
                if (CreationFailure != null)
                    throw CreationFailure;

                Documents.Enqueue(document);
                return Interlocked.Increment(ref _nextId);
            });

        private async Task<T> Run<T>(Func<T> action)
        {
            await Task.Yield();

            if (Failure != null)
                throw Failure;

            return action();
        }
    }

    /// <summary>Writer using the fake gateway instead of a real server.</summary>
    internal sealed class TestableDocSeriesWriter : DocSeriesWriter
    {
        public FakeDocSeriesGateway Fake { get; } = new();

        public TestableDocSeriesWriter()
        {
            Host = "https://docseries.test";
            Username = "ortolan";
            Password = "secret";
        }

        protected override IDocSeriesGateway CreateGateway()
            => Fake;
    }
}
