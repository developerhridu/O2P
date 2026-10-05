using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;

namespace O2P.Application.Copying
{
    /// <summary>
    /// Whether a failed batch is worth trying again by itself. Only failures of the connection are: a
    /// dropped or refused connection, a stalled read, a database restarting. A data error - a value that
    /// does not fit, a constraint - fails the same way every time, so retrying it would only delay the
    /// message the operator needs.
    ///
    /// Recognises the driver exceptions by name and error number rather than by type, so this stays
    /// testable without either driver.
    /// </summary>
    public static class ChunkFailure
    {
        /// <summary>Oracle errors that mean the connection, not the data, failed.</summary>
        private static readonly HashSet<int> TransientOracleErrors = new()
        {
            3113, // end-of-file on communication channel
            3114, // not connected to ORACLE
            3135, // connection lost contact
            12153, 12170, // TNS timeouts
            12514, 12516, 12518, 12519, 12520, 12521, 12528, // listener / service not (yet) available
            12537, 12541, 12543, 12545, 12547, 12560, 12570, 12571, 12582, // TNS connection failures
            50000, // ODP.NET connection request timed out
        };

        /// <summary>The source table's storage changed during the read. Worth one more try, not five.</summary>
        public const int ObjectNoLongerExists = 8103;

        /// <param name="exception">What the batch failed with.</param>
        /// <param name="attempt">Which attempt just failed (1 = the first).</param>
        /// <param name="stalled">The batch was stopped by the stall watchdog.</param>
        public static bool IsWorthRetrying(Exception exception, int attempt, bool stalled)
        {
            // A read that stopped moving over a WAN is almost always a dead connection nobody noticed.
            if (stalled) return true;

            for (var e = exception; e != null; e = e.InnerException)
            {
                if (e is SocketException || e is IOException || e is TimeoutException) return true;

                var type = e.GetType().Name;
                if (type == "OracleException" && OracleNumber(e) is int number)
                {
                    if (number == ObjectNoLongerExists) return attempt < 2;
                    if (TransientOracleErrors.Contains(number)) return true;
                    return false; // any other ORA- error is about the data or the statement
                }

                if (type == "PostgresException")
                {
                    var state = Property<string>(e, "SqlState") ?? "";
                    // 08: connection exceptions; 57P01-03: shutdown/restart; 53300: too many connections.
                    return state.StartsWith("08") || state is "57P01" or "57P02" or "57P03" or "53300";
                }

                if (type == "NpgsqlException" && Property<bool>(e, "IsTransient")) return true;

                if (e.Message.Contains("Failed to connect", StringComparison.OrdinalIgnoreCase)
                    || e.Message.Contains("TNS:packet reader failure", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static int? OracleNumber(Exception e) =>
            e.GetType().GetProperty("Number")?.GetValue(e) is int n ? n : null;

        private static T? Property<T>(Exception e, string name) =>
            e.GetType().GetProperty(name)?.GetValue(e) is T value ? value : default;
    }
}
