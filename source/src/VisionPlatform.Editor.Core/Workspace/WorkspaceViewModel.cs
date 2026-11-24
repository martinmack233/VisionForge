using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Windows.Input;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Contracts.Results;
using VisionPlatform.Editor.Core.Common;
using VisionPlatform.Editor.Core.Persistence;
using VisionPlatform.Editor.Core.Session;
using VisionPlatform.Runtime.Projects;

namespace VisionPlatform.Editor.Core.Workspace;

public enum WorkspacePage { Dashboard, Projects, Editor, Examples, Devices, Monitor, Settings, AI }
public sealed record NavigationItem(WorkspacePage Page, string Icon, string Title);
public sealed record WorkspaceRunRecord(DateTimeOffset Time, string Project, string Status, double Milliseconds, int NodeCount, string Decision);
public sealed record WorkspaceLogEntry(DateTimeOffset Time, string Message);

public sealed class WorkspaceCommand(Action<object?> action, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) { if (CanExecute(parameter)) action(parameter); }
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>Navigation and user-local workspace state only; execution remains in IEditorSession.</summary>
public sealed class WorkspaceViewModel : ObservableObject, IDisposable
{
    private readonly WorkspaceStateStore _store;
    private readonly Func<Task<bool>> _confirmDiscard;
    private readonly List<RecentProjectEntry> _recent = [];
    private WorkspacePage _currentPage;
    private bool _expanded = true, _working, _disposed, _runCandidate;
    private string _search = "", _notice = "", _projectCopyName = "", _rename = "";
    private RecentProjectEntry? _selectedProject;
    private readonly SemaphoreSlim _persistGate = new(1, 1);
    private Task _pendingRecent = Task.CompletedTask;
    private WorkspaceSettings _settings = new();
    private string _defaultProjects = "", _examples = "", _logs = "";
    private bool _logEnabled;
    private int _resultBudget;
    private string _resultBudgetText = "512";
    public IEditorSession Session { get; }
    public IReadOnlyList<NavigationItem> Navigation { get; } = [new(WorkspacePage.Dashboard, "⌂", "首页"), new(WorkspacePage.Projects, "▣", "工程管理"), new(WorkspacePage.Editor, "⌘", "算法编辑器"), new(WorkspacePage.Examples, "◈", "方案示例"), new(WorkspacePage.Devices, "▤", "设备管理"), new(WorkspacePage.AI, "◇", "AI 工作区"), new(WorkspacePage.Monitor, "◉", "运行监控"), new(WorkspacePage.Settings, "⚙", "设置")];
    public ObservableCollection<RecentProjectEntry> RecentProjects { get; } = [];
    public ObservableCollection<WorkspaceRunRecord> RunHistory { get; } = [];
    public ObservableCollection<WorkspaceLogEntry> Logs { get; } = [];
    public WorkspaceSettings Settings => _settings;
    public WorkspacePage CurrentPage { get => _currentPage; set { if (Set(ref _currentPage, value)) Notify(nameof(PageTitle)); } }
    public string PageTitle => Navigation.First(n => n.Page == CurrentPage).Title;
    public bool NavigationExpanded { get => _expanded; set { if (Set(ref _expanded, value)) Notify(nameof(NavigationWidth)); } }
    public double NavigationWidth => NavigationExpanded ? 184 : 64;
    public string ProjectSearch { get => _search; set { if (Set(ref _search, value)) RefreshRecent(); } }
    public string Notice { get => _notice; private set => Set(ref _notice, value); }
    public bool IsWorking { get => _working; private set { if (Set(ref _working, value)) RefreshCommands(); } }
    public RecentProjectEntry? SelectedProject { get => _selectedProject; set { if (Set(ref _selectedProject, value)) { RenameText = value?.Name ?? ""; Notify(nameof(ProjectInformation)); RefreshCommands(); } } }
    public string RenameText { get => _rename; set => Set(ref _rename, value); }
    public string CopyName { get => _projectCopyName; set => Set(ref _projectCopyName, value); }
    public string ProjectInformation => SelectedProject is { } p ? $"{p.Name}\n{p.Path}\n最后打开：{p.LastOpened.LocalDateTime:g}\n状态：{p.Availability}" : "选择最近工程查看位置和基本信息。";
    public string RuntimeStatus => Session.IsBusy ? "正在运行或加载" : Session.Results.Any(r => r.Status == "Cancelled") ? "已取消" : Session.Results.Any(r => r.Status == "Failed") ? "执行失败" : "就绪";
    public string CurrentDecision => Session.CurrentOutputs.Values.SelectMany(v => v.Values).OfType<InspectionReport>().LastOrDefault() is { } r ? r.IsOk ? "OK" : "NG" : "尚无判定输出";
    public string CommunicationStatus => Session.CurrentOutputs.Values.SelectMany(v => v.Values).OfType<CommunicationStatus>().LastOrDefault() is { } c ? c.State + " · " + c.Description : "本次没有通信结果";
    public bool IsTemplateProject => Session.ProjectPath is { } path && IsExamplePath(path);
    public bool IsExamplePath(string path)
    {
        var full = Path.GetFullPath(path);
        return new[] { _settings.ExamplesDirectory, Path.Combine(AppContext.BaseDirectory, "Examples") }
            .Any(directory => full.StartsWith(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) || AdditionalTemplatePathCheck?.Invoke(full) == true;
    }
    public string LastElapsed => RunHistory.FirstOrDefault() is { } run ? run.Milliseconds.ToString("F3", CultureInfo.InvariantCulture) + " ms" : "尚未运行";
    public string Version => $"{VisionPlatform.Contracts.Build.ProductBuild.Name} {VisionPlatform.Contracts.Build.ProductBuild.Version}\n{VisionPlatform.Contracts.Build.ProductBuild.Runtime}\n{VisionPlatform.Contracts.Build.ProductBuild.Identity}\n程序目录：{AppContext.BaseDirectory}";
    public string Components => string.Join("\n", new[] { "VisionPlatform.Runtime", "Nodify", "OpenCvSharp", "CvInspect", "Tesseract", "System.IO.Ports", "VisionPlatform.Acquisition.Boq" }.Select(name =>
    {
        var path = Path.Combine(AppContext.BaseDirectory, name + ".dll");
        return File.Exists(path) ? name + " " + System.Reflection.AssemblyName.GetAssemblyName(path).Version : name + "：未随本宿主提供";
    }));
    public string DefaultProjectDirectory { get => _defaultProjects; set => Set(ref _defaultProjects, value); }
    public string ExamplesDirectory { get => _examples; set => Set(ref _examples, value); }
    public string LogDirectory { get => _logs; set => Set(ref _logs, value); }
    public bool LogEnabled { get => _logEnabled; set => Set(ref _logEnabled, value); }
    public int ResultImageBudgetMiB { get => _resultBudget; set { if (Set(ref _resultBudget, value)) ResultImageBudgetText = value.ToString(CultureInfo.InvariantCulture); } }
    public string ResultImageBudgetText { get => _resultBudgetText; set => Set(ref _resultBudgetText, value); }
    public string ActiveCacheDescription => $"当前会话结果图像预算：{Session.ResultImageBudgetBytes / 1048576} MiB；每个视口显示像素缓存：64 MiB（编辑器与设备页分别计，合计上限128 MiB）。新预算下次启动生效。";
    public WorkspaceCommand NavigateCommand { get; }
    public WorkspaceCommand ToggleNavigationCommand { get; }
    public WorkspaceCommand NewCommand { get; }
    public WorkspaceCommand OpenCommand { get; }
    public WorkspaceCommand OpenRecentCommand { get; }
    public WorkspaceCommand CopyProjectCommand { get; }
    public WorkspaceCommand RenameProjectCommand { get; }
    public WorkspaceCommand SaveSettingsCommand { get; }
    public WorkspaceCommand ShowExamplesCommand { get; }
    public Func<Task>? OpenDialogAction { get; set; }
    public Func<string, Task<string>>? PrepareOpenProject { get; set; }
    public Func<string, bool>? AdditionalTemplatePathCheck { get; set; }
    public event EventHandler? ExamplesDirectoryChanged;

    public WorkspaceViewModel(IEditorSession session, string? stateDirectory = null, Func<Task<bool>>? confirmDiscard = null)
    {
        Session = session; _store = new(stateDirectory); _confirmDiscard = confirmDiscard ?? (() => Task.FromResult(true));
        NavigateCommand = new(p => { if (p is WorkspacePage page) CurrentPage = page; });
        ToggleNavigationCommand = new(_ => NavigationExpanded = !NavigationExpanded);
        NewCommand = new(_ => Run(NewProjectAsync), Ready);
        OpenCommand = new(_ => Run(() => OpenDialogAction?.Invoke() ?? Task.CompletedTask), Ready);
        OpenRecentCommand = new(p => Run(() => OpenProjectAsync(p is RecentProjectEntry recent ? recent.Path : SelectedProject?.Path ?? throw new InvalidOperationException("请选择工程。"))), Ready);
        CopyProjectCommand = new(_ => Run(CopySelectedProjectAsync), () => Ready() && SelectedProject?.Exists == true);
        RenameProjectCommand = new(_ => Run(RenameSelectedProjectAsync), () => Ready() && SelectedProject?.Exists == true);
        SaveSettingsCommand = new(_ => Run(SaveSettingsAsync), () => !IsWorking);
        ShowExamplesCommand = new(_ => CurrentPage = WorkspacePage.Examples);
        ApplySettings(_settings); Session.PropertyChanged += SessionChanged; Session.Results.CollectionChanged += ResultsChanged;
    }
    private bool Ready() => !IsWorking && !Session.IsBusy;
    private void RefreshCommands()
    { foreach (var command in new[] { NewCommand, OpenCommand, OpenRecentCommand, CopyProjectCommand, RenameProjectCommand, SaveSettingsCommand }) command?.Refresh(); }
    private async void Run(Func<Task> action)
    {
        if (_disposed) return;
        try { IsWorking = true; await action(); }
        catch (Exception ex) { Notice = ex.Message; Session.SetStatus(ex.Message); }
        finally { IsWorking = false; }
    }
    public async Task InitializeAsync()
    {
        try
        {
            var state = await _store.LoadAsync(); ApplySettings(state.Settings);
            _recent.Clear(); _recent.AddRange(state.RecentProjects); RefreshRecent();
        }
        catch (Exception ex) { Notice = "设置未加载，使用当前默认值；原文件未覆盖：" + ex.Message; }
    }
    private void ApplySettings(WorkspaceSettings value)
    {
        _settings = value; DefaultProjectDirectory = value.DefaultProjectDirectory; ExamplesDirectory = value.ExamplesDirectory;
        LogDirectory = value.LogDirectory; LogEnabled = value.LogEnabled; ResultImageBudgetMiB = value.ResultImageBudgetMiB; Notify(nameof(Settings));
    }
    public async Task SaveSettingsAsync()
    {
        if (!int.TryParse(ResultImageBudgetText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var budget)) throw new ArgumentException("结果图像预算必须是64..4096之间的整数。");
        var next = _settings with { DefaultProjectDirectory = DefaultProjectDirectory, ExamplesDirectory = ExamplesDirectory, LogDirectory = LogDirectory, LogEnabled = LogEnabled, ResultImageBudgetMiB = budget };
        var errors = next.Validate(); if (errors.Count != 0) throw new ArgumentException(string.Join("\n", errors));
        var changed = !string.Equals(_settings.ExamplesDirectory, next.ExamplesDirectory, StringComparison.OrdinalIgnoreCase);
        var previous = _settings; _settings = next;
        try { await PersistAsync(); } catch { _settings = previous; throw; }
        ApplySettings(next); Notice = "工作区设置已保存；结果图像预算下次启动生效。";
        if (changed) ExamplesDirectoryChanged?.Invoke(this, EventArgs.Empty);
    }
    public async Task NewProjectAsync()
    {
        if (!await _confirmDiscard()) return; await Session.NewAsync(); CurrentPage = WorkspacePage.Editor;
    }
    public async Task OpenProjectAsync(string path)
    {
        if (!await _confirmDiscard()) return;
        var full = Path.GetFullPath(path);
        if (IsExamplePath(full)) full = PrepareOpenProject is { } prepare ? await prepare(full) : throw new InvalidOperationException("内置示例是只读模板，请从示例中心创建工程副本。");
        await Session.OpenAsync(full); CurrentPage = WorkspacePage.Editor;
        await RememberProjectAsync(full, Session.ProjectName);
    }
    public async Task RememberProjectAsync(string path, string name)
    {
        var full = Path.GetFullPath(path); _recent.RemoveAll(p => string.Equals(p.Path, full, StringComparison.OrdinalIgnoreCase));
        _recent.Insert(0, new(full, name, DateTimeOffset.Now)); if (_recent.Count > WorkspaceStateStore.MaximumRecentProjects) _recent.RemoveRange(WorkspaceStateStore.MaximumRecentProjects, _recent.Count - WorkspaceStateStore.MaximumRecentProjects);
        RefreshRecent(); await PersistAsync();
    }
    private void RefreshRecent()
    {
        RecentProjects.Clear(); foreach (var entry in _recent.Where(p => string.IsNullOrWhiteSpace(ProjectSearch) || p.Name.Contains(ProjectSearch, StringComparison.OrdinalIgnoreCase) || p.Path.Contains(ProjectSearch, StringComparison.OrdinalIgnoreCase))) RecentProjects.Add(entry);
    }
    private ProjectStore ProjectStore() => new(Session.Registry, new Dictionary<string, string> { ["cvinspect.pattern"] = "0.34.0+0eddfbaa" });
    public async Task CopySelectedProjectAsync()
    {
        var selected = SelectedProject ?? throw new InvalidOperationException("请选择工程。");
        var name = string.IsNullOrWhiteSpace(CopyName) ? selected.Name + " 副本" : CopyName.Trim();
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or "..") throw new ArgumentException("副本名称不能包含路径字符。");
        var targetDirectory = Path.Combine(_settings.DefaultProjectDirectory, name + "-" + Guid.NewGuid().ToString("N")[..8]);
        if (IsExamplePath(Path.Combine(targetDirectory, "project.json"))) throw new InvalidOperationException("工程副本不能写入只读示例目录，请更改默认工程目录。");
        // Preserve external references via absolute paths; this is a project copy, not an asset export.
        var store = ProjectStore(); var source = await store.LoadAsync(selected.Path);
        var sourceRoot = Path.GetDirectoryName(selected.Path)!;
        var nodes = source.Nodes.Select(n => n.NodeTypeId is "ImageInput" or "TemplateMatching" ? RebaseNode(n, sourceRoot) : n).ToList();
        var copy = source with { ProjectId = Guid.NewGuid(), ProjectName = name, Nodes = nodes, TemplateResources = source.TemplateResources.Select(t => t with { Path = VisionPlatform.Runtime.Projects.ProjectStore.ResolveResourcePath(sourceRoot, t.Path) }).ToList() };
        var layout = await EditorLayoutStore.LoadAsync(selected.Path, source.ProjectId);
        var target = Path.Combine(targetDirectory, "project.json"); await store.SaveAsync(target, copy);
        if (layout is not null) await EditorLayoutStore.SaveAsync(target, layout with { ProjectId = copy.ProjectId });
        await RememberProjectAsync(target, name); SelectedProject = _recent.First(p => p.Path == target);
        Notice = "已创建独立工程副本。资源保持原有效位置；源工程未修改。";
    }
    private static NodeSpecification RebaseNode(NodeSpecification n, string root)
    {
        var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(n.Parameters.GetRawText(), JsonDefaults.Options)!;
        var key = n.NodeTypeId == "ImageInput" ? "Path" : "TemplatePath";
        if (values.TryGetValue(key, out var path) && path.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(path.GetString()))
            values[key] = JsonDefaults.Element(VisionPlatform.Runtime.Projects.ProjectStore.ResolveResourcePath(root, path.GetString()!));
        return n with { Parameters = JsonDefaults.Element(values) };
    }
    public async Task RenameSelectedProjectAsync()
    {
        var selected = SelectedProject ?? throw new InvalidOperationException("请选择工程。"); var name = RenameText.Trim();
        if (IsExamplePath(selected.Path)) throw new InvalidOperationException("内置示例是只读模板，请先复制为新工程。");
        if (name.Length is 0 or > 120) throw new ArgumentException("工程名长度必须为1..120。");
        if (string.Equals(Session.ProjectPath, selected.Path, StringComparison.OrdinalIgnoreCase))
        {
            Session.ProjectName = name; await Session.SaveAsync(selected.Path);
        }
        else
        {
            var store = ProjectStore(); var project = await store.LoadAsync(selected.Path); await store.SaveAsync(selected.Path, project with { ProjectName = name });
        }
        await RememberProjectAsync(selected.Path, name); SelectedProject = _recent.First(p => p.Path == selected.Path);
        Notice = "工程名称已更新；文件位置和资源引用保持有效。";
    }
    private async Task PersistAsync()
    {
        await _persistGate.WaitAsync();
        try { await _store.SaveAsync(new(1, _settings, _recent.ToArray())); }
        finally { _persistGate.Release(); }
    }
    private void ResultsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    { if (Session.IsBusy && Session.Results.Count > 0) _runCandidate = true; }
    private void SessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed) return;
        // Examples open through the same Session, without passing the ordinary file dialog.
        if (e.PropertyName == nameof(IEditorSession.ProjectPath) && Session.ProjectPath is { } opened && !IsExamplePath(opened))
            _pendingRecent = RememberOpenedAsync(opened, Session.ProjectName);
        if (e.PropertyName == nameof(IEditorSession.IsBusy))
        {
            RefreshCommands();
            if (!Session.IsBusy && _runCandidate) { _runCandidate = false; RecordRun(); }
        }
        Notify(nameof(RuntimeStatus)); Notify(nameof(CurrentDecision)); Notify(nameof(CommunicationStatus)); Notify(nameof(IsTemplateProject));
    }
    private async Task RememberOpenedAsync(string path, string name)
    {
        try { await RememberProjectAsync(path, name); }
        catch (Exception error) { if (!_disposed) Notice = "工程已打开，最近工程记录未保存：" + error.Message; }
    }
    public async Task FlushAsync()
    {
        await _pendingRecent;
        await _persistGate.WaitAsync();
        _persistGate.Release();
    }
    private void RecordRun()
    {
        var status = Session.Results.Any(r => r.Status == "Failed") ? "Failed" : Session.Results.Any(r => r.Status == "Cancelled") ? "Cancelled" : "Success";
        var item = new WorkspaceRunRecord(DateTimeOffset.Now, Session.ProjectName, status, Session.Results.Sum(r => r.Milliseconds), Session.Results.Count, CurrentDecision);
        RunHistory.Insert(0, item); while (RunHistory.Count > 50) RunHistory.RemoveAt(RunHistory.Count - 1);
        var error = string.Join("; ", Session.Results.Where(r => !string.IsNullOrEmpty(r.Error)).Select(r => r.Name + ": " + r.Error));
        var message = $"{item.Project} · {item.Status} · {item.Milliseconds:F3} ms · {item.Decision}" + (error.Length == 0 ? "" : " · " + error);
        Logs.Insert(0, new(item.Time, message)); while (Logs.Count > 100) Logs.RemoveAt(Logs.Count - 1);
        Notify(nameof(LastElapsed));
        if (_settings.LogEnabled) _ = WriteLogAsync(new(item.Time, message));
    }
    private async Task WriteLogAsync(WorkspaceLogEntry entry)
    {
        try
        {
            var directory = _settings.LogDirectory; Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "runtime-" + entry.Time.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".jsonl");
            var text = JsonSerializer.Serialize(entry) + Environment.NewLine;
            // Bound each log file. Do not delete user files or log transport payloads.
            if (File.Exists(path) && new FileInfo(path).Length > 4 * 1024 * 1024) return;
            await File.AppendAllTextAsync(path, text).ConfigureAwait(false);
        }
        catch { /* User-local diagnostics are best effort and never change Runtime results. */ }
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; Session.PropertyChanged -= SessionChanged; Session.Results.CollectionChanged -= ResultsChanged;
    }
}

