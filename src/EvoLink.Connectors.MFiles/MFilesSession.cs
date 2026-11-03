using System.Collections.Concurrent;
using System.Globalization;
using EvoLink.Connectors.Common;
using EvoLink.Connectors.MFiles.Gateway;
using Ortolan.Core.Pipeline;

namespace EvoLink.Connectors.MFiles
{
    /// <summary>
    /// Connection to a vault for the duration of a job (one instance per module run): resolution of the classes, properties and
    /// value lists by name, alias or ID, cache of the value list items and of the user accounts, conversion of the entity values.
    /// <para>
    /// Every cache belongs to the instance (no static state) and is thread-safe: the modules may process their entities in
    /// parallel. A failed load is not cached, the next call tries again.
    /// </para>
    /// </summary>
    public sealed class MFilesSession : IDisposable
    {
        private readonly Cached<IReadOnlyList<MFilesClassInfo>> _classes;
        private readonly Cached<PropertyIndex> _properties;
        private readonly Cached<IReadOnlyList<MFilesValueListInfo>> _valueLists;
        private readonly Cached<IReadOnlyList<MFilesUserAccount>> _users;
        private readonly ConcurrentDictionary<string, MFilesClassInfo?> _classByIdentifier = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, MFilesPropertyDefInfo?> _propertyByIdentifier = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, MFilesValueListInfo?> _valueListByIdentifier = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<int, Cached<ValueListIndex>> _items = new();

        public MFilesSession(IMFilesGateway gateway)
        {
            Gateway = gateway;
            _classes = new(gateway.GetClasses);
            _properties = new(() => new PropertyIndex(gateway.GetPropertyDefs()));
            _valueLists = new(gateway.GetValueLists);
            _users = new(gateway.GetUserAccounts);
        }

        public IMFilesGateway Gateway { get; }

        public string VaultName => Gateway.VaultName;

        // ---------------------------------------------------------------- classes, properties, value lists

        /// <summary>Class by name (exact, then case-insensitive), alias or ID; null when not found or ambiguous.</summary>
        public MFilesClassInfo? FindClass(string identifier)
            => _classByIdentifier.GetOrAdd(identifier.Trim(), id =>
            {
                var classes = _classes.Value;

                if (UniqueByName(classes, c => c.Name, id) is { } byName)
                    return byName;

                if (Gateway.FindClassIdByAlias(id) is { } aliasId && classes.FirstOrDefault(c => c.Id == aliasId) is { } byAlias)
                    return byAlias;

                return int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? classes.FirstOrDefault(c => c.Id == number) : null;
            });

        /// <summary>Property definition by name (exact, then case-insensitive), alias, ID or GUID; null when not found.</summary>
        public MFilesPropertyDefInfo? FindProperty(string identifier)
            => _propertyByIdentifier.GetOrAdd(identifier.Trim(), id =>
            {
                var index = _properties.Value;

                if (UniqueByName(index.All, p => p.Name, id) is { } byName)
                    return byName;

                if (Gateway.FindPropertyDefIdByAlias(id) is { } aliasId && index.ById.TryGetValue(aliasId, out var byAlias))
                    return byAlias;

                if (int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && index.ById.TryGetValue(number, out var byId))
                    return byId;

                var guid = NormalizeGuid(id);
                return index.All.FirstOrDefault(p => p.Guid != null && NormalizeGuid(p.Guid) == guid);
            });

        public MFilesPropertyDefInfo? FindProperty(int id)
            => _properties.Value.ById.GetValueOrDefault(id);

        /// <summary>Value list or object type by name (singular or plural), alias or ID; null when not found.</summary>
        public MFilesValueListInfo? FindValueList(string identifier)
            => _valueListByIdentifier.GetOrAdd(identifier.Trim(), id =>
            {
                var lists = _valueLists.Value;

                if ((UniqueByName(lists, l => l.Name, id) ?? UniqueByName(lists, l => l.PluralName ?? string.Empty, id)) is { } byName)
                    return byName;

                if (Gateway.FindValueListIdByAlias(id) is { } aliasId && lists.FirstOrDefault(l => l.Id == aliasId) is { } byAlias)
                    return byAlias;

                return int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? lists.FirstOrDefault(l => l.Id == number) : null;
            });

        public MFilesValueListInfo? FindValueList(int id)
            => _valueLists.Value.FirstOrDefault(l => l.Id == id);

