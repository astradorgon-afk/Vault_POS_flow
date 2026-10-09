using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Authentication.Cookies;
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
        options.AddPolicy(permission.Code, policy => policy.RequireAssertion(context =>
            context.User.HasClaim("permission", permission.Code)
            || (permission.Code == Permissions.Administration.ViewAudit &&
                (context.User.IsInRole(Roles.Owner) || context.User.IsInRole(Roles.Administrator)))));
    }

    options.AddPolicy(
        "people.manage",
        policy => policy.RequireAssertion(context =>
            context.User.HasClaim("permission", Permissions.Administration.ManageUsers)
            || context.User.HasClaim("permission", Permissions.Administration.ManageRoles)));
});
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options => options.LoginPath = "/login");
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<UserSession>();
builder.Services.AddScoped<NotificationStore>();
builder.Services.AddScoped<PageTrail>();
builder.Services.AddSingleton<PwaSessions>();
builder.Services.AddSingleton<PwaLoginHandoffs>();
builder.Services.AddHttpClient("pwa-api", (services, client) =>
    client.BaseAddress = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<ApiOptions>>().Value.BaseAddress);
builder.Services.AddBarcodeScanning(perSession: true);
builder.Services.AddScoped<VaultFlowAuthenticationStateProvider>();
builder.Services.AddScoped<AuthenticationStateProvider>(services =>
    services.GetRequiredService<VaultFlowAuthenticationStateProvider>());
builder.Services.AddHttpClient<VaultFlowApiClient>((services, client) =>
{
    ApiOptions options = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<ApiOptions>>().Value;
    client.BaseAddress = options.BaseAddress;
});
builder.Services.AddOptions<ApiOptions>()
    .BindConfiguration(ApiOptions.SectionName)
    .Validate(options => options.BaseAddress.IsAbsoluteUri, "Api:BaseAddress must be an absolute URI.")
    .ValidateOnStart();

var app = builder.Build();

// Configure the HTTP request pipeline.
var forwarded = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedProto
};
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
app.MapPwaGateway();
app.MapFallbackToFile("/offline/{*path:nonfile}", "offline/index.html");
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
