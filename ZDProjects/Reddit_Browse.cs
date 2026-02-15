// =====================================================
// Reddit_Browse.cs - 浏览 Reddit 首页帖子
// 功能：滚动 feed，检测帖子，跟踪已浏览帖子
// 使用纯字符串解析（不依赖任何 XML 库）
// =====================================================

// ========== Helper Functions ==========
// Log/LogErr 由 ScriptHelpers.cs 提供，设置 _logTag 自定义前缀
_logTag = "Browse";
// ⚠️ GetVar, SetVar, GetProfileConfig, HumanizedDelay, HumanizedTap, HumanizedSwipe,
// ShouldTriggerProbabilistic, ParseBounds, FindBoundsByResourceId 等函数
// 已迁移至 Core/ScriptHelpers.cs，由 ModuleLoader 自动注入

// ========== Main Logic ==========
try {
    Log("Starting Reddit Browse...");
    
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
    int scrollCount = int.Parse(GetVar("browse_scroll_count", "3"));
    int scrollDelay = int.Parse(GetVar("browse_scroll_delay", "2000"));
    
    // 屏幕尺寸 - 从变量读取或使用默认值
    int screenWidth = int.Parse(GetVar("screen_width", "1080"));
    int screenHeight = int.Parse(GetVar("screen_height", "2400"));
    
    Log("Parameters: scrollCount=" + scrollCount.ToString() + ", scrollDelay=" + scrollDelay.ToString());
    Log("Screen size: " + screenWidth.ToString() + "x" + screenHeight.ToString());
    
    // 打开 Reddit
    Log("Opening Reddit app...");
    app.Open("com.reddit.frontpage");
    System.Threading.Thread.Sleep(HumanizedDelay(3000, profile, rng));
    
    // 跟踪已浏览的帖子（使用 Y 坐标范围作为唯一标识）
    var viewedPosts = new System.Collections.Generic.HashSet<string>();
    int totalPostsFound = 0;
    
    // 滚动并收集帖子
    for (int scroll = 0; scroll <= scrollCount; scroll++) {
        Log("=== Scroll " + scroll.ToString() + "/" + scrollCount.ToString() + " ===");
        
        // 获取当前屏幕的 UI 层级
        string layout = hierarchy.GetLayout();
        
        // 从变量读取 selector（由 RedditModule 初始化时设置，或使用默认值）
        string sel_postUnit = GetVar("reddit_sel_post_unit", "post_unit");
        
        // 查找所有帖子的 bounds
        var postBounds = FindBoundsByResourceId(layout, sel_postUnit);
        Log("Found " + postBounds.Count.ToString() + " posts on screen");
        
        int newPostsThisScroll = 0;
        
        foreach (var boundsStr in postBounds) {
            int[] bounds = ParseBounds(boundsStr);
            int postTop = bounds[1];
            int postBottom = bounds[3];
            
            // 使用 Y 坐标范围作为帖子的唯一标识
            string postId = postTop.ToString() + "-" + postBottom.ToString();
            
            // 检查是否在屏幕可见区域内（至少 50% 可见）
            int postHeight = postBottom - postTop;
            bool isVisible = postBottom > 0 && postTop < screenHeight && postTop > -postHeight / 2;
            
            if (isVisible && !viewedPosts.Contains(postId)) {
                viewedPosts.Add(postId);
                newPostsThisScroll++;
                totalPostsFound++;
                
                Log("New post #" + totalPostsFound.ToString() + ": Y=" + postTop.ToString() + "-" + postBottom.ToString());
            }
        }
        
        Log("New posts this scroll: " + newPostsThisScroll.ToString());
        
        // 如果不是最后一次滚动，则向下滚动
        if (scroll < scrollCount) {
            Log("Scrolling down...");
            
            // 从屏幕中部向上滑动（向下滚动）
            int startX = screenWidth / 2;
            int startY = screenHeight * 2 / 3;
            int endY = screenHeight / 3;
            
            HumanizedSwipe(startX, startY, startX, endY, profile, rng);
            System.Threading.Thread.Sleep(HumanizedDelay(scrollDelay, profile, rng));
            
            // Probabilistic behaviors
            if (ShouldTriggerProbabilistic("accidental_back", profile, rng)) {
                Log("Probabilistic: accidental back");
                input.Shell("input keyevent 4");
                System.Threading.Thread.Sleep(HumanizedDelay(1000, profile, rng));
                app.Open("com.reddit.frontpage");
                System.Threading.Thread.Sleep(HumanizedDelay(2000, profile, rng));
            }
            if (ShouldTriggerProbabilistic("scroll_back", profile, rng)) {
                Log("Probabilistic: scroll back up");
                HumanizedSwipe(startX, endY, startX, startY, profile, rng);
                System.Threading.Thread.Sleep(HumanizedDelay(800, profile, rng));
            }
            if (ShouldTriggerProbabilistic("longer_pause", profile, rng)) {
                Log("Probabilistic: longer pause");
                System.Threading.Thread.Sleep(HumanizedDelay(3000, profile, rng));
            }
        }
    }
    
    Log("=== Browse Complete ===");
    Log("Total unique posts viewed: " + totalPostsFound.ToString());
    
    // 保存结果到变量
    SetVar("browse_posts_count", totalPostsFound.ToString());
    SetVar("browse_result", "SUCCESS");
    
    return "SUCCESS: Viewed " + totalPostsFound.ToString() + " posts";
}
catch (System.Exception ex) {
    LogErr("Exception: " + ex.Message);
    LogErr("Stack trace: " + ex.StackTrace);
    SetVar("browse_result", "ERROR");
    SetVar("browse_error", ex.Message);
    return "ERROR: " + ex.Message;
}
