// =====================================================
// Reddit_Comment.cs - Reddit 评论功能
// 功能：打开评论区，读取评论，可选回复功能
// 使用纯字符串解析（不依赖任何 XML 库）
// =====================================================

// ========== Helper Functions ==========
// Log/LogErr 由 ScriptHelpers.cs 提供，设置 _logTag 自定义前缀
_logTag = "Comment";

// ⚠️ GetVar, SetVar, GetProfileConfig, HumanizedDelay, HumanizedTap, HumanizedSwipe,
// ShouldTriggerProbabilistic, ParseBounds, GetCenter, FindBoundsByResourceId 等函数
// 已迁移至 Core/ScriptHelpers.cs，由 ModuleLoader 自动注入

// ========== Comment 专用 XML 解析 ==========

// 提取评论文本（排除已知的非评论节点）
Func<string, System.Collections.Generic.List<string>> ExtractCommentTexts = (xml) => {
    var results = new System.Collections.Generic.List<string>();
    
    // 已知的非评论 resource-id（需要排除）
    var excludeIds = new System.Collections.Generic.HashSet<string>();
    excludeIds.Add("reply_text_view");
    excludeIds.Add("title_text");
    excludeIds.Add("mini_context_bar_title");
    excludeIds.Add("post_title");
    excludeIds.Add("post_media_replay_label");
    
    // 已知的非评论文本模式
    var excludePatterns = new System.Collections.Generic.List<string>();
    excludePatterns.Add("upvotes");
    excludePatterns.Add("comments");
    excludePatterns.Add("Join the conversation");
    excludePatterns.Add("r/");
    
    int pos = 0;
    while (pos < xml.Length) {
        int foundPos = xml.IndexOf("text=\"", pos);
        if (foundPos < 0) break;
        
        int textStart = foundPos + 6;
        int textEnd = xml.IndexOf("\"", textStart);
        if (textEnd <= textStart) { pos = foundPos + 1; continue; }
        
        string text = xml.Substring(textStart, textEnd - textStart);
        
        // 跳过空文本或太短的文本
        if (string.IsNullOrEmpty(text) || text.Length < 10) {
            pos = textEnd + 1;
            continue;
        }
        
        // 检查是否匹配排除模式
        bool shouldExclude = false;
        foreach (var pattern in excludePatterns) {
            if (text.Contains(pattern)) {
                shouldExclude = true;
                break;
            }
        }
        
        if (!shouldExclude) {
            // 检查 resource-id 是否在排除列表中
            int nodeStart = xml.LastIndexOf('<', foundPos);
            int nodeEnd = xml.IndexOf('>', foundPos);
            if (nodeStart >= 0 && nodeEnd > nodeStart) {
                string nodeStr = xml.Substring(nodeStart, nodeEnd - nodeStart + 1);
                
                int ridPos = nodeStr.IndexOf("resource-id=\"");
                if (ridPos >= 0) {
                    int ridStart = ridPos + 13;
                    int ridEnd = nodeStr.IndexOf("\"", ridStart);
                    if (ridEnd > ridStart) {
                        string rid = nodeStr.Substring(ridStart, ridEnd - ridStart);
                        if (excludeIds.Contains(rid)) {
                            shouldExclude = true;
                        }
                    }
                }
            }
        }
        
        if (!shouldExclude && !results.Contains(text)) {
            results.Add(text);
        }
        
        pos = textEnd + 1;
    }
    
    return results;
};

// ⚠️ FindEditTextBounds 已迁移至 Core/ScriptHelpers.cs，由 ModuleLoader 自动注入

