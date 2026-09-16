namespace Pos.Web.Services;

/// <summary>Connection settings for the VaultFlow API.</summary>
public sealed class ApiOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "Api";

    /// <summary>Gets or sets the API's absolute base address.</summary>
    public Uri BaseAddress { get; set; } = new("http://localhost:5177");
}
