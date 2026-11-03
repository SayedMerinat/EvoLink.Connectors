using System.Globalization;
using System.Runtime.InteropServices;
using MFilesAPI;

namespace EvoLink.Connectors.MFiles.Gateway
{
    /// <summary>
    /// Gateway based on the COM API of M-Files (<c>Interop.MFilesAPI</c>, the M-Files client must be installed).
    /// Every COM failure is classified: lost connection → <see cref="MFilesConnectionException"/>, refused operation →
    /// <see cref="MFilesOperationException"/> carrying the readable part of the M-Files message.
    /// </summary>
    public sealed class ComMFilesGateway : IMFilesGateway
    {
        private const int SegmentSize = 1000;
        private const int PropertiesBatchSize = 500;
        private const int ValueListBatchSize = 5000;
        private const int BuiltInClassPropertyDef = (int)MFBuiltInPropertyDef.MFBuiltInPropertyDefClass;

        private readonly MFilesConnectionSettings _settings;
        private MFilesServerApplication? _server;
        private Vault? _vault;

        public ComMFilesGateway(MFilesConnectionSettings settings)
        {
            _settings = settings;
        }

        public string VaultName => _settings.Display;

        private Vault Vault => _vault ?? throw new InvalidOperationException("La connexion au coffre M-Files n'est pas ouverte.");

        public void Connect()
        {
            if (_vault != null)
                return;

            try
            {
                _server = new MFilesServerApplication();
            }
            catch (COMException ex) when (ex.HResult == MFilesErrors.ClassNotRegistered)
            {
                throw new MFilesConnectionException("Le client M-Files (API COM) n'est pas installé sur cette machine.", ex);
            }

            try
            {
                var user = _settings.AuthType == MFilesAuthType.CurrentWindowsUser ? string.Empty : _settings.Username ?? string.Empty;
                var password = _settings.AuthType == MFilesAuthType.CurrentWindowsUser ? string.Empty : _settings.Password ?? string.Empty;
                var domain = _settings.AuthType == MFilesAuthType.SpecificWindowsUser ? _settings.Domain ?? string.Empty : string.Empty;

                _server.Connect(
                    AuthType: ToAuthType(_settings.AuthType),
                    UserName: user,
                    Password: password,
                    Domain: domain,
                    ProtocolSequence: _settings.Protocol,
                    NetworkAddress: _settings.Server,
                    Endpoint: _settings.Port.ToString(CultureInfo.InvariantCulture));

                var wanted = NormalizeGuid(_settings.Vault);
                var vault = _server.GetVaults().Cast<VaultOnServer>().FirstOrDefault(v =>
                    string.Equals(v.Name, _settings.Vault, StringComparison.OrdinalIgnoreCase) || NormalizeGuid(v.GUID) == wanted);

                if (vault == null)
                    throw new MFilesConnectionException($"Le coffre '{_settings.Vault}' est introuvable (ou hors ligne) sur le serveur '{_settings.Server}'.");

                _vault = _server.LogInToVault(vault.GUID);
            }
            catch (MFilesConnectionException)
            {
                Release();
                throw;
            }
            catch (Exception ex)
            {
                // Every failure of the connection phase (server stopped, authentication refused, protocol...) is an unavailability.
                Release();
                throw new MFilesConnectionException($"Connexion au coffre M-Files {_settings.Display} impossible : {MFilesErrors.ReadableMessage(ex)}", ex);
            }
        }

        public void Disconnect()
        {
            try
            {
                if (_vault != null)
                    Invoke(() => _vault.LogOutSilent());

                if (_server != null)
                    Invoke(() => _server.Disconnect());
            }
            finally
            {
                _vault = null;
                _server = null;
            }
        }

        public void Dispose()
            => Release();

        public IReadOnlyList<MFilesClassInfo> GetClasses()
            => Invoke(() => Vault.ClassOperations.GetAllObjectClasses().Cast<ObjectClass>()
                .Select(c => new MFilesClassInfo(c.ID, c.Name, c.ObjectType, c.NamePropertyDef))
                .ToList());

        public int? FindClassIdByAlias(string alias)
            => Invoke(() => Positive(Vault.ClassOperations.GetObjectClassIDByAlias(alias)));

