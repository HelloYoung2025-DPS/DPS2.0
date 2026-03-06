// =====================================================
// StartReddit_OwnCode.cs - 启动模拟器并打开 Reddit
// 复制此代码到 ZennoDroid 的 Own Code 动作块
// 功能：启动安卓模拟器 → 等待就绪 → 打开 Reddit APP
// =====================================================

Action<string> Log = (m) => project.SendInfoToLog("[StartReddit] " + m, true);
Action<string> LogErr = (m) => project.SendErrorToLog("[StartReddit] " + m, true);

Func<string, string, string> GetVar = (name, def) => {
    try {
        string v = project.Variables[name].Value;
        return string.IsNullOrEmpty(v) ? def : v;
    } catch { return def; }
};

Action<string, string> SetVar = (name, val) => {
    try { project.Variables[name].Value = val ?? ""; } catch { }
};

try {
    var droid = instance.DroidInstance;
    var action = droid.Action;
    var app = droid.App;
    var input = droid.Input;

    // ========== 第1步：启动模拟器 ==========
    Log("正在启动安卓模拟器...");
    action.Start();

    // 等待模拟器完全启动（最多等 60 秒）
    int maxWaitSec = int.Parse(GetVar("emulator_boot_timeout", "60"));
    int waited = 0;
    int checkInterval = 3000; // 每 3 秒检查一次
    bool deviceReady = false;

    while (waited < maxWaitSec * 1000) {
        System.Threading.Thread.Sleep(checkInterval);
        waited += checkInterval;
        try {
            // 尝试获取 UI 层级，能获取到说明设备已就绪
            string testLayout = droid.Hierarchy.GetLayout();
            if (!string.IsNullOrEmpty(testLayout) && testLayout.Length > 10) {
                deviceReady = true;
                Log("模拟器已就绪！等待了 " + (waited / 1000).ToString() + " 秒");
                break;
            }
        } catch {
            // 设备还没准备好，继续等
            Log("等待模拟器启动... (" + (waited / 1000).ToString() + "s/" + maxWaitSec.ToString() + "s)");
        }
    }

    if (!deviceReady) {
        LogErr("模拟器启动超时（" + maxWaitSec.ToString() + " 秒）");
        return "ERROR: 模拟器启动超时";
    }

    // ========== 第2步：打开 Reddit APP ==========
    string packageName = "com.reddit.frontpage";
    Log("正在打开 Reddit APP (" + packageName + ")...");
    app.Open(packageName);
    
    // 等待 APP 加载
    int appWait = int.Parse(GetVar("app_launch_wait", "5000"));
    System.Threading.Thread.Sleep(appWait);

    // ========== 第3步：验证 Reddit 是否已打开 ==========
    string topApp = app.Top;
    Log("当前顶层 APP: " + topApp);
    
    if (!string.IsNullOrEmpty(topApp) && topApp.Contains("reddit")) {
        Log("✓ Reddit APP 已成功打开！");
        SetVar("reddit_status", "RUNNING");
        return "SUCCESS: Reddit APP 已运行";
    }

    // 如果第一次没打开，重试一次
    Log("Reddit 未检测到，重试...");
    app.Open(packageName);
    System.Threading.Thread.Sleep(appWait);
    
    topApp = app.Top;
    if (!string.IsNullOrEmpty(topApp) && topApp.Contains("reddit")) {
        Log("✓ Reddit APP 已成功打开（重试后）！");
        SetVar("reddit_status", "RUNNING");
        return "SUCCESS: Reddit APP 已运行";
    }

    LogErr("Reddit APP 未能成功打开，顶层 APP: " + (topApp ?? "null"));
    SetVar("reddit_status", "FAILED");
    return "ERROR: Reddit APP 启动失败";
}
catch (System.Exception ex) {
    LogErr("异常: " + ex.Message);
    LogErr("堆栈: " + ex.StackTrace);
    return "ERROR: " + ex.Message;
}
