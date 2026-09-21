using LMMentor.Backend.Dev;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient();

// Em desenvolvimento (dotnet watch), o backend sobe o Vite dev server e faz proxy da UI para ele.
// Defina LMMENTOR_DEV_PROXY=false para desativar e servir o build estático mesmo em Development.
var devProxyEnabled = builder.Environment.IsDevelopment()
    && builder.Configuration["LMMENTOR_DEV_PROXY"] != "false";

if (devProxyEnabled)
{
    var adminUiPath = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "LMMentor.AdminUI"));
    const string viteUrl = "http://localhost:5173";

    builder.Services.AddSingleton<ViteDevServer>(sp => new ViteDevServer(sp.GetRequiredService<ILogger<ViteDevServer>>(), adminUiPath, viteUrl));
}

var app = builder.Build();

// O AdminUI é sempre servido a partir de /admin; a raiz redireciona para lá.
app.MapGet("/", () => Results.Redirect("/admin/"));

if (devProxyEnabled)
{
    // Somente /admin/* vai para o Vite dev server; todas as demais rotas seguem para o backend.
    app.UseMiddleware<ViteProxyMiddleware>();
}
else
{
    // Produção: serve o build do AdminUI emitido em wwwroot pelo Vite, sob o prefixo /admin.
    var webRoot = Path.Combine(app.Environment.ContentRootPath, "wwwroot");
    Directory.CreateDirectory(webRoot);

    app.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(webRoot), RequestPath = "/admin" });

    var indexHtml = Path.Combine(webRoot, "index.html");
    if (File.Exists(indexHtml))
    {
        // /admin/ e rotas SPA do AdminUI voltam para o index.html.
        app.MapGet("/admin/", async context => await context.Response.SendFileAsync(indexHtml));

        app.MapFallback(async context =>
        {
            var path = context.Request.Path;
            var isAdminRoute = path == "/admin" || path.StartsWithSegments("/admin/");
            if (!isAdminRoute)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            await context.Response.SendFileAsync(indexHtml);
        });
    }
}

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

app.Run();
