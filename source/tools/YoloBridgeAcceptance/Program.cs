using System.Diagnostics;
using System.Text.Json;
using VisionPlatform.Algorithms.AI;

if (args.Length != 2 || !File.Exists(args[0]))
{
    Console.Error.WriteLine("Usage: YoloBridgeAcceptance <existing-python.exe> <report.json>. Missing Python is a failure, never a skipped test.");
    return 2;
}
var python = Path.GetFullPath(args[0]); var reportPath = Path.GetFullPath(args[1]);
var directory = Path.Combine(Path.GetTempPath(), "VisionPlatform-YoloProcessTest-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
var allPids = new List<int>(); var results = new List<object>(); var failures = new List<string>();
var runner = new PythonYoloBridgeRunner();
try
{
    await Run("normal-utf8-stdout", async () =>
    {
        var (script, request) = Fixture("import sys\nprint('真实协议响应', flush=True)\n");
        var response = await runner.RunAsync(python, script, request, 5000, default);
        Assert(response.Trim() == "真实协议响应", "UTF-8 stdout changed."); return new { response = response.Trim() };
    });
    await Run("timeout-kills-and-waits", async () =>
    {
        var (script, request) = Fixture("import os,sys,json,time\nopen(sys.argv[1]+'.pids','w').write(json.dumps([os.getpid()]))\ntime.sleep(120)\n");
        var task = runner.RunAsync(python, script, request, 1500, default);
        var pids = await ReadPids(request + ".pids");
        await Expect<TimeoutException>(task);
        await AssertDead(pids); return new { pids, timeout_ms = 1500 };
    });
    await Run("cancel-kills-and-waits", async () =>
    {
        using var cts = new CancellationTokenSource();
        var (script, request) = Fixture("import os,sys,json,time\nopen(sys.argv[1]+'.pids','w').write(json.dumps([os.getpid()]))\ntime.sleep(120)\n");
        var task = runner.RunAsync(python, script, request, 15000, cts.Token);
        var pids = await ReadPids(request + ".pids"); var watch = Stopwatch.StartNew(); cts.Cancel();
        await Expect<OperationCanceledException>(task); await AssertDead(pids);
        Assert(watch.ElapsedMilliseconds < 5000, "Cancellation did not promptly terminate the process.");
        return new { pids, cancellation_ms = watch.Elapsed.TotalMilliseconds };
    });
    await Run("stdout-budget-kills-before-timeout", async () =>
    {
        var (script, request) = Fixture("import os,sys,json,time\nopen(sys.argv[1]+'.pids','w').write(json.dumps([os.getpid()]))\nsys.stdout.write('x'*(9*1024*1024));sys.stdout.flush()\ntime.sleep(120)\n");
        var watch = Stopwatch.StartNew(); var task = runner.RunAsync(python, script, request, 15000, default);
        var pids = await ReadPids(request + ".pids"); await Expect<InvalidDataException>(task); await AssertDead(pids);
        Assert(watch.ElapsedMilliseconds < 5000, "Output budget failure waited for the process timeout.");
        return new { pids, elapsed_ms = watch.Elapsed.TotalMilliseconds };
    });
    await Run("stderr-budget-kills-before-timeout", async () =>
    {
        var (script, request) = Fixture("import os,sys,json,time\nopen(sys.argv[1]+'.pids','w').write(json.dumps([os.getpid()]))\nsys.stderr.write('x'*70000);sys.stderr.flush()\ntime.sleep(120)\n");
        var watch = Stopwatch.StartNew(); var task = runner.RunAsync(python, script, request, 15000, default);
        var pids = await ReadPids(request + ".pids"); await Expect<InvalidDataException>(task); await AssertDead(pids);
        Assert(watch.ElapsedMilliseconds < 5000, "Log budget failure waited for the process timeout.");
        return new { pids, elapsed_ms = watch.Elapsed.TotalMilliseconds };
    });
    await Run("cancellation-kills-entire-child-tree", async () =>
    {
        using var cts = new CancellationTokenSource();
        var (script, request) = Fixture("import os,sys,json,time,subprocess\nchild=subprocess.Popen([sys.executable,'-B','-c','import time; time.sleep(120)'],stdin=subprocess.DEVNULL,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,creationflags=getattr(subprocess,'CREATE_NO_WINDOW',0))\nopen(sys.argv[1]+'.pids','w').write(json.dumps([os.getpid(),child.pid]))\ntime.sleep(120)\n");
        var task = runner.RunAsync(python, script, request, 15000, cts.Token); var pids = await ReadPids(request + ".pids");
        Assert(pids.Length == 2 && pids.Distinct().Count() == 2 && pids.All(IsAlive), "Fixture did not establish a live parent/child pair.");
        var watch = Stopwatch.StartNew(); cts.Cancel(); await Expect<OperationCanceledException>(task); await AssertDead(pids);
        return new { pids, cancellation_ms = watch.Elapsed.TotalMilliseconds };
    });
    await Run("nonzero-exit-is-clear-failure", async () =>
    {
        var (script, request) = Fixture("import sys\nprint('{\"schema_version\":1,\"success\":false,\"error\":\"fixture failure\"}',flush=True)\nsys.exit(7)\n");
        var exception = await Expect<InvalidOperationException>(runner.RunAsync(python, script, request, 5000, default));
        Assert(exception.Message.Contains("exit 7") && exception.Message.Contains("fixture failure"), "Exit error lacks diagnostic context.");
        return new { message = exception.Message };
    });
}
finally
{
    // Only processes started by fixtures are eligible for cleanup, if a test failed before its assertions.
    foreach (var pid in allPids)
        try { using var p = Process.GetProcessById(pid); if (!p.HasExited) { p.Kill(entireProcessTree: true); p.WaitForExit(5000); } } catch (ArgumentException) { }
    var absolute = Path.GetFullPath(directory);
    Assert(absolute.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) &&
        Path.GetFileName(absolute).StartsWith("VisionPlatform-YoloProcessTest-", StringComparison.Ordinal), "Unexpected temporary cleanup target.");
    Directory.Delete(absolute, recursive: true);
}
Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
var report = new { utc = DateTimeOffset.UtcNow, python, all_passed = failures.Count == 0, passed = results.Count - failures.Count,
    total = results.Count, tests = results, failures, temporary_directory_released = !Directory.Exists(directory),
    note = "Real OS processes and process trees. These transport fixtures do not measure detection accuracy or industrial uptime." };