// ========== Main Logic ==========
try {
    Log("Starting Reddit Comment...");
    
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
    int postIndex = int.Parse(GetVar("comment_post_index", "0"));
    bool enableReply = GetVar("comment_enable_reply", "false").ToLower() == "true";
    string replyText = GetVar("comment_reply_text", "");
    int scrollCount = int.Parse(GetVar("comment_scroll_count", "2"));
    
    int screenWidth = int.Parse(GetVar("screen_width", "1080"));
    int screenHeight = int.Parse(GetVar("screen_height", "2400"));
    
    Log("Parameters: postIndex=" + postIndex.ToString() + ", enableReply=" + enableReply.ToString());
    
    // 打开 Reddit
    Log("Ensuring Reddit is open...");
    app.Open("com.reddit.frontpage");
    System.Threading.Thread.Sleep(HumanizedDelay(3000, profile, rng));
    
    // 获取当前 UI 层级
    Log("Getting UI hierarchy...");
    string layout = hierarchy.GetLayout();
    
    // 查找所有帖子
    // 从变量读取 selector（由 RedditModule 初始化时设置，或使用默认值）
    string sel_postUnit = GetVar("reddit_sel_post_unit", "post_unit");
    string sel_commentButton = GetVar("reddit_sel_comment_button", "post_comment_button");
    
    // 查找所有帖子
    var postBounds = FindBoundsByResourceId(layout, sel_postUnit);
    Log("Found " + postBounds.Count.ToString() + " posts on screen");
    
    if (postBounds.Count == 0) {
        LogErr("No posts found on screen");
        SetVar("comment_result", "ERROR");
        return "ERROR: No posts found";
    }
    
    if (postIndex >= postBounds.Count) {
        LogErr("Post index " + postIndex.ToString() + " out of range");
        SetVar("comment_result", "ERROR");
        return "ERROR: Post index out of range";
    }
    
    Log("Target post: index " + postIndex.ToString());
    
    // 查找评论按钮
    var commentButtonBounds = FindBoundsByResourceId(layout, sel_commentButton);
    if (commentButtonBounds.Count == 0) {
        LogErr("Comment button not found");
        SetVar("comment_result", "ERROR");
        return "ERROR: Comment button not found";
    }
    
    if (postIndex >= commentButtonBounds.Count) {
        LogErr("Comment button index out of range");
        SetVar("comment_result", "ERROR");
        return "ERROR: Comment button index out of range";
    }
    
    int[] bounds = ParseBounds(commentButtonBounds[postIndex]);
    var buttonCenter = GetCenter(bounds);
    
    Log("Clicking comment button at: (" + buttonCenter.Item1.ToString() + ", " + buttonCenter.Item2.ToString() + ")");
    HumanizedTap(bounds, profile, rng);
    System.Threading.Thread.Sleep(HumanizedDelay(2000, profile, rng));
    
    // 验证是否进入评论页
    string commentLayout = hierarchy.GetLayout();
    bool enteredComments = (commentLayout != layout);
    
    if (!enteredComments) {
        LogErr("Failed to enter comment section");
        SetVar("comment_result", "ERROR");
        return "ERROR: Failed to enter comments";
    }
    
    Log("Entered comment section");
    
    // 提取初始评论
    var allComments = new System.Collections.Generic.List<string>();
    var initialComments = ExtractCommentTexts(commentLayout);
    
    Log("Initial comments found: " + initialComments.Count.ToString());
    allComments.AddRange(initialComments);
    
    // 滚动并收集更多评论
    for (int i = 0; i < scrollCount; i++) {
        Log("Scrolling " + (i + 1).ToString() + "/" + scrollCount.ToString() + "...");
        
        int startX = screenWidth / 2;
        int startY = screenHeight * 2 / 3;
        int endY = screenHeight / 3;
        
        HumanizedSwipe(startX, startY, startX, endY, profile, rng);
        System.Threading.Thread.Sleep(HumanizedDelay(1500, profile, rng));
        
        // 获取新评论
        string newLayout = hierarchy.GetLayout();
        var newComments = ExtractCommentTexts(newLayout);
        
        // 只添加新的评论（避免重复）
        int newCount = 0;
        foreach (var comment in newComments) {
            if (!allComments.Contains(comment)) {
                allComments.Add(comment);
                newCount++;
            }
        }
        
        Log("New comments this scroll: " + newCount.ToString());
    }
    
    Log("Total unique comments collected: " + allComments.Count.ToString());
    
    // 保存评论数据
    string commentsText = string.Join("\n---\n", allComments);
    SetVar("comment_text", commentsText);
    SetVar("comment_count", allComments.Count.ToString());
    
    // 可选：回复功能
    if (enableReply && !string.IsNullOrEmpty(replyText)) {
        Log("Reply feature enabled, attempting to reply...");
        
        string replyLayout = hierarchy.GetLayout();
        var inputFields = FindEditTextBounds(replyLayout);
        
        if (inputFields.Count > 0) {
            int[] inputBoundsArr = ParseBounds(inputFields[0]);
            var inputCenter = GetCenter(inputBoundsArr);
            
            Log("Clicking reply input at: (" + inputCenter.Item1.ToString() + ", " + inputCenter.Item2.ToString() + ")");
            HumanizedTap(inputBoundsArr, profile, rng);
            System.Threading.Thread.Sleep(HumanizedDelay(1000, profile, rng));
            
            Log("Typing reply text: " + replyText);
            input.SendText(replyText);
            System.Threading.Thread.Sleep(HumanizedDelay(500, profile, rng));
            
            Log("Reply text entered (not submitted - manual submission required)");
            SetVar("comment_reply_entered", "true");
        } else {
            Log("Reply input field not found");
            SetVar("comment_reply_entered", "false");
        }
    }
    
    // 返回 feed（按返回键）
    Log("Returning to feed...");
    input.Shell("input keyevent 4"); // KEYCODE_BACK = 4
    System.Threading.Thread.Sleep(HumanizedDelay(1500, profile, rng));
    
    // 验证是否返回 feed
    string feedLayout = hierarchy.GetLayout();
    var feedPosts = FindBoundsByResourceId(feedLayout, sel_postUnit);
    
    if (feedPosts.Count > 0) {
        Log("Returned to feed successfully");
        SetVar("comment_result", "SUCCESS");
        return "SUCCESS: Read " + allComments.Count.ToString() + " comments";
    } else {
        Log("May not have returned to feed properly");
        SetVar("comment_result", "SUCCESS");
        return "SUCCESS: Read comments (feed return uncertain)";
    }
}
catch (System.Exception ex) {
    LogErr("Exception: " + ex.Message);
    LogErr("Stack trace: " + ex.StackTrace);
    SetVar("comment_result", "ERROR");
    SetVar("comment_error", ex.Message);
    return "ERROR: " + ex.Message;
}
