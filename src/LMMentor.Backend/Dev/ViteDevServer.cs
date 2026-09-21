using System.Diagnostics;
using System.Net.Sockets;

namespace LMMentor.Backend.Dev;

/// <summary>
/// Gerencia o processo do Vite dev server (watch mode) iniciado pelo backend.
/// </summary>
public sealed class ViteDevServer : IAsyncDisposable
{
    private readonly ILogger<ViteDevServer> _logger;
    private readonly Process? _process;

    public string Url { get; }

    /// <summary>Completa quando o Vite começa a aceitar conexões (ou falha ao iniciar).</summary>
    public Task Ready { get; }

    public ViteDevServer(ILogger<ViteDevServer> logger, string projectPath, string url)
    {
        _logger = logger;
        Url = url;

        // Usa o binário local do Vite (node_modules/.bin) em vez de npm, que pode resolver para shims quebrados.
        var isWindows = OperatingSystem.IsWindows();
        var viteBin = Path.Combine(projectPath, "node_modules", ".bin", isWindows ? "vite.cmd" : "vite");

        if (!File.Exists(viteBin))
        {
            throw new InvalidOperationException($"Vite não encontrado em {viteBin}. Execute 'npm install' em {projectPath}.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = viteBin,
            WorkingDirectory = projectPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // Porta fixa e sem fallback: se a porta já estiver em uso, o Vite falha em vez de escolher outra.
        startInfo.ArgumentList.Add("dev");
        startInfo.ArgumentList.Add("--strictPort");

        _process = Process.Start(startInfo) ?? throw new InvalidOperationException("Não foi possível iniciar o Vite dev server.");

        _logger.LogInformation("Vite dev server iniciado (PID {Pid}) em {Url}", _process.Id, url);

        var hostPort = new Uri(url);
        Ready = WaitForReadyAsync(hostPort.Host, hostPort.Port);

        Task.Run(async () =>
        {
            await PumpAsync(_process.StandardOutput, stdout: true).ConfigureAwait(false);
            await PumpAsync(_process.StandardError, stdout: false).ConfigureAwait(false);
            await _process.WaitForExitAsync().ConfigureAwait(false);
            _logger.LogWarning("Vite dev server encerrado com código {ExitCode}", _process.ExitCode);
        });
    }

    private async Task WaitForReadyAsync(string host, int port)
    {
        // Aguarda o Vite aceitar conexões TCP (timeout de 60s para a primeira subida).
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            if (_process is not null && _process.HasExited)
            {
                throw new InvalidOperationException($"O Vite dev server terminou antes de ficar pronto (código {_process.ExitCode}).");
            }

            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(host, port).ConfigureAwait(false);
                _logger.LogInformation("Vite dev server pronto em {Url}", Url);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(250).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException($"Timeout aguardando o Vite dev server ficar pronto em {Url}.");
    }

    private async Task PumpAsync(StreamReader reader, bool stdout)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            if (stdout)
            {
                _logger.LogDebug("[vite] {Line}", line);
            }
            else
            {
                _logger.LogInformation("[vite:err] {Line}", line);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is null)
        {
            return;
        }

        try
        {
            if (!_process.HasExited)
            {
                _logger.LogInformation("Encerrando o Vite dev server (PID {Pid})", _process.Id);
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao encerrar o Vite dev server");
        }

        _process.Dispose();
    }
}
