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
    public ObservableCollection<SavedArchivePassword> Passwords { get; }
    public SavedArchivePassword? SelectedPassword { get; set; }
    public string PasswordName { get; set; } = "";
    public ICommand RemovePassword { get; }
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
        Passwords = new(settings.Passwords);
        RemovePassword = new RelayCommand(_ => { if (SelectedPassword != null) Passwords.Remove(SelectedPassword); });
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
        Save = new RelayCommand(_ => { Settings.Folders = Folders.ToList(); Settings.Passwords = Passwords.ToList(); save(); });
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
    public bool AddPassword(string password)
    {
        if (password.Length == 0) { Notice = "추가할 암호를 입력하세요."; return false; }
        if (password.Contains('\0') || password.Contains('\r') || password.Contains('\n') || password.Length > 1024)
        { Notice = "암호는 줄바꿈 없이 1,024자 이하로 입력하세요."; return false; }
        try
        {
            if (Passwords.Any(p => PasswordProtection.Unprotect(p.ProtectedValue) == password))
            { Notice = "이미 등록된 암호입니다."; return false; }
            Passwords.Add(new SavedArchivePassword { Name = string.IsNullOrWhiteSpace(PasswordName) ? $"저장 암호 {Passwords.Count + 1}" : PasswordName.Trim(), ProtectedValue = PasswordProtection.Protect(password) });
            PasswordName = ""; Changed(nameof(PasswordName));
            Notice = "암호를 추가했습니다. ‘설정 저장’을 누르면 적용하고 이전 실패 파일을 다시 시도합니다.";
            return true;
        }
        catch { Notice = "암호 추가 실패: Windows 암호화 저장소를 확인하세요."; return false; }
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
