using System.ComponentModel;
using EvoLink.Connectors.Common;
using EvoLink.Connectors.MFiles.Gateway;
using Ortolan.Core.Models;
using Ortolan.Core.Modules;
using Ortolan.Core.Pipeline;
using Ortolan.Core.Security;
using Ortolan.Core.Serialization;

namespace EvoLink.Connectors.MFiles
{
    /// <summary>
    /// Reads the objects of an M-Files class (latest versions, not deleted), optionally filtered by simple conditions
    /// (property = value). One entity per object, the properties being renamed through <see cref="Fields"/>.
    /// </summary>
    [OrtolanType("mfiles-provider")]
    [Description("Lit les objets d'une classe M-Files (une entité par objet), filtrés par des conditions simples propriété = valeur.")]
    public class MFilesProvider : ProviderModule, IMFilesConnectionOptions
    {
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

        [Description("Classe lue : nom, alias ou ID.")]
        public string Class { get; set; } = string.Empty;

        [Description("Conditions de recherche propriété = valeur (propriété : nom, alias ou ID ; listes de valeurs : nom ou ID de l'élément ; valeur vide = propriété vide).")]
        public Dictionary<string, string?> Conditions { get; set; } = [];

        [Description("Correspondance des champs : propriété de l'entité → propriété M-Files (nom, alias ou ID). Vide = noms des propriétés M-Files.")]
        public Dictionary<string, string> Fields { get; set; } = [];

        [Description("Ne lit que les propriétés présentes dans 'fields'.")]
        public bool OnlyMappedFields { get; set; }

        [Description("Propriétés M-Files lues (nom, alias ou ID). Vide = toutes les propriétés des objets.")]
        public List<string> Properties { get; set; } = [];

        [Description("Lit les listes de valeurs sous forme d'ID plutôt que de noms.")]
        public bool LookupsAsIds { get; set; }

        [Description("Propriété de l'entité recevant l'ID de l'objet M-Files (vide = aucune ; l'ID est toujours dans les métadonnées 'mfiles.objectId').")]
        public string? ObjectIdProperty { get; set; }

        [Description("Nombre maximal d'objets lus (0 = tous, recherche segmentée).")]
        public int MaxResults { get; set; }

        public override Task<List<Entity>> FetchAsync(JobContext context)
        {
            ConnectorGuard.Required(DisplayName, ("class", Class));

            if (MaxResults < 0)
                throw ConnectorGuard.Invalid(DisplayName, "'maxResults' doit être positif ou nul.");

            var session = MFilesConnector.Open(DisplayName, this, CreateGateway, Unavailable, context);

            try
            {
                return Task.FromResult(MFilesConnector.Call(() => Fetch(session, context), Unavailable));
            }
            finally
            {
                session.Close(context, DisplayName);
            }
        }

        /// <summary>Creates the gateway to the vault (overridden by the tests).</summary>
        protected virtual IMFilesGateway CreateGateway(MFilesConnectionSettings settings)
            => new ComMFilesGateway(settings);

        private List<Entity> Fetch(MFilesSession session, JobContext context)
        {
            var objectClass = session.FindClass(Class)
                ?? throw ConnectorGuard.Invalid(DisplayName, $"Classe M-Files '{Class}' introuvable dans le coffre {session.VaultName}.");

            var map = MFilesFieldMap.Build(DisplayName, session, Fields, OnlyMappedFields, Properties);
            var conditions = MFilesFieldMap.BuildConditions(DisplayName, session, Conditions);

            var objects = session.Gateway.Search(new MFilesSearch(objectClass.Id, conditions, MaxResults));
            context.LogDebug($"[{DisplayName}] {objects.Count} objet(s) de la classe '{objectClass.Name}' trouvé(s).");

            return objects.Select(o => map.ToEntity(o, LookupsAsIds, ObjectIdProperty)).ToList();
        }

        private Exception Unavailable(Exception ex)
            => ConnectorGuard.SourceUnavailable(DisplayName, $"M-Files {MFilesConnectionSettings.From(this).Display}", ex);
    }
}