        // ---------------------------------------------------------------- value list items

        /// <summary>Items of a value list, loaded once per session (thread-safe).</summary>
        public IReadOnlyList<MFilesValueListItem> GetItems(int valueListId)
            => Items(valueListId).Snapshot();

        /// <summary>
        /// Item of a value list designated by its name (exact, then case-insensitive), its display ID or its ID.
        /// Null when it does not exist (and was not created).
        /// </summary>
        /// <exception cref="InvalidOperationException">Several items have this name.</exception>
        public MFilesValueListItem? FindItem(int valueListId, object? value, bool createMissing = false)
        {
            if (value is MFilesLookup lookup)
                value = lookup.Id;

            var text = ValueConverter.ToText(value)?.Trim();
            if (string.IsNullOrEmpty(text))
                return null;

            var index = Items(valueListId);
            var found = index.Find(text, ValueListName(valueListId));

            if (found != null || !createMissing)
                return found;

            var list = FindValueList(valueListId);
            if (list is { IsObjectType: true })
                throw new MFilesValueNotFoundException($"L'objet '{text}' est introuvable dans le type d'objet '{list.Name}' (les objets ne peuvent pas être créés comme éléments de liste).");

            // One creation per name even when several entities need the same missing item at the same time.
            lock (index.CreationLock)
            {
                found = index.Find(text, ValueListName(valueListId));
                if (found != null)
                    return found;

                var created = Gateway.AddValueListItem(valueListId, text);
                index.Add(created);
                return created;
            }
        }

        /// <summary>Same as <see cref="FindItem"/>, throws when the item does not exist.</summary>
        public MFilesValueListItem RequireItem(int valueListId, object? value, bool createMissing = false)
            => FindItem(valueListId, value, createMissing)
               ?? throw new MFilesValueNotFoundException($"L'élément '{ValueConverter.ToText(value)}' est introuvable dans la liste de valeurs '{ValueListName(valueListId)}'.");

        // ---------------------------------------------------------------- users

