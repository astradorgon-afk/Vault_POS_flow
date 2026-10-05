namespace Pos.Web.Services;

public sealed partial class VaultFlowApiClient
{
    /// <summary>Gets one page of audit activity using the supplied filters.</summary>
    public Task<ApiResult<PosAuditLogPage>> GetAuditLogAsync(
        string? category,
        string? search,
        DateOnly? from,
        DateOnly? to,
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        List<string> parameters = [$"offset={Math.Max(0, offset)}", $"limit={Math.Clamp(limit, 1, 100)}"];
        if (!string.IsNullOrWhiteSpace(category))
        {
            parameters.Add($"category={Uri.EscapeDataString(category)}");
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            parameters.Add($"search={Uri.EscapeDataString(search.Trim())}");
        }
        if (from is { } start)
        {
            parameters.Add($"from={start:yyyy-MM-dd}");
        }
        if (to is { } end)
        {
            parameters.Add($"to={end:yyyy-MM-dd}");
        }

        return GetAsync<PosAuditLogPage>($"/api/v1/audit?{string.Join('&', parameters)}", cancellationToken);
    }
}
