using System.Runtime.InteropServices;
namespace CodexPrefix;
internal static class UpgradeValidation
{
    internal static void Run(Action<bool, string> outerCheck)
    {
        var evidence = new List<object>();
        Action<bool, string> check = (passed, name) => { evidence.Add(new { passed, name }); outerCheck(passed, name); };
        check(Recognition.IsChatToolbar("聊天工具栏") && Recognition.IsChatToolbar("Chat toolbar"), "正式入口仅允许明确聊天工具栏");
        foreach (var bad in new[] { "", "文件路径", "应用程序菜单", "Chat toolbar fake" }) check(!Recognition.IsChatToolbar(bad), "其他工具栏不作为会话菜单");
        check(Recognition.IsHeaderMenuButton("readonly=true;haspopup=menu;expanded=false"), "标题菜单需要 haspopup=menu 证据");
        foreach(var bad in new[] { "", "notpopup=menu", "haspopup=true", "haspopup=dialog", "haspopup=menu-fake" }) check(!Recognition.IsHeaderMenuButton(bad), "非菜单弹出能力必须拒绝");
        check(!Recognition.IsRenameMenuLabel("Delete") && !Recognition.IsRenameMenuLabel("归档") && Recognition.IsRenameMenuLabel("重命名"), "只触发原生重命名菜单项");
        check(WindowTracker.ResolveInsertAfter(11, 12, 0, false, false, true, false) == WindowTracker.HwndNoTopmost, "置顶锚点不能传播给普通宿主");
        check(WindowTracker.ResolveInsertAfter(11, 11, 13, false, true, true, true) == WindowTracker.HwndNoTopmost, "意外置顶小条需要降级");
        check(WindowTracker.ResolveInsertAfter(11, 11, 13, false, false, false, false) is null, "已经位于正确层级无需重排");
        check(WindowTracker.PlaceAboveFlags == 0x213, "层级调整必须保留 owner 层级并禁止激活");
        check(MainWindow.ShouldSuppressFullActivation(false, false) && !MainWindow.ShouldSuppressFullActivation(false, true), "完整模式保护宿主焦点，列表编辑恢复输入");
        var lifecycle = new AssistanceLifecycle();
        var host = new WindowIdentity(101, 202, 303);
        check(!lifecycle.Active && !lifecycle.CanOperate(host), "无宿主启动不得开放辅助");
        lifecycle.Enable(host);
        check(lifecycle.CanOperate(host) && !lifecycle.CanOperate(host with { Started = 304 }), "辅助只能作用于同一宿主实例");
        check(lifecycle.Observe(false) && !lifecycle.Active, "宿主销毁后强制收纳");
        check(!lifecycle.Observe(true) && !lifecycle.Active, "宿主重开不能自动恢复已收纳辅助");
        lifecycle.Enable(host); lifecycle.Suspend();
        check(!lifecycle.Active && lifecycle.Host is null, "用户暂停同时清理宿主绑定");
        check(!UpdateService.CanOfferUpdate(new(0, 6, 0), new(0, 7, 0, "rc.1")), "正式版不能自动收到未验收候选版");
        check(UpdateService.CanOfferUpdate(new(0, 7, 0, "rc.1"), new(0, 7, 0)), "候选版可升级至同版本正式版");
        check(UpdateService.CanOfferUpdate(new(0, 7, 0, "rc.1"), new(0, 7, 0, "rc.2")), "候选渠道可接收后续候选版");
        check(UpdateService.RepositoryName == "CodexPrefix-Release", "公开更新源仅为单独脱敏分发仓");
        UpgradeStorageValidation.Run(check);
        int reads = 0;
        var value = CodexBridge.PollTransient(() => { if (++reads < 3) throw new COMException("重建", unchecked((int)0x80040201)); return "ok"; }, _ => true, 100, 1, default);
        check(value == "ok" && reads == 3, "UIA 瞬态只读可在预算内恢复");
        bool fatal = false;
        try { CodexBridge.PollTransient<string>(() => throw new COMException("拒绝", unchecked((int)0x80070005)), _ => true, 20, 1, default); } catch(COMException) { fatal = true; }
        check(fatal, "权限错误不可伪装成瞬态");
        System.IO.File.WriteAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "upgrade-validation.json"), System.Text.Json.JsonSerializer.Serialize(evidence, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), new System.Text.UTF8Encoding(false));
    }
}
