using WheelWizard.CustomMusic.Catalog;

namespace WheelWizard.CustomMusic;

public static class CustomMusicExtensions
{
    public static IServiceCollection AddCustomMusic(this IServiceCollection services)
    {
        services.AddSingleton<IMusicCatalogService, MusicCatalogService>();
        services.AddSingleton<ICustomMusicService, CustomMusicService>();
        services.AddSingleton<IMusicPlayer, MusicPlayer>();
        return services;
    }
}
