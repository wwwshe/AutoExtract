using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using AutoExtract.Core;

namespace AutoExtract.App;

public sealed class RelayCommand(Action<object?> action) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? p) => true;
    public void Execute(object? p) => action(p);
}
public sealed class MainViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public AppSettings Settings { get; }
    public ObservableCollection<WatchFolder> Folders { get; }
    public ObservableCollection<JobRecord> History { get; }
    public WatchFolder? SelectedFolder { get; set; }
    public JobRecord? SelectedJob { get; set; }
    public string Status => Settings.Paused ? "일시정지" : "감시 중";
    public string FolderCount => $"등록 폴더 {Folders.Count}개";
    public string Recent => History.FirstOrDefault()?.File ?? "아직 처리한 압축파일이 없습니다.";
    private string notice = "설정 변경 후 ‘설정 저장’을 눌러 적용하세요. 창을 닫아도 트레이에 상주합니다.";
    public string Notice { get => notice; set { notice = value; Changed(); } }
    public ICommand AddFolder { get; }
    public ICommand RemoveFolder { get; }
    public ICommand BrowseEngine { get; }
    public ICommand Save { get; }
    public ICommand TogglePause { get; }
    public ICommand OpenOutput { get; }
    public ICommand ClearHistory { get; }
    private bool clearingHistory;
    public MainViewModel(AppSettings settings, AppState state, Action save, Action<bool> pause, Func<Task>? clearHistory = null)
    {
        Settings = settings; Folders = new(settings.Folders); History = new(state.History);
        AddFolder = new RelayCommand(_ =>
        {
            var picker = new Microsoft.Win32.OpenFolderDialog { Title = "감시할 폴더 선택" };
            if (picker.ShowDialog() != true) return;
            if (Folders.Any(f => f.Path.Equals(picker.FolderName, StringComparison.OrdinalIgnoreCase))) return;
            Folders.Add(new WatchFolder { Path = picker.FolderName }); Changed(nameof(FolderCount));
        });
        RemoveFolder = new RelayCommand(_ => { if (SelectedFolder != null) Folders.Remove(SelectedFolder); Changed(nameof(FolderCount)); });
        BrowseEngine = new RelayCommand(_ =>
        {
            var picker = new Microsoft.Win32.OpenFileDialog { Title = "반디집 bz.exe 선택", Filter = "반디집 콘솔 실행 파일|bz.exe" };
            if (picker.ShowDialog() == true) { Settings.BandizipPath = picker.FileName; Changed(nameof(Settings)); }
        });
        Save = new RelayCommand(_ => { Settings.Folders = Folders.ToList(); save(); });
        TogglePause = new RelayCommand(_ => pause(!Settings.Paused));
        ClearHistory = new RelayCommand(_ =>
        {
            if (clearHistory == null) ClearHistoryDisplay();
            else _ = ClearStoredHistoryAsync(clearHistory);
        });
        OpenOutput = new RelayCommand(_ =>
        {
            try { if (SelectedJob?.Output is { } path && System.IO.Directory.Exists(path)) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe") { ArgumentList = { path }, UseShellExecute = true }); }
            catch (Exception e) { Notice = "폴더 열기 실패: " + e.Message; }
        });
    }
    public void Refresh() { Changed(nameof(Status)); Changed(nameof(Settings)); }
    public void AddRecord(JobRecord record)
    { History.Insert(0, record); while (History.Count > 500) History.RemoveAt(History.Count - 1); Changed(nameof(Recent)); }
    public void ClearHistoryDisplay()
    { History.Clear(); SelectedJob = null; Changed(nameof(SelectedJob)); Changed(nameof(Recent)); }
    private async Task ClearStoredHistoryAsync(Func<Task> clear)
    {
        if (clearingHistory) return;
        clearingHistory = true;
        try { Notice = "작업 기록을 삭제하고 있습니다."; await clear(); Notice = "작업 기록을 삭제했습니다."; }
        catch (Exception e) { Notice = "기록 삭제 실패: " + e.Message; }
        finally { clearingHistory = false; }
    }
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
