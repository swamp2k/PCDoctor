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
        var info = Updater.ParseRelease(Release("v1.0.30"), new Version(1, 0, 25));
        Assert.Equal(new Version(1, 0, 30), info?.Version);
    }

    [Fact]
    public void Same_or_older_release_is_ignored() =>
        Assert.Null(Updater.ParseRelease(Release("v1.0.30"), new Version(1, 0, 30)));

    [Fact]
    public void Link_outside_the_project_is_refused() =>
        Assert.Null(Updater.ParseRelease(Release("v1.0.30", "https://evil.example/PCDoctor.exe"), new Version(1, 0, 1)));

    [Fact]
    public void Release_without_the_exe_is_ignored() =>
        Assert.Null(Updater.ParseRelease(Release("v1.0.30", name: "other.zip"), new Version(1, 0, 1)));

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
        Assert.Equal(expected, Updater.CanSelfUpdate(path));
}
