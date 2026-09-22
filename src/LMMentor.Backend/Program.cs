using LMMentor.Backend.Admin;
using LMMentor.Backend.Data;
using LMMentor.Backend.Dev;
using Microsoft.AspNetCore.Authentication.Cookies;

var builder = WebApplication.CreateBuilder(args);

// Serialização JSON em camelCase para todas as Minimal APIs (alinhado ao frontend).
builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
});

builder.Services.AddHttpClient();
builder.AddLmMentorData();

// Sessão do admin via cookie autenticado (HttpOnly, SameSite=Lax).
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "lmmentor.admin";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;

        // Rotas de API retornam 401/403 (em vez de redirecionar para login) para o frontend tratar.
        options.Events.OnRedirectToLogin = context =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            }

            context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };

        options.Events.OnRedirectToAccessDenied = context =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            }

            context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };
    });

builder.Services.AddAuthorization();

// Em desenvolvimento (dotnet watch), o backend sobe o Vite dev server e faz proxy da UI para ele.
// Defina LMMENTOR_DEV_PROXY=false para desativar e servir o build estático mesmo em Development.
var devProxyEnabled = builder.Environment.IsDevelopment()
    && builder.Configuration["LMMENTOR_DEV_PROXY"] != "false";

if (devProxyEnabled)
{
    builder.AddViteIntegration();
}

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

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
app.MapAuthEndpoints();
app.MapApiEndpoints();
app.MapOllamaCompatibilityEndpoints();

// API pública OpenAI-compatible (autenticada por chave Bearer, sem cookie).
app.MapRelayEndpoints();

app.Run();
