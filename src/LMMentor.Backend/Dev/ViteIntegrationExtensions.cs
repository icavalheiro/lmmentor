using Microsoft.Extensions.FileProviders;

namespace LMMentor.Backend.Dev;

/// <summary>
/// Extensões para a integração do AdminUI com o backend: Vite dev server em desenvolvimento
/// e build estático servido de wwwroot em produção.
/// </summary>
public static class ViteIntegrationExtensions
{
    /// <summary>Registra o Vite dev server (watch mode) iniciado pelo backend.</summary>
    public static WebApplicationBuilder AddViteIntegration(this WebApplicationBuilder builder)
    {
        var adminUiPath = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "LMMentor.AdminUI"));
        const string viteUrl = "http://localhost:5173";

        builder.Services.AddSingleton<ViteDevServer>(sp => new ViteDevServer(sp.GetRequiredService<ILogger<ViteDevServer>>(), adminUiPath, viteUrl));
        return builder;
    }

    /// <summary>Encaminha /admin e /admin/* para o Vite dev server; as demais rotas seguem para o backend.</summary>
    public static IApplicationBuilder UseViteProxy(this IApplicationBuilder app)
    {
        app.UseMiddleware<ViteProxyMiddleware>();
        return app;
    }

    /// <summary>Serve o build do AdminUI emitido em wwwroot pelo Vite, sob o prefixo /admin.</summary>
    public static WebApplication UseAdminSpaFiles(this WebApplication app)
    {
        var webRoot = Path.Combine(app.Environment.ContentRootPath, "wwwroot");
        Directory.CreateDirectory(webRoot);

        app.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(webRoot), RequestPath = "/admin" });

        var indexHtml = Path.Combine(webRoot, "index.html");
        if (!File.Exists(indexHtml))
        {
            return app;
        }

        // /admin/ e rotas SPA do AdminUI voltam para o index.html.
        app.MapGet("/admin/", async context => await context.Response.SendFileAsync(indexHtml));

        app.MapFallback(async context =>
        {
            var path = context.Request.Path;
            // Prefixo "/admin" (sem barra final): no .NET 10, StartsWithSegments("/admin/") falha para subcaminhos.
            var isAdminRoute = path == "/admin" || path.StartsWithSegments("/admin");
            if (!isAdminRoute)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            await context.Response.SendFileAsync(indexHtml);
        });

        return app;
    }
}
