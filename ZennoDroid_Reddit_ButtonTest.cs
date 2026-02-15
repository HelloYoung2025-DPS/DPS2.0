// =====================================================
// Reddit Button Test - ZennoDroid Own Code
// 测试点击点赞/评论/分享按钮是否有效
// =====================================================

// ========== Helper Functions ==========
Action<string> Log = (m) => project.SendInfoToLog("[Reddit Test] " + m, true);
Action<string> LogErr = (m) => project.SendErrorToLog("[Reddit Test] " + m, true);

Func<string, string, string> GetVar = (name, def) => {
    try {
        string v = project.Variables[name].Value;
        return string.IsNullOrEmpty(v) ? def : v;
    } catch { return def; }
};

Action<string, string> SetVar = (name, val) => {
    project.Variables[name].Value = val ?? "";
};

// ========== Main Logic ==========
try {
    Log("Starting Reddit button test...");
    
    var droid = instance.DroidInstance;
    var input = droid.Input;
    var app = droid.App;
    var hierarchy = droid.Hierarchy;
    
    // 确保 Reddit 已打开
    Log("Opening Reddit app...");
    app.Open("com.reddit.frontpage");
    System.Threading.Thread.Sleep(5000);
    
    // 获取当前 UI 层级
    Log("Getting UI hierarchy...");
    string layout = hierarchy.GetLayout();
    Log("Layout length: " + layout.Length.ToString());
    
    // 保存 XML 到变量（截取前 5000 字符）
    if (layout.Length > 5000) {
        SetVar("reddit_layout", layout.Substring(0, 5000));
    } else {
        SetVar("reddit_layout", layout);
    }
    
    // 测试 1: 点击点赞按钮（第一个帖子）
    // 根据 XML 分析：bounds="[42,1676][306,1765]"
    // 中心点：(174, 1720)
    Log("Test 1: Clicking upvote button at (174, 1720)...");
    input.Tap(174, 1720);
    System.Threading.Thread.Sleep(2000);
    
    // 获取点击后的 UI 状态
    string layoutAfterUpvote = hierarchy.GetLayout();
    bool upvoteChanged = layoutAfterUpvote != layout;
    Log("Upvote test - UI changed: " + upvoteChanged.ToString());
    SetVar("test_upvote_result", upvoteChanged ? "CHANGED" : "NO_CHANGE");
    
    System.Threading.Thread.Sleep(1000);
    
    // 测试 2: 点击评论按钮
    // 根据 XML 分析：bounds="[322,1676][469,1765]"
    // 中心点：(395, 1720)
    Log("Test 2: Clicking comment button at (395, 1720)...");
    input.Tap(395, 1720);
    System.Threading.Thread.Sleep(3000);
    
    // 检查是否进入评论页面
    string layoutAfterComment = hierarchy.GetLayout();
    bool commentPageOpened = layoutAfterComment.Contains("comment") || layoutAfterComment != layoutAfterUpvote;
    Log("Comment test - Page opened: " + commentPageOpened.ToString());
    SetVar("test_comment_result", commentPageOpened ? "OPENED" : "NO_CHANGE");
    
    // 如果进入了评论页面，返回
    if (commentPageOpened) {
        Log("Returning to feed...");
        input.Shell("input keyevent 4");
        System.Threading.Thread.Sleep(2000);
    }
    
    // 测试 3: 点击分享按钮
    // 根据 XML 分析：bounds="[871,1676][1038,1765]"
    // 中心点：(954, 1720)
    Log("Test 3: Clicking share button at (954, 1720)...");
    input.Tap(954, 1720);
    System.Threading.Thread.Sleep(2000);
    
    // 检查是否弹出分享菜单
    string layoutAfterShare = hierarchy.GetLayout();
    bool shareMenuOpened = layoutAfterShare.Contains("share") || layoutAfterShare.Contains("Share");
    Log("Share test - Menu opened: " + shareMenuOpened.ToString());
    SetVar("test_share_result", shareMenuOpened ? "OPENED" : "NO_CHANGE");
    
    // 如果弹出了分享菜单，关闭
    if (shareMenuOpened) {
        Log("Closing share menu...");
        input.Shell("input keyevent 4");
        System.Threading.Thread.Sleep(1000);
    }
    
    // 测试 4: 点击帖子本身（进入详情页）
    // 根据 XML 分析：第一个帖子 bounds="[0,83][1080,1786]"
    // 中心点：(540, 934)
    Log("Test 4: Clicking post at (540, 934)...");
    input.Tap(540, 934);
    System.Threading.Thread.Sleep(3000);
    
    // 检查是否进入帖子详情页
    string layoutAfterPostClick = hierarchy.GetLayout();
    bool postDetailOpened = layoutAfterPostClick != layoutAfterShare;
    Log("Post click test - Detail opened: " + postDetailOpened.ToString());
    SetVar("test_post_click_result", postDetailOpened ? "OPENED" : "NO_CHANGE");
    
    // 返回首页
    if (postDetailOpened) {
        Log("Returning to feed...");
        input.Shell("input keyevent 4");
        System.Threading.Thread.Sleep(2000);
    }
    
    // 测试 5: 滚动浏览
    Log("Test 5: Scrolling feed...");
    input.Swipe(540, 1500, 540, 500, 500);
    System.Threading.Thread.Sleep(2000);
    
    string layoutAfterScroll = hierarchy.GetLayout();
    bool scrollWorked = layoutAfterScroll != layoutAfterPostClick;
    Log("Scroll test - Content changed: " + scrollWorked.ToString());
    SetVar("test_scroll_result", scrollWorked ? "SCROLLED" : "NO_CHANGE");
    
    // 汇总结果
    Log("=== Test Results ===");
    Log("Upvote: " + GetVar("test_upvote_result", "UNKNOWN"));
    Log("Comment: " + GetVar("test_comment_result", "UNKNOWN"));
    Log("Share: " + GetVar("test_share_result", "UNKNOWN"));
    Log("Post Click: " + GetVar("test_post_click_result", "UNKNOWN"));
    Log("Scroll: " + GetVar("test_scroll_result", "UNKNOWN"));
    
    Log("Test completed successfully");
    return "SUCCESS";
}
catch (System.Exception ex) {
    LogErr("Exception: " + ex.Message);
    LogErr("Stack trace: " + ex.StackTrace);
    SetVar("last_error", ex.Message);
    return "ERROR: " + ex.Message;
}
