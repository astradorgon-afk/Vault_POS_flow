using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Authentication.Cookies;
using Pos.Application.Identity;
using Pos.Web.Components;
using Pos.Web.Security;
using Pos.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddAuthorizationCore(options =>
{
    foreach (PermissionDefinition permission in Permissions.All)
    {
        options.AddPolicy(permission.Code, policy => policy.RequireClaim("permission", permission.Code));
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
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
