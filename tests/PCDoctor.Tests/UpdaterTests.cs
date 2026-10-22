using PCDoctor.Core;
using Xunit;

namespace PCDoctor.Tests;

public class UpdaterTests
{
    static string Release(string tag, string url = "https://github.com/swamp2k/PCDoctor/releases/download/v1.0.30/PCDoctor.exe", string name = "PCDoctor.exe") =>
        $$"""{"tag_name":"{{tag}}","assets":[{"name":"{{name}}","browser_download_url":"{{url}}"}]}""";

    [Theory]
    [InlineData("v1.0.23", 23)]
    [InlineData("1.0.23+abc123", 23)]
    public void Reads_version_texts(string text, int build)
    {
        Assert.True(Updater.TryParseVersion(text, out var v));
        Assert.Equal(new Version(1, 0, build), v);
    }

    [Fact]
    public void Newer_release_is_offered()
    {
        var info = Updater.ParseRelease(Release("v1.0.30"), new Version(1, 0, 25), Updater.WindowsAsset);
        Assert.Equal(new Version(1, 0, 30), info?.Version);
    }

    [Fact]
    public void Same_or_older_release_is_ignored() =>
        Assert.Null(Updater.ParseRelease(Release("v1.0.30"), new Version(1, 0, 30), Updater.WindowsAsset));

    [Fact]
    public void Link_outside_the_project_is_refused() =>
        Assert.Null(Updater.ParseRelease(Release("v1.0.30", "https://evil.example/PCDoctor.exe"), new Version(1, 0, 1), Updater.WindowsAsset));

    [Fact]
    public void Release_without_the_exe_is_ignored() =>
        Assert.Null(Updater.ParseRelease(Release("v1.0.30", name: "other.zip"), new Version(1, 0, 1), Updater.WindowsAsset));

    [Fact]
    public void Install_swaps_files_and_cleanup_removes_the_old_one()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;
        string exe = Path.Combine(dir, "PCDoctor.exe");
        File.WriteAllText(exe, "old");
        File.WriteAllText(exe + ".new", "new");
        Updater.Install(exe, exe + ".new");
        Assert.Equal("new", File.ReadAllText(exe));
        Assert.Equal("old", File.ReadAllText(exe + ".old"));
        Updater.CleanUp(exe);
        Assert.False(File.Exists(exe + ".old"));
    }

    [Theory]
    [InlineData(@"C:\Users\m\Downloads\PCDoctor.exe", true)]
    [InlineData(@"C:\Users\m\Downloads\PCDoctor (1).exe", true)]
    [InlineData(@"C:\Program Files\dotnet\dotnet.exe", false)]
    [InlineData("/usr/bin/dotnet", false)]
    public void Self_update_works_whatever_the_exe_is_called(string path, bool expected) =>
        Assert.Equal(expected, Updater.CanSelfUpdate(path, windows: true));

    [Theory]
    [InlineData("/home/deck/PCDoctor-linux-x64", true)]
    [InlineData("/home/deck/Downloads/PCDoctor", true)]
    [InlineData("/usr/bin/dotnet", false)]
    [InlineData("/home/deck/src/PCDoctor.dll", false)]
    public void Self_update_works_on_linux(string path, bool expected) =>
        Assert.Equal(expected, Updater.CanSelfUpdate(path, windows: false));

    [Fact]
    public void Linux_picks_the_linux_file_from_a_release()
    {
        string json = """{"tag_name":"v1.0.30","assets":[""" +
            """{"name":"PCDoctor.exe","browser_download_url":"https://github.com/swamp2k/PCDoctor/releases/download/v1.0.30/PCDoctor.exe"},""" +
            """{"name":"PCDoctor-linux-x64","browser_download_url":"https://github.com/swamp2k/PCDoctor/releases/download/v1.0.30/PCDoctor-linux-x64"}]}""";
        Assert.EndsWith("/PCDoctor-linux-x64", Updater.ParseRelease(json, new Version(1, 0, 1), Updater.LinuxAsset)!.DownloadUrl);
        Assert.EndsWith("/PCDoctor.exe", Updater.ParseRelease(json, new Version(1, 0, 1), Updater.WindowsAsset)!.DownloadUrl);
    }
}
