using Avalonia;

namespace PCDoctor.App;

class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // The FPS helper: the same exe, started with administrator rights by "Hvorfor hakker mit spil?". No window.
        if (OperatingSystem.IsWindows() && args.Length > 0 && args[0] == PCDoctor.Platform.Windows.FpsCapture.HelperArg)
            return PCDoctor.Platform.Windows.FpsCapture.RunHelper(args);
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
