using System.Security.Claims;
using LMMentor.Backend.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace LMMentor.Backend.Admin;

public sealed record LoginRequest(string Username, string Password);

/// <summary>Endpoints de autenticação do admin (login/logout/sessão atual).</summary>
public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/auth/login", async (HttpContext context, LoginRequest request, AdminCredentialService credentials) =>
        {
            if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrEmpty(request.Password))
            {
                return Results.Unauthorized();
            }

            var isValid = credentials.Verify(request.Username, request.Password);
            if (!isValid)
            {
                return Results.Unauthorized();
            }

            var identity = new ClaimsIdentity(CookieAuthenticationDefaults.AuthenticationScheme);
            identity.AddClaim(new Claim(ClaimTypes.Name, request.Username));
            var principal = new ClaimsPrincipal(identity);

            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
            return Results.Ok(new { ok = true });
        });

        app.MapPost("/api/auth/logout", async (HttpContext context) =>
        {
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Ok(new { ok = true });
        });

        app.MapGet("/api/auth/me", (ClaimsPrincipal user) =>
        {
            var isAuthenticated = user.Identity?.IsAuthenticated == true;
            if (!isAuthenticated)
            {
                return Results.Unauthorized();
            }

            return Results.Ok(new { username = user.Identity!.Name });
        });

        return app;
    }
}
