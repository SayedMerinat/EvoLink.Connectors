using EvoLink.Connectors.Common;
using EvoLink.Connectors.MFiles.Gateway;
using Ortolan.Core.Pipeline;

namespace EvoLink.Connectors.MFiles
{
    /// <summary>Authentication used to connect to the M-Files server.</summary>
    public enum MFilesAuthType
    {
        /// <summary>Windows account running the Ortolan service.</summary>
        CurrentWindowsUser = 0,

        /// <summary>Windows account given by Username / Password / Domain.</summary>
        SpecificWindowsUser = 1,

        /// <summary>M-Files account given by Username / Password.</summary>
        MFilesUser = 2,
    }

    /// <summary>
    /// Connection properties shared by every M-Files module. They are declared on each module (flat properties) so that the
    /// password is recognised as <c>[Sensitive]</c> by the configuration protector.
    /// </summary>
    public interface IMFilesConnectionOptions
    {
        string Server { get; }
        string Protocol { get; }
        int Port { get; }
        string Vault { get; }
        MFilesAuthType AuthType { get; }
        string? Username { get; }
        string? Password { get; }
        string? Domain { get; }
    }

    /// <summary>Immutable copy of the connection properties given to the gateway.</summary>
    public sealed record MFilesConnectionSettings(string Server, string Protocol, int Port, string Vault, MFilesAuthType AuthType,
        string? Username, string? Password, string? Domain)
    {
        public const string DefaultProtocol = "ncacn_ip_tcp";
        public const int DefaultPort = 2266;
        public const int GrpcPort = 7766;

        public static MFilesConnectionSettings From(IMFilesConnectionOptions options)
            => new(string.IsNullOrWhiteSpace(options.Server) ? "localhost" : options.Server.Trim(),
                string.IsNullOrWhiteSpace(options.Protocol) ? DefaultProtocol : options.Protocol.Trim(),
                options.Port > 0 ? options.Port : DefaultPort,
                options.Vault.Trim(),
                options.AuthType,
                options.Username,
                options.Password,
                options.Domain);

        /// <summary>Validates the connection properties (configuration error, no retry).</summary>
        public static void Validate(string module, IMFilesConnectionOptions options)
        {
            ConnectorGuard.Required(module, ("vault", options.Vault));

            if (!Enum.IsDefined(options.AuthType))
                throw ConnectorGuard.Invalid(module, $"Type d'authentification M-Files inconnu : {options.AuthType}.");

            if (options.AuthType != MFilesAuthType.CurrentWindowsUser)
                ConnectorGuard.Required(module, ("username", options.Username));

            if (options.Port is < 0 or > 65535)
                throw ConnectorGuard.Invalid(module, $"Port M-Files invalide : {options.Port}.");
        }

        /// <summary>
        /// Hint added to a connection failure when the port doesn't match the protocol (a frequent mistake: 7766, the gRPC port,
        /// with ncacn_ip_tcp). Empty when the pair is the standard one.
        /// </summary>
        public string EndpointHint
        {
            get
            {
                var grpc = Protocol.Equals("grpc", StringComparison.OrdinalIgnoreCase);
                var tcp = Protocol.Equals(DefaultProtocol, StringComparison.OrdinalIgnoreCase);

                if (tcp && Port == GrpcPort)
                    return $" Le port {GrpcPort} est celui de gRPC : avec le protocole {DefaultProtocol}, le port standard est {DefaultPort} (ou retirez 'port').";
                if (grpc && Port == DefaultPort)
                    return $" Le port {DefaultPort} est celui de {DefaultProtocol} : avec le protocole grpc, le port standard est {GrpcPort}.";
                if ((tcp && Port != DefaultPort) || (grpc && Port != GrpcPort))
                    return $" Vérifiez le port {Port} (standard pour {Protocol} : {(tcp ? DefaultPort : GrpcPort)}).";

                return string.Empty;
            }
        }

        /// <summary>Server and vault, for the messages (never the credentials).</summary>
        public string Display => $"{Vault}@{Server}";

        // The password must never appear in a log or an exception message.
        public override string ToString()
            => $"M-Files {Display} ({AuthType}{(string.IsNullOrEmpty(Username) ? string.Empty : $", {Username}")})";
    }

    /// <summary>
    /// Opening of a session shared by the modules: validation, creation of the gateway, connection and classification of the failures.
    /// </summary>
    internal static class MFilesConnector
    {
        /// <summary>
        /// Opens a session. A connection failure is converted by <paramref name="toUnavailable"/> (source or target unavailability).
        /// </summary>
        public static MFilesSession Open(string module, IMFilesConnectionOptions options, Func<MFilesConnectionSettings, IMFilesGateway> createGateway,
            Func<Exception, Exception> toUnavailable, JobContext context)
        {
            MFilesConnectionSettings.Validate(module, options);

            var settings = MFilesConnectionSettings.From(options);
            var gateway = createGateway(settings);

            try
            {
                gateway.Connect();
            }
            catch (Exception ex) when (MFilesErrors.IsConnectionFailure(ex))
            {
                gateway.Dispose();
                throw toUnavailable(ex);
            }
            catch
            {
                gateway.Dispose();
                throw;
            }

            context.LogDebug($"[{module}] Connecté au coffre {settings}.");
            return new MFilesSession(gateway);
        }

        /// <summary>Runs a call to the vault, converting a connection failure into an unavailability.</summary>
        public static T Call<T>(Func<T> call, Func<Exception, Exception> toUnavailable)
        {
            try
            {
                return call();
            }
            catch (Exception ex) when (MFilesErrors.IsConnectionFailure(ex))
            {
                throw toUnavailable(ex);
            }
        }

        public static void Call(Action call, Func<Exception, Exception> toUnavailable)
            => Call(() => { call(); return true; }, toUnavailable);
    }
}
