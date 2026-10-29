using Microsoft.Extensions.Logging;

using Tunapp.Core;

namespace Tunapp
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                    fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
                });

            builder.Services.AddSingleton<IAppEnvironment, MauiAppEnvironment>();
            builder.Services.AddSingleton<IAppNavigation, MauiNavigationService>();
            builder.Services.AddSingleton(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
                { Timeout = TimeSpan.FromSeconds(20) });
            // Firebase client configuration is public; authorization remains in Firestore rules.
            builder.Services.AddSingleton(services => new FirebaseAuthClient(services.GetRequiredService<HttpClient>(),
                "AIzaSyBiBSM11DpKJQsLtTnmTWMd4PM8u-pG4Uo"));
            builder.Services.AddSingleton(services => new FarmService(Path.Combine(
                services.GetRequiredService<IAppEnvironment>().DataDirectory, "tunapp-v1.json")));
            builder.Services.AddSingleton<CloudViewModel>();
            builder.Services.AddTransient<LocalFarmViewModel>();
            builder.Services.AddSingleton<CloudPage>();
            builder.Services.AddTransient<MainPage>();
            builder.Services.AddSingleton<AppShell>();

#if DEBUG
    		builder.Logging.AddDebug();
#endif

            return builder.Build();
        }
    }
}
