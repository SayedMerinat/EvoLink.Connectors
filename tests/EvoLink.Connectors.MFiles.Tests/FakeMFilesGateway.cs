using System.Collections.Concurrent;
using EvoLink.Connectors.MFiles;
using EvoLink.Connectors.MFiles.Gateway;

namespace EvoLink.Connectors.MFiles.Tests
{
    /// <summary>
    /// In-memory vault implementing the gateway: classes, properties, value lists, objects and users, with call counters and
    /// failure injection. Thread-safe (the modules may run in parallel).
    /// </summary>
    public sealed class FakeMFilesGateway : IMFilesGateway
    {
        private readonly Lock _lock = new();
        private readonly ConcurrentDictionary<string, int> _calls = new();
        private int _nextObjectId = 1;

        public string VaultName => "Test@fake";

        public List<MFilesClassInfo> Classes { get; } = [];
        public List<MFilesPropertyDefInfo> PropertyDefs { get; } = [];
        public List<MFilesValueListInfo> ValueLists { get; } = [];
        public Dictionary<int, List<MFilesValueListItem>> Items { get; } = [];
        public List<MFilesUserAccount> Users { get; } = [];
        public Dictionary<string, int> ClassAliases { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> PropertyAliases { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> ValueListAliases { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<FakeObject> Objects { get; } = [];

        public ConcurrentQueue<MFilesCreateRequest> Creates { get; } = new();
        public ConcurrentQueue<(MFilesObjectRef Target, IReadOnlyList<MFilesPropertyValue> Values, MFilesAuditInfo? Audit)> Updates { get; } = new();

        /// <summary>Exception thrown by <see cref="Connect"/>.</summary>
        public Exception? ConnectFailure { get; set; }

        /// <summary>Failure injected by operation name (Search, Create, Update, Delete, GetValueListItems...).</summary>
        public Func<string, Exception?>? FailOn { get; set; }

        /// <summary>Delay of the value list loads, to widen the race windows of the concurrency tests.</summary>
        public int ItemsLoadDelayMs { get; set; }

        public MFilesConnectionSettings? Settings { get; set; }

        public int Calls(string operation)
            => _calls.GetValueOrDefault(operation);

        public void Connect()
        {
            Count(nameof(Connect));
            if (ConnectFailure != null)
                throw ConnectFailure;
        }

        public void Disconnect()
            => Count(nameof(Disconnect));

        public void Dispose()
            => Count(nameof(Dispose));

        public IReadOnlyList<MFilesClassInfo> GetClasses()
        {
            Count(nameof(GetClasses));
            return [.. Classes];
        }

        public int? FindClassIdByAlias(string alias)
            => ClassAliases.TryGetValue(alias, out var id) ? id : null;

        public IReadOnlyList<MFilesPropertyDefInfo> GetPropertyDefs()
        {
            Count(nameof(GetPropertyDefs));
            return [.. PropertyDefs];
        }

        public int? FindPropertyDefIdByAlias(string alias)
            => PropertyAliases.TryGetValue(alias, out var id) ? id : null;

        public IReadOnlyList<MFilesValueListInfo> GetValueLists()
            => [.. ValueLists];

        public int? FindValueListIdByAlias(string alias)
            => ValueListAliases.TryGetValue(alias, out var id) ? id : null;

        public IReadOnlyList<MFilesValueListItem> GetValueListItems(int valueListId)
        {
            Count(nameof(GetValueListItems));
            Count($"{nameof(GetValueListItems)}:{valueListId}");

            if (ItemsLoadDelayMs > 0)
                Thread.Sleep(ItemsLoadDelayMs);

            lock (_lock)
                return Items.TryGetValue(valueListId, out var items) ? [.. items] : [];
        }

        public MFilesValueListItem AddValueListItem(int valueListId, string name)
        {
            Count(nameof(AddValueListItem));

            lock (_lock)
            {
                if (!Items.TryGetValue(valueListId, out var items))
                    Items[valueListId] = items = [];

                var item = new MFilesValueListItem(items.Count == 0 ? 1 : items.Max(i => i.Id) + 1, name);
                items.Add(item);
                return item;
            }
        }

        public IReadOnlyList<MFilesObject> Search(MFilesSearch search)
        {
            Count(nameof(Search));

            lock (_lock)
            {
                var found = Objects
                    .Where(o => !o.Deleted && o.ClassId == search.ClassId && search.Conditions.All(c => Matches(o, c)))
                    .Select(o => o.ToObject());

                return [.. search.MaxResults > 0 ? found.Take(search.MaxResults) : found];
            }
        }

        public MFilesObject Create(MFilesCreateRequest request)
        {
            Count(nameof(Create));
            Creates.Enqueue(request);

            lock (_lock)
            {
                var obj = new FakeObject(request.ObjectType, _nextObjectId++, request.ClassId);

                foreach (var value in request.Values)
                    obj.Properties[value.PropertyDefId] = value;

                obj.Properties[MFilesKeys.ClassPropertyDef] = new MFilesPropertyValue(MFilesKeys.ClassPropertyDef, MFilesDataType.Lookup, new MFilesLookup(request.ClassId));
                obj.File = request.File;
                ApplyAudit(obj, request.Audit);
                Objects.Add(obj);
                return obj.ToObject();
            }
        }

        public MFilesObject Update(MFilesObjectRef target, IReadOnlyList<MFilesPropertyValue> values, MFilesAuditInfo? audit)
        {
            Count(nameof(Update));
            Updates.Enqueue((target, values, audit));

            lock (_lock)
            {
                var obj = Find(target);

                foreach (var value in values)
                    obj.Properties[value.PropertyDefId] = value;

                obj.Version++;
                ApplyAudit(obj, audit);
                return obj.ToObject();
            }
        }

        public void Delete(MFilesObjectRef target, bool destroy)
        {
            Count(nameof(Delete));

            lock (_lock)
            {
                var obj = Find(target);

                if (destroy)
                    Objects.Remove(obj);
                else
                    obj.Deleted = true;
            }
        }

        public IReadOnlyList<MFilesUserAccount> GetUserAccounts()
        {
            Count(nameof(GetUserAccounts));
            return [.. Users];
        }

        // ---------------------------------------------------------------- test helpers

        public FakeObject AddObject(int classId, params (int PropertyDef, object? Value)[] values)
        {
            var objectType = Classes.First(c => c.Id == classId).ObjectType;
            var obj = new FakeObject(objectType, _nextObjectId++, classId);
            obj.Properties[MFilesKeys.ClassPropertyDef] = new MFilesPropertyValue(MFilesKeys.ClassPropertyDef, MFilesDataType.Lookup, new MFilesLookup(classId));

            foreach (var (propertyDef, value) in values)
            {
                var definition = PropertyDefs.First(p => p.Id == propertyDef);
                obj.Properties[propertyDef] = new MFilesPropertyValue(propertyDef, definition.DataType, value);
            }

            Objects.Add(obj);
            return obj;
        }

        public FakeObject? Object(int id)
        {
            lock (_lock)
                return Objects.FirstOrDefault(o => o.Id == id);
        }

        public List<FakeObject> Alive(int classId)
        {
            lock (_lock)
                return [.. Objects.Where(o => o.ClassId == classId && !o.Deleted)];
        }

        private FakeObject Find(MFilesObjectRef target)
            => Objects.FirstOrDefault(o => o.ObjectType == target.ObjectType && o.Id == target.Id && !o.Deleted)
               ?? throw new MFilesOperationException($"Objet {target.Id} introuvable.");

        private static void ApplyAudit(FakeObject obj, MFilesAuditInfo? audit)
        {
            if (audit == null)
                return;

            if (audit.CreatedBy is { } createdBy)
                obj.Properties[25] = new MFilesPropertyValue(25, MFilesDataType.Lookup, new MFilesLookup(createdBy));
            if (audit.CreatedUtc is { } created)
                obj.Properties[20] = new MFilesPropertyValue(20, MFilesDataType.Timestamp, created);
            if (audit.LastModifiedBy is { } modifiedBy)
                obj.Properties[23] = new MFilesPropertyValue(23, MFilesDataType.Lookup, new MFilesLookup(modifiedBy));
            if (audit.LastModifiedUtc is { } modified)
                obj.Properties[21] = new MFilesPropertyValue(21, MFilesDataType.Timestamp, modified);
        }

        private static bool Matches(FakeObject obj, MFilesPropertyValue condition)
        {
            var current = obj.Properties.GetValueOrDefault(condition.PropertyDefId);

            if (current?.Value is IEnumerable<MFilesLookup> lookups && condition.Value is IReadOnlyList<MFilesLookup> { Count: 1 } single)
                return lookups.Any(l => l.Id == single[0].Id);

            return MFilesSession.SameValue(current, condition);
        }

        private void Count(string operation)
        {
            _calls.AddOrUpdate(operation, 1, (_, v) => v + 1);

            if (FailOn?.Invoke(operation) is { } failure)
                throw failure;
        }
    }

    public sealed class FakeObject(int objectType, int id, int classId)
    {
        public int ObjectType { get; } = objectType;
        public int Id { get; } = id;
        public int ClassId { get; } = classId;
        public int Version { get; set; } = 1;
        public bool Deleted { get; set; }
        public MFilesFile? File { get; set; }
        public Dictionary<int, MFilesPropertyValue> Properties { get; } = [];

        public object? Value(int propertyDef)
            => Properties.GetValueOrDefault(propertyDef)?.Value;

        public MFilesObject ToObject()
            => new(new MFilesObjectRef(ObjectType, Id, Version), ClassId, new Dictionary<int, MFilesPropertyValue>(Properties));
    }
}
