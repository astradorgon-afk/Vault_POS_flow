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

    /// <summary>
    /// Further base-relative sections this link stands for. One menu entry can
    /// open a module of several pages (Restock: approvals, fulfillment,
    /// requests) and stay current on any of them.
    /// </summary>
    [Parameter]
    public IReadOnlyList<string> Also { get; set; } = [];

    [Inject]
    private NavigationManager Navigation { get; set; } = default!;

    /// <inheritdoc />
    protected override bool ShouldMatch(string uriAbsolute)
    {
        string relative = Navigation.ToBaseRelativePath(uriAbsolute);
        int query = relative.IndexOfAny(['?', '#']);
        if (query >= 0)
        {
            relative = relative[..query];
        }

        if (!base.ShouldMatch(uriAbsolute) && !Also.Any(section => Within(relative, section)))
        {
            return false;
        }

        return !Except.Any(section => Within(relative, section));
    }

    private static bool Within(string relative, string section)
        => relative.Equals(section, StringComparison.OrdinalIgnoreCase) ||
           relative.StartsWith(section + "/", StringComparison.OrdinalIgnoreCase);
}
