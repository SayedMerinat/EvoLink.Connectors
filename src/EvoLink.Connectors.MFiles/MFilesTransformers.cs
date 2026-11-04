using System.Collections;
using System.Collections.Concurrent;
using System.ComponentModel;
using EvoLink.Connectors.Common;
using EvoLink.Connectors.MFiles.Gateway;
using Newtonsoft.Json.Linq;
using Ortolan.Core.Models;
using Ortolan.Core.Modules;
using Ortolan.Core.Pipeline;
using Ortolan.Core.Security;
using Ortolan.Core.Serialization;

namespace EvoLink.Connectors.MFiles
{
    /// <summary>
    /// Base of the transformers reading the vault: one session per run of the module (opened before the first entity, closed at
    /// the end even on failure). A lost connection is a source unavailability.
    /// </summary>
    public abstract class MFilesTransformerModule : TransformerModule, IMFilesConnectionOptions
    {
        private MFilesSession? _session;

        [Description("Adresse du serveur M-Files.")]
        public string Server { get; set; } = "localhost";

        [Description("Protocole de connexion (ncacn_ip_tcp, ncacn_http, ncacn_spnego, grpc...).")]
        public string Protocol { get; set; } = MFilesConnectionSettings.DefaultProtocol;

        [Description("Port du serveur M-Files.")]
        public int Port { get; set; } = MFilesConnectionSettings.DefaultPort;

        [Description("Nom ou GUID du coffre.")]
        public string Vault { get; set; } = string.Empty;

        [Description("Authentification : CurrentWindowsUser (compte du service), SpecificWindowsUser ou MFilesUser.")]
        public MFilesAuthType AuthType { get; set; } = MFilesAuthType.CurrentWindowsUser;

        [Description("Utilisateur (authentification SpecificWindowsUser ou MFilesUser).")]
        public string? Username { get; set; }

        [Sensitive]
        [Description("Mot de passe de l'utilisateur.")]
        public string? Password { get; set; }

        [Description("Domaine Windows (authentification SpecificWindowsUser).")]
        public string? Domain { get; set; }

        protected MFilesSession Session => _session ?? throw new InvalidOperationException("La connexion au coffre M-Files n'est pas ouverte.");

        public override async Task<List<Entity>> ActAsync(List<Entity> input, JobContext context)
        {
            ValidateConfiguration();

            if (!input.Any(ShouldProcess))
                return input;

            _session = MFilesConnector.Open(DisplayName, this, CreateGateway, Unavailable, context);

            try
            {
                MFilesConnector.Call(() => Prepare(_session, context), Unavailable);
                return await base.ActAsync(input, context);
            }
            finally
            {
                _session.Close(context, DisplayName);
                _session = null;
            }
        }

        /// <summary>Creates the gateway to the vault (overridden by the tests).</summary>
        protected virtual IMFilesGateway CreateGateway(MFilesConnectionSettings settings)
            => new ComMFilesGateway(settings);

        /// <summary>Checks the required properties (configuration error, before any connection).</summary>
        protected abstract void ValidateConfiguration();

        /// <summary>Resolves the configured classes / properties / value lists once the vault is open.</summary>
        protected abstract void Prepare(MFilesSession session, JobContext context);

        protected override Exception? TranslateAccessFailure(Exception exception)
            => MFilesErrors.IsConnectionFailure(exception) ? Unavailable(exception) : null;

        /// <summary>
        /// Business "not found": failure of the entity when <paramref name="fail"/> is true, otherwise a warning, and
        /// <paramref name="clear"/> (the property that should have received the result) emptied when given.
        /// </summary>
        protected Entity NotFound(Entity entity, string message, bool fail, JobContext context, string? clear = null)
        {
            if (fail)
            {
                context.LogWarning($"[{DisplayName}] {message}");
                throw new MFilesValueNotFoundException(message);
            }

            if (clear != null)
            {
                entity[clear] = null;
                message += $" Propriété '{clear}' vidée.";
            }

            context.LogWarning($"[{DisplayName}] {message}");
            return entity;
        }

        private Exception Unavailable(Exception ex)
            => ConnectorGuard.SourceUnavailable(DisplayName, $"M-Files {MFilesConnectionSettings.From(this).Display}", ex);
    }

