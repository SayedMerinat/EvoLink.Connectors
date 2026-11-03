namespace EvoLink.Connectors.MFiles.Gateway
{
    /// <summary>
    /// Raw access to an M-Files vault. The implementations only translate the calls (no cache, no name resolution):
    /// the resolution by name / alias / ID, the caches and the value conversions live in <see cref="MFilesSession"/>.
    /// <para>
    /// Failures: an unreachable server, a refused authentication or a lost connection throw a
    /// <see cref="MFilesConnectionException"/>; an operation refused by the server for one object throws a
    /// <see cref="MFilesOperationException"/>.
    /// </para>
    /// </summary>
    public interface IMFilesGateway : IDisposable
    {
        /// <summary>Display name of the vault (used in the messages, never contains a secret).</summary>
        string VaultName { get; }

        /// <summary>Connects to the server and logs in to the vault.</summary>
        void Connect();

        /// <summary>Logs out of the vault and disconnects from the server (may throw, unlike <see cref="IDisposable.Dispose"/>).</summary>
        void Disconnect();

        IReadOnlyList<MFilesClassInfo> GetClasses();

        /// <summary>ID of the class with this alias, null when no class has it.</summary>
        int? FindClassIdByAlias(string alias);

        IReadOnlyList<MFilesPropertyDefInfo> GetPropertyDefs();

        /// <summary>ID of the property definition with this alias, null when no property has it.</summary>
        int? FindPropertyDefIdByAlias(string alias);

        IReadOnlyList<MFilesValueListInfo> GetValueLists();

        /// <summary>ID of the value list with this alias, null when no value list has it.</summary>
        int? FindValueListIdByAlias(string alias);

        /// <summary>Every (non deleted) item of a value list or of an object type.</summary>
        IReadOnlyList<MFilesValueListItem> GetValueListItems(int valueListId);

        MFilesValueListItem AddValueListItem(int valueListId, string name);

        /// <summary>Objects (latest versions, not deleted) of a class matching every condition, with all their properties.</summary>
        IReadOnlyList<MFilesObject> Search(MFilesSearch search);

        MFilesObject Create(MFilesCreateRequest request);

        /// <summary>
        /// Checks the object out, sets the given properties only (the other ones are kept), applies the modification info
        /// and checks it in. The check-out is undone when a step fails.
        /// </summary>
        MFilesObject Update(MFilesObjectRef target, IReadOnlyList<MFilesPropertyValue> values, MFilesAuditInfo? audit);

        /// <summary>Marks the object as deleted, or destroys it (every version) when <paramref name="destroy"/> is true.</summary>
        void Delete(MFilesObjectRef target, bool destroy);

        IReadOnlyList<MFilesUserAccount> GetUserAccounts();
    }

    /// <summary>Data types of M-Files (same numeric values as <c>MFilesAPI.MFDataType</c>).</summary>
    public enum MFilesDataType
    {
        Uninitialized = 0,
        Text = 1,
        Integer = 2,
        Floating = 3,
        Date = 5,
        Time = 6,
        Timestamp = 7,
        Boolean = 8,
        Lookup = 9,
        MultiSelectLookup = 10,
        Integer64 = 11,
        FileTime = 12,
        MultiLineText = 13,
        Acl = 14,
    }

    public sealed record MFilesClassInfo(int Id, string Name, int ObjectType, int NamePropertyDefId);

    /// <summary>Property definition. <paramref name="IsAutomatic"/>: calculated / auto-numbered, never written.</summary>
    public sealed record MFilesPropertyDefInfo(int Id, string Name, MFilesDataType DataType, int? ValueListId, bool IsAutomatic, string? Guid = null)
    {
        public bool IsLookup => DataType is MFilesDataType.Lookup or MFilesDataType.MultiSelectLookup;
    }

    /// <summary>Value list or object type (<paramref name="IsObjectType"/>: its items are objects, they can't be created as items).</summary>
    public sealed record MFilesValueListInfo(int Id, string Name, string? PluralName, bool IsObjectType);

    public sealed record MFilesValueListItem(int Id, string Name, string? DisplayId = null);

    public sealed record MFilesUserAccount(int Id, string LoginName);

    /// <summary>Reference to a value list item, an object or a user account. The name is informative (read only).</summary>
    public sealed record MFilesLookup(int Id, string? Name = null);

    /// <summary>
    /// Typed value of a property. <see cref="Value"/> is null (empty), a <see cref="string"/>, a <see cref="long"/>,
    /// a <see cref="double"/>, a <see cref="bool"/>, a <see cref="DateTime"/>, an <see cref="MFilesLookup"/> (lookup)
    /// or a list of <see cref="MFilesLookup"/> (multi-select lookup).
    /// </summary>
    public sealed record MFilesPropertyValue(int PropertyDefId, MFilesDataType DataType, object? Value);

    public sealed record MFilesObjectRef(int ObjectType, int Id, int Version);

    public sealed record MFilesObject(MFilesObjectRef Ref, int ClassId, IReadOnlyDictionary<int, MFilesPropertyValue> Properties);

    /// <summary>Search of the objects of a class, every condition being an equality (empty value = property empty).</summary>
    public sealed record MFilesSearch(int ClassId, IReadOnlyList<MFilesPropertyValue> Conditions, int MaxResults = 0);

    /// <summary>File attached to a new object (single-file document when it is the only file).</summary>
    public sealed record MFilesFile(string Path, string Title, string Extension);

    /// <summary>Creation / last modification info set with the administrative operations (null = left untouched).</summary>
    public sealed record MFilesAuditInfo(int? CreatedBy = null, DateTime? CreatedUtc = null, int? LastModifiedBy = null, DateTime? LastModifiedUtc = null)
    {
        public bool HasCreationInfo => CreatedBy != null || CreatedUtc != null;
        public bool HasModificationInfo => LastModifiedBy != null || LastModifiedUtc != null;
        public bool IsEmpty => !HasCreationInfo && !HasModificationInfo;
    }

    public sealed record MFilesCreateRequest(int ObjectType, int ClassId, IReadOnlyList<MFilesPropertyValue> Values, MFilesFile? File = null, MFilesAuditInfo? Audit = null,
        IReadOnlyList<MFilesFile>? AdditionalFiles = null)
    {
        /// <summary>Every file of the new object (several files make a multi-file document).</summary>
        public IReadOnlyList<MFilesFile> AllFiles => [.. File == null ? [] : new[] { File }, .. AdditionalFiles ?? []];
    }
}
