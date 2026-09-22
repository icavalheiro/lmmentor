using LiteDB;
using LMMentor.Backend.Data.Entities;

namespace LMMentor.Backend.Data;

public sealed record DailyUsageDto(string Date, long Tokens, long Requests);

public sealed record UsageByEntityDto(string Id, string Label, long Tokens, long Requests);

/// <summary>Resumo de uso para o dashboard (janela dos últimos N dias).</summary>
public sealed record UsageSummaryDto(
    long TotalTokens,
    long TotalRequests,
    long AvgTokensPerSecond,
    long AvgTokensPerDay,
    IReadOnlyList<DailyUsageDto> Daily,
    IReadOnlyList<UsageByEntityDto> ByModel,
    IReadOnlyList<UsageByEntityDto> ByKey);

/// <summary>
/// Registra o uso por requisição do relay e agrega para o dashboard. O log é gravado
/// pelo relay OpenAI-compatible; até ele existir as consultas retornam dados zerados.
/// </summary>
public sealed class UsageService
{
    private const string CollectionName = "usage_log";

    private readonly LMMentorDb _db;
    private readonly ILiteCollection<UsageLogEntry> _collection;
    private readonly EndpointService _endpoints;
    private readonly ApiKeyService _keys;

    public UsageService(LMMentorDb db, EndpointService endpoints, ApiKeyService keys)
    {
        _db = db;
        _collection = db.Db.GetCollection<UsageLogEntry>(CollectionName);
        _endpoints = endpoints;
        _keys = keys;
    }

    /// <summary>Registra o uso de uma requisição atendida pelo relay.</summary>
    public void Log(string? modelId, string? apiKeyId, long promptTokens, long completionTokens, bool success)
    {
        _collection.Insert(new UsageLogEntry
        {
            Timestamp = DateTime.UtcNow,
            ModelId = modelId,
            ApiKeyId = apiKeyId,
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            TotalTokens = promptTokens + completionTokens,
            Success = success,
        });
    }

    /// <summary>Agrega o uso dos últimos <paramref name="days"/> dias para o dashboard.</summary>
    public UsageSummaryDto GetSummary(int days)
    {
        var since = DateTime.UtcNow.AddDays(-days);
        var entries = _collection.FindAll().Where(u => u.Timestamp >= since).ToList();

        var totalTokens = entries.Sum(e => e.TotalTokens);
        var totalRequests = (long)entries.Count;

        // Média de tokens/s sobre a janela inteira.
        var windowSeconds = Math.Max(1, (long)(DateTime.UtcNow - since).TotalSeconds);
        var avgTokensPerSecond = totalTokens / windowSeconds;
        var avgTokensPerDay = days > 0 ? totalTokens / days : 0;

        var daily = BuildDaily(entries, days);
        var byModel = BuildByEntity(entries, e => e.ModelId, id => _endpoints.GetAllModels()
            .FirstOrDefault(m => m.Id == id) is { } model ? EndpointService.EffectiveName(model) : id ?? "—");
        var byKey = BuildByEntity(entries, e => e.ApiKeyId, id => _keys.GetAll()
            .FirstOrDefault(k => k.Id == id)?.Name ?? id ?? "—");

        return new UsageSummaryDto(totalTokens, totalRequests, avgTokensPerSecond, avgTokensPerDay, daily, byModel, byKey);
    }

    private static List<DailyUsageDto> BuildDaily(List<UsageLogEntry> entries, int days)
    {
        var result = new List<DailyUsageDto>();
        for (var i = days - 1; i >= 0; i--)
        {
            var day = DateTime.UtcNow.Date.AddDays(-i);
            var dayEntries = entries.Where(e => e.Timestamp.Date == day).ToList();
            result.Add(new DailyUsageDto(
                day.ToString("yyyy-MM-dd"),
                dayEntries.Sum(e => e.TotalTokens),
                (long)dayEntries.Count));
        }

        return result;
    }

    private static List<UsageByEntityDto> BuildByEntity(
        List<UsageLogEntry> entries,
        Func<UsageLogEntry, string?> selector,
        Func<string?, string> labelResolver)
    {
        return entries
            .Where(e => selector(e) is not null)
            .GroupBy(e => selector(e)!)
            .OrderByDescending(g => g.Sum(e => e.TotalTokens))
            .Take(10)
            .Select(g => new UsageByEntityDto(g.Key, labelResolver(g.Key), g.Sum(e => e.TotalTokens), (long)g.Count()))
            .ToList();
    }
}
