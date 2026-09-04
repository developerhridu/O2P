using O2P.Application.Interfaces;
using O2P.Domain.Entities;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace O2P.Application.Validation
{
    public class ValidationService : IValidationService
    {
        private readonly ISourceCountExecutor _sourceExecutor;
        private readonly ITargetCountExecutor _targetExecutor;

        public ValidationService(ISourceCountExecutor sourceExecutor, ITargetCountExecutor targetExecutor)
        {
            _sourceExecutor = sourceExecutor;
            _targetExecutor = targetExecutor;
        }

        public async Task<ValidationResult> ValidateTableRunAsync(TableRun tableRun, Connection sourceConnection, string sourcePassword, Connection targetConnection, string targetPassword, CancellationToken cancellationToken)
        {
            var result = new ValidationResult
            {
                TableRunId = tableRun.Id,
                CheckKind = "rowcount"
            };

            if (sourceConnection.Host.Equals("mock", System.StringComparison.OrdinalIgnoreCase) ||
                targetConnection.Host.Equals("mock", System.StringComparison.OrdinalIgnoreCase))
            {
                result.SourceValue = "4000";
                result.TargetValue = "4000";
                result.Passed = true;
                result.DetailJson = "{\"status\": \"Mock count verification successful.\"}";
                return result;
            }

            try
            {
                long sourceCount = await _sourceExecutor.GetRowCountAsync(sourceConnection, sourcePassword, tableRun.ManifestTable.Owner, tableRun.ManifestTable.TableName, tableRun.ManifestTable.WhereClause, cancellationToken);
                result.SourceValue = sourceCount.ToString();

                long targetCount = await _targetExecutor.GetRowCountAsync(targetConnection, targetPassword, tableRun.JobRun.TargetSchema, tableRun.TargetTableName, cancellationToken);
                result.TargetValue = targetCount.ToString();

                result.Passed = (sourceCount == targetCount);
            }
            catch (Exception ex)
            {
                result.Passed = false;
                result.DetailJson = $"{{\"error\": \"{ex.Message}\"}}";
            }

            return result;
        }
    }
}
