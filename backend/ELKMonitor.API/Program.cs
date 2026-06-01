using ELKMonitor.API.Agents.CategorizationAgent;
using ELKMonitor.API.Agents.DataFetchingAgent;
using ELKMonitor.API.Agents.SubCategorizationAgent;
using ELKMonitor.API.Dashboard;
using ELKMonitor.API.Elasticsearch;
using ELKMonitor.API.Helpers;
using ELKMonitor.API.Services;
using Elastic.Clients.Elasticsearch;

var builder = WebApplication.CreateBuilder(args);

// ── Configuration ──────────────────────────────────────────────────────────
var esSettings = builder.Configuration
    .GetSection("Elasticsearch")
    .Get<ElasticsearchSettings>() ?? new ElasticsearchSettings();

// ── Infrastructure ─────────────────────────────────────────────────────────
builder.Services.AddSingleton(esSettings);
builder.Services.AddSingleton<ElasticsearchClient>(_ => ElasticsearchClientFactory.Create(esSettings));

// ── HttpClient Registrations ───────────────────────────────────────────────
builder.Services.AddHttpClient("CategorizerClient", client =>
{
    var url = builder.Configuration["Categorizer:Url"] ?? "http://localhost:8000";
    client.BaseAddress = new Uri(url);
    client.Timeout = TimeSpan.FromSeconds(15);
});

// ── 3-Agent Pipeline ───────────────────────────────────────────────────────
// Agent 1: Fetches and parses raw documents from Elasticsearch
builder.Services.AddSingleton<IDataFetchingAgent, DataFetchingAgent>();

// Agent 2: Classifies documents into error categories A–F using keyword/regex rules
builder.Services.AddSingleton<ICategorizationAgent, CategorizationAgent>();

// Agent 3: Assigns subcategory, normalizes message, and generates error signature
builder.Services.AddSingleton<ISubCategorizationAgent, SubCategorizationAgent>();

// ── Orchestrators (thin services that coordinate the 3 agents) ─────────────
builder.Services.AddScoped<ILogService, LogService>();
builder.Services.AddScoped<IDashboardService, DashboardService>();

// ── Helpers ────────────────────────────────────────────────────────────────
builder.Services.AddScoped<MockDataSeeder>();

// ── ASP.NET Core ───────────────────────────────────────────────────────────
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new()
    {
        Title       = "ELK Monitor API",
        Version     = "v1",
        Description = "Centralized ERROR/FATAL log monitoring — multi-agent pipeline: " +
                      "DataFetchingAgent → CategorizationAgent → SubCategorizationAgent"
    });
});

// ── CORS ───────────────────────────────────────────────────────────────────
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAngular", policy =>
        policy.WithOrigins(
                "http://localhost:4200",
                "http://localhost:80",
                "http://localhost",
                "http://frontend")
            .AllowAnyMethod()
            .AllowAnyHeader());
});

var app = builder.Build();

app.UseCors("AllowAngular");

app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "ELK Monitor API v1");
    c.RoutePrefix    = "swagger";
    c.DocumentTitle  = "ELK Monitor API";
});

app.MapControllers();

// ── Seed Mock Data (runs only when index is empty) ─────────────────────────
using (var scope = app.Services.CreateScope())
{
    var seeder = scope.ServiceProvider.GetRequiredService<MockDataSeeder>();
    await seeder.SeedAsync();
}

app.Run();
