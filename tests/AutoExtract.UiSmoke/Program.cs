using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AutoExtract.App;
using AutoExtract.Core;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--activation-client")
            return InstanceActivationService.ShowExistingAsync(args[1]).GetAwaiter().GetResult() ? 0 : 1;
        CheckInstanceActivation();
        var errors = new StringWriter();
        PresentationTraceSources.DataBindingSource.Listeners.Add(new TextWriterTraceListener(errors));
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        var app = new App(); app.InitializeComponent();
        var vm = new MainViewModel(new AppSettings { Folders = [new() { Path = @"D:\다운로드", IncludeSubdirectories = true }, new() { Path = @"C:\Users\사용자\Downloads" }] },
            new AppState { History = [new(@"D:\다운로드\예제.zip", "hash", DateTimeOffset.Now, "성공", "압축 해제 완료", @"D:\다운로드\예제")] }, () => { }, _ => { });
        Console.WriteLine("Loading WPF window and taskbar icon");
        var window = new MainWindow { DataContext = vm, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
        Console.WriteLine("Created WPF window; icon width=" + window.Icon.Width);
        window.Show();
        Console.WriteLine("Shown WPF test window");
        var root = (FrameworkElement)window.Content;
        var path = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "ui"); Directory.CreateDirectory(path);
        void Render(string name)
        {
            root.UpdateLayout();
            var width = (int)Math.Ceiling(root.ActualWidth); var height = (int)Math.Ceiling(root.ActualHeight);
            var visual = new DrawingVisual();
            using (var draw = visual.RenderOpen()) { draw.DrawRectangle(window.Background, null, new Rect(0, 0, width, height)); draw.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, width, height)); }
            var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); image.Render(visual);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using var file = File.Create(Path.Combine(path, name + ".png")); encoder.Save(file);
        }
        Render("settings");
        Console.WriteLine("Rendered settings");
        ((TabControl)window.FindName("Tabs")).SelectedIndex = 1; Render("history");
        Console.WriteLine("Rendered history");
        window.AllowClose = true; window.Close();
        var log = errors.ToString(); File.WriteAllText(Path.Combine(path, "bindings.txt"), log);
        if (!string.IsNullOrWhiteSpace(log)) { Console.WriteLine(log); return 1; }
        if (vm.Folders.Count != 2 || vm.History.Count != 1 || vm.Status != "감시 중") return 1;
        vm.ClearHistory.Execute(null);
        if (vm.History.Count != 0 || vm.SelectedJob != null) throw new InvalidOperationException("기록 전체 삭제 명령 실패");
        window = new MainWindow { DataContext = vm, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
        window.Show();
        root = (FrameworkElement)window.Content;
        ((TabControl)window.FindName("Tabs")).SelectedIndex = 1; Render("history-empty");
        window.WindowState = WindowState.Minimized;
        window.Hide();
        window.ShowSettings(true);
        if (!window.IsVisible || window.WindowState != WindowState.Normal) throw new InvalidOperationException("기존 창 복원 실패");
        Console.WriteLine("PASS hidden/minimized settings window restore");
        window.AllowClose = true; window.Close();
        Console.WriteLine("PASS WPF XAML / MVVM binding / settings and history render: " + path);
        if (args.Contains("--skip-recycle")) return 0;
        // This file is generated solely for this integration test; no user files are touched.
        var recycleFile = Path.Combine(path, "recycle-probe-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(recycleFile, "AutoExtract generated recycle integration fixture");
        new FileRecycleService().Recycle(recycleFile);
        if (File.Exists(recycleFile)) throw new IOException("Shell recycle did not complete");
        Console.WriteLine("PASS Windows Shell recycle-only integration (generated fixture)");
        Console.WriteLine("PASS WPF XAML / MVVM binding / settings and history render: " + path);
        return 0;
    }
    private static void CheckInstanceActivation()
    {
        var name = "AutoExtract.Test." + Guid.NewGuid().ToString("N");
        var requests = 0;
        var server = new InstanceActivationService(name, () => { Interlocked.Increment(ref requests); return Task.CompletedTask; });
        try
        {
            void LaunchClient()
            {
                var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
                if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase)) info.ArgumentList.Add(typeof(Program).Assembly.Location);
                info.ArgumentList.Add("--activation-client"); info.ArgumentList.Add(name);
                using var child = Process.Start(info)!;
                if (!child.WaitForExit(8000)) { child.Kill(); throw new TimeoutException("Activation client timeout"); }
                if (child.ExitCode != 0) throw new InvalidOperationException("별도 프로세스의 기존 창 열기 요청 실패");
            }
            LaunchClient();
            if (requests != 1) throw new InvalidOperationException("Activation request was not delivered exactly once");
            using (var invalid = new System.IO.Pipes.NamedPipeClientStream(".", name, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous))
            {
                invalid.Connect(2000);
                using var reader = new StreamReader(invalid, leaveOpen: true);
                _ = reader.ReadLine(); invalid.Write("INVALID\n"u8); invalid.Flush();
                if (reader.ReadLine() != null) throw new InvalidOperationException("Invalid request must be rejected");
            }
            LaunchClient();
            if (requests != 2) throw new InvalidOperationException("잘못된 요청 이후 활성화 수신 실패");
            Console.WriteLine("PASS cross-process activation / repeated launch / invalid request recovery");
        }
        finally { server.DisposeAsync().AsTask().GetAwaiter().GetResult(); server.Stop(); }
        using var timeout = new CancellationTokenSource(200);
        if (InstanceActivationService.ShowExistingAsync(name, timeout.Token).GetAwaiter().GetResult()) throw new InvalidOperationException("종료한 서버가 활성화 요청을 수락함");
        Console.WriteLine("PASS activation shutdown / unavailable instance timeout");
    }
}
