using System.Text;
using PCDoctor.Core;
using PCDoctor.Diagnostics;
using PCDoctor.Platform.Windows;

namespace PCDoctor.Tests;

public class ProcessRunnerTests
{
    // A plain shell per OS, so the runner itself is tested everywhere.
    static (string exe, string[] args) Shell(string script) =>
        OperatingSystem.IsWindows() ? ("cmd.exe", ["/c", script]) : ("/bin/sh", ["-c", script]);

    static string Tmp => Path.GetTempPath();

    [Fact]
    public async Task Captures_exit_code_stdout_and_stderr()
    {
        var (exe, args) = Shell("echo hello && echo oops 1>&2 && exit 3");
        var r = await ProcessRunner.RunAsync(exe, args, Tmp, 30, 10_000, CancellationToken.None);
        Assert.Equal(3, r.ExitCode);
        Assert.Contains("hello", r.StdOut);
        Assert.Contains("oops", r.StdErr);
        Assert.False(r.TimedOut);
    }

    [Fact]
    public async Task Timeout_kills_the_process()
    {
        var (exe, args) = Shell(OperatingSystem.IsWindows() ? "ping -n 60 127.0.0.1 >NUL" : "sleep 60");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = await ProcessRunner.RunAsync(exe, args, Tmp, 1, 10_000, CancellationToken.None);
        Assert.True(r.TimedOut);
        Assert.Null(r.ExitCode);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task Stop_cancels_and_kills_quickly()
    {
        var (exe, args) = Shell(OperatingSystem.IsWindows() ? "ping -n 60 127.0.0.1 >NUL" : "sleep 60");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProcessRunner.RunAsync(exe, args, Tmp, 60, 10_000, cts.Token));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task Long_output_is_cut_and_saved_in_full()
    {
        var (exe, args) = Shell(OperatingSystem.IsWindows()
            ? "for /L %i in (1,1,3000) do @echo line %i"
            : "i=1; while [ $i -le 3000 ]; do echo line $i; i=$((i+1)); done");
        var r = await ProcessRunner.RunAsync(exe, args, Tmp, 60, 2_000, CancellationToken.None);
        Assert.True(r.Truncated);
        Assert.True(r.StdOut.Length < 2_200);
        Assert.Contains("line 1\n", r.StdOut.Replace("\r", ""));
        Assert.Contains("line 3000", r.StdOut); // the end is kept
        Assert.NotNull(r.FullOutputFile);
        Assert.Contains("line 1500", await File.ReadAllTextAsync(r.FullOutputFile!));
        File.Delete(r.FullOutputFile!);
    }

    [Fact]
    public async Task A_command_waiting_for_input_does_not_hang()
    {
        var (exe, args) = Shell(OperatingSystem.IsWindows() ? "set /p x=" : "cat");
        var r = await ProcessRunner.RunAsync(exe, args, Tmp, 10, 10_000, CancellationToken.None);
        Assert.False(r.TimedOut);
    }
}

public class PowerShellTests
{
    [Fact]
    public void Command_is_passed_encoded_so_quotes_and_pipes_survive()
    {
        string cmd = "Get-ChildItem \"C:\\Program Files\" | Where-Object { $_.Name -like '*a*' }; echo `$x & 'b'";
        var args = PowerShellTool.BuildArguments(cmd);
        Assert.Equal("-EncodedCommand", args[^2]);
        string decoded = Encoding.Unicode.GetString(Convert.FromBase64String(args[^1]));
        Assert.EndsWith(cmd, decoded);
        Assert.Contains("-NoProfile", args);
        Assert.Contains("-NonInteractive", args);
    }

    [WindowsFact]
    public async Task Runs_powershell_as_current_user_without_elevation()
    {
        var tool = new PowerShellTool(new AgentSettings());
        var r = await tool.RunAsync(Json.Parse("""
            {"command":"([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator); 'æøå \"quoted\"'"}
            """), CancellationToken.None);
        Assert.False(r.IsError, r.Content);
        Assert.Contains("æøå", System.Text.Json.JsonDocument.Parse(r.Content).RootElement.GetProperty("stdout").GetString());
    }

    [WindowsFact]
    public async Task Hardware_tool_returns_cpu_and_gpu()
    {
        var r = await new HardwareTool().RunAsync(Json.Parse("{}"), CancellationToken.None);
        Assert.False(r.IsError);
        Assert.Contains("\"cpu\"", r.Content);
        Assert.Contains("\"gpu\"", r.Content);
    }
}

public class DiagnosticsToolTests
{
    [Theory]
    [InlineData("1.1.1.1; rm -rf /")]
    [InlineData("-c 100 example.com")]
    [InlineData("example.com && calc")]
    [InlineData("")]
    public async Task Tcp_rejects_anything_that_is_not_a_plain_host(string host)
    {
        var r = await new TcpTool().RunAsync(Json.Parse($$"""{"host":{{System.Text.Json.JsonSerializer.Serialize(host)}},"port":443}"""), CancellationToken.None);
        Assert.True(r.IsError);
    }

    [Fact]
    public async Task Ping_reports_invalid_hosts_without_running_them()
    {
        var r = await new PingTool().RunAsync(Json.Parse("""{"hosts":["bad host; whoami"],"count":1}"""), CancellationToken.None);
        Assert.Contains("Not a valid hostname", r.Content);
    }

    [Fact]
    public async Task Processes_tool_returns_structured_json()
    {
        var r = await new ProcessesTool().RunAsync(Json.Parse("{}"), CancellationToken.None);
        Assert.False(r.IsError);
        var root = System.Text.Json.JsonDocument.Parse(r.Content).RootElement;
        Assert.True(root.GetProperty("process_count").GetInt32() > 0);
        Assert.True(root.GetProperty("top_by_memory").GetArrayLength() > 0);
    }

    [Fact]
    public async Task Tcp_to_a_closed_local_port_reports_not_connected()
    {
        var r = await new TcpTool().RunAsync(Json.Parse("""{"host":"127.0.0.1","port":1}"""), CancellationToken.None);
        Assert.Contains("\"connected\":false", r.Content);
    }
}
