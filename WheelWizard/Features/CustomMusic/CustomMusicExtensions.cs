using WheelWizard.CustomMusic.Catalog;

namespace WheelWizard.CustomMusic;

public static class CustomMusicExtensions
{
    public static IServiceCollection AddCustomMusic(this IServiceCollection services)
    {
        services.AddSingleton<IMusicCatalogService, MusicCatalogService>();
        services.AddSingleton<ICustomMusicService, CustomMusicService>();
        return services;
    }
}
