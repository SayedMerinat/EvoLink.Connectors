using System.Runtime.InteropServices;

namespace EvoLink.Connectors.MFiles.Gateway
{
    /// <summary>
    /// The M-Files server can't be reached: connection or authentication refused, vault offline, RPC connection lost.
    /// Translated by the modules into a source / target unavailability (the restart policy can act).
    /// </summary>
    public class MFilesConnectionException : Exception
    {
        public MFilesConnectionException(string message, Exception? inner = null) : base(message, inner) { }
    }

    /// <summary>An operation on one object or value was refused by the server (invalid value, missing permission...).</summary>
    public class MFilesOperationException : Exception
    {
        public MFilesOperationException(string message, Exception? inner = null) : base(message, inner) { }
    }

    /// <summary>A value (value list item, user, object) referenced by an entity does not exist in the vault.</summary>
    public class MFilesValueNotFoundException : InvalidOperationException
    {
        public MFilesValueNotFoundException(string message) : base(message) { }
    }

    /// <summary>
    /// Classification of the COM failures of the M-Files API.
    /// </summary>
    public static class MFilesErrors
    {
        // RPC / DCOM failures: the server stopped, the network dropped or the connection was lost.
        private static readonly HashSet<int> ConnectionHResults =
        [
            unchecked((int)0x800706BA), // RPC_S_SERVER_UNAVAILABLE
            unchecked((int)0x800706BB), // RPC_S_SERVER_TOO_BUSY
            unchecked((int)0x800706BE), // RPC_S_CALL_FAILED
            unchecked((int)0x800706BF), // RPC_S_CALL_FAILED_DNE
            unchecked((int)0x800706B5), // RPC_S_UNKNOWN_IF
            unchecked((int)0x800706D9), // EPT_S_NOT_REGISTERED
            unchecked((int)0x80010108), // RPC_E_DISCONNECTED
            unchecked((int)0x80010114), // RPC_E_INVALID_OBJECT
            unchecked((int)0x80080005), // CO_E_SERVER_EXEC_FAILURE
            unchecked((int)0x8007274C), // WSAETIMEDOUT
            unchecked((int)0x8007274D), // WSAECONNREFUSED
            unchecked((int)0x80072746), // WSAECONNRESET
        ];

        /// <summary>COM class not registered: the M-Files client is not installed on the machine.</summary>
        public const int ClassNotRegistered = unchecked((int)0x80040154);

        /// <summary>True when the exception means that the server or the connection is lost (not a refusal of the operation).</summary>
        public static bool IsConnectionFailure(Exception exception)
            => exception switch
            {
                MFilesConnectionException => true,
                InvalidComObjectException => true,
                TimeoutException => true,
                COMException com => ConnectionHResults.Contains(com.HResult),
                _ => false,
            };

        /// <summary>
        /// First line of an M-Files error message: the COM messages of M-Files append a long technical trace to the readable text.
        /// </summary>
        public static string ReadableMessage(Exception exception)
        {
            var message = exception.Message ?? string.Empty;
            var firstLine = message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();

            return string.IsNullOrEmpty(firstLine) ? exception.GetType().Name : firstLine;
        }
    }
}