await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(JsonSerializer.Serialize(new { report.all_passed, report.passed, report.total, reportPath }));
return failures.Count == 0 ? 0 : 1;

async Task Run(string name, Func<Task<object>> action)
{
    var watch = Stopwatch.StartNew();
    try { var detail = await action(); results.Add(new { name, passed = true, elapsed_ms = watch.Elapsed.TotalMilliseconds, detail }); }
    catch (Exception ex) { failures.Add(name + ": " + ex.Message); results.Add(new { name, passed = false, elapsed_ms = watch.Elapsed.TotalMilliseconds, error = ex.ToString() }); }
}
(string Script, string Request) Fixture(string code)
{
    var id = Guid.NewGuid().ToString("N"); var script = Path.Combine(directory, id + ".py"); var request = Path.Combine(directory, id + ".json");
    File.WriteAllText(script, code); File.WriteAllText(request, "{}"); return (script, request);
}
async Task<int[]> ReadPids(string path)
{
    var watch = Stopwatch.StartNew();
    while (watch.ElapsedMilliseconds < 4000)
    {
        try
        {
            if (File.Exists(path))
            {
                var pids = JsonSerializer.Deserialize<int[]>(await File.ReadAllTextAsync(path));
                if (pids is { Length: > 0 }) { allPids.AddRange(pids); return pids; }
            }
        }
        catch (IOException) { } catch (JsonException) { }
        await Task.Delay(10);
    }
    throw new Exception("The Python fixture did not publish its PID; this is an explicit test failure.");
}
static async Task<T> Expect<T>(Task task) where T : Exception
{
    try { await task; } catch (T ex) { return ex; }
    throw new Exception("Expected " + typeof(T).Name + " was not raised.");
}
static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
static bool IsAlive(int pid)
{
    try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
    catch (ArgumentException) { return false; }
}
static async Task AssertDead(IEnumerable<int> pids)
{
    var watch = Stopwatch.StartNew();
    while (pids.Any(IsAlive) && watch.ElapsedMilliseconds < 2000) await Task.Delay(10);
    Assert(!pids.Any(IsAlive), "External process or descendant survived cancellation/timeout.");
}