        /// <summary>User account by login (case-insensitive, the domain being optional on either side); null when not found.</summary>
        public MFilesUserAccount? FindUser(string? login)
        {
            if (string.IsNullOrWhiteSpace(login))
                return null;

            var users = _users.Value;
            var wanted = login.Trim();

            var exact = users.Where(u => string.Equals(u.LoginName, wanted, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count == 1)
                return exact[0];

            var withoutDomain = users.Where(u => string.Equals(StripDomain(u.LoginName), StripDomain(wanted), StringComparison.OrdinalIgnoreCase)).ToList();
            return withoutDomain.Count == 1 ? withoutDomain[0] : null;
        }

        public MFilesUserAccount? FindUser(int id)
            => _users.Value.FirstOrDefault(u => u.Id == id);

        // ---------------------------------------------------------------- conversions

        /// <summary>
        /// Converts an entity value into the typed value of a property. The lookups accept an item name, a display ID or an ID
        /// (several for a multi-select lookup: list, JSON array or "a;b").
        /// </summary>
        /// <exception cref="FormatException">The value can't be converted into the type of the property.</exception>
        /// <exception cref="MFilesValueNotFoundException">A referenced item does not exist (and was not created).</exception>
        public MFilesPropertyValue ToPropertyValue(MFilesPropertyDefInfo property, object? value, bool createMissingItems = false)
        {
            if (ValueConverter.IsEmpty(value))
                return new MFilesPropertyValue(property.Id, property.DataType, null);

            object? converted = property.DataType switch
            {
                MFilesDataType.Text or MFilesDataType.MultiLineText => ValueConverter.ToText(value),
                MFilesDataType.Integer => ValueConverter.ToInt64(value) is { } i && i is >= int.MinValue and <= int.MaxValue ? i : Invalid(property, value),
                MFilesDataType.Integer64 => ValueConverter.ToInt64(value) ?? Invalid(property, value),
                MFilesDataType.Floating => ValueConverter.ToDecimal(value) is { } d ? (double)d : Invalid(property, value),
                MFilesDataType.Boolean => ValueConverter.ToBoolean(value) ?? Invalid(property, value),
                MFilesDataType.Date => ValueConverter.ToDateTime(value)?.Date ?? Invalid(property, value),
                MFilesDataType.Time => ToTime(value) ?? Invalid(property, value),
                MFilesDataType.Timestamp => ValueConverter.ToDateTime(value) ?? Invalid(property, value),
                MFilesDataType.Lookup => ToLookup(property, value is string or MFilesLookup ? value : ValueConverter.ToList(value) is [var single] ? single : Invalid(property, value), createMissingItems),
                MFilesDataType.MultiSelectLookup => ValueConverter.ToList(value)
                    .Where(v => !ValueConverter.IsEmpty(v))
                    .Select(v => ToLookup(property, v, createMissingItems))
                    .DistinctBy(l => l.Id)
                    .ToList(),
                _ => throw new FormatException($"Le type de la propriété M-Files '{property.Name}' ({property.DataType}) n'est pas pris en charge."),
            };

            return new MFilesPropertyValue(property.Id, property.DataType, converted is List<MFilesLookup> { Count: 0 } ? null : converted);
        }

        /// <summary>Converts a property value read in M-Files into an entity value (lookups as names or IDs).</summary>
        public static object? ToEntityValue(MFilesPropertyValue? value, bool lookupsAsIds)
            => value?.Value switch
            {
                null => null,
                MFilesLookup lookup => lookupsAsIds ? (object)(long)lookup.Id : lookup.Name,
                IEnumerable<MFilesLookup> lookups => lookups.Select(l => lookupsAsIds ? (object?)(long)l.Id : l.Name).ToList(),
                var other => other,
            };

        /// <summary>True when two values of a property are equal for M-Files (used to write only the changed properties).</summary>
        public static bool SameValue(MFilesPropertyValue? current, MFilesPropertyValue desired)
            => Canonical(current?.Value, desired.DataType) == Canonical(desired.Value, desired.DataType);

        private static string Canonical(object? value, MFilesDataType dataType)
            => value switch
            {
                null => string.Empty,
                string s => dataType is MFilesDataType.Text or MFilesDataType.MultiLineText ? s.TrimEnd() : s,
                MFilesLookup lookup => lookup.Id.ToString(CultureInfo.InvariantCulture),
                IEnumerable<MFilesLookup> lookups => string.Join(",", lookups.Select(l => l.Id).Order()),
                DateTime dt => dataType switch
                {
                    MFilesDataType.Date => dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    MFilesDataType.Time => dt.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                    _ => dt.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
                },
                double d => Math.Round(d, 10).ToString("R", CultureInfo.InvariantCulture),
                bool b => b ? "1" : "0",
                _ => ValueConverter.ToText(value) ?? string.Empty,
            };

        // ---------------------------------------------------------------- lifetime

        /// <summary>Disconnects, a failure being reported as a warning (it does not change the result of the job).</summary>
        public void Close(JobContext context, string module)
        {
            try
            {
                Gateway.Disconnect();
            }
            catch (Exception ex)
            {
                context.LogWarning($"[{module}] Erreur à la déconnexion du coffre {VaultName} : {MFilesErrors.ReadableMessage(ex)}");
            }
            finally
            {
                Gateway.Dispose();
            }
        }

        public void Dispose()
            => Gateway.Dispose();

        // ---------------------------------------------------------------- helpers

        private ValueListIndex Items(int valueListId)
            => _items.GetOrAdd(valueListId, id => new Cached<ValueListIndex>(() => new ValueListIndex(Gateway.GetValueListItems(id)))).Value;

        private string ValueListName(int valueListId)
            => FindValueList(valueListId)?.Name ?? valueListId.ToString(CultureInfo.InvariantCulture);

        private MFilesLookup ToLookup(MFilesPropertyDefInfo property, object? value, bool createMissing)
        {
            if (property.ValueListId is not { } valueListId)
                throw new FormatException($"La propriété M-Files '{property.Name}' n'est liée à aucune liste de valeurs.");

            var item = FindItem(valueListId, value, createMissing)
                ?? throw new MFilesValueNotFoundException($"La valeur '{ValueConverter.ToText(value)}' de la propriété '{property.Name}' est introuvable dans la liste de valeurs '{ValueListName(valueListId)}'.");

            return new MFilesLookup(item.Id, item.Name);
        }

        // Zero of the OLE Automation dates: DateTime.MinValue can't be marshalled to a COM DATE.
        private static readonly DateTime TimeBase = new(1899, 12, 30);

        private static DateTime? ToTime(object? value)
            => value switch
            {
                TimeSpan ts => TimeBase.Add(ts),
                _ => ValueConverter.ToDateTime(value) is { } dt ? TimeBase.Add(dt.TimeOfDay) : null,
            };

        private static object Invalid(MFilesPropertyDefInfo property, object? value)
            => throw new FormatException($"La valeur '{ValueConverter.ToText(value)}' n'est pas valide pour la propriété M-Files '{property.Name}' ({property.DataType}).");

        private static T? UniqueByName<T>(IEnumerable<T> items, Func<T, string> name, string wanted) where T : class
        {
            var candidates = items.Where(i => string.Equals(name(i), wanted, StringComparison.OrdinalIgnoreCase)).ToList();

            if (candidates.Count == 1)
                return candidates[0];

            var exact = candidates.Where(i => string.Equals(name(i), wanted, StringComparison.Ordinal)).ToList();
            return exact.Count == 1 ? exact[0] : null;
        }

        private static string StripDomain(string login)
            => login[(login.LastIndexOfAny(['\\', '/']) + 1)..].Split('@')[0];

        private static string NormalizeGuid(string value)
            => value.Trim().Trim('{', '}').ToUpperInvariant();

        /// <summary>Lazy value whose failed loads are not cached (unlike <see cref="Lazy{T}"/>).</summary>
        private sealed class Cached<T>(Func<T> factory) where T : class
        {
            private readonly Lock _lock = new();
            private T? _value;

            public T Value
            {
                get
                {
                    var value = Volatile.Read(ref _value);
                    if (value != null)
                        return value;

                    lock (_lock)
                    {
                        if (_value == null)
                            Volatile.Write(ref _value, factory());

                        return _value!;
                    }
                }
            }
        }

        private sealed class PropertyIndex(IReadOnlyList<MFilesPropertyDefInfo> all)
        {
            public IReadOnlyList<MFilesPropertyDefInfo> All { get; } = all;
            public IReadOnlyDictionary<int, MFilesPropertyDefInfo> ById { get; } = all.GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First());
        }

