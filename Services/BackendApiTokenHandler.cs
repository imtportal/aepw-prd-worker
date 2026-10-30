using System.Net.Http.Headers;
using Azure.Core;
using Microsoft.Extensions.Logging;

namespace Aepw.Evidence.Worker.Services;

/// <summary>
/// Attaches an Entra ID bearer token (from the Worker's Managed Identity) to every Backend API
/// call. Token audience = the API app registration (BackendApiScope).
/// </summary>
public sealed class BackendApiTokenHandler : DelegatingHandler
{
    private readonly TokenCredential _credential;
    private readonly string[] _scopes;
    private readonly ILogger<BackendApiTokenHandler> _log;

    public BackendApiTokenHandler(TokenCredential credential, ILogger<BackendApiTokenHandler> log)
    {
        _credential = credential;
        _log = log;
        var scope = Environment.GetEnvironmentVariable("BackendApiScope")
                    ?? throw new InvalidOperationException("Missing app setting 'BackendApiScope'.");
        _scopes = new[] { scope };
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            _log.LogInformation("[AUTH] Requesting Entra token for scope {Scope}...", _scopes[0]);
            var token = await _credential.GetTokenAsync(new TokenRequestContext(_scopes), cancellationToken);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
            _log.LogInformation("[AUTH] Token acquired (expires {Expiry:o}).", token.ExpiresOn);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[AUTH] FAILED to acquire token for scope {Scope}.", _scopes[0]);
            throw;
        }
        return await base.SendAsync(request, cancellationToken);
    }
}