        public IReadOnlyList<MFilesPropertyDefInfo> GetPropertyDefs()
            => Invoke(() => Vault.PropertyDefOperations.GetPropertyDefs().Cast<PropertyDef>()
                .Select(p => new MFilesPropertyDefInfo(p.ID, p.Name, (MFilesDataType)(int)p.DataType, p.BasedOnValueList ? p.ValueList : null,
                    p.AutomaticValueType != MFAutomaticValueType.MFAutomaticValueTypeNone, p.GUID))
                .ToList());

        public int? FindPropertyDefIdByAlias(string alias)
            => Invoke(() => Positive(Vault.PropertyDefOperations.GetPropertyDefIDByAlias(alias)));

        public IReadOnlyList<MFilesValueListInfo> GetValueLists()
            => Invoke(() => Vault.ValueListOperations.GetValueLists().Cast<ObjType>()
                .Select(v => new MFilesValueListInfo(v.ID, v.NameSingular, v.NamePlural, v.RealObjectType))
                .ToList());

        public int? FindValueListIdByAlias(string alias)
            => Invoke(() => Positive(Vault.ValueListOperations.GetValueListIDByAlias(alias)));

        public IReadOnlyList<MFilesValueListItem> GetValueListItems(int valueListId)
            => Invoke(() =>
            {
                var items = new List<MFilesValueListItem>();
                uint lastId = 0;

                while (true)
                {
                    var batch = Vault.ValueListItemOperations.GetValueListItemsInIdOrder(valueListId, lastId, ValueListBatchSize).Cast<ValueListItem>().ToList();
                    if (batch.Count == 0)
                        break;

                    items.AddRange(batch.Where(i => !i.Deleted).Select(ToItem));
                    lastId = (uint)batch[^1].ID;

                    if (batch.Count < ValueListBatchSize)
                        break;
                }

                return items;
            });

        public MFilesValueListItem AddValueListItem(int valueListId, string name)
            => Invoke(() =>
            {
                var item = new ValueListItem { ValueListID = valueListId, Name = name };
                return ToItem(Vault.ValueListItemOperations.AddValueListItem(valueListId, item));
            });

        public IReadOnlyList<MFilesObject> Search(MFilesSearch search)
            => Invoke(() =>
            {
                var versions = search.MaxResults > 0
                    ? Vault.ObjectSearchOperations.SearchForObjectsByConditionsEx(BaseConditions(search), MFSearchFlags.MFSearchFlagNone, false, search.MaxResults)
                        .Cast<ObjectVersion>().ToList()
                    : SearchSegmented(search);

                return ReadObjects(versions);
            });

        public MFilesObject Create(MFilesCreateRequest request)
            => Invoke(() =>
            {
                var values = ToPropertyValues(request.Values);
                values.Add(-1, Property(BuiltInClassPropertyDef, MFilesDataType.Lookup, new MFilesLookup(request.ClassId)));

                var files = new SourceObjectFiles();
                foreach (var file in request.AllFiles)
                    files.AddFile(file.Title, file.Extension, file.Path);

                var checkInNow = request.Audit == null || request.Audit.IsEmpty;
                var created = Vault.ObjectOperations.CreateNewObjectEx(request.ObjectType, values, files, SFD: files.Count == 1, CheckIn: checkInNow);

                if (checkInNow)
                    return ToObject(created.ObjVer, created.Properties);

                var objVer = created.ObjVer;

                try
                {
                    objVer = ApplyAudit(objVer, request.Audit!);
                    var checkedIn = Vault.ObjectOperations.CheckIn(objVer);
                    return ToObject(checkedIn.ObjVer, Vault.ObjectPropertyOperations.GetProperties(checkedIn.ObjVer));
                }
                catch (Exception ex)
                {
                    // The object exists (checked out): checking it in keeps it, the failure of the audit info is reported.
                    CheckInAfterFailure(objVer, ex);
                    throw;
                }
            });

        public MFilesObject Update(MFilesObjectRef target, IReadOnlyList<MFilesPropertyValue> values, MFilesAuditInfo? audit)
            => Invoke(() =>
            {
                var checkedOut = Vault.ObjectOperations.CheckOut(ObjId(target));
                var objVer = checkedOut.ObjVer;

                try
                {
                    if (values.Count > 0)
                        objVer = Vault.ObjectPropertyOperations.SetProperties(objVer, ToPropertyValues(values)).ObjVer;

                    if (audit is { HasModificationInfo: true })
                        objVer = ApplyAudit(objVer, audit with { CreatedBy = null, CreatedUtc = null });

                    var checkedIn = Vault.ObjectOperations.CheckIn(objVer);
                    return ToObject(checkedIn.ObjVer, Vault.ObjectPropertyOperations.GetProperties(checkedIn.ObjVer));
                }
                catch (Exception ex)
                {
                    UndoCheckOut(objVer, ex);
                    throw;
                }
            });

