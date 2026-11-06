using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;

namespace Pos.Web.Components.Layout;

/// <summary>
/// A prefix-matching navigation link that stays inactive inside sibling
/// sections nested under its own address. <c>/restock</c> owns
/// <c>/restock?store=…</c> but not <c>/restock/approvals</c>, which has its own
/// link; without the exclusion both links light up at once.
/// </summary>
public sealed class SectionNavLink : NavLink
{
    /// <summary>Base-relative paths (for example <c>restock/approvals</c>) this link must not claim.</summary>
    [Parameter]
    public IReadOnlyList<string> Except { get; set; } = [];

    [Inject]
    private NavigationManager Navigation { get; set; } = default!;

    /// <inheritdoc />
    protected override bool ShouldMatch(string uriAbsolute)
    {
        if (!base.ShouldMatch(uriAbsolute))
        {
            return false;
        }

        string relative = Navigation.ToBaseRelativePath(uriAbsolute);
        int query = relative.IndexOfAny(['?', '#']);
        if (query >= 0)
        {
            relative = relative[..query];
        }

        foreach (string excluded in Except)
        {
            if (relative.Equals(excluded, StringComparison.OrdinalIgnoreCase) ||
                relative.StartsWith(excluded + "/", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}
