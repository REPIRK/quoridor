using System.IO;
using System.Windows;
using Quoridor.App.Theme;

namespace Quoridor.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // The two things the session library cannot know for itself, said once and before
        // anything asks: where this platform keeps a preferences file, and where a game
        // should look for the engine preferences it is played under. On Windows both
        // answers are the roaming profile; on a phone neither would be.
        Settings.UseFile(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Quoridor",
            "settings.json"));

        GameSession.Preferences = () => EnginePreferences.Of(Settings.Current);

        AppTheme theme = Settings.Current.Theme == nameof(AppTheme.Light) ? AppTheme.Light : AppTheme.Dark;

        // Brushes must exist before any window resolves its DynamicResource references,
        // otherwise the first frame renders with default colours.
        Palette.Install(Resources, theme);

        // However the theme gets changed — the button, Ctrl+T, the settings screen — it
        // is remembered from here rather than from each of those places.
        Palette.Changed += changed =>
        {
            Settings.Current.Theme = changed.ToString();
            Settings.Current.Save();
        };

        new MainWindow().Show();
    }
}