    /// <summary>
    /// Searches in a class the object whose <see cref="MatchField"/> equals a property of the entity, then writes its ID and the
    /// login of the user account referenced by <see cref="UserField"/> into the entity (generalisation of the EvoBus
    /// MFilesExternalUserSearchTransformer).
    /// </summary>
    [OrtolanType("mfiles-user-lookup")]
    [Description("Recherche dans une classe M-Files l'objet dont un champ vaut une propriété de l'entité, puis écrit son ID et le login du compte utilisateur lié.")]
    public class MFilesUserLookup : MFilesTransformerModule
    {
        private const int UsersValueList = 6;

        private readonly ConcurrentDictionary<string, LookupResult> _results = new(StringComparer.Ordinal);
        private MFilesClassInfo? _class;
        private MFilesPropertyDefInfo? _match;
        private MFilesPropertyDefInfo? _user;

        [Description("Classe des objets recherchés : nom, alias ou ID.")]
        public string Class { get; set; } = string.Empty;

        [Description("Propriété de l'entité contenant la valeur recherchée.")]
        public string SourceProperty { get; set; } = string.Empty;

        [Description("Propriété M-Files comparée à la valeur (nom, alias ou ID).")]
        public string MatchField { get; set; } = string.Empty;

        [Description("Propriété M-Files de l'objet trouvé désignant le compte utilisateur (nom, alias ou ID). Vide = seul l'ID de l'objet est écrit.")]
        public string? UserField { get; set; }

        [Description("Propriété de l'entité recevant l'ID de l'objet trouvé (vide = la propriété source est remplacée, comme EvoBus).")]
        public string? IdProperty { get; set; }

        [Description("Propriétés de l'entité recevant le login du compte utilisateur.")]
        public List<string> LoginProperties { get; set; } = [MFilesKeys.CreatedBy, MFilesKeys.LastModifiedBy];

        [Description("Met l'entité en échec lorsqu'aucun objet (ou plusieurs) ou aucun compte n'est trouvé (sinon simple avertissement).")]
        public bool FailWhenNotFound { get; set; }

        [Description("Vide la propriété qui devait recevoir l'ID lorsque rien n'est trouvé, au lieu d'y laisser la valeur recherchée (l'entité est alors écrite sans cette valeur).")]
        public bool ClearWhenNotFound { get; set; }

        private string Destination => string.IsNullOrWhiteSpace(IdProperty) ? SourceProperty : IdProperty;

        protected override void ValidateConfiguration()
            => ConnectorGuard.Required(DisplayName, ("vault", Vault), ("class", Class), ("sourceProperty", SourceProperty), ("matchField", MatchField));

        protected override void Prepare(MFilesSession session, JobContext context)
        {
            _results.Clear();
            _class = session.FindClass(Class) ?? throw ConnectorGuard.Invalid(DisplayName, $"Classe M-Files '{Class}' introuvable dans le coffre {session.VaultName}.");
            _match = MFilesFieldMap.RequireProperty(DisplayName, session, MatchField, "matchField");
            _user = string.IsNullOrWhiteSpace(UserField) ? null : MFilesFieldMap.RequireProperty(DisplayName, session, UserField, "userField");

            if (_user is { IsLookup: false } or { ValueListId: not UsersValueList })
                throw ConnectorGuard.Invalid(DisplayName, $"La propriété M-Files '{_user.Name}' (userField) ne désigne pas un compte utilisateur.");
        }

        public override Task<Entity?> TransformAsync(Entity entity, JobContext context)
        {
            var value = ValueConverter.ToText(entity.GetValueOrDefault(SourceProperty))?.Trim();

            if (string.IsNullOrEmpty(value))
            {
                context.LogDebug($"[{DisplayName}] Propriété '{SourceProperty}' vide, entité inchangée.");
                return Task.FromResult<Entity?>(entity);
            }

            var result = _results.GetOrAdd(value, Search);

            if (result.Error != null)
                return Task.FromResult<Entity?>(NotFound(entity, result.Error, FailWhenNotFound, context, ClearWhenNotFound ? Destination : null));

            // Without its user account, the object found is not usable: the entity is left untouched.
            if (_user != null && result.Login == null)
                return Task.FromResult<Entity?>(NotFound(entity, result.UserError!, FailWhenNotFound, context, ClearWhenNotFound ? Destination : null));

            entity[Destination] = (long)result.ObjectId;

            if (result.Login != null)
            {
                foreach (var property in LoginProperties.Where(p => !string.IsNullOrWhiteSpace(p)))
                    entity[property] = result.Login;
            }

            return Task.FromResult<Entity?>(entity);
        }

