namespace O2P.Domain.Entities
{
    public class TypeMappingRule
    {
        public long Id { get; set; }
        public string Scope { get; set; } = "global_default";
        public long? ApplicationId { get; set; }
        public long? ManifestTableId { get; set; }
        public string? ColumnName { get; set; }
        public string MatchJson { get; set; } = "{}";
        public string TargetType { get; set; } = null!;
        public string? OptionsJson { get; set; }
        public bool IsEnabled { get; set; } = true;
    }
}
