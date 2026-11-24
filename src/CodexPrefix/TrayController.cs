using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace CodexPrefix;

// 使用框架维护 Shell 通知与菜单生命周期，不在 Shell 回调中重入弹出菜单。
internal sealed class TrayController : IDisposable
{
    readonly Forms.NotifyIcon icon;
    readonly Forms.ContextMenuStrip menu = new();
    readonly Forms.ToolStripMenuItem enable = new("启用辅助");
    readonly Forms.ToolStripMenuItem suspend = new("收纳并暂停");
    readonly System.Drawing.Icon artwork;
    bool commandPending;

    internal TrayController(Dispatcher dispatcher, Func<bool> canEnable, Func<bool> isActive,
        Action onEnable, Action onSuspend, Action onExit)
    {
        using var stream = System.Windows.Application.GetResourceStream(new Uri("assets/app-icon/release-v2/app.ico", UriKind.Relative))!.Stream;
        using var loaded = new System.Drawing.Icon(stream);
        artwork = (System.Drawing.Icon)loaded.Clone();
        icon = new Forms.NotifyIcon { Icon = artwork, Text = "CodexPrefix · 会话前缀", ContextMenuStrip = menu, Visible = true };
        void Queue(Action command)
        {
            if (commandPending) return;
            commandPending = true;
            menu.Close();
            dispatcher.BeginInvoke(() => { try { command(); } finally { commandPending = false; } });
        }
        enable.Click += (_, _) => Queue(onEnable);
        suspend.Click += (_, _) => Queue(onSuspend);
        var exit = new Forms.ToolStripMenuItem("退出");
        exit.Click += (_, _) => Queue(onExit);
        menu.Items.AddRange([enable, suspend, new Forms.ToolStripSeparator(), exit]);
        menu.Opening += (_, e) =>
        {
            if (commandPending) { e.Cancel = true; return; }
            enable.Enabled = canEnable();
            suspend.Enabled = isActive();
        };
        icon.MouseDoubleClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left && canEnable()) Queue(onEnable); };
    }
    public void Dispose() { icon.Visible = false; icon.Dispose(); menu.Dispose(); artwork.Dispose(); }
}
