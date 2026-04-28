using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NpcSoulEngine.Functions.Services;

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication()
    .ConfigureServices((ctx, services) =>
    {
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();

        // Cosmos — singleton, one client per app lifetime (connection pooling)
        services.AddSingleton(sp =>
        {
            var connStr = ctx.Configuration["CosmosConnectionString"]
                ?? throw new InvalidOperationException("CosmosConnectionString not configured");
            return new CosmosClient(connStr, new CosmosClientOptions
            {
                SerializerOptions = new CosmosSerializationOptions
                {
                    PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase
                },
                ConnectionMode = ConnectionMode.Direct,
                RequestTimeout = TimeSpan.FromSeconds(10),
                ApplicationPreferredRegions = new[] { "East US", "West US 2" }
            });
        });

        // OpenAI client — singleton
        services.AddSingleton(sp =>
        {
            var endpoint = ctx.Configuration["OpenAiEndpoint"]
                ?? throw new InvalidOperationException("OpenAiEndpoint not configured");
            var key = ctx.Configuration["OpenAiKey"]
                ?? throw new InvalidOperationException("OpenAiKey not configured");
            return new Azure.AI.OpenAI.AzureOpenAIClient(new Uri(endpoint), new Azure.AzureKeyCredential(key));
        });

        // Application services
        services.AddSingleton<ICosmosMemoryStore, CosmosMemoryStore>();
        services.AddSingleton<IDialoguePromptBuilder, DialoguePromptBuilder>();
        services.AddSingleton<IEmotionalWeightCalculator, EmotionalWeightCalculator>();
        services.AddSingleton<IGossipService, GossipService>();
        services.AddSingleton<IMemoryDecayService, MemoryDecayService>();

        // Phase 7 — archetype classifier (typed HttpClient for Azure ML endpoint)
        services.AddHttpClient<IArchetypeClassifierService, AzureMLArchetypeClassifier>();

        // Phase 3 — prompt hardening services
        services.AddSingleton<IPromptInjectionGuard, PromptInjectionGuard>();
        services.AddSingleton<ITokenBudgetTracker, TokenBudgetTracker>();
        services.AddSingleton<ISemanticResponseCache>(sp =>
        {
            var cfg = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<FunctionConfig>>().Value;
            return new SemanticResponseCache(cfg.DialogueCacheTtl, cfg.DialogueCacheMaxEntries);
        });
        services.AddSingleton<IContentSafetyValidator>(sp =>
        {
            var logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ContentSafetyValidator>>();
            var endpoint = ctx.Configuration["ContentSafetyEndpoint"];
            var key      = ctx.Configuration["ContentSafetyKey"];
            return new ContentSafetyValidator(logger, endpoint, key);
        });

        services.AddOptions<FunctionConfig>()
            .Configure<Microsoft.Extensions.Configuration.IConfiguration>((opts, cfg) =>
            {
                opts.CosmosDatabaseName             = cfg["CosmosDatabaseName"]             ?? "NpcSoulEngine";
                opts.Gpt4oDeploymentName            = cfg["Gpt4oDeploymentName"]            ?? "gpt-4o";
                opts.Gpt4oMiniDeploymentName        = cfg["Gpt4oMiniDeploymentName"]        ?? "gpt-4o-mini";
                opts.MaxTokensPerDialogueCall       = int.Parse(cfg["MaxTokensPerDialogueCall"]       ?? "2000");
                opts.MaxOutputTokensPerDialogueCall = int.Parse(cfg["MaxOutputTokensPerDialogueCall"] ?? "400");
                opts.GossipMaxHops                  = int.Parse(cfg["GossipMaxHops"]                  ?? "3");
                opts.DialogueCacheMaxEntries        = int.Parse(cfg["DialogueCacheMaxEntries"]        ?? "200");
                opts.DialogueCacheTtlMinutes        = int.Parse(cfg["DialogueCacheTtlMinutes"]        ?? "5");
                opts.AzureMLEndpointUri             = cfg["AzureMLEndpointUri"];
                opts.AzureMLEndpointKey             = cfg["AzureMLEndpointKey"];
            });
    })
    .Build();

await host.RunAsync();

namespace NpcSoulEngine.Functions.Services
{
    public sealed class FunctionConfig
    {
        public string CosmosDatabaseName             { get; set; } = "NpcSoulEngine";
        public string Gpt4oDeploymentName            { get; set; } = "gpt-4o";
        public string Gpt4oMiniDeploymentName        { get; set; } = "gpt-4o-mini";
        public int    MaxTokensPerDialogueCall        { get; set; } = 2000;
        public int    MaxOutputTokensPerDialogueCall  { get; set; } = 400;
        public int    GossipMaxHops                   { get; set; } = 3;
        public int    DialogueCacheMaxEntries         { get; set; } = 200;
        public int    DialogueCacheTtlMinutes         { get; set; } = 5;
        public string? AzureMLEndpointUri             { get; set; }
        public string? AzureMLEndpointKey             { get; set; }

        public TimeSpan DialogueCacheTtl => TimeSpan.FromMinutes(DialogueCacheTtlMinutes);
    }
}
