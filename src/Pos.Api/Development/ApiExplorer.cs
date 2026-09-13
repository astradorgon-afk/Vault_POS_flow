using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Scalar.AspNetCore;

namespace Pos.Api.Development;

/// <summary>
/// The interactive API explorer served at <c>/scalar</c> in Development: every
/// route, a sign-in token field, and a request form per endpoint. It is a
/// developer surface, not the product UI, and is never mapped outside
/// Development.
/// </summary>
internal static class ApiExplorer
{
    /// <summary>The route prefix the explorer page and its assets are served under.</summary>
    public const string RoutePrefix = "/scalar";

    // The API's own policy is default-src 'none' because it serves JSON only.
    // The explorer is an HTML page with inline configuration and styles, so its
    // route alone gets a same-origin policy; frames and base URIs stay closed.
    private const string ExplorerContentSecurityPolicy =
        "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data:; font-src 'self' data:; connect-src 'self'; worker-src 'self' blob:; " +
        "frame-ancestors 'none'; base-uri 'none'";

    /// <summary>Registers the OpenAPI document, including the bearer token scheme.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection, for chaining.</returns>
    public static IServiceCollection AddApiDocument(this IServiceCollection services)
        => services.AddOpenApi(options =>
        {
            options.AddDocumentTransformer<BearerSecuritySchemeTransformer>();
            options.AddOperationTransformer<BearerRequirementTransformer>();
        });

    /// <summary>Maps the OpenAPI document and the explorer page. Development only.</summary>
    /// <param name="app">The application.</param>
    public static void MapApiExplorer(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapOpenApi();

        app.UseWhen(
            context => context.Request.Path.StartsWithSegments(RoutePrefix, StringComparison.OrdinalIgnoreCase),
            branch => branch.Use(async (context, next) =>
            {
                context.Response.OnStarting(() =>
                {
                    context.Response.Headers.ContentSecurityPolicy = ExplorerContentSecurityPolicy;
                    return Task.CompletedTask;
                });

                await next(context).ConfigureAwait(false);
            }));

        app.MapScalarApiReference(options => options
            .WithTitle("VaultFlow API")
            .DisableDefaultFonts()
            .AddPreferredSecuritySchemes(BearerSecuritySchemeTransformer.SchemeName));
    }

    /// <summary>Declares the JWT bearer scheme so the explorer offers a token field.</summary>
    private sealed class BearerSecuritySchemeTransformer : IOpenApiDocumentTransformer
    {
        public const string SchemeName = "Bearer";

        public Task TransformAsync(
            OpenApiDocument document,
            OpenApiDocumentTransformerContext context,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(document);

            document.Components ??= new OpenApiComponents();
            document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>(StringComparer.Ordinal);
            document.Components.SecuritySchemes[SchemeName] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                Description = "Sign in with POST /api/v1/auth/login and paste the returned accessToken.",
            };

            return Task.CompletedTask;
        }
    }

    /// <summary>Marks every route that is not anonymous as needing the bearer token.</summary>
    private sealed class BearerRequirementTransformer : IOpenApiOperationTransformer
    {
        public Task TransformAsync(
            OpenApiOperation operation,
            OpenApiOperationTransformerContext context,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(operation);
            ArgumentNullException.ThrowIfNull(context);

            if (context.Description.ActionDescriptor.EndpointMetadata.OfType<IAllowAnonymous>().Any())
            {
                return Task.CompletedTask;
            }

            operation.Security ??= [];
            operation.Security.Add(new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference(BearerSecuritySchemeTransformer.SchemeName, context.Document)] = [],
            });

            return Task.CompletedTask;
        }
    }
}
