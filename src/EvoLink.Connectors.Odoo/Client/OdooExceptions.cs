namespace EvoLink.Connectors.Odoo.Client
{
    /// <summary>
    /// Odoo can't be used right now: network failure, HTTP 5xx, timeout, authentication refused. The modules convert it
    /// into a source / target unavailability so that the restart policy can act.
    /// </summary>
    public sealed class OdooUnavailableException : Exception
    {
        public OdooUnavailableException(string message, Exception? inner = null) : base(message, inner) { }
    }

    /// <summary>
    /// Business error returned by Odoo for a JSON-RPC call (validation, missing field, access right on a record...).
    /// It concerns the processed record only: the entity fails, the job continues.
    /// </summary>
    public sealed class OdooRpcException : Exception
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
}
