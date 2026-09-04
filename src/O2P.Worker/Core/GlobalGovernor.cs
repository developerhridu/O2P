using System;
using System.Threading;
using System.Threading.Tasks;

namespace O2P.Worker.Core
{
    /// <summary>
    /// Enforces node-wide concurrency limits across all active jobs.
    /// Acts as a singleton in the worker process.
    /// </summary>
    public class GlobalGovernor : IDisposable
    {
        private readonly SemaphoreSlim _oracleSessionSemaphore;
        private readonly SemaphoreSlim _chunkWorkerSemaphore;

        public GlobalGovernor(int maxOracleSessions, int maxChunkWorkers)
        {
            _oracleSessionSemaphore = new SemaphoreSlim(maxOracleSessions, maxOracleSessions);
            _chunkWorkerSemaphore = new SemaphoreSlim(maxChunkWorkers, maxChunkWorkers);
        }

        public async Task<GovernorLease> AcquireWorkerSlotAsync(CancellationToken cancellationToken)
        {
            // Acquire both slots: an oracle session and a chunk worker slot
            // To prevent deadlocks, acquire in a fixed order (session then worker)
            await _oracleSessionSemaphore.WaitAsync(cancellationToken);
            try
            {
                await _chunkWorkerSemaphore.WaitAsync(cancellationToken);
                return new GovernorLease(this);
            }
            catch
            {
                _oracleSessionSemaphore.Release();
                throw;
            }
        }

        /// <summary>
        /// Acquires a single Oracle-session slot without a chunk-worker slot. Used by the planning
        /// phase, which opens its own Oracle connection: routing it through the same session
        /// semaphore keeps planning + chunk-reading together under one global Oracle-session cap,
        /// so a burst of planning can't storm a constrained source and trigger ORA-50000 timeouts.
        /// </summary>
        public async Task<OracleSessionLease> AcquireOracleSessionAsync(CancellationToken cancellationToken)
        {
            await _oracleSessionSemaphore.WaitAsync(cancellationToken);
            return new OracleSessionLease(this);
        }

        internal void ReleaseWorkerSlot()
        {
            _chunkWorkerSemaphore.Release();
            _oracleSessionSemaphore.Release();
        }

        internal void ReleaseOracleSession()
        {
            _oracleSessionSemaphore.Release();
        }

        public void Dispose()
        {
            _oracleSessionSemaphore.Dispose();
            _chunkWorkerSemaphore.Dispose();
        }

        public class GovernorLease : IDisposable
        {
            private readonly GlobalGovernor _governor;
            private bool _disposed;

            public GovernorLease(GlobalGovernor governor)
            {
                _governor = governor;
            }

            public void Dispose()
            {
                if (!_disposed)
                {
                    _governor.ReleaseWorkerSlot();
                    _disposed = true;
                }
            }
        }

        public class OracleSessionLease : IDisposable
        {
            private readonly GlobalGovernor _governor;
            private bool _disposed;

            public OracleSessionLease(GlobalGovernor governor)
            {
                _governor = governor;
            }

            public void Dispose()
            {
                if (!_disposed)
                {
                    _governor.ReleaseOracleSession();
                    _disposed = true;
                }
            }
        }
    }
}
