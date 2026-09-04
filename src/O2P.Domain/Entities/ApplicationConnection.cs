namespace O2P.Domain.Entities
{
    public class ApplicationConnection
    {
        public long ApplicationId { get; set; }
        [System.Text.Json.Serialization.JsonIgnore]
        public Application? Application { get; set; }

        public string Slot { get; set; } = null!; // 'oracle_test' | 'oracle_live' | 'pg_test' | 'pg_live'

        public long ConnectionId { get; set; }
        public Connection? Connection { get; set; }
    }
}