        public void Delete(MFilesObjectRef target, bool destroy)
            => Invoke(() =>
            {
                if (destroy)
                    Vault.ObjectOperations.DestroyObject(ObjId(target), true, -1);
                else
                    Vault.ObjectOperations.RemoveObject(ObjId(target));

                return true;
            });

        public IReadOnlyList<MFilesUserAccount> GetUserAccounts()
            => Invoke(() => Vault.UserOperations.GetUserAccounts().Cast<UserAccount>()
                .Select(u => new MFilesUserAccount(u.ID, u.LoginName))
                .ToList());

        // ---------------------------------------------------------------- search

        private List<ObjectVersion> SearchSegmented(MFilesSearch search)
        {
            var results = new List<ObjectVersion>();

            for (var segment = 0; ; segment++)
            {
                var conditions = BaseConditions(search);
                var segmentCondition = new SearchCondition { ConditionType = MFConditionType.MFConditionTypeEqual };
                segmentCondition.Expression.SetObjectIDSegmentExpression(SegmentSize);
                segmentCondition.TypedValue.SetValue(MFDataType.MFDatatypeInteger, segment);
                conditions.Add(-1, segmentCondition);

                var found = Vault.ObjectSearchOperations.SearchForObjectsByConditionsEx(conditions, MFSearchFlags.MFSearchFlagNone, false, SegmentSize)
                    .Cast<ObjectVersion>().ToList();

                results.AddRange(found);

                if (found.Count == 0 && !HasObjectsFrom(search, (segment + 1) * SegmentSize))
                    break;
            }

            return results;
        }

        private bool HasObjectsFrom(MFilesSearch search, int minimumId)
        {
            var conditions = BaseConditions(search);
            var condition = new SearchCondition { ConditionType = MFConditionType.MFConditionTypeGreaterThanOrEqual };
            condition.Expression.SetStatusValueExpression(MFStatusType.MFStatusTypeObjectID);
            condition.TypedValue.SetValue(MFDataType.MFDatatypeInteger, minimumId);
            conditions.Add(-1, condition);

            return Vault.ObjectSearchOperations.SearchForObjectsByConditionsEx(conditions, MFSearchFlags.MFSearchFlagNone, false, 1).Count > 0;
        }

        private static SearchConditions BaseConditions(MFilesSearch search)
        {
            var conditions = new SearchConditions();

            var deleted = new SearchCondition { ConditionType = MFConditionType.MFConditionTypeEqual };
            deleted.Expression.SetStatusValueExpression(MFStatusType.MFStatusTypeDeleted);
            deleted.TypedValue.SetValue(MFDataType.MFDatatypeBoolean, false);
            conditions.Add(-1, deleted);

            conditions.Add(-1, PropertyCondition(new MFilesPropertyValue(BuiltInClassPropertyDef, MFilesDataType.Lookup, new MFilesLookup(search.ClassId))));

            foreach (var condition in search.Conditions)
                conditions.Add(-1, PropertyCondition(condition));

            return conditions;
        }

        private static SearchCondition PropertyCondition(MFilesPropertyValue value)
        {
            var condition = new SearchCondition { ConditionType = MFConditionType.MFConditionTypeEqual };
            condition.Expression.SetPropertyValueExpression(value.PropertyDefId, MFParentChildBehavior.MFParentChildBehaviorNone);

            // A multi-select lookup "equals" one item when it contains it.
            if (value is { DataType: MFilesDataType.MultiSelectLookup, Value: IReadOnlyList<MFilesLookup> { Count: 1 } single })
                SetTypedValue(condition.TypedValue, MFilesDataType.Lookup, single[0]);
            else
                SetTypedValue(condition.TypedValue, value.DataType, value.Value);

            return condition;
        }

