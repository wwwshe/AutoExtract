using System.ComponentModel;

namespace AutoExtract.App;
public partial class MainWindow : System.Windows.Window
{
    public bool AllowClose { get; set; }
    private void AddPassword_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.AddPassword(NewPassword.Password)) NewPassword.Clear();
    }
    public MainWindow()
    {
        InitializeComponent();
        // Decode the high-resolution PNG synchronously from the embedded resource.
        // Avoid selecting the ICO's first (16px) frame for the taskbar.
        using var stream = typeof(MainWindow).Assembly.GetManifestResourceStream("AutoExtract.WindowIcon.png")
            ?? throw new InvalidOperationException("창 아이콘 리소스가 없습니다.");
        var image = new System.Windows.Media.Imaging.BitmapImage();
        image.BeginInit();
        image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = 256;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        Icon = image;
    }
    protected override void OnClosing(CancelEventArgs e) { NewPassword.Clear(); if (!AllowClose) { e.Cancel = true; Hide(); } base.OnClosing(e); }
    public void ShowSettings(bool history = false) { Tabs.SelectedIndex = history ? 1 : 0; Show(); WindowState = System.Windows.WindowState.Normal; Activate(); }
}
