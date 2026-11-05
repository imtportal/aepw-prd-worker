using Aepw.Evidence.Worker;
using Aepw.Evidence.Worker.Services;
using Azure.Identity;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Graph;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureGeneratedFunctionMetadataProvider()
    .ConfigureGeneratedFunctionExecutor()
    .ConfigureServices(services =>
    {
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();

        services.AddSingleton<DefaultAzureCredential>();
        services.AddSingleton<Azure.Core.TokenCredential>(sp =>
            sp.GetRequiredService<DefaultAzureCredential>());

        services.AddSingleton(_ => new GraphServiceClient(
            new ClientSecretCredential(
                Environment.GetEnvironmentVariable("Graph__TenantId")
                    ?? throw new InvalidOperationException("Missing app setting 'Graph__TenantId'."),
                Environment.GetEnvironmentVariable("Graph__ClientId")
                    ?? throw new InvalidOperationException("Missing app setting 'Graph__ClientId'."),
                Environment.GetEnvironmentVariable("Graph__ClientSecret")
                    ?? throw new InvalidOperationException("Missing app setting 'Graph__ClientSecret'.")),
            new[] { "https://graph.microsoft.com/.default" }));

        services.AddSingleton<GraphDownloader>();

        services.AddSingleton<SqlEvidenceRepository>();

        services.AddTransient<BackendApiTokenHandler>();

        services.AddHttpClient<BackendApiClient>(client =>
        {
            var baseUrl = Environment.GetEnvironmentVariable("BackendApiBaseUrl")
                ?? throw new InvalidOperationException("Missing app setting 'BackendApiBaseUrl'.");

            client.BaseAddress = new Uri(baseUrl);
            client.Timeout = TimeSpan.FromMinutes(5);
        })
        .AddHttpMessageHandler<BackendApiTokenHandler>();
    })
    .Build();

host.Run();
