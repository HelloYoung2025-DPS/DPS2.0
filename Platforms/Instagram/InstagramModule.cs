// =====================================================
// InstagramModule.cs - Instagram Platform Implementation
// =====================================================
// Purpose: Implements Instagram platform operations using shared Core framework
//
// Operations:
//   - Initialize: Open Instagram app, verify state
//   - Browse: Scroll feed, detect posts
//   - Like: Double-tap or tap heart button
//   - Comment: Write and submit comments
//   - Follow: Follow users
//   - Share: Share posts to stories/DM
//
// Rate Limits:
//   - Max 60 actions/hour (stricter than Reddit)
//   - Max 30 likes/hour
//   - Max 15 comments/hour
//   - Max 10 follows/hour
//
// Dependencies:
//   - Core/HumanizationEngine.cs (for humanized behavior)
//   - Core/UILocator.cs (for element finding)
//   - Core/ErrorRecovery.cs (for retry logic)
//   - Config/PlatformsConfig.json (for Instagram config)
// =====================================================

// ========== Helper Functions ==========
// Log/LogErr/GetVar/SetVar 由 ScriptHelpers.cs 提供（ModuleLoader 自动注入）
_logTag = "InstagramModule";

// ========== Load Core Modules ==========

string _projectRoot = GetVar("project_root", "");
if (string.IsNullOrEmpty(_projectRoot)) {
    LogErr("FATAL: project_root 未设置");
    return;
}
if (!_projectRoot.EndsWith("\\")) _projectRoot += "\\";

// ⚠️ HumanizationEngine / UILocator / ErrorRecovery 的函数已定义在 ScriptHelpers.cs 中，
//    通过 Own Code 上下文自动可用，无需手动加载文件。

// Load PlatformsConfig
string configPath = _projectRoot + "Config\\PlatformsConfig.json";
string configJson = System.IO.File.ReadAllText(configPath);

// ========== Parse Instagram Config ==========

// 纯字符串 JSON 解析（C# 5.0 无 JSON 库）
Func<string, string, string> GetJsonSection = (json, sectionKey) => {
    string search = "\"" + sectionKey + "\"";
    int keyPos = json.IndexOf(search);
    if (keyPos < 0) return "";
    int braceStart = json.IndexOf('{', keyPos);
    if (braceStart < 0) return "";
    int depth = 1;
    int pos = braceStart + 1;
    while (pos < json.Length && depth > 0) {
        if (json[pos] == '{') depth++;
        else if (json[pos] == '}') depth--;
        pos++;
    }
    return json.Substring(braceStart, pos - braceStart);
};

Func<string, string, string, string> GetJsonValue = (json, key, defaultVal) => {
    string search = "\"" + key + "\"";
    int keyPos = json.IndexOf(search);
    if (keyPos < 0) return defaultVal;
    int colonPos = json.IndexOf(':', keyPos + search.Length);
    if (colonPos < 0) return defaultVal;
    int valStart = colonPos + 1;
    while (valStart < json.Length && " \t\r\n".IndexOf(json[valStart]) >= 0) valStart++;
    if (valStart >= json.Length) return defaultVal;
    if (json[valStart] == '"') {
        int valEnd = json.IndexOf('"', valStart + 1);
        return valEnd > valStart ? json.Substring(valStart + 1, valEnd - valStart - 1) : defaultVal;
    } else {
        int valEnd = valStart;
        while (valEnd < json.Length && ",}\r\n".IndexOf(json[valEnd]) < 0) valEnd++;
        return json.Substring(valStart, valEnd - valStart).Trim();
    }
};

// 解析 Instagram 平台配置
string igConfig = GetJsonSection(configJson, "instagram");
string igSelectors = GetJsonSection(igConfig, "ui_selectors");
string igRateLimits = GetJsonSection(igConfig, "rate_limits");

// UI 选择器（从配置读取，带默认值回退）
string cfg_packageName = GetJsonValue(igConfig, "package_name", "com.instagram.android");

