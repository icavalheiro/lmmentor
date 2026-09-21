using LMMentor.Backend.Data;
using LMMentor.Backend.Dev;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient();
builder.AddLmMentorData();

// Em desenvolvimento (dotnet watch), o backend sobe o Vite dev server e faz proxy da UI para ele.
// Defina LMMENTOR_DEV_PROXY=false para desativar e servir o build estático mesmo em Development.
var devProxyEnabled = builder.Environment.IsDevelopment()
    && builder.Configuration["LMMENTOR_DEV_PROXY"] != "false";

if (devProxyEnabled)
{
    builder.AddViteIntegration();
}

var app = builder.Build();

app.BootstrapAdminCredentials();

// O AdminUI é sempre servido a partir de /admin; a raiz redireciona para lá.
app.MapGet("/", () => Results.Redirect("/admin/"));

if (devProxyEnabled)
{
    // Somente /admin/* vai para o Vite dev server; todas as demais rotas seguem para o backend.
    app.UseViteProxy();
}
else
{
    // Produção: serve o build do AdminUI emitido em wwwroot pelo Vite, sob o prefixo /admin.
    app.UseAdminSpaFiles();
}

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

app.Run();