        private LookupResult Search(string value)
        {
            MFilesPropertyValue condition;

            try
            {
                condition = Session.ToPropertyValue(_match!, value);
            }
            catch (MFilesValueNotFoundException)
            {
                // The value does not even exist in the value list of the compared property: no object can match.
                return LookupResult.Failed($"Aucun objet '{_class!.Name}' dont '{_match!.Name}' vaut '{value}'.");
            }

            var objects = Session.Gateway.Search(new MFilesSearch(_class!.Id, [condition], MaxResults: 2));

            if (objects.Count == 0)
                return LookupResult.Failed($"Aucun objet '{_class.Name}' dont '{_match!.Name}' vaut '{value}'.");

            if (objects.Count > 1)
                return LookupResult.Failed($"Plusieurs objets '{_class.Name}' dont '{_match!.Name}' vaut '{value}' : la correspondance est ambiguë.");

            var found = objects[0];

            if (_user == null)
                return new LookupResult(found.Ref.Id, null, null, null);

            var userValue = found.Properties.GetValueOrDefault(_user.Id)?.Value;
            var userId = userValue switch
            {
                MFilesLookup lookup => lookup.Id,
                IEnumerable<MFilesLookup> lookups => lookups.Select(l => (int?)l.Id).FirstOrDefault(),
                _ => null,
            };

            if (userId == null)
                return new LookupResult(found.Ref.Id, null, null, $"L'objet {found.Ref.Id} ('{value}') n'a pas de valeur pour '{_user.Name}'.");

            var account = Session.FindUser(userId.Value);
            return account == null
                ? new LookupResult(found.Ref.Id, null, null, $"Le compte utilisateur {userId} de l'objet {found.Ref.Id} ('{value}') est introuvable.")
                : new LookupResult(found.Ref.Id, account.LoginName, null, null);
        }

        private sealed record LookupResult(int ObjectId, string? Login, string? Error, string? UserError)
        {
            public static LookupResult Failed(string error) => new(0, null, error, null);
        }
    }

    /// <summary>
    /// Replaces the name of a value list item by its ID (or several names by their IDs for a multi-select lookup).
    /// </summary>
    [OrtolanType("mfiles-value-lookup")]
    [Description("Remplace le nom d'un élément de liste de valeurs M-Files par son ID (plusieurs noms : liste d'ID).")]
    public class MFilesValueLookup : MFilesTransformerModule
    {
        private int _valueListId;
        private string _valueListName = string.Empty;

        [Description("Liste de valeurs : nom, alias ou ID (ou utilisez 'property').")]
        public string? ValueList { get; set; }

        [Description("Propriété M-Files dont la liste de valeurs est utilisée (nom, alias ou ID), si 'valueList' est vide.")]
        public string? Property { get; set; }

        [Description("Propriété de l'entité contenant le nom (ou le code / l'ID) de l'élément.")]
        public string SourceProperty { get; set; } = string.Empty;

        [Description("Propriété de l'entité recevant l'ID (vide = la propriété source est remplacée).")]
        public string? TargetProperty { get; set; }

        [Description("Séparateur de plusieurs noms dans une valeur texte (vide = la valeur est un seul nom).")]
        public string? Separator { get; set; }

        [Description("Crée les éléments manquants (impossible pour les listes d'objets).")]
        public bool CreateMissing { get; set; }

        [Description("Met l'entité en échec lorsqu'un élément est introuvable (sinon avertissement et entité inchangée).")]
        public bool FailWhenNotFound { get; set; }

