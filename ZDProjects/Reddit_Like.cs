// =====================================================
// Reddit_Like.cs - 点赞 Reddit 帖子
// 功能：动态定位点赞按钮，点击，验证状态变化
// 使用纯字符串解析（不依赖任何 XML 库）
// =====================================================

// ========== Helper Functions ==========
// Log/LogErr 由 ScriptHelpers.cs 提供，设置 _logTag 自定义前缀
_logTag = "Like";

// ⚠️ GetVar, SetVar, GetProfileConfig, HumanizedDelay, HumanizedTap, HumanizedSwipe,
// ShouldTriggerProbabilistic, ParseBounds, GetCenter, FindBoundsByResourceId 等函数
// 已迁移至 Core/ScriptHelpers.cs，由 ModuleLoader 自动注入

// ⚠️ FindNodesByResourceId, ExtractBounds 已迁移至 Core/ScriptHelpers.cs，由 ModuleLoader 自动注入

// ========== Like 专用 XML 解析 ==========

// 在 post_unit 节点后查找 post_footer，然后找其第一个子节点的 bounds
Func<string, int, string> FindUpvoteBoundsForPost = (xml, postIndex) => {
    // 从变量读取 selectors（由 RedditModule 初始化时设置，或使用默认值）
    string sel_postUnit = GetVar("reddit_sel_post_unit", "post_unit");
    string sel_postFooter = GetVar("reddit_sel_post_footer", "post_footer");
    
    // 先找所有 post_unit
    var postNodes = FindNodesByResourceId(xml, sel_postUnit);
    if (postIndex >= postNodes.Count) return "";
    
    // 找到目标 post_unit 在 xml 中的位置
    string targetPost = postNodes[postIndex];
    int postPos = xml.IndexOf(targetPost);
    if (postPos < 0) return "";
    
    // 从 post_unit 位置开始，找 post_footer
    int footerSearchStart = postPos + targetPost.Length;
    
    // 如果有下一个 post_unit，限制搜索范围
    int searchEnd = xml.Length;
    if (postIndex + 1 < postNodes.Count) {
        int nextPostPos = xml.IndexOf(postNodes[postIndex + 1], footerSearchStart);
        if (nextPostPos > 0) searchEnd = nextPostPos;
    }
    
    string searchArea = xml.Substring(footerSearchStart, searchEnd - footerSearchStart);
    
    // 在这个范围内找 post_footer
    int footerPos = searchArea.IndexOf("resource-id=\"" + sel_postFooter + "\"");
    if (footerPos < 0) return "";
    
    // 找到 post_footer 节点的结束位置
    int footerNodeEnd = searchArea.IndexOf('>', footerPos);
    if (footerNodeEnd < 0) return "";
    
    // 从 post_footer 之后找第一个有 bounds 的子节点
    string afterFooter = searchArea.Substring(footerNodeEnd + 1);
    int firstChildStart = afterFooter.IndexOf('<');
    if (firstChildStart < 0) return "";
    
    int firstChildEnd = afterFooter.IndexOf('>', firstChildStart);
    if (firstChildEnd < 0) return "";
    
    string firstChild = afterFooter.Substring(firstChildStart, firstChildEnd - firstChildStart + 1);
    return ExtractBounds(firstChild);
};

// ========== Main Logic ==========
try {
    Log("Starting Reddit Like...");
    
    var droid = instance.DroidInstance;
    var input = droid.Input;
    var app = droid.App;
    var hierarchy = droid.Hierarchy;
    
    // Initialize humanization profile
    string profileName = GetVar("humanization_profile", "casual");
    var profile = GetProfileConfig(profileName);
    var rng = new System.Random();
    Log("Using humanization profile: " + profileName);
    
    // 读取参数
    int postIndex = int.Parse(GetVar("like_post_index", "0"));
    int verifyDelay = int.Parse(GetVar("like_verify_delay", "1500"));
    
    Log("Parameters: postIndex=" + postIndex.ToString());
    
    // 打开 Reddit
    Log("Ensuring Reddit is open...");
    app.Open("com.reddit.frontpage");
    System.Threading.Thread.Sleep(HumanizedDelay(3000, profile, rng));
    
    // 获取当前 UI 层级
    Log("Getting UI hierarchy...");
    string layout = hierarchy.GetLayout();
    
    // 查找所有帖子
    // 从变量读取 selector
    string sel_postUnit = GetVar("reddit_sel_post_unit", "post_unit");
    var postNodes = FindNodesByResourceId(layout, sel_postUnit);
    Log("Found " + postNodes.Count.ToString() + " posts on screen");
    
    if (postNodes.Count == 0) {
        LogErr("No posts found on screen");
        SetVar("like_result", "ERROR");
        return "ERROR: No posts found";
    }
    
    if (postIndex >= postNodes.Count) {
        LogErr("Post index " + postIndex.ToString() + " out of range (max: " + (postNodes.Count - 1).ToString() + ")");
        SetVar("like_result", "ERROR");
        return "ERROR: Post index out of range";
    }
    
    Log("Target post: index " + postIndex.ToString());
    
    // 查找点赞按钮的 bounds
    string upvoteBounds = FindUpvoteBoundsForPost(layout, postIndex);
    if (string.IsNullOrEmpty(upvoteBounds)) {
        LogErr("Upvote button not found for post " + postIndex.ToString());
        SetVar("like_result", "ERROR");
        return "ERROR: Upvote button not found";
    }
    
    int[] bounds = ParseBounds(upvoteBounds);
    var upvoteCenter = GetCenter(bounds);
    Log("Upvote button at: (" + upvoteCenter.Item1.ToString() + ", " + upvoteCenter.Item2.ToString() + ")");
    
    // 点击点赞按钮
    Log("Clicking upvote button...");
    HumanizedTap(bounds, profile, rng);
    System.Threading.Thread.Sleep(HumanizedDelay(verifyDelay, profile, rng));
    
    // 验证 UI 是否变化
    Log("Verifying UI change...");
    string newLayout = hierarchy.GetLayout();
    bool uiChanged = (newLayout != layout);
    
    Log("UI changed: " + uiChanged.ToString());
    
    if (uiChanged) {
        Log("Like action successful");
        SetVar("like_result", "SUCCESS");
        SetVar("like_ui_changed", "true");
        return "SUCCESS: Upvote clicked and UI changed";
    } else {
        Log("Like action completed but UI unchanged (may already be liked)");
        SetVar("like_result", "SUCCESS");
        SetVar("like_ui_changed", "false");
        return "SUCCESS: Upvote clicked (UI unchanged)";
    }
}
catch (System.Exception ex) {
    LogErr("Exception: " + ex.Message);
    LogErr("Stack trace: " + ex.StackTrace);
    SetVar("like_result", "ERROR");
    SetVar("like_error", ex.Message);
    return "ERROR: " + ex.Message;
}
