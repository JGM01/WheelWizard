using WheelWizard.Core.Recomp;
using System.Net.Http.Headers;
using WheelWizard.Shared;

namespace WheelWizard.Recomp;

public static class RecompExtensions
{
    /// <summary>
    /// Registers the Mario Kart Wii recomp frontend.
    /// This release-based setup integration targets Windows, so other platforms register none of these services;
    /// <c>ISettingsManager.IsRecompModeActive()</c> is false there, so nothing ever resolves these.
    /// </summary>
    public static IServiceCollection AddRecomp(this IServiceCollection services)
    {
        if (!OperatingSystem.IsWindows())
            return services;

        services
            .AddHttpClient(RecompSetupDownloader.HttpClientName)
            .ConfigureHttpClient(
                (serviceProvider, client) =>
                {
                    client.ConfigureWheelWizardClient(serviceProvider);

                    // GitHub release assets are served as an octet-stream redirect.
                    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
                }
            );

        services.AddSingleton<IRecompDolphinDataService, RecompDolphinDataService>();
        services.AddSingleton<IRecompEnvironment, RecompEnvironment>();
        services.AddSingleton<IRecompProcessRunner, RecompProcessRunner>();
        services.AddSingleton<IRecompSetupDownloader, RecompSetupDownloader>();
        services.AddSingleton<IRecompRetroWfcPayloadProbe, RecompRetroWfcPayloadProbe>();
        services.AddSingleton<IRecompInstallService, RecompInstallService>();
        services.AddTransient<RecompLauncher>();

        return services;
    }
}
