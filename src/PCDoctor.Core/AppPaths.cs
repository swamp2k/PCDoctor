namespace PCDoctor.Core;

/// <summary>The app's local data folder (%LOCALAPPDATA%\PCDoctor on Windows).</summary>
public static class AppPaths
{
    public static string DataDir
    {
        get
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCDoctor");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string SubDir(string name)
    {
        string dir = Path.Combine(DataDir, name);
        Directory.CreateDirectory(dir);
        return dir;
    }
}
