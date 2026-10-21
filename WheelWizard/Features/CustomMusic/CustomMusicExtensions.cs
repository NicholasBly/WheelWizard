using WheelWizard.CustomMusic.Catalog;
using WheelWizard.CustomMusic.Library;
using WheelWizard.CustomMusic.Playback;
using WheelWizard.CustomMusic.ViewModels;

namespace WheelWizard.CustomMusic;

public static class CustomMusicExtensions
{
    public static IServiceCollection AddCustomMusic(this IServiceCollection services)
    {
        services.AddSingleton<IMusicCatalogService, MusicCatalogService>();
        services.AddSingleton<ICustomMusicLibrary, CustomMusicLibrary>();
        services.AddSingleton<IGameDiscMusicReader, GameDiscMusicReader>();
        services.AddSingleton<IMusicReferenceService, MusicReferenceService>();
        services.AddSingleton<ICustomMusicInstaller, CustomMusicInstaller>();
        services.AddSingleton<ICustomMusicService, CustomMusicService>();
        services.AddSingleton<IAudioOutput>(_ => AudioOutput.Create());
        services.AddSingleton<IMusicPreviewPlayer, MusicPreviewPlayer>();
        services.AddSingleton<IMusicPreviewService, MusicPreviewService>();
        services.AddTransient<CustomMusicViewModel>();
        return services;
    }
}
