using Oracle.ManagedDataAccess.Client;
using System;

namespace O2P.Infrastructure.Oracle.ChangeTracking
{
    /// <summary>Oracle errors a change copy can meet when reading a table as it was, in plain words.</summary>
    internal static class OracleErrors
    {
        /// <summary>
        /// Turns the reading-the-past errors into messages that say what to do. Anything else is left
        /// alone. None of these move the tracker, so running the copy again is always safe.
        /// </summary>
        public static Exception ForAsOfRead(OracleException ex, string owner, string table) => ex.Number switch
        {
            // Oracle maps SCNs to time in steps of about three seconds, so a table whose definition changed
            // moments ago cannot be read as of a moment that close to it.
            1466 => new InvalidOperationException(
                $"{owner}.{table} changed definition moments before this copy, so it cannot yet be read as of the copy's starting point. Run Copy changes again in a minute.", ex),

            1555 => new InvalidOperationException(
                $"Oracle no longer holds enough undo to read {owner}.{table} as of this copy's starting point (ORA-01555). Run Copy changes again; if it keeps happening, ask your DBA to raise undo_retention.", ex),

            // A row locked by a distributed transaction left in doubt cannot be read at all.
            1591 => new InvalidOperationException(
                $"A row of {owner}.{table} is locked by a distributed transaction left in doubt (ORA-01591). Your DBA must resolve it before this table can be copied.", ex),

            1031 => new InvalidOperationException(
                $"This account cannot read {owner}.{table} as of an earlier moment (ORA-01031). Ask your DBA to GRANT FLASHBACK ON {owner}.{table}.", ex),

            _ => ex
        };
    }
}