        [Description("Vide la propriété qui devait recevoir l'ID lorsqu'un élément est introuvable, au lieu d'y laisser le nom recherché.")]
        public bool ClearWhenNotFound { get; set; }

        protected override void ValidateConfiguration()
        {
            ConnectorGuard.Required(DisplayName, ("vault", Vault), ("sourceProperty", SourceProperty));

            if (string.IsNullOrWhiteSpace(ValueList) && string.IsNullOrWhiteSpace(Property))
                throw ConnectorGuard.Invalid(DisplayName, "Indiquez la liste de valeurs ('valueList') ou la propriété M-Files ('property').");

            if (Separator is { Length: > 1 })
                throw ConnectorGuard.Invalid(DisplayName, "Le séparateur ('separator') doit être un seul caractère.");
        }

        protected override void Prepare(MFilesSession session, JobContext context)
        {
            if (!string.IsNullOrWhiteSpace(ValueList))
            {
                var list = session.FindValueList(ValueList)
                    ?? throw ConnectorGuard.Invalid(DisplayName, $"Liste de valeurs M-Files '{ValueList}' introuvable dans le coffre {session.VaultName}.");
                _valueListId = list.Id;
                _valueListName = list.Name;
                return;
            }

            var property = MFilesFieldMap.RequireProperty(DisplayName, session, Property!, "property");
            _valueListId = property.ValueListId ?? throw ConnectorGuard.Invalid(DisplayName, $"La propriété M-Files '{property.Name}' n'est liée à aucune liste de valeurs.");
            _valueListName = session.FindValueList(_valueListId)?.Name ?? property.Name;
        }

        public override Task<Entity?> TransformAsync(Entity entity, JobContext context)
        {
            var value = entity.GetValueOrDefault(SourceProperty);

            if (ValueConverter.IsEmpty(value))
            {
                context.LogDebug($"[{DisplayName}] Propriété '{SourceProperty}' vide, entité inchangée.");
                return Task.FromResult<Entity?>(entity);
            }

            var multiple = value is JArray || (value is IEnumerable and not string) || (!string.IsNullOrEmpty(Separator) && value is string);
            var names = multiple ? ValueConverter.ToList(value, string.IsNullOrEmpty(Separator) ? ';' : Separator[0]) : [value];
            var ids = new List<object?>();
            var missing = new List<string>();

            foreach (var name in names.Where(n => !ValueConverter.IsEmpty(n)))
            {
                if (Session.FindItem(_valueListId, name, CreateMissing) is { } item)
                    ids.Add((long)item.Id);
                else
                    missing.Add(ValueConverter.ToText(name) ?? string.Empty);
            }

            if (missing.Count > 0)
                return Task.FromResult<Entity?>(NotFound(entity, $"Élément(s) {string.Join(", ", missing.Select(m => $"'{m}'"))} introuvable(s) dans la liste de valeurs '{_valueListName}'.", FailWhenNotFound, context,
                    ClearWhenNotFound ? (string.IsNullOrWhiteSpace(TargetProperty) ? SourceProperty : TargetProperty) : null));

            entity[string.IsNullOrWhiteSpace(TargetProperty) ? SourceProperty : TargetProperty] = multiple ? ids : ids.FirstOrDefault();
            return Task.FromResult<Entity?>(entity);
        }
    }

    /// <summary>
    /// Searches in a class the object whose <see cref="MFilesUserLookup.MatchField"/> equals a property of the entity and writes its ID
    /// (native equivalent of the ObjectLookupTransformer of EvoBus).
    /// </summary>
    [OrtolanType("mfiles-object-lookup")]
    [Description("Recherche dans une classe M-Files l'objet dont un champ vaut une propriété de l'entité et écrit son ID (équivalent d'ObjectLookupTransformer d'EvoBus).")]
    public class MFilesObjectLookup : MFilesUserLookup
    {
        protected override void ValidateConfiguration()
        {
            base.ValidateConfiguration();

            if (!string.IsNullOrWhiteSpace(UserField))
                throw ConnectorGuard.Invalid(DisplayName, "userField n'est pas utilisé par mfiles-object-lookup : utilisez mfiles-user-lookup pour lire un compte utilisateur.");
        }
    }
}