        /// <summary>Items of one value list, indexed by name, display ID and ID; thread-safe.</summary>
        private sealed class ValueListIndex
        {
            private readonly Lock _lock = new();
            private readonly List<MFilesValueListItem> _items = [];
            private readonly Dictionary<int, MFilesValueListItem> _byId = [];
            private readonly Dictionary<string, List<MFilesValueListItem>> _byName = new(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, MFilesValueListItem> _byDisplayId = new(StringComparer.OrdinalIgnoreCase);

            public ValueListIndex(IEnumerable<MFilesValueListItem> items)
            {
                foreach (var item in items)
                    AddUnsafe(item);
            }

            /// <summary>Serialises the creation of the missing items (distinct from the read lock).</summary>
            public Lock CreationLock { get; } = new();

            public MFilesValueListItem? Find(string text, string valueListName)
            {
                lock (_lock)
                {
                    if (_byName.TryGetValue(text, out var candidates))
                    {
                        if (candidates.Count == 1)
                            return candidates[0];

                        var exact = candidates.Where(c => string.Equals(c.Name, text, StringComparison.Ordinal)).ToList();
                        if (exact.Count == 1)
                            return exact[0];

                        throw new InvalidOperationException($"Plusieurs éléments de la liste de valeurs '{valueListName}' s'appellent '{text}' (IDs {string.Join(", ", candidates.Select(c => c.Id))}) : utilisez l'ID.");
                    }

                    if (_byDisplayId.TryGetValue(text, out var byDisplayId))
                        return byDisplayId;

                    return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? _byId.GetValueOrDefault(id) : null;
                }
            }

            public void Add(MFilesValueListItem item)
            {
                lock (_lock)
                    AddUnsafe(item);
            }

            public IReadOnlyList<MFilesValueListItem> Snapshot()
            {
                lock (_lock)
                    return [.. _items];
            }

            private void AddUnsafe(MFilesValueListItem item)
            {
                _items.Add(item);
                _byId[item.Id] = item;

                if (!_byName.TryGetValue(item.Name, out var list))
                    _byName[item.Name] = list = [];
                list.Add(item);

                if (!string.IsNullOrEmpty(item.DisplayId))
                    _byDisplayId.TryAdd(item.DisplayId, item);
            }
        }
    }
}
