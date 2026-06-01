using ELKMonitor.API.Elasticsearch;
using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.QueryDsl;

namespace ELKMonitor.API.Helpers
{
    /// <summary>
    /// Seeds 250+ realistic mock ERROR/FATAL logs into Elasticsearch on first startup.
    /// Runs only when the target index has no documents (idempotent).
    /// </summary>
    public class MockDataSeeder
    {
        private readonly ElasticsearchClient _esClient;
        private readonly ILogger<MockDataSeeder> _logger;
        private const string IndexName = "logs-mock-2024";

        private static readonly string[] Apps = { "OrderService", "PaymentGateway", "UserAuthAPI", "InventoryService", "NotificationSvc", "ReportingEngine", "ApiGateway" };
        private static readonly string[] Servers = { "prod-server-01", "prod-server-02", "prod-server-03", "staging-server-01", "staging-server-02" };
        private static readonly string[] Envs = { "Production", "Staging", "UAT" };

        public MockDataSeeder(ElasticsearchClient esClient, ILogger<MockDataSeeder> logger)
        {
            _esClient = esClient;
            _logger = logger;
        }

        public async Task SeedAsync()
        {
            try
            {
                var countResp = await _esClient.CountAsync<object>(c => c.Indices(IndexName));
                if (countResp.IsValidResponse && countResp.Count > 0)
                {
                    _logger.LogInformation("Mock index already has {Count} documents. Skipping seed.", countResp.Count);
                    return;
                }

                _logger.LogInformation("Seeding 250 mock logs into '{Index}'...", IndexName);
                var logs = GenerateMockLogs(250);
                var bulkResp = await _esClient.BulkAsync(b => b.Index(IndexName).CreateMany(logs));

                if (bulkResp.IsValidResponse && !bulkResp.Errors)
                {
                    _logger.LogInformation("Seeded {Count} mock logs successfully.", logs.Count);
                }
                else
                {
                    _logger.LogWarning("Bulk indexing failed or had some errors. IsValid: {IsValid}, Reason: {Reason}", 
                        bulkResp.IsValidResponse, bulkResp.ElasticsearchServerError?.Error?.Reason);
                    if (bulkResp.ItemsWithErrors != null)
                    {
                        foreach (var item in bulkResp.ItemsWithErrors.Take(3))
                        {
                            _logger.LogWarning("Bulk item error: {Error}", item.Error?.Reason);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Mock data seeding failed — app will still start. Ensure Elasticsearch is running.");
            }
        }

        private static List<MockLogDocument> GenerateMockLogs(int count)
        {
            var rng = new Random(42);
            var now = DateTime.UtcNow;
            var templates = GetErrorTemplates();
            var logs = new List<MockLogDocument>();

            for (int i = 0; i < count; i++)
            {
                var template = templates[rng.Next(templates.Count)];
                logs.Add(new MockLogDocument
                {
                    Timestamp = now.AddHours(-rng.NextDouble() * 24 * 7),
                    Level = rng.NextDouble() < 0.75 ? "ERROR" : "FATAL",
                    Message = template.Message,
                    ExceptionType = template.ExType,
                    StackTrace = template.Stack,
                    Application = Apps[rng.Next(Apps.Length)],
                    HostName = Servers[rng.Next(Servers.Length)],
                    EnvName = Envs[rng.Next(Envs.Length)],
                    Logger = $"App.Services.{template.Logger}",
                    Thread = $"Thread-{rng.Next(1, 50)}"
                });
            }
            return logs.OrderByDescending(l => l.Timestamp).ToList();
        }

        private static List<(string Message, string ExType, string Stack, string Logger)> GetErrorTemplates()
        {
            return new List<(string, string, string, string)>
            {
                // Database
                ("SqlException: Cannot open database 'OrdersDB'. Login failed for user 'app_user'.", "System.Data.SqlClient.SqlException", "at SqlConnection.Open()\n   at OrderRepository.GetOrderAsync(Int32 id)", "OrderRepository"),
                ("Entity Framework Core: The connection string 'DefaultConnection' was not found.", "Microsoft.EntityFrameworkCore.DbUpdateException", "at DbContext.SaveChangesAsync()\n   at PaymentService.ProcessPaymentAsync()", "PaymentService"),
                ("DbUpdateConcurrencyException: Database operation expected to affect 1 row(s) but actually affected 0.", "Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException", "at DbContext.SaveChangesAsync()\n   at StockService.UpdateStockAsync()", "StockService"),
                ("NpgsqlException: 53300: too many connections (max_connections = 100)", "Npgsql.NpgsqlException", "at NpgsqlConnection.OpenAsync()\n   at AppDbContext.GetConnectionAsync()", "AppDbContext"),
                ("SQL Server deadlock detected. Transaction chosen as deadlock victim.", "System.Data.SqlClient.SqlException", "at SqlCommand.ExecuteReaderAsync()\n   at OrderRepository.BulkUpdateAsync()", "OrderRepository"),
                // Auth
                ("SecurityTokenExpiredException: IDX10223: Lifetime validation failed. Token is expired.", "System.IdentityModel.Tokens.Jwt.SecurityTokenExpiredException", "at Validators.ValidateLifetime()\n   at JwtMiddleware.ValidateTokenAsync()", "JwtMiddleware"),
                ("UnauthorizedAccessException: User does not have permission to access '/api/admin/users'.", "System.UnauthorizedAccessException", "at AdminController.GetUsers()\n   at ActionMethodExecutor", "AdminController"),
                ("AuthenticationException: Invalid credentials. Login failed.", "System.Security.Authentication.AuthenticationException", "at AuthService.LoginAsync(LoginDto dto)\n   at AuthController.Login(LoginDto dto)", "AuthService"),
                // Network
                ("HttpRequestException: Connection refused (localhost:8080). Target machine actively refused it.", "System.Net.Http.HttpRequestException", "at HttpClient.SendAsync(HttpRequestMessage)\n   at ServiceDiscovery.CallDownstreamAsync()", "ServiceDiscovery"),
                ("SocketException: No route to host. Cannot connect to 'payment-provider-api.external.com:443'.", "System.Net.Sockets.SocketException", "at Socket.ConnectAsync(EndPoint)\n   at PaymentProviderClient.CallApiAsync()", "PaymentProviderClient"),
                ("WebException: Remote server returned error: (502) Bad Gateway.", "System.Net.WebException", "at HttpWebRequest.GetResponse()\n   at SmsGatewayService.SendSmsAsync()", "SmsGatewayService"),
                // Timeout
                ("TimeoutException: The operation timed out after 30000ms. SQL command exceeded timeout.", "System.TimeoutException", "at SqlCommand.ExecuteReader()\n   at ReportGenerator.GenerateLargeReportAsync()", "ReportGenerator"),
                ("TaskCanceledException: HTTP request to external API exceeded timeout of 10 seconds.", "System.Threading.Tasks.TaskCanceledException", "at HttpClient.SendAsync(HttpRequestMessage, CancellationToken)\n   at ProxyMiddleware.ForwardRequestAsync()", "ProxyMiddleware"),
                // Memory
                ("OutOfMemoryException: Failed to allocate 1GB buffer for report generation.", "System.OutOfMemoryException", "at Byte[].ctor(Int64)\n   at ExcelExporter.GenerateExcelAsync(DataTable)", "ExcelExporter"),
                ("StackOverflowException: Process terminated due to infinite recursive tree traversal.", "System.StackOverflowException", "at TreeHelper.TraverseNode(TreeNode)\n   at TreeHelper.TraverseNode(TreeNode)", "TreeHelper"),
                // Application
                ("NullReferenceException: Object reference not set. 'customer' was null when accessing 'customer.EmailAddress'.", "System.NullReferenceException", "at NotificationService.SendOrderConfirmation(Order)\n   at OrderService.PlaceOrderAsync(PlaceOrderDto)", "OrderService"),
                ("ArgumentNullException: Value cannot be null. (Parameter 'connectionString')", "System.ArgumentNullException", "at PaymentDbContext..ctor(DbContextOptions)\n   at ServiceProvider.GetService(Type)", "PaymentDbContext"),
                ("InvalidOperationException: Cannot access a disposed DbContext instance.", "System.InvalidOperationException", "at DbContext.CheckDisposed()\n   at CategoryService.GetCategoriesAsync()", "CategoryService"),
                ("KeyNotFoundException: Key 'order_processing_fee' not present in configuration dictionary.", "System.Collections.Generic.KeyNotFoundException", "at Dictionary.get_Item(TKey)\n   at FeeCalculator.CalculateFees(PaymentRequest)", "FeeCalculator"),
                ("FormatException: Cannot parse 'N/A' as decimal for field 'UnitPrice'.", "System.FormatException", "at Number.ParseDecimal(ReadOnlySpan)\n   at CsvProductImporter.ParseRow(String[])", "CsvImporter"),
                // Deployment
                ("InvalidOperationException: Unable to resolve service for type 'IOrderRepository' in DI container.", "System.InvalidOperationException: unable to resolve", "at ServiceProvider.GetRequiredService(Type)\n   at Program.<>c.b__0(WebApplication)", "Program"),
                ("ConfigurationException: Required env variable 'STRIPE_API_KEY' is not set.", "Microsoft.Extensions.Configuration.ConfigurationException", "at Startup.ValidateConfiguration(IConfiguration)\n   at Program.Main(String[])", "Program"),
                // API
                ("HttpRequestException: 503 Service Unavailable from 'https://shipping.api.internal/calculate'.", "System.Net.Http.HttpRequestException", "at HttpResponseMessage.EnsureSuccessStatusCode()\n   at ShippingService.CalculateShippingAsync(ShipmentRequest)", "ShippingService"),
                ("JsonException: Cannot convert JSON value to System.Decimal. Path: $.amount", "System.Text.Json.JsonException", "at JsonSerializer.Deserialize(String json, Type)\n   at WebhookProcessor.ProcessStripeWebhookAsync(String)", "WebhookProcessor"),
                ("BadHttpRequestException: Invalid request body. Expected application/json but got text/plain.", "Microsoft.AspNetCore.Http.BadHttpRequestException", "at HttpContext.Request.ReadFromJsonAsync()\n   at OrderController.CreateOrder(HttpContext)", "OrderController"),
                ("HttpRequestException: 429 Too Many Requests from external rate-limited API.", "System.Net.Http.HttpRequestException", "at HttpClient.SendAsync(HttpRequestMessage)\n   at ExternalApiClient.CallWithRetryAsync()", "ExternalApiClient"),
            };
        }
    }

    public class MockLogDocument
    {
        [System.Text.Json.Serialization.JsonPropertyName("@timestamp")]
        public DateTime Timestamp { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("level")]
        public string Level { get; set; } = "ERROR";

        [System.Text.Json.Serialization.JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("exception")]
        public MockExceptionInfo Exception => new() { ExceptionType = ExceptionType, Stacktrace = StackTrace };

        [System.Text.Json.Serialization.JsonIgnore]
        public string ExceptionType { get; set; } = string.Empty;

        [System.Text.Json.Serialization.JsonIgnore]
        public string StackTrace { get; set; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("fields")]
        public MockFieldsInfo Fields => new() { Application = Application, Environment = EnvName, Logger = Logger };

        [System.Text.Json.Serialization.JsonIgnore]
        public string Application { get; set; } = string.Empty;

        [System.Text.Json.Serialization.JsonIgnore]
        public string EnvName { get; set; } = string.Empty;

        [System.Text.Json.Serialization.JsonIgnore]
        public string Logger { get; set; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("host")]
        public MockHostInfo Host => new() { Name = HostName };

        [System.Text.Json.Serialization.JsonIgnore]
        public string HostName { get; set; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("thread")]
        public string Thread { get; set; } = string.Empty;
    }

    public class MockExceptionInfo
    {
        [System.Text.Json.Serialization.JsonPropertyName("type")] public string? ExceptionType { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("stacktrace")] public string? Stacktrace { get; set; }
    }

    public class MockFieldsInfo
    {
        [System.Text.Json.Serialization.JsonPropertyName("application")] public string? Application { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("environment")] public string? Environment { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("logger")] public string? Logger { get; set; }
    }

    public class MockHostInfo
    {
        [System.Text.Json.Serialization.JsonPropertyName("name")] public string? Name { get; set; }
    }
}
