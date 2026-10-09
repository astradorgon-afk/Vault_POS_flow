namespace Pos.Web.Services;

/// <summary>
/// Carries a one-time "offline access is ready" notice from the sign-in page
/// into the workspace so the message appears after sign-in instead of
/// interrupting the login form. Lives for the lifetime of the Blazor circuit.
/// </summary>
public sealed class OfflineReadinessNotice
{
    /// <summary>Raised when the notice is announced, updated, or dismissed.</summary>
    public event Action? Changed;

    /// <summary>Gets whether the workspace should show the notice.</summary>
    public bool IsVisible { get; private set; }

    /// <summary>Gets whether VaultFlow is already installed on this device.</summary>
    public bool IsInstalled { get; private set; }

    /// <summary>Announces that offline access is ready on this device.</summary>
    /// <param name="installed">Whether the app is already installed on this device.</param>
    public void Announce(bool installed)
    {
        IsInstalled = installed;
        IsVisible = true;
        Changed?.Invoke();
    }

    /// <summary>Hides the notice until the next sign-in.</summary>
    public void Dismiss()
    {
        if (!IsVisible)
        {
            return;
        }

        IsVisible = false;
        Changed?.Invoke();
    }
}
