using System.Diagnostics;
using AutoExtract.Core;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace AutoExtract.App;

public sealed class AutoStartService
{
    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled) key.SetValue("AutoExtract", "\"" + Environment.ProcessPath + "\" --background");
        else key.DeleteValue("AutoExtract", false);
    }
}
public sealed class TrayIconService : IDisposable
{
    private readonly Forms.NotifyIcon icon;
    private readonly System.Drawing.Icon trayIcon;
    private readonly Forms.ToolStripMenuItem status;
    public TrayIconService(Action show, Action<bool> pause, Action history, Action exit)
    {
        status = new Forms.ToolStripMenuItem("감시 중") { Enabled = false };
        var menu = new Forms.ContextMenuStrip(); menu.Items.Add(status); menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("감시 시작", null, (_, _) => pause(false));
        menu.Items.Add("감시 일시정지", null, (_, _) => pause(true));
        menu.Items.Add("설정 열기", null, (_, _) => show());
        menu.Items.Add("최근 처리 내역", null, (_, _) => history());
        menu.Items.Add(new Forms.ToolStripSeparator()); menu.Items.Add("프로그램 종료", null, (_, _) => exit());
        using (var stream = typeof(TrayIconService).Assembly.GetManifestResourceStream("AutoExtract.TrayIcon.ico")
            ?? throw new InvalidOperationException("트레이 아이콘 리소스가 없습니다."))
        using (var source = new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize))
            trayIcon = (System.Drawing.Icon)source.Clone();
        icon = new Forms.NotifyIcon { Icon = trayIcon, Text = "AutoExtract · 자동 압축 해제", ContextMenuStrip = menu, Visible = true };
        icon.DoubleClick += (_, _) => show();
    }
    public void SetPaused(bool paused) { status.Text = paused ? "일시정지" : "감시 중"; icon.Text = "AutoExtract · " + status.Text; }
    private bool disposed;
    public void Dispose() { if (disposed) return; disposed = true; icon.Visible = false; icon.ContextMenuStrip?.Dispose(); icon.Dispose(); trayIcon.Dispose(); }
}
