using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.HttpOverrides;
using System.Net;
using Pos.Application.Identity;
using Pos.SharedUI.Scanning;
using Pos.Web.Components;
using Pos.Web.Security;
using Pos.Web.Services;
using Pos.Web.Pwa;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddAuthorizationCore(options =>
{
    foreach (PermissionDefinition permission in Permissions.All)
    {
        // The permission claim is the only grant. A role shortcut here would let
        // a page render that the API then refuses, because RequirePermission on
        // the endpoint reads the permission and nothing else.
        options.AddPolicy(permission.Code, policy => policy.RequireAssertion(context =>
            context.User.HasClaim("permission", permission.Code)));
    }

    options.AddPolicy(
        "people.manage",
        policy => policy.RequireAssertion(context =>
            context.User.HasClaim("permission", Permissions.Administration.ManageUsers)
            || context.User.HasClaim("permission", Permissions.Administration.ManageRoles)));
});
// The authorization middleware judges a full page load from HttpContext.User,
// so the request has to carry the session this browser already holds. Without
// it every reload of an [Authorize] page was challenged and read as a sign-out.
builder.Services.AddWebSessionAuthentication();
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<UserSession>();
builder.Services.AddScoped<ForwardedCaller>();
builder.Services.AddScoped<CircuitHandler, ForwardedCallerCircuitHandler>();
builder.Services.AddTransient<ForwardedCallerHandler>();
builder.Services.AddScoped<NotificationStore>();
builder.Services.AddScoped<OfflineReadinessNotice>();
builder.Services.AddScoped<PageTrail>();
builder.Services.AddSingleton<PwaSessions>();
builder.Services.AddSingleton<PwaLoginHandoffs>();
builder.Services.AddHostedService<PwaSessionSweeper>();
builder.Services.AddHttpClient("pwa-api", (services, client) =>
{
    client.BaseAddress = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<ApiOptions>>().Value.BaseAddress;
    // The snapshot download pages through the catalog and open orders, so this
    // one is allowed longer than a screen would wait.
    client.Timeout = TimeSpan.FromSeconds(120);
}).AddHttpMessageHandler<ForwardedCallerHandler>();
builder.Services.AddBarcodeScanning(perSession: true);
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<WebSessions>();
builder.Services.AddSingleton<OneUseTokens<PendingWebSignIn>>();
builder.Services.AddSingleton<OneUseTokens<WebSessionTicket>>();
builder.Services.AddScoped<WebSessionRestorer>();
builder.Services.AddScoped<VaultFlowAuthenticationStateProvider>();
builder.Services.AddScoped<AuthenticationStateProvider>(services =>
    services.GetRequiredService<VaultFlowAuthenticationStateProvider>());
builder.Services.AddHttpClient<VaultFlowApiClient>((services, client) =>
{
    ApiOptions options = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<ApiOptions>>().Value;
    client.BaseAddress = options.BaseAddress;
    // Someone is watching a spinner behind every one of these. The default 100
    // seconds is far past the point where a screen should say so and stop.
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddOptions<ApiOptions>()
    .BindConfiguration(ApiOptions.SectionName)
    .Validate(options => options.BaseAddress.IsAbsoluteUri, "Api:BaseAddress must be an absolute URI.")
    .ValidateOnStart();

var app = builder.Build();

// Configure the HTTP request pipeline.
var forwarded = new ForwardedHeadersOptions
{
    // The address goes on to the API as the caller (see ForwardedCaller), so
    // its sign-in limits count each browser rather than this server.
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
};
TrustedProxies.AddFrom(forwarded, app.Configuration);
if (app.Environment.IsDevelopment())
{
    // Docker Desktop forwards local ngrok/Caddy requests from an address owned
    // by this host. Trust only those local proxy addresses for the HTTPS scheme.
    foreach (IPAddress address in Dns.GetHostAddresses(Dns.GetHostName()))
    {
        if (IPAddress.IsLoopback(address))
        {
            continue;
        }
        forwarded.KnownProxies.Add(address);
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            forwarded.KnownProxies.Add(address.MapToIPv6());
        }
    }
}
// Published phone pilots are exposed through a local ngrok agent, which also
// connects from loopback. The framework's default loopback trust applies
// outside Development; never trust arbitrary peers.
app.UseForwardedHeaders(forwarded);
app.Use((context, next) =>
{
    context.RequestServices.GetRequiredService<ForwardedCaller>().CaptureFrom(context);
    return next(context);
});

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapWebSessionEndpoints();
app.MapPwaGateway();
app.MapFallbackToFile("/offline/{*path:nonfile}", "offline/index.html");
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
