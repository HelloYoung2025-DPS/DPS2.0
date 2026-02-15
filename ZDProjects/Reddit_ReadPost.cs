// =====================================================
// Reddit_ReadPost.cs - 阅读 Reddit 帖子详情
// 功能：点击帖子，滚动内容，提取文本，返回 feed
// 使用纯字符串解析（不依赖任何 XML 库）
// =====================================================

// ========== Helper Functions ==========
// Log/LogErr 由 ScriptHelpers.cs 提供，设置 _logTag 自定义前缀
_logTag = "ReadPost";

// ⚠️ GetVar, SetVar, GetProfileConfig, HumanizedDelay, HumanizedTap, HumanizedSwipe,
// ShouldTriggerProbabilistic, ParseBounds, GetCenter, FindBoundsByResourceId 等函数
// 已迁移至 Core/ScriptHelpers.cs，由 ModuleLoader 自动注入

// ⚠️ ExtractAllText 已迁移至 Core/ScriptHelpers.cs，由 ModuleLoader 自动注入

// ========== Main Logic ==========
try {
    Log("Starting Reddit ReadPost...");
    
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
    int postIndex = int.Parse(GetVar("readpost_post_index", "0"));
    int scrollCount = int.Parse(GetVar("readpost_scroll_count", "2"));
    int scrollDelay = int.Parse(GetVar("readpost_scroll_delay", "1500"));
    
    int screenWidth = int.Parse(GetVar("screen_width", "1080"));
    int screenHeight = int.Parse(GetVar("screen_height", "2400"));
    
    Log("Parameters: postIndex=" + postIndex.ToString() + ", scrollCount=" + scrollCount.ToString());
    
    // 打开 Reddit
    Log("Ensuring Reddit is open...");
    app.Open("com.reddit.frontpage");
    System.Threading.Thread.Sleep(HumanizedDelay(3000, profile, rng));
    
    // 获取当前 UI 层级
    Log("Getting UI hierarchy...");
    string layout = hierarchy.GetLayout();
    
    // 从变量读取 selector（由 RedditModule 初始化时设置，或使用默认值）
    string sel_postUnit = GetVar("reddit_sel_post_unit", "post_unit");
    
    // 查找所有帖子的 bounds
    var postBounds = FindBoundsByResourceId(layout, sel_postUnit);
    Log("Found " + postBounds.Count.ToString() + " posts on screen");
    
    if (postBounds.Count == 0) {
        LogErr("No posts found on screen");
        SetVar("readpost_result", "ERROR");
        return "ERROR: No posts found";
    }
    
    if (postIndex >= postBounds.Count) {
        LogErr("Post index " + postIndex.ToString() + " out of range");
        SetVar("readpost_result", "ERROR");
        return "ERROR: Post index out of range";
    }
    
    // 获取目标帖子并点击
    int[] bounds = ParseBounds(postBounds[postIndex]);
    var postCenter = GetCenter(bounds);
    
    Log("Clicking post at: (" + postCenter.Item1.ToString() + ", " + postCenter.Item2.ToString() + ")");
    HumanizedTap(bounds, profile, rng);
    System.Threading.Thread.Sleep(HumanizedDelay(2000, profile, rng));
    
    // 验证是否进入帖子详情页
    string detailLayout = hierarchy.GetLayout();
    bool enteredDetail = (detailLayout != layout);
    
    if (!enteredDetail) {
        LogErr("Failed to enter post detail page");
        SetVar("readpost_result", "ERROR");
        return "ERROR: Failed to enter post detail";
    }
    
    Log("Entered post detail page");
    
    // 提取初始文本
    var initialTexts = ExtractAllText(detailLayout);
    Log("Initial text items: " + initialTexts.Count.ToString());
    
    var allText = new System.Text.StringBuilder();
    foreach (var t in initialTexts) {
        allText.AppendLine(t);
    }
    
    // 滚动并收集更多内容
    for (int i = 0; i < scrollCount; i++) {
        Log("Scrolling " + (i + 1).ToString() + "/" + scrollCount.ToString() + "...");
        
        int startX = screenWidth / 2;
        int startY = screenHeight * 2 / 3;
        int endY = screenHeight / 3;
        
        HumanizedSwipe(startX, startY, startX, endY, profile, rng);
        
        // Content-correlated delay: longer pause for longer content
        int currentLength = allText.Length;
        int baseReadDelay = 2000;
        if (currentLength > 5000) {
            baseReadDelay = 4000;
            Log("Long content detected, using extended delay");
        } else if (currentLength > 2000) {
            baseReadDelay = 3000;
        }
        
        System.Threading.Thread.Sleep(HumanizedDelay(baseReadDelay, profile, rng));
        
        // 获取新内容
        string newLayout = hierarchy.GetLayout();
        var newTexts = ExtractAllText(newLayout);
        
        allText.AppendLine("--- Scroll " + (i + 1).ToString() + " ---");
        foreach (var t in newTexts) {
            allText.AppendLine(t);
        }
    }
    
    string fullText = allText.ToString();
    Log("Total text extracted: " + fullText.Length.ToString() + " chars");
    
    // 保存提取的文本
    SetVar("readpost_text", fullText);
    SetVar("readpost_text_length", fullText.Length.ToString());
    
    // 返回 feed（按返回键）
    Log("Returning to feed...");
    input.Shell("input keyevent 4"); // KEYCODE_BACK = 4
    System.Threading.Thread.Sleep(HumanizedDelay(1500, profile, rng));
    
    // 验证是否返回 feed
    string feedLayout = hierarchy.GetLayout();
    var feedPosts = FindBoundsByResourceId(feedLayout, sel_postUnit);
    
    if (feedPosts.Count > 0) {
        Log("Returned to feed successfully");
        SetVar("readpost_result", "SUCCESS");
        return "SUCCESS: Read post and returned to feed";
    } else {
        Log("May not have returned to feed properly");
        SetVar("readpost_result", "SUCCESS");
        return "SUCCESS: Read post (feed return uncertain)";
    }
}
catch (System.Exception ex) {
    LogErr("Exception: " + ex.Message);
    LogErr("Stack trace: " + ex.StackTrace);
    SetVar("readpost_result", "ERROR");
    SetVar("readpost_error", ex.Message);
    return "ERROR: " + ex.Message;
}