        private List<MFilesObject> ReadObjects(List<ObjectVersion> versions)
        {
            var objects = new List<MFilesObject>(versions.Count);

            foreach (var chunk in versions.Chunk(PropertiesBatchSize))
            {
                var objVers = new ObjVers();
                foreach (var version in chunk)
                    objVers.Add(-1, version.ObjVer);

                var properties = Vault.ObjectPropertyOperations.GetPropertiesOfMultipleObjects(objVers);

                for (var i = 0; i < chunk.Length; i++)
                    objects.Add(ToObject(chunk[i].ObjVer, properties[i + 1]));
            }

            return objects;
        }

        // ---------------------------------------------------------------- audit / check-out

        private ObjVer ApplyAudit(ObjVer objVer, MFilesAuditInfo audit)
        {
            if (audit.HasCreationInfo)
            {
                objVer = Vault.ObjectPropertyOperations.SetCreationInfoAdmin(objVer,
                    audit.CreatedBy != null, UserValue(audit.CreatedBy), audit.CreatedUtc != null, TimestampValue(audit.CreatedUtc)).ObjVer;
            }

            if (audit.HasModificationInfo)
            {
                objVer = Vault.ObjectPropertyOperations.SetLastModificationInfoAdmin(objVer,
                    audit.LastModifiedBy != null, UserValue(audit.LastModifiedBy), audit.LastModifiedUtc != null, TimestampValue(audit.LastModifiedUtc)).ObjVer;
            }

            return objVer;
        }

        private static TypedValue UserValue(int? userId)
        {
            var value = new TypedValue();
            if (userId != null)
                value.SetValue(MFDataType.MFDatatypeLookup, userId.Value);
            else
                value.SetValueToNULL(MFDataType.MFDatatypeLookup);
            return value;
        }

        private static TypedValue TimestampValue(DateTime? utc)
        {
            var value = new TypedValue();
            if (utc != null)
                value.SetValue(MFDataType.MFDatatypeTimestamp, utc.Value);
            else
                value.SetValueToNULL(MFDataType.MFDatatypeTimestamp);
            return value;
        }

        private void UndoCheckOut(ObjVer objVer, Exception original)
        {
            try
            {
                Vault.ObjectOperations.UndoCheckout(objVer);
            }
            catch (Exception undo)
            {
                throw new AggregateException($"Échec de la mise à jour, puis de l'annulation de l'extraction de l'objet {objVer.ID} : l'objet reste extrait.", original, undo);
            }
        }

        private void CheckInAfterFailure(ObjVer objVer, Exception original)
        {
            try
            {
                Vault.ObjectOperations.CheckIn(objVer);
            }
            catch (Exception checkIn)
            {
                throw new AggregateException($"L'objet {objVer.ID} a été créé mais reste extrait (échec de l'archivage).", original, checkIn);
            }
        }

        // ---------------------------------------------------------------- conversions

        private static MFilesObject ToObject(ObjVer objVer, PropertyValues values)
        {
            var properties = new Dictionary<int, MFilesPropertyValue>();
            var classId = -1;

            foreach (PropertyValue value in values)
            {
                var converted = FromTypedValue(value.PropertyDef, value.TypedValue);
                properties[value.PropertyDef] = converted;

                if (value.PropertyDef == BuiltInClassPropertyDef && converted.Value is MFilesLookup lookup)
                    classId = lookup.Id;
            }

            return new MFilesObject(new MFilesObjectRef(objVer.Type, objVer.ID, objVer.Version), classId, properties);
        }

        private static MFilesPropertyValue FromTypedValue(int propertyDef, TypedValue typedValue)
        {
            var dataType = (MFilesDataType)(int)typedValue.DataType;

            if (typedValue.IsNULL())
                return new MFilesPropertyValue(propertyDef, dataType, null);

            object? value = dataType switch
            {
                MFilesDataType.Lookup => ToLookup(typedValue.GetValueAsLookup()),
                MFilesDataType.MultiSelectLookup => typedValue.GetValueAsLookups().Cast<Lookup>().Select(ToLookup).ToList() is { Count: > 0 } list ? list : null,
                MFilesDataType.Text or MFilesDataType.MultiLineText => typedValue.Value as string ?? typedValue.GetValueAsUnlocalizedText(),
                MFilesDataType.Integer or MFilesDataType.Integer64 => Common.ValueConverter.ToInt64(typedValue.Value),
                MFilesDataType.Floating => typedValue.Value is double d ? d : (double?)Common.ValueConverter.ToDecimal(typedValue.Value),
                MFilesDataType.Boolean => Common.ValueConverter.ToBoolean(typedValue.Value),
                MFilesDataType.Date or MFilesDataType.Time or MFilesDataType.Timestamp => Common.ValueConverter.ToDateTime(typedValue.Value),
                _ => typedValue.GetValueAsUnlocalizedText(),
            };

            return new MFilesPropertyValue(propertyDef, dataType, value);
        }

