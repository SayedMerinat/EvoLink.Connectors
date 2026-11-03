namespace EvoLink.Connectors.DocSeries
{
    /// <summary>Content type (document type) of DocSeries.</summary>
    public sealed record DocSeriesContentType(int Id, string Name);

    /// <summary>Definition of a field of a content type (<c>DefField</c>).</summary>
    public sealed record DocSeriesFieldDefinition(int Id, string Name, string? Title, int FieldType, bool IsRequired = false)
    {
        /// <summary>Static list, defined in the content type (values read from the document structure).</summary>
        public const int StaticListType = 3;

        /// <summary>External table (dynamic list whose items can't be created through the API).</summary>
        public const int ExternalTableType = 4;

        /// <summary>Internal table (dynamic list whose items can be created through the API).</summary>
        public const int InternalTableType = 6;

        public bool IsDynamicList => FieldType is ExternalTableType or InternalTableType;
        public bool IsStaticList => FieldType == StaticListType;
    }

    /// <summary>Document to create: content type, field values (by field name) and optional attached file.</summary>
    public sealed record DocSeriesNewDocument(string ContentType, IReadOnlyDictionary<string, string> Fields, string? FilePath, bool AutomaticEmptyFields);

    /// <summary>
    /// Access to a DocSeries server, implemented over <c>EvoLink.DocSeries.API</c> (<see cref="DocSeriesGateway"/>) and by
    /// an in-memory fake in the tests. Unreachable server / authentication failures surface as access failures
    /// (<see cref="HttpRequestException"/>), errors specific to a request as <see cref="DocSeriesRequestException"/>.
    /// </summary>
    public interface IDocSeriesGateway
    {
        Task<IReadOnlyList<DocSeriesContentType>> GetContentTypesAsync();

        Task<IReadOnlyList<DocSeriesFieldDefinition>> GetFieldsAsync(int contentTypeId);

        /// <summary>Texts of the items of a dynamic list (internal or external table).</summary>
        Task<IReadOnlyList<string>> GetDynamicListAsync(int defFieldId);

        /// <summary>Display values of a static list of a content type.</summary>
        Task<IReadOnlyList<string>> GetStaticListAsync(int contentTypeId, DocSeriesFieldDefinition field);

        /// <summary>Name of the display column of an internal table (needed to create an item).</summary>
        Task<string> GetInternalTableColumnAsync(int defFieldId);

        Task AddDynamicListItemAsync(int defFieldId, string column, string value);

        /// <summary>Creates a document and returns its object identifier.</summary>
        Task<int> CreateDocumentAsync(DocSeriesNewDocument document);
    }

    /// <summary>A request was refused by DocSeries (invalid value, unknown content type...): the entity fails, the job goes on.</summary>
    public sealed class DocSeriesRequestException : Exception
    {
        public DocSeriesRequestException(string message, Exception? inner = null) : base(message, inner) { }
    }
}
