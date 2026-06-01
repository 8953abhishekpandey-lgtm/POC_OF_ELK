namespace ELKMonitor.API.Elasticsearch
{
    public class ElasticsearchSettings
    {
        public string Url { get; set; } = "http://localhost:9200";
        public string Username { get; set; } = "elastic";
        public string Password { get; set; } = "changeme";
        public string CloudId { get; set; } = string.Empty;
        public string ApiKey { get; set; } = string.Empty;
        public string IndexPattern { get; set; } = "logs-*";
        public bool VerifySsl { get; set; } = false;
        public int RequestTimeoutSeconds { get; set; } = 30;
    }
}