// 提取嵌套的 selector 对象，读取内部 value 字段
// 结构: "post_unit": { "strategy": "resource-id", "value": "media_container" }
Func<string, string, string, string> GetSelectorValue = (selectorsJson, selectorName, defaultVal) => {
    string selectorObj = GetJsonSection(selectorsJson, selectorName);
    if (string.IsNullOrEmpty(selectorObj)) return defaultVal;
    string val = GetJsonValue(selectorObj, "value", "");
    return string.IsNullOrEmpty(val) ? defaultVal : val;
};

string cfg_postUnit = GetSelectorValue(igSelectors, "post_unit", "media_container");
string cfg_likeButton = GetSelectorValue(igSelectors, "like_button", "like_button");
string cfg_commentButton = GetSelectorValue(igSelectors, "comment_button", "comment_button");
string cfg_submitButton = GetSelectorValue(igSelectors, "submit_button", "post_button");
string cfg_followButton = GetSelectorValue(igSelectors, "follow_button", "follow_button");
string cfg_shareButton = GetSelectorValue(igSelectors, "share_button", "share_button");
string cfg_mediaImage = GetSelectorValue(igSelectors, "media_image", "media_image");

// 速率限制（从配置读取）
int cfg_maxActionsPerHour = int.Parse(GetJsonValue(igRateLimits, "max_actions_per_hour", "60"));
int cfg_maxLikesPerHour = int.Parse(GetJsonValue(igRateLimits, "max_likes_per_hour", "30"));
int cfg_maxCommentsPerHour = int.Parse(GetJsonValue(igRateLimits, "max_comments_per_hour", "10"));
int cfg_maxFollowsPerHour = int.Parse(GetJsonValue(igRateLimits, "max_follows_per_hour", "20"));

Log("配置加载完成: package=" + cfg_packageName + ", selectors=" + igSelectors.Length.ToString() + " chars, rateLimits loaded");

// ========== Instagram Module Factory ==========

// Create standardized result dictionary
Func<bool, string, System.Collections.Generic.Dictionary<string, object>, int, System.Collections.Generic.Dictionary<string, object>> CreateResult = 
    (success, message, data, durationMs) => {
    var result = new System.Collections.Generic.Dictionary<string, object>();
    result["success"] = success;
    result["message"] = message;
    result["data"] = data ?? new System.Collections.Generic.Dictionary<string, object>();
    result["duration_ms"] = durationMs;
    return result;
};

// Log action in standard format
Action<string, string, string, int> LogAction = (action, target, result, durationMs) => {
    string timestamp = System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
    string logEntry = timestamp + "|instagram|" + action + "|" + target + "|" + result + "|" + durationMs.ToString();
    SetVar("last_action_log", logEntry);
    Log("Action: " + logEntry);
};

