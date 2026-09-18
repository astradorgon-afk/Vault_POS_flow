using Pos.Api.Authorization;
using Pos.Application.Identity;
using Pos.Domain.Inventory;
using Pos.Infrastructure.Inventory;

namespace Pos.Api.Endpoints;

/// <summary>Authorization-scoped inventory document drill-down routes.</summary>
public static class InventoryTimelineEndpoints
{
    public static IEndpointRouteBuilder MapInventoryTimelineEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/inventory/timeline/{documentType}/{documentId:guid}", GetAsync)
            .RequireAuthorization()
            .WithMetadata(new RequirePermissionAttribute(Permissions.Inventory.View) { Scope = ScopeSource.None })
            .WithTags("Inventory")
            .WithName("GetInventoryDocumentTimeline")
            .WithSummary("Gets the scoped movement chain for an inventory document.");

        return app;
    }

    private static async Task<IResult> GetAsync(
        string documentType,
        Guid documentId,
        InventoryTimelineService timeline,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse(documentType, ignoreCase: true, out ReferenceDocumentType parsed))
        {
            return TypedResults.BadRequest(new { errorCode = "inventory.document_type_invalid" });
        }

        InventoryTimeline? result = await timeline
            .GetAsync(parsed, documentId, cancellationToken)
            .ConfigureAwait(false);

        return result is null
            ? TypedResults.NotFound(new { errorCode = "inventory.timeline_not_found" })
            : TypedResults.Ok(result);
    }
}
