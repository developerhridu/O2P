using O2P.Application.ChangeTracking;
using O2P.Application.Interfaces;
using O2P.Application.Schema;
using O2P.Domain.Entities;
using O2P.Infrastructure.Oracle.Reader;
using Oracle.ManagedDataAccess.Client;
using Oracle.ManagedDataAccess.Types;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace O2P.Infrastructure.Oracle.ChangeTracking
{
    public partial class OracleChangeSource
    {
        public async Task<IChangeRowReader> OpenRowReaderAsync(
            Connection connection, string password, string owner, string table,
            IReadOnlyList<TrackedColumn> columns, string? whereClause, IReadOnlyList<OracleKeyColumn> key,
            decimal asOfScn, CancellationToken cancellationToken)
        {
            if (OracleSessions.IsMock(connection)) return new EmptyRowReader();

            // Its own session with the default NLS settings: the row filter is operator-written and may
            // rely on implicit conversions, so it must run exactly as it ran in the bulk copy.
            var conn = await OracleSessions.OpenUnpooledAsync(connection, password, cancellationToken);
            return new RowReader(conn, owner, table, columns, whereClause, key, asOfScn);
        }

        private sealed class EmptyRowReader : IChangeRowReader
        {
            public Task<IReadOnlyList<object[]>> ReadAsync(IReadOnlyList<IReadOnlyList<string?>> keys, CancellationToken cancellationToken) =>
                Task.FromResult<IReadOnlyList<object[]>>(Array.Empty<object[]>());

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        private sealed class RowReader : IChangeRowReader
        {
            private readonly OracleConnection _conn;
            private readonly string _owner;
            private readonly string _table;
            private readonly IReadOnlyList<OracleKeyColumn> _key;
            private readonly decimal _asOf;
            private readonly string _select;
            private readonly bool _hasLob;

            public RowReader(OracleConnection conn, string owner, string table, IReadOnlyList<TrackedColumn> columns,
                string? whereClause, IReadOnlyList<OracleKeyColumn> key, decimal asOf)
            {
                _conn = conn;
                _owner = owner;
                _table = table;
                _key = key;
                _asOf = asOf;
                _hasLob = columns.Any(c => OracleValues.IsLob(c.OracleType));

                // Same projection, in the same order, as the bulk copy wrote - the tracked column list is
                // frozen from it. AS OF the one SCN every table in this copy is read at, so a child row
                // never refers to a parent that is not being copied too.
                var columnList = string.Join(", ", columns.Select(c => SqlIdentifier.QuoteOracle(c.Name)));
                var filter = string.IsNullOrWhiteSpace(whereClause) ? "" : $"({OracleValues.ValidateReadOnlyWhereClause(whereClause)}) AND ";
                _select = $"SELECT {columnList} FROM {SqlIdentifier.OracleQualified(owner, table)} AS OF SCN :asof WHERE {filter}";
            }

            public async Task<IReadOnlyList<object[]>> ReadAsync(IReadOnlyList<IReadOnlyList<string?>> keys, CancellationToken cancellationToken)
            {
                var rows = new List<object[]>(keys.Count);
                foreach (var batch in keys.Chunk(OracleKeyBinding.BatchSize))
                {
                    var (predicate, parameters) = OracleKeyBinding.KeyPredicate(_key, batch);
                    await using var cmd = new OracleCommand(_select + predicate, _conn) { BindByName = true, CommandTimeout = 600 };
                    cmd.Parameters.Add(new OracleParameter("asof", OracleDbType.Decimal) { Value = new OracleDecimal(_asOf) });
                    cmd.Parameters.AddRange(parameters.ToArray());

                    // The bulk reader's LOB tuning, for the same reason: a large prefetch would try to
                    // buffer whole LOBs before returning the first row.
                    if (_hasLob)
                    {
                        cmd.FetchSize = 1 * 1024 * 1024;
                        cmd.InitialLOBFetchSize = 65536;
                    }

                    try
                    {
                        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                        while (await reader.ReadAsync(cancellationToken))
                        {
                            var values = new object[reader.FieldCount];
                            reader.GetValues(values);
                            for (var i = 0; i < values.Length; i++) values[i] = OracleValues.Normalize(values[i]);
                            rows.Add(values);
                        }
                    }
                    catch (OracleException ex)
                    {
                        throw OracleErrors.ForAsOfRead(ex, _owner, _table);
                    }
                }

                return rows;
            }

            public ValueTask DisposeAsync() => _conn.DisposeAsync();
        }
    }
}
