using System.Threading.Channels;
using LiteDB;
using LMMentor.Backend.Data.Entities;

namespace LMMentor.Backend.Data;

/// <summary>
/// Grava registros de uso em uma fila consumida por uma thread dedicada, mantendo o
/// caminho do relay livre de I/O no banco. No shutdown a fila é drenada antes de fechar.
/// </summary>
public sealed class UsageLogger : IAsyncDisposable
{
    private const string CollectionName = "usage_log";

    private readonly Channel<UsageLogEntry> _channel;
    private readonly ILiteCollection<UsageLogEntry> _collection;
    private readonly Task _consumer;
    private bool _disposed;

    public UsageLogger(LMMentorDb db)
    {
        _collection = db.Db.GetCollection<UsageLogEntry>(CollectionName);
        _channel = Channel.CreateUnbounded<UsageLogEntry>();
        _consumer = Task.Run(ConsumeAsync);
    }

    /// <summary>Enfileira o registro; a gravação no banco acontece na thread do consumidor.</summary>
    public void Enqueue(string? modelId, string? apiKeyId, long promptTokens, long completionTokens, bool success)
    {
        _channel.Writer.TryWrite(new UsageLogEntry
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

    private async Task ConsumeAsync()
    {
        await foreach (var entry in _channel.Reader.ReadAllAsync())
        {
            try
            {
                _collection.Insert(entry);
            }
            catch
            {
                // Falha de gravação não pode derrubar o consumidor: descarta a entrada.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Fecha a escrita e espera o consumidor drenar os registros restantes.
        _channel.Writer.Complete();
        await _consumer;
    }
}
