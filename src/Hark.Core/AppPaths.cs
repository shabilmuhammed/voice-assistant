namespace Hark.Core;

/// <summary>Where Hark keeps its files: %LOCALAPPDATA%\Hark on Windows.</summary>
public static class AppPaths
{
    public static string DataDirectory
    {
        get
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hark");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");
    public static string DatabaseFile => Path.Combine(DataDirectory, "chats.db");
    public static string LogFile => Path.Combine(DataDirectory, "hark.log");
}
