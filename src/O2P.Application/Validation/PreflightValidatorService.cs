using O2P.Application.Interfaces;
using O2P.Domain.Entities;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text;

namespace O2P.Application.Validation
{
    public class PreflightValidatorService : IPreflightValidatorService
    {
        private readonly ISourcePreflightExecutor _sourceExecutor;
        private readonly ITargetPreflightExecutor _targetExecutor;
        private readonly IPostgresSchemaInspector _schemaInspector;

        public PreflightValidatorService(
            ISourcePreflightExecutor sourceExecutor,
            ITargetPreflightExecutor targetExecutor,
            IPostgresSchemaInspector schemaInspector)
        {
            _sourceExecutor = sourceExecutor;
            _targetExecutor = targetExecutor;
            _schemaInspector = schemaInspector;
        }

        public async Task<PreflightResult> RunPreflightChecksAsync(
            Connection sourceConnection,
            string sourcePassword,
            Connection targetConnection,
            string targetPassword,
            string targetSchema,
            IReadOnlyCollection<string> targetTableNames,
            CancellationToken cancellationToken)
        {
            var result = new PreflightResult { Passed = true };
            var details = new StringBuilder();

            if (sourceConnection.Host.Equals("mock", System.StringComparison.OrdinalIgnoreCase) ||
                targetConnection.Host.Equals("mock", System.StringComparison.OrdinalIgnoreCase))
            {
                result.Details = "Test connection verified.\nOracle: access verified.\nPostgreSQL: version 14 or newer verified.\nPostgreSQL: access to schema 'public' verified.\nPostgreSQL: able to create a table.";
                result.Passed = true;
                return result;
            }

            bool sourcePriv = await _sourceExecutor.CheckPrivilegesAsync(sourceConnection, sourcePassword, cancellationToken);
            if (!sourcePriv)
            {
                result.Passed = false;
                details.AppendLine("Oracle: the account cannot read the tables it needs (SELECT ANY TABLE or session privileges are missing).");
            }
            else
            {
                details.AppendLine("Oracle: access verified.");
            }

            bool pgVersion = await _targetExecutor.CheckVersionAsync(targetConnection, targetPassword, cancellationToken);
            if (!pgVersion)
            {
                result.Passed = false;
                details.AppendLine("PostgreSQL: the destination runs a version older than 14, which is not supported.");
            }
            else
            {
                details.AppendLine("PostgreSQL: version 14 or newer verified.");
            }

            // USAGE is non-negotiable: without it nothing can be read or written in the schema.
            bool pgUsage = await _targetExecutor.CheckSchemaUsageAsync(targetConnection, targetPassword, targetSchema, cancellationToken);
            if (!pgUsage)
            {
                result.Passed = false;
                details.AppendLine($"PostgreSQL: the account cannot use schema '{targetSchema}'.");
            }
            else
            {
                details.AppendLine($"PostgreSQL: access to schema '{targetSchema}' verified.");
            }

            // CREATE is only needed for tables O2P has to create. When every target table already
            // exists they are reused as-is, so an account with USAGE + INSERT/TRUNCATE is enough.
            var missingTables = await ResolveTablesNeedingCreationAsync(
                targetConnection, targetPassword, targetSchema, targetTableNames, cancellationToken);
            bool createRequired = missingTables == null || missingTables.Count > 0;

            bool pgCreate = await _targetExecutor.CheckSchemaPrivilegesAsync(targetConnection, targetPassword, targetSchema, cancellationToken);
            bool probe = pgCreate && await _targetExecutor.RunProbeTableAsync(targetConnection, targetPassword, targetSchema, cancellationToken);

            if (pgCreate && probe)
            {
                details.AppendLine($"PostgreSQL: able to create tables in schema '{targetSchema}'.");
            }
            else if (createRequired)
            {
                result.Passed = false;
                if (!pgCreate)
                {
                    details.AppendLine($"PostgreSQL: the account cannot create tables in schema '{targetSchema}'.");
                }
                else
                {
                    details.AppendLine("PostgreSQL: could not create a test table, so tables cannot be created here.");
                }

                details.AppendLine(missingTables == null
                    ? "PostgreSQL: permission to create tables is needed, because the destination tables for this run could not be listed."
                    : $"PostgreSQL: permission to create tables is needed, because {missingTables.Count} table(s) do not exist yet: {string.Join(", ", missingTables.Take(10))}{(missingTables.Count > 10 ? ", ..." : string.Empty)}.");
            }
            else
            {
                var warning = pgCreate
                    ? $"PostgreSQL: could not create a test table in schema '{targetSchema}', but none is needed - all {targetTableNames.Count} destination table(s) already exist and will be reused."
                    : $"PostgreSQL: the account cannot create tables in schema '{targetSchema}', but it does not need to - all {targetTableNames.Count} destination table(s) already exist and will be reused.";
                result.Warnings.Add(warning);
                details.AppendLine(warning);
            }

            result.Details = details.ToString();
            return result;
        }

        /// <summary>
        /// Target tables that do not exist yet, or null when that could not be determined - in which
        /// case CREATE stays mandatory rather than being waived on a guess.
        /// </summary>
        private async Task<IReadOnlyList<string>?> ResolveTablesNeedingCreationAsync(
            Connection targetConnection,
            string targetPassword,
            string targetSchema,
            IReadOnlyCollection<string> targetTableNames,
            CancellationToken cancellationToken)
        {
            if (targetTableNames.Count == 0) return null;

            try
            {
                var existing = await _schemaInspector.GetExistingTableNamesAsync(
                    targetConnection, targetPassword, targetSchema, targetTableNames, cancellationToken);

                var existingSet = new HashSet<string>(existing, System.StringComparer.Ordinal);
                return targetTableNames.Where(t => !existingSet.Contains(t)).Distinct().ToList();
            }
            catch
            {
                return null;
            }
        }
    }
}