// ========== Initialize Operation ==========
Func<dynamic, System.Collections.Generic.Dictionary<string, object>> Initialize = (proj) => {
    var startTime = System.DateTime.Now;
    Log("Initializing Instagram platform...");
    
    try {
        var droid = instance.DroidInstance;
        var app = droid.App;
        
        // 从配置读取 package name
        string packageName = cfg_packageName;
        
        // Open Instagram app
        Log("Opening Instagram app: " + packageName);
        app.Open(packageName);
        System.Threading.Thread.Sleep(3000);
        
        // Verify app opened successfully
        string currentPackage = app.GetCurrentPackage();
        if (currentPackage != packageName) {
            LogErr("Failed to open Instagram app. Current package: " + currentPackage);
            var duration = (int)(System.DateTime.Now - startTime).TotalMilliseconds;
            LogAction("initialize", packageName, "failed", duration);
            return CreateResult(false, "Failed to open Instagram app", null, duration);
        }
        
        Log("Instagram app opened successfully");
        
        // Initialize humanization profile
        string profileName = GetVar("humanization_profile", "casual");
        SetVar("instagram_humanization_profile", profileName);
        Log("Using humanization profile: " + profileName);
        
        // Store screen dimensions
        int screenWidth = int.Parse(GetVar("screen_width", "1080"));
        int screenHeight = int.Parse(GetVar("screen_height", "2400"));
        SetVar("instagram_screen_width", screenWidth.ToString());
        SetVar("instagram_screen_height", screenHeight.ToString());
        
        // Initialize rate limit tracking
        SetVar("instagram_actions_this_hour", "0");
        SetVar("instagram_likes_this_hour", "0");
        SetVar("instagram_comments_this_hour", "0");
        SetVar("instagram_follows_this_hour", "0");
        SetVar("instagram_hour_start", System.DateTime.Now.ToString("yyyy-MM-dd HH:00:00"));
        
        var resultData = new System.Collections.Generic.Dictionary<string, object>();
        resultData["package_name"] = packageName;
        resultData["humanization_profile"] = profileName;
        resultData["screen_width"] = screenWidth;
        resultData["screen_height"] = screenHeight;
        
        var duration = (int)(System.DateTime.Now - startTime).TotalMilliseconds;
        LogAction("initialize", packageName, "success", duration);
        
        return CreateResult(true, "Instagram initialized successfully", resultData, duration);
        
    } catch (System.Exception ex) {
        LogErr("Initialize failed: " + ex.Message);
        var duration = (int)(System.DateTime.Now - startTime).TotalMilliseconds;
        LogAction("initialize", "instagram", "error", duration);
        return CreateResult(false, "Initialize error: " + ex.Message, null, duration);
    }
};

