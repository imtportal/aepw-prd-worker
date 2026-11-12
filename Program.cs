using Aepw.Evidence.Worker.Services;
using Azure.Identity;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Graph;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices(services =>
    {
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();

        // Managed Identity — used for Backend API bearer token.
        var managedIdentityCredential = new DefaultAzureCredential(); 
        services.AddSingleton<Azure.Core.TokenCredential>(managedIdentityCredential);

        // SharePointPOC App Registration — used for Microsoft Graph.
        var tenantId = Environment.GetEnvironmentVariable("Graph__TenantId")
            ?? throw new InvalidOperationException("Missing app setting 'Graph__TenantId'.");

        var clientId = Environment.GetEnvironmentVariable("Graph__ClientId")
            ?? throw new InvalidOperationException("Missing app setting 'Graph__ClientId'.");

        var clientSecret = Environment.GetEnvironmentVariable("Graph__ClientSecret")
            ?? throw new InvalidOperationException("Missing app setting 'Graph__ClientSecret'.");

        var graphCredential = new ClientSecretCredential(
            tenantId,
            clientId,
            clientSecret);

        // Microsoft Graph — download files.
        services.AddSingleton(_ => new GraphServiceClient(
            graphCredential,
            new[] { "https://graph.microsoft.com/.default" }));

        services.AddSingleton<GraphDownloader>();

        // Direct SQL save via stored procedure.
        services.AddSingleton<SqlEvidenceRepository>();

        // Backend API.
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