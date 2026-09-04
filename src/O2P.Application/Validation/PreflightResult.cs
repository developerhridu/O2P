namespace O2P.Application.Validation
{
    public class PreflightResult
    {
        public bool Passed { get; set; }
        public string Details { get; set; } = string.Empty;
    }
}