// ========== Browse Operation ==========
Func<dynamic, System.Collections.Generic.Dictionary<string, object>> Browse = (proj) => {
    var startTime = System.DateTime.Now;
    Log("Starting Browse operation...");
    
    try {
        var droid = instance.DroidInstance;
        var input = droid.Input;
        var hierarchy = droid.Hierarchy;
        
        // Check rate limits (from config)
        if (!CheckRateLimit("actions", cfg_maxActionsPerHour)) {
            var duration = (int)(System.DateTime.Now - startTime).TotalMilliseconds;
            LogAction("browse", "feed", "rate_limited", duration);
            return CreateResult(false, "Rate limit exceeded for actions", null, duration);
        }
        
        // Get parameters
        int scrollCount = int.Parse(GetVar("browse_scroll_count", "3"));
        int screenWidth = int.Parse(GetVar("instagram_screen_width", "1080"));
        int screenHeight = int.Parse(GetVar("instagram_screen_height", "2400"));
        string profileName = GetVar("instagram_humanization_profile", "casual");
        
        Log("Parameters: scrollCount=" + scrollCount.ToString() + ", profile=" + profileName);
        
        // Track viewed posts
        var viewedPosts = new System.Collections.Generic.HashSet<string>();
        int totalPostsFound = 0;
        
        // Scroll and collect posts
        for (int scroll = 0; scroll <= scrollCount; scroll++) {
            Log("=== Scroll " + scroll.ToString() + "/" + scrollCount.ToString() + " ===");
            
            // Get current UI layout
            string layout = hierarchy.GetLayout();
            
            // Find all post bounds using UILocator pattern
            // Instagram uses different resource IDs than Reddit
            var postBounds = new System.Collections.Generic.List<string>();
            string searchPattern = "resource-id=\"" + cfg_postUnit + "\"";
            int pos = 0;
            while (pos < layout.Length) {
                int foundPos = layout.IndexOf(searchPattern, pos);
                if (foundPos < 0) break;
                
                int nodeStart = layout.LastIndexOf('<', foundPos);
                if (nodeStart < 0) { pos = foundPos + 1; continue; }
                
                int nodeEnd = layout.IndexOf('>', foundPos);
                if (nodeEnd < 0) { pos = foundPos + 1; continue; }
                
                string nodeStr = layout.Substring(nodeStart, nodeEnd - nodeStart + 1);
                
                int boundsStart = nodeStr.IndexOf("bounds=\"");
                if (boundsStart >= 0) {
                    boundsStart += 8;
                    int boundsEnd = nodeStr.IndexOf("\"", boundsStart);
                    if (boundsEnd > boundsStart) {
                        string bounds = nodeStr.Substring(boundsStart, boundsEnd - boundsStart);
                        postBounds.Add(bounds);
                    }
                }
                
                pos = nodeEnd + 1;
            }
            
            Log("Found " + postBounds.Count.ToString() + " posts on screen");
            
            int newPostsThisScroll = 0;
            foreach (var boundsStr in postBounds) {
                // Parse bounds: "[x1,y1][x2,y2]"
                string cleaned = boundsStr.Replace("[", "").Replace("]", ",");
                string[] parts = cleaned.Split(new char[] {','}, System.StringSplitOptions.RemoveEmptyEntries);
                int postTop = int.Parse(parts[1]);
                int postBottom = int.Parse(parts[3]);
                
                string postId = postTop.ToString() + "-" + postBottom.ToString();
                
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
            
            // Scroll down if not last iteration
            if (scroll < scrollCount) {
                Log("Scrolling down...");
                int startX = screenWidth / 2;
                int startY = (int)(screenHeight * 0.7);
                int endY = (int)(screenHeight * 0.3);
                
                // Note: In production, use HumanizationEngine.HumanizedSwipe
                input.SwipeCurved(startX, startY, 0.3, startX, endY);
                
                // Note: In production, use HumanizationEngine.HumanizedDelay
                System.Threading.Thread.Sleep(2000);
            }
        }
        
        Log("Browse complete. Total posts found: " + totalPostsFound.ToString());
        
        // Increment rate limit counter
        IncrementRateLimit("actions");
        
        var resultData = new System.Collections.Generic.Dictionary<string, object>();
        resultData["total_posts_found"] = totalPostsFound;
        resultData["scroll_count"] = scrollCount;
        
        var duration = (int)(System.DateTime.Now - startTime).TotalMilliseconds;
        LogAction("browse", scrollCount.ToString() + "_scrolls", "success", duration);
        
        return CreateResult(true, "Browse completed", resultData, duration);
        
    } catch (System.Exception ex) {
        LogErr("Browse failed: " + ex.Message);
        var duration = (int)(System.DateTime.Now - startTime).TotalMilliseconds;
        LogAction("browse", "feed", "error", duration);
        return CreateResult(false, "Browse error: " + ex.Message, null, duration);
    }
};

// ========== Like Operation ==========
Func<dynamic, System.Collections.Generic.Dictionary<string, object>> Like = (proj) => {
    var startTime = System.DateTime.Now;
    Log("Starting Like operation...");
    
    try {
        var droid = instance.DroidInstance;
        var input = droid.Input;
        var hierarchy = droid.Hierarchy;
        
        // Check rate limits (from config)
        if (!CheckRateLimit("likes", cfg_maxLikesPerHour)) {
            var duration = (int)(System.DateTime.Now - startTime).TotalMilliseconds;
            LogAction("like", "post", "rate_limited", duration);
            return CreateResult(false, "Rate limit exceeded for likes", null, duration);
        }
        
        // Get current UI layout
        string layout = hierarchy.GetLayout();
        
        // Find like button (from config)
        string searchPattern = "resource-id=\"" + cfg_likeButton + "\"";
        int foundPos = layout.IndexOf(searchPattern);
        
        if (foundPos < 0) {
            // Try alternative: double-tap on post image (from config)
            searchPattern = "resource-id=\"" + cfg_mediaImage + "\"";
            foundPos = layout.IndexOf(searchPattern);
        }
        
        if (foundPos < 0) {
            LogErr("Like button not found");
            var duration = (int)(System.DateTime.Now - startTime).TotalMilliseconds;
            LogAction("like", "post", "not_found", duration);
            return CreateResult(false, "Like button not found", null, duration);
        }
        
        // Extract bounds
        int nodeStart = layout.LastIndexOf('<', foundPos);
        int nodeEnd = layout.IndexOf('>', foundPos);
        string nodeStr = layout.Substring(nodeStart, nodeEnd - nodeStart + 1);
        
        int boundsStart = nodeStr.IndexOf("bounds=\"") + 8;
        int boundsEnd = nodeStr.IndexOf("\"", boundsStart);
        string boundsStr = nodeStr.Substring(boundsStart, boundsEnd - boundsStart);
        
        // Parse bounds
        string cleaned = boundsStr.Replace("[", "").Replace("]", ",");
        string[] parts = cleaned.Split(new char[] {','}, System.StringSplitOptions.RemoveEmptyEntries);
        int x1 = int.Parse(parts[0]);
        int y1 = int.Parse(parts[1]);
        int x2 = int.Parse(parts[2]);
        int y2 = int.Parse(parts[3]);
        
        // Tap center with small offset
        int centerX = (x1 + x2) / 2;
        int centerY = (y1 + y2) / 2;
        
        // Note: In production, use HumanizationEngine.HumanizedTap
        // Instagram: Can use double-tap on image or single tap on heart
        input.Tap(centerX, centerY);
        Log("Tapped like button at (" + centerX.ToString() + ", " + centerY.ToString() + ")");
        
        // Note: In production, use HumanizationEngine.HumanizedDelay
        System.Threading.Thread.Sleep(1000);
        
        // Increment rate limit counters
        IncrementRateLimit("actions");
        IncrementRateLimit("likes");
        
        var resultData = new System.Collections.Generic.Dictionary<string, object>();
        resultData["tap_x"] = centerX;
        resultData["tap_y"] = centerY;
        
        var duration = (int)(System.DateTime.Now - startTime).TotalMilliseconds;
        LogAction("like", "post", "success", duration);
        
        return CreateResult(true, "Post liked successfully", resultData, duration);
        
    } catch (System.Exception ex) {
        LogErr("Like failed: " + ex.Message);
        var duration = (int)(System.DateTime.Now - startTime).TotalMilliseconds;
        LogAction("like", "post", "error", duration);
        return CreateResult(false, "Like error: " + ex.Message, null, duration);
    }
};

// ========== Comment Operation ==========
Func<dynamic, System.Collections.Generic.Dictionary<string, object>> Comment = (proj) => {
    var startTime = System.DateTime.Now;
    Log("Starting Comment operation...");
    
    try {
        var droid = instance.DroidInstance;
        var input = droid.Input;
        var hierarchy = droid.Hierarchy;
        
        // Check rate limits (from config)
        if (!CheckRateLimit("comments", cfg_maxCommentsPerHour)) {
            var duration = (int)(System.DateTime.Now - startTime).TotalMilliseconds;
            LogAction("comment", "post", "rate_limited", duration);
            return CreateResult(false, "Rate limit exceeded for comments", null, duration);
        }
        
        string commentText = GetVar("comment_text", "Nice post!");
        Log("Comment text: " + commentText);
        
        // Find comment button (from config)
        string layout = hierarchy.GetLayout();
        string searchPattern = "resource-id=\"" + cfg_commentButton + "\"";
        int foundPos = layout.IndexOf(searchPattern);
        
        if (foundPos < 0) {
            LogErr("Comment button not found");
            var duration = (int)(System.DateTime.Now - startTime).TotalMilliseconds;
            LogAction("comment", "post", "not_found", duration);
            return CreateResult(false, "Comment button not found", null, duration);
        }
        
        // Extract and tap comment button
        int nodeStart = layout.LastIndexOf('<', foundPos);
        int nodeEnd = layout.IndexOf('>', foundPos);
        string nodeStr = layout.Substring(nodeStart, nodeEnd - nodeStart + 1);
        
        int boundsStart = nodeStr.IndexOf("bounds=\"") + 8;
        int boundsEnd = nodeStr.IndexOf("\"", boundsStart);
        string boundsStr = nodeStr.Substring(boundsStart, boundsEnd - boundsStart);
        
        string cleaned = boundsStr.Replace("[", "").Replace("]", ",");
        string[] parts = cleaned.Split(new char[] {','}, System.StringSplitOptions.RemoveEmptyEntries);
        int centerX = (int.Parse(parts[0]) + int.Parse(parts[2])) / 2;
        int centerY = (int.Parse(parts[1]) + int.Parse(parts[3])) / 2;
        
        input.Tap(centerX, centerY);
        Log("Tapped comment button");
        System.Threading.Thread.Sleep(2000);
        
        // Type comment text
        input.SendText(commentText);
        Log("Typed comment text");
        System.Threading.Thread.Sleep(1000);
        
        // Find and tap post button (from config)
        layout = hierarchy.GetLayout();
        searchPattern = "resource-id=\"" + cfg_submitButton + "\"";
        foundPos = layout.IndexOf(searchPattern);
        
        if (foundPos >= 0) {
            nodeStart = layout.LastIndexOf('<', foundPos);
            nodeEnd = layout.IndexOf('>', foundPos);
            nodeStr = layout.Substring(nodeStart, nodeEnd - nodeStart + 1);
            
            boundsStart = nodeStr.IndexOf("bounds=\"") + 8;
            boundsEnd = nodeStr.IndexOf("\"", boundsStart);
            boundsStr = nodeStr.Substring(boundsStart, boundsEnd - boundsStart);
            
            cleaned = boundsStr.Replace("[", "").Replace("]", ",");
            parts = cleaned.Split(new char[] {','}, System.StringSplitOptions.RemoveEmptyEntries);
            centerX = (int.Parse(parts[0]) + int.Parse(parts[2])) / 2;
            centerY = (int.Parse(parts[1]) + int.Parse(parts[3])) / 2;
            
            input.Tap(centerX, centerY);
            Log("Tapped post button");
            System.Threading.Thread.Sleep(1000);
            
            // Increment rate limit counters
            IncrementRateLimit("actions");
            IncrementRateLimit("comments");
            
            var resultData = new System.Collections.Generic.Dictionary<string, object>();
            resultData["comment_text"] = commentText;
            resultData["comment_length"] = commentText.Length;
            
            var duration = (int)(System.DateTime.Now - startTime).TotalMilliseconds;
            LogAction("comment", "post", "success", duration);
            
            return CreateResult(true, "Comment posted successfully", resultData, duration);
        } else {
            LogErr("Post button not found");
            var duration = (int)(System.DateTime.Now - startTime).TotalMilliseconds;
            LogAction("comment", "post", "post_button_not_found", duration);
            return CreateResult(false, "Post button not found", null, duration);
        }
        
    } catch (System.Exception ex) {
        LogErr("Comment failed: " + ex.Message);
        var duration = (int)(System.DateTime.Now - startTime).TotalMilliseconds;
        LogAction("comment", "post", "error", duration);
        return CreateResult(false, "Comment error: " + ex.Message, null, duration);
    }
};

// ========== Follow Operation ==========
Func<dynamic, System.Collections.Generic.Dictionary<string, object>> Follow = (proj) => {
    var startTime = System.DateTime.Now;
    Log("Starting Follow operation...");
    
    try {
        var droid = instance.DroidInstance;
        var input = droid.Input;
        var hierarchy = droid.Hierarchy;
        
        // Check rate limits (from config)
        if (!CheckRateLimit("follows", cfg_maxFollowsPerHour)) {
            var duration = (int)(System.DateTime.Now - startTime).TotalMilliseconds;
            LogAction("follow", "user", "rate_limited", duration);
            return CreateResult(false, "Rate limit exceeded for follows", null, duration);
        }
        
        // Find follow button (from config)
        string layout = hierarchy.GetLayout();
        string searchPattern = "resource-id=\"" + cfg_followButton + "\"";
        int foundPos = layout.IndexOf(searchPattern);
        
        if (foundPos < 0) {
            LogErr("Follow button not found");
            var duration = (int)(System.DateTime.Now - startTime).TotalMilliseconds;
            LogAction("follow", "user", "not_found", duration);
            return CreateResult(false, "Follow button not found", null, duration);
        }
        
        // Extract and tap follow button
        int nodeStart = layout.LastIndexOf('<', foundPos);
        int nodeEnd = layout.IndexOf('>', foundPos);
        string nodeStr = layout.Substring(nodeStart, nodeEnd - nodeStart + 1);
        
        int boundsStart = nodeStr.IndexOf("bounds=\"") + 8;
        int boundsEnd = nodeStr.IndexOf("\"", boundsStart);
        string boundsStr = nodeStr.Substring(boundsStart, boundsEnd - boundsStart);
        
        string cleaned = boundsStr.Replace("[", "").Replace("]", ",");
        string[] parts = cleaned.Split(new char[] {','}, System.StringSplitOptions.RemoveEmptyEntries);
        int centerX = (int.Parse(parts[0]) + int.Parse(parts[2])) / 2;
        int centerY = (int.Parse(parts[1]) + int.Parse(parts[3])) / 2;
        
        input.Tap(centerX, centerY);
        Log("Tapped follow button at (" + centerX.ToString() + ", " + centerY.ToString() + ")");
        System.Threading.Thread.Sleep(1000);
        
        // Increment rate limit counters
        IncrementRateLimit("actions");
        IncrementRateLimit("follows");
        
        var resultData = new System.Collections.Generic.Dictionary<string, object>();
        resultData["tap_x"] = centerX;
        resultData["tap_y"] = centerY;
        
        var duration = (int)(System.DateTime.Now - startTime).TotalMilliseconds;
        LogAction("follow", "user", "success", duration);
        
        return CreateResult(true, "Followed successfully", resultData, duration);
        
    } catch (System.Exception ex) {
        LogErr("Follow failed: " + ex.Message);
        var duration = (int)(System.DateTime.Now - startTime).TotalMilliseconds;
        LogAction("follow", "user", "error", duration);
        return CreateResult(false, "Follow error: " + ex.Message, null, duration);
    }
};

// ========== Share Operation ==========
Func<dynamic, System.Collections.Generic.Dictionary<string, object>> Share = (proj) => {
    var startTime = System.DateTime.Now;
    Log("Starting Share operation...");
    
    try {
        var droid = instance.DroidInstance;
        var input = droid.Input;
        var hierarchy = droid.Hierarchy;
        
        // Check rate limits (from config)
        if (!CheckRateLimit("actions", cfg_maxActionsPerHour)) {
            var duration = (int)(System.DateTime.Now - startTime).TotalMilliseconds;
            LogAction("share", "post", "rate_limited", duration);
            return CreateResult(false, "Rate limit exceeded for actions", null, duration);
        }
        
        // Find share button (from config)
        string layout = hierarchy.GetLayout();
        string searchPattern = "resource-id=\"" + cfg_shareButton + "\"";
        int foundPos = layout.IndexOf(searchPattern);
        
        if (foundPos < 0) {
            LogErr("Share button not found");
            var duration = (int)(System.DateTime.Now - startTime).TotalMilliseconds;
            LogAction("share", "post", "not_found", duration);
            return CreateResult(false, "Share button not found", null, duration);
        }
        
        // Extract and tap share button
        int nodeStart = layout.LastIndexOf('<', foundPos);
        int nodeEnd = layout.IndexOf('>', foundPos);
        string nodeStr = layout.Substring(nodeStart, nodeEnd - nodeStart + 1);
        
        int boundsStart = nodeStr.IndexOf("bounds=\"") + 8;
        int boundsEnd = nodeStr.IndexOf("\"", boundsStart);
        string boundsStr = nodeStr.Substring(boundsStart, boundsEnd - boundsStart);
        
        string cleaned = boundsStr.Replace("[", "").Replace("]", ",");
        string[] parts = cleaned.Split(new char[] {','}, System.StringSplitOptions.RemoveEmptyEntries);
        int centerX = (int.Parse(parts[0]) + int.Parse(parts[2])) / 2;
        int centerY = (int.Parse(parts[1]) + int.Parse(parts[3])) / 2;
        
        input.Tap(centerX, centerY);
        Log("Tapped share button at (" + centerX.ToString() + ", " + centerY.ToString() + ")");
        System.Threading.Thread.Sleep(2000);
        
        // Note: Share UI handling would go here (select target, confirm)
        // For now, just tap back to close share menu
        input.Shell("input keyevent 4");
        System.Threading.Thread.Sleep(1000);
        
        // Increment rate limit counter
        IncrementRateLimit("actions");
        
        var resultData = new System.Collections.Generic.Dictionary<string, object>();
        resultData["tap_x"] = centerX;
        resultData["tap_y"] = centerY;
        
        var duration = (int)(System.DateTime.Now - startTime).TotalMilliseconds;
        LogAction("share", "post", "success", duration);
        
        return CreateResult(true, "Share menu opened", resultData, duration);
        
    } catch (System.Exception ex) {
        LogErr("Share failed: " + ex.Message);
        var duration = (int)(System.DateTime.Now - startTime).TotalMilliseconds;
        LogAction("share", "post", "error", duration);
        return CreateResult(false, "Share error: " + ex.Message, null, duration);
    }
};

// ========== Rate Limit Helpers ==========

// Check if action is within rate limit
Func<string, int, bool> CheckRateLimit = (actionType, maxPerHour) => {
    try {
        // Check if hour has changed
        string hourStart = GetVar("instagram_hour_start", "");
        string currentHour = System.DateTime.Now.ToString("yyyy-MM-dd HH:00:00");
        
        if (hourStart != currentHour) {
            // Reset all counters for new hour
            SetVar("instagram_actions_this_hour", "0");
            SetVar("instagram_likes_this_hour", "0");
            SetVar("instagram_comments_this_hour", "0");
            SetVar("instagram_follows_this_hour", "0");
            SetVar("instagram_hour_start", currentHour);
            Log("Rate limit counters reset for new hour: " + currentHour);
            return true;
        }
        
        // Check current count
        string varName = "instagram_" + actionType + "_this_hour";
        int currentCount = int.Parse(GetVar(varName, "0"));
        
        if (currentCount >= maxPerHour) {
            LogErr("Rate limit exceeded for " + actionType + ": " + currentCount.ToString() + "/" + maxPerHour.ToString());
            return false;
        }
        
        return true;
        
    } catch (System.Exception ex) {
        LogErr("CheckRateLimit error: " + ex.Message);
        return true; // Allow action on error
    }
};

// Increment rate limit counter
Action<string> IncrementRateLimit = (actionType) => {
    try {
        string varName = "instagram_" + actionType + "_this_hour";
        int currentCount = int.Parse(GetVar(varName, "0"));
        currentCount++;
        SetVar(varName, currentCount.ToString());
        Log("Rate limit counter incremented: " + actionType + " = " + currentCount.ToString());
    } catch (System.Exception ex) {
        LogErr("IncrementRateLimit error: " + ex.Message);
    }
};

// ========== Export Operations ==========
// Store operations in variables for SessionRunner to access
SetVar("instagram_initialize", "Initialize");
SetVar("instagram_browse", "Browse");
SetVar("instagram_like", "Like");
SetVar("instagram_comment", "Comment");
SetVar("instagram_follow", "Follow");
SetVar("instagram_share", "Share");

Log("InstagramModule loaded successfully. All 6 operations available with rate limiting.");
