using AutoExtract.Core;
using System.IO;

namespace AutoExtract.App;

public partial class App : System.Windows.Application
{
    private Mutex? mutex;
    private bool ownsMutex;
    private SettingsService storage = null!;
    private AppSettings settings = null!;
    private FolderWatcherService watcher = null!;
    private MainViewModel vm = null!;
    private MainWindow window = null!;
    private TrayIconService? tray;
    private InstanceActivationService? activation;
    private bool updating, exiting;
    private string activeEnginePath = "";
    protected override async void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            // Global per-user instance also prevents overlapping parent/child watch sets across sessions.
            var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
            mutex = new Mutex(false, "Global\\AutoExtract-" + sid);
            try { ownsMutex = mutex.WaitOne(0); } catch (AbandonedMutexException) { ownsMutex = true; }
            if (!ownsMutex)
            {
                if (!e.Args.Contains("--background") && !await InstanceActivationService.ShowExistingAsync(InstanceActivationService.NameForUser(sid)))
                    System.Windows.MessageBox.Show("기존 프로그램에 창 열기 요청을 전달하지 못했습니다. 잠시 후 다시 실행하거나 시스템 트레이에서 설정을 여세요.", "AutoExtract");
                Shutdown(); return;
            }
            storage = new SettingsService(); settings = storage.Load<AppSettings>("settings.json");
            activeEnginePath = settings.BandizipPath;
            var extraction = new ArchiveExtractionService(new BandizipEngine(() => activeEnginePath), new FileRecycleService(), storage);
            watcher = new FolderWatcherService(extraction);
            vm = new MainViewModel(settings, extraction.State, () => _ = ApplyAsync(), paused => _ = PauseAsync(paused), () => extraction.ClearHistoryAsync());
            window = new MainWindow { DataContext = vm }; MainWindow = window;
            activation = new InstanceActivationService(InstanceActivationService.NameForUser(sid), () => Dispatcher.InvokeAsync(() =>
            {
                if (!exiting) window.ShowSettings();
            }).Task);
            extraction.Recorded += record => Dispatcher.BeginInvoke(() => vm.AddRecord(record));
            extraction.HistoryCleared += () => Dispatcher.BeginInvoke(vm.ClearHistoryDisplay);
            tray = new TrayIconService(() => window.ShowSettings(), p => _ = PauseAsync(p), () => window.ShowSettings(true), () => _ = ExitAsync());
            tray.SetPaused(settings.Paused);
            if (!e.Args.Contains("--background") || settings.Folders.Count == 0) window.ShowSettings();
            if (BandizipEngine.FindExecutable(settings.BandizipPath) == null) vm.Notice = "반디집 bz.exe가 없습니다. 반디집 설치 후 경로를 설정하세요. 원본은 처리하지 않습니다.";
            await watcher.StartAsync(settings);
        }
        catch (Exception error) { System.Windows.MessageBox.Show(error.Message, "AutoExtract 시작 실패"); Shutdown(1); }
    }
    private async Task ApplyAsync()
    {
        if (updating || exiting) return; updating = true;
        try
        {
            foreach (var folder in settings.Folders) { folder.Path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder.Path)); }
            settings.Folders = settings.Folders.GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
            await watcher.StopAsync(); // stop before committing changed engine/recycle settings
            activeEnginePath = settings.BandizipPath;
            new AutoStartService().SetEnabled(settings.AutoStart);
            storage.Save("settings.json", settings);
            await watcher.StartAsync(settings);
            vm.Notice = BandizipEngine.FindExecutable(settings.BandizipPath) == null ? "설정 저장됨. 반디집 bz.exe를 찾을 수 없습니다. 경로를 확인하세요." : "설정을 저장하고 감시 상태를 적용했습니다.";
            tray?.SetPaused(settings.Paused); vm.Refresh();
        }
        catch (Exception error) { vm.Notice = "설정 적용 실패: " + error.Message; }
        finally { updating = false; }
    }
    private async Task PauseAsync(bool paused) { if (updating || exiting) return; settings.Paused = paused; await ApplyAsync(); }
    private async Task ExitAsync()
    {
        if (exiting) return; exiting = true;
        try { if (activation != null) await activation.DisposeAsync(); await watcher.StopAsync(); }
        finally { tray?.Dispose(); window.AllowClose = true; window.Close(); Shutdown(); }
    }
    protected override void OnExit(System.Windows.ExitEventArgs e)
    { activation?.Stop(); tray?.Dispose(); if (ownsMutex) mutex?.ReleaseMutex(); mutex?.Dispose(); base.OnExit(e); }
}