        private static MFilesLookup ToLookup(Lookup lookup)
            => new(lookup.Item, lookup.DisplayValue);

        private static PropertyValues ToPropertyValues(IReadOnlyList<MFilesPropertyValue> values)
        {
            var result = new PropertyValues();

            foreach (var value in values)
                result.Add(-1, Property(value.PropertyDefId, value.DataType, value.Value));

            return result;
        }

        private static PropertyValue Property(int propertyDef, MFilesDataType dataType, object? value)
        {
            var property = new PropertyValue { PropertyDef = propertyDef };
            SetTypedValue(property.TypedValue, dataType, value);
            return property;
        }

        private static void SetTypedValue(TypedValue typedValue, MFilesDataType dataType, object? value)
        {
            var mfType = (MFDataType)(int)dataType;

            switch (value)
            {
                case null:
                    typedValue.SetValueToNULL(mfType);
                    break;

                case MFilesLookup lookup when dataType == MFilesDataType.MultiSelectLookup:
                    typedValue.SetValueToMultiSelectLookup(ToLookups([lookup]));
                    break;

                case MFilesLookup lookup:
                    typedValue.SetValueToLookup(new Lookup { Item = lookup.Id });
                    break;

                case IEnumerable<MFilesLookup> lookups:
                    typedValue.SetValueToMultiSelectLookup(ToLookups(lookups));
                    break;

                case long number when dataType == MFilesDataType.Integer:
                    typedValue.SetValue(mfType, checked((int)number));
                    break;

                default:
                    typedValue.SetValue(mfType, value);
                    break;
            }
        }

        private static Lookups ToLookups(IEnumerable<MFilesLookup> lookups)
        {
            var result = new Lookups();

            foreach (var lookup in lookups)
                result.Add(-1, new Lookup { Item = lookup.Id });

            return result;
        }

        private static MFilesValueListItem ToItem(ValueListItem item)
            => new(item.ID, item.Name, item.DisplayIDAvailable ? item.DisplayID : null);

        private static ObjID ObjId(MFilesObjectRef target)
        {
            var id = new ObjID();
            id.SetIDs(target.ObjectType, target.Id);
            return id;
        }

        private static MFAuthType ToAuthType(MFilesAuthType authType)
            => authType switch
            {
                MFilesAuthType.CurrentWindowsUser => MFAuthType.MFAuthTypeLoggedOnWindowsUser,
                MFilesAuthType.SpecificWindowsUser => MFAuthType.MFAuthTypeSpecificWindowsUser,
                MFilesAuthType.MFilesUser => MFAuthType.MFAuthTypeSpecificMFilesUser,
                _ => throw new ArgumentOutOfRangeException(nameof(authType), authType, "Type d'authentification M-Files inconnu."),
            };

        private static int? Positive(int id)
            => id >= 0 ? id : null;

        private static string NormalizeGuid(string? value)
            => (value ?? string.Empty).Trim().Trim('{', '}').ToUpperInvariant();

        /// <summary>Runs a COM call and classifies its failure.</summary>
        private static T Invoke<T>(Func<T> call)
        {
            try
            {
                return call();
            }
            catch (Exception ex) when (ex is MFilesConnectionException or MFilesOperationException or AggregateException)
            {
                throw;
            }
            catch (Exception ex) when (MFilesErrors.IsConnectionFailure(ex))
            {
                throw new MFilesConnectionException($"Connexion au coffre M-Files perdue : {MFilesErrors.ReadableMessage(ex)}", ex);
            }
            catch (COMException ex)
            {
                throw new MFilesOperationException(MFilesErrors.ReadableMessage(ex), ex);
            }
        }

        private static void Invoke(Action call)
            => Invoke(() => { call(); return true; });

        private void Release()
        {
            _vault = null;

            if (_server != null && Marshal.IsComObject(_server))
                Marshal.ReleaseComObject(_server);

            _server = null;
        }
    }
}
