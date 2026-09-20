using System.Collections.Generic;

namespace O2P.Application.Validation
{
    public class PreflightResult
    {
        public bool Passed { get; set; }

        /// <summary>
        /// Human-readable summary of every check, warnings included, so existing consumers that only
        /// read this keep showing the full picture.
        /// </summary>
        public string Details { get; set; } = string.Empty;

        /// <summary>Non-blocking findings. Also appended to <see cref="Details"/>.</summary>
        public List<string> Warnings { get; set; } = new();
    }
}
