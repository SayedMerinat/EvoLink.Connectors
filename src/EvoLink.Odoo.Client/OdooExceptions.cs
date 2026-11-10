namespace EvoLink.Odoo.Client
{
    /// <summary>
    /// Base of the failures of the client. The messages never contain the API key.
    /// </summary>
    public abstract class OdooException : Exception
    {
        protected OdooException(string message, Exception? inner = null) : base(message, inner) { }
    }

    /// <summary>
    /// Odoo can't be used right now: network failure, HTTP 5xx, timeout, authentication or access refused, answer that is not
    /// JSON-RPC (maintenance page). Retrying later may succeed: an integration should report it as an unavailability,
    /// not as a failure of the processed record.
    /// </summary>
    public sealed class OdooUnavailableException : OdooException
    {
        public OdooUnavailableException(string message, Exception? inner = null) : base(message, inner) { }
    }

    /// <summary>
    /// The connection settings are wrong (address that is not an Odoo instance, HTTP 404...): retrying can't succeed.
    /// </summary>
    public sealed class OdooConfigurationException : OdooException
    {
        public OdooConfigurationException(string message, Exception? inner = null) : base(message, inner) { }
    }

    /// <summary>
    /// Business error returned by Odoo for a JSON-RPC call (validation, missing field, access right on a record...).
    /// It concerns the processed record only.
    /// </summary>
    public sealed class OdooRpcException : OdooException
    {
        /// <summary>Python class of the Odoo exception, e.g. odoo.exceptions.ValidationError.</summary>
        public string? OdooName { get; }

        /// <summary>Readable message of Odoo.</summary>
        public string OdooMessage { get; }

        /// <summary>JSON-RPC error code (200 = server error, 100 = session expired).</summary>
        public int Code { get; }

        /// <summary>Python traceback sent by Odoo (never included in <see cref="Exception.Message"/>).</summary>
        public string? Debug { get; }

        public OdooRpcException(string message, string odooMessage, string? odooName = null, int code = 0, string? debug = null)
            : base(message)
        {
            OdooMessage = odooMessage;
            OdooName = odooName;
            Code = code;
            Debug = debug;
        }

        /// <summary>True when Odoo refused the credentials (the call can't succeed until the configuration or the server changes).</summary>
        public bool IsAccessDenied
            => Code == 100 || OdooName is "odoo.exceptions.AccessDenied" or "odoo.http.SessionExpiredException";
    }

    /// <summary>
    /// <see cref="OdooClientExtensions.GetAsync"/>: no record has the requested identifier (deleted, archived, or not visible
    /// to the user).
    /// </summary>
    public sealed class OdooRecordNotFoundException : OdooException
    {
        public string Model { get; }

        public long Id { get; }

        public OdooRecordNotFoundException(string model, long id)
            : base($"Aucun enregistrement '{model}' n'a l'identifiant {id} (supprimé, archivé ou non visible pour l'utilisateur).")
        {
            Model = model;
            Id = id;
        }
    }

    /// <summary>
    /// A search that must designate one record found several of them (<see cref="OdooClientExtensions.FindIdAsync"/>,
    /// <see cref="OdooClientExtensions.UpsertAsync"/>): the domain is not selective enough, nothing was written.
    /// </summary>
    public sealed class OdooAmbiguousResultException : OdooException
    {
        public string Model { get; }

        public IReadOnlyList<long> Ids { get; }

        public OdooAmbiguousResultException(string model, IReadOnlyList<long> ids)
            : base($"Plusieurs enregistrements '{model}' correspondent ({string.Join(", ", ids)}{(ids.Count >= OdooClientExtensions.AmbiguityProbe ? ", ..." : string.Empty)}) alors qu'un seul était attendu : précisez le domaine.")
        {
            Model = model;
            Ids = ids;
        }
    }
}
