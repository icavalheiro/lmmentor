using Microsoft.Extensions.Hosting;

namespace LMMentor.Backend.Data;

/// <summary>
/// Rotina em background que reexecuta a descoberta de modelos em todos os endpoints
/// a cada 5 minutos, mantendo o status e o catálogo atualizados sem intervenção manual.
/// </summary>
public sealed class ModelRefreshService : BackgroundService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);

    private readonly EndpointService _endpoints;
    private readonly ILogger<ModelRefreshService> _logger;

    public ModelRefreshService(EndpointService endpoints, ILogger<ModelRefreshService> logger)
    {
        _endpoints = endpoints;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(RefreshInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RefreshAllAsync(stoppingToken);
        }
    }

    private async Task RefreshAllAsync(CancellationToken ct)
    {
        foreach (var endpoint in _endpoints.GetAll())
        {
            if (ct.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await _endpoints.RefreshAsync(endpoint.Id, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Falha em um endpoint não interrompe o refresh dos demais.
                _logger.LogWarning(ex, "Scheduled model refresh failed for endpoint {EndpointName}.", endpoint.Name);
            }
        }
    }
}
