using O2P.Application.Interfaces;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace O2P.Worker.Core
{
    public class TokenBucketRateLimiter : IRateLimiter
    {
        private readonly double _tokensPerSecond;
        private double _availableTokens;
        private readonly double _capacity;
        private long _lastUpdateTicks;

        private readonly object _syncRoot = new object();

        public TokenBucketRateLimiter(double tokensPerSecond, double maxBurstTokens)
        {
            if (tokensPerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(tokensPerSecond));
            
            _tokensPerSecond = tokensPerSecond;
            _capacity = maxBurstTokens;
            _availableTokens = maxBurstTokens; // Start full
            _lastUpdateTicks = Stopwatch.GetTimestamp();
        }

        public async Task WaitAsync(int tokensToConsume, CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                TimeSpan delay;
                lock (_syncRoot)
                {
                    Refill();

                    if (_availableTokens >= tokensToConsume)
                    {
                        _availableTokens -= tokensToConsume;
                        return;
                    }

                    var tokensNeeded = tokensToConsume - _availableTokens;
                    delay = TimeSpan.FromSeconds(tokensNeeded / _tokensPerSecond);
                }

                // Wait before trying again
                await Task.Delay(delay, cancellationToken);
            }
        }

        private void Refill()
        {
            var nowTicks = Stopwatch.GetTimestamp();
            var elapsedSeconds = (double)(nowTicks - _lastUpdateTicks) / Stopwatch.Frequency;
            _lastUpdateTicks = nowTicks;

            var newTokens = elapsedSeconds * _tokensPerSecond;
            _availableTokens = Math.Min(_capacity, _availableTokens + newTokens);
        }
    }
}
