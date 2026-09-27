using Microsoft.Extensions.DependencyInjection;
using Pos.Shared.Scanning;

namespace Pos.SharedUI.Scanning;

/// <summary>Registers barcode scanning for an app.</summary>
public static class ScanningServiceCollectionExtensions
{
    /// <summary>
    /// Adds the scan router and the keyboard-wedge listener. A desktop or
    /// handheld app has one user, so one of each for the app; a web server has
    /// many, so one per signed-in session.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="perSession">True for a web server, false for a device app.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddBarcodeScanning(this IServiceCollection services, bool perSession)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (perSession)
        {
            services.AddScoped(_ => new ScanRouter());
        }
        else
        {
            // One router for the app, so a handheld's broadcast receiver (which
            // lives outside any page) reaches the screen that is open.
            services.AddSingleton(_ => new ScanRouter());
        }

        // The page script belongs to one page, so the listener does too.
        services.AddScoped<KeyboardScanListener>();
        return services;
    }
}
