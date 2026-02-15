// =====================================================
// PlatformBase.cs - Platform Module Interface Definitions
// =====================================================
// Purpose: Defines the "interface" for platform modules using Func/Action delegates
//          Since ZennoDroid C# 5.0 doesn't support interfaces/classes, we use
//          delegate signatures as contracts that each platform must implement
//
// Platform Contract:
//   Each platform module (Reddit, Instagram, TikTok, Facebook) must implement:
//   - Initialize: Set up platform, verify app state
//   - Browse: Scroll feed, detect content
//   - Like: Find and tap like button
//   - Comment: Open comment UI, type text, submit
//   - Follow: Find and tap follow button
//   - Share: Open share UI, select target
//
// Usage Pattern:
//   1. Platform module assigns implementations to these delegates
//   2. SessionRunner calls platform operations through delegates
//   3. All operations return standardized result dictionaries
//   4. All operations log actions in standard format
//
// Result Format:
//   Dictionary with keys: "success" (true/false), "message", "data", "duration_ms"
//
// Action Log Format:
//   timestamp|platform|action|target|result|duration_ms
//   2026-02-07 18:00:00|reddit|like|post_abc123|success|1234
//
// Dependencies:
//   - project.Variables (for logging)
//   - System.Collections.Generic.Dictionary (for results)
//   - System.DateTime (for timestamps)
// =====================================================

// Log, LogErr, GetVar, SetVar defined in ScriptHelpers.cs

// ========== Result Helpers ==========

// Build standardized result dictionary
Func<bool, string, string, int, System.Collections.Generic.Dictionary<string, string>> BuildResult = (success, message, data, durationMs) => {
    var result = new System.Collections.Generic.Dictionary<string, string>();
    result.Add("success", success.ToString());
    result.Add("message", message);
    result.Add("data", data);
    result.Add("duration_ms", durationMs.ToString());
    return result;
};

// Check if result indicates success
Func<System.Collections.Generic.Dictionary<string, string>, bool> IsSuccess = (result) => {
    if (result.ContainsKey("success")) {
        return result["success"] == "True";
    }
    return false;
};

// Get message from result
Func<System.Collections.Generic.Dictionary<string, string>, string> ResultMessage = (result) => {
    if (result.ContainsKey("message")) {
        return result["message"];
    }
    return "";
};

// Get data from result
Func<System.Collections.Generic.Dictionary<string, string>, string> ResultData = (result) => {
    if (result.ContainsKey("data")) {
        return result["data"];
    }
    return "";
};

// ========== Action Logging ==========

// Log platform action in standard format
Action<string, string, string, string, int> LogAction = (platform, action, target, result, durationMs) => {
    string timestamp = System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
    string logLine = timestamp + "|" + platform + "|" + action + "|" + target + "|" + result + "|" + durationMs.ToString();
    project.SendInfoToLog("[Action] " + logLine, true);
    
    // Also store in variables for session tracking
    string actionLog = GetVar("action_log", "");
    actionLog = actionLog + logLine + "\n";
    SetVar("action_log", actionLog);
};

// ========== Platform Operation Signatures ==========

// Platform Initialize
// Signature: Func<string, System.Collections.Generic.Dictionary<string, string>>
// Input: platformName (e.g., "reddit", "instagram")
// Output: Result dictionary with success/failure
// Example implementation:
//   Func<string, System.Collections.Generic.Dictionary<string, string>> Initialize = (platformName) => {
//       var startTime = System.DateTime.Now;
//       try {
//           instance.DroidInstance.App.Open("com.reddit.frontpage");
//           System.Threading.Thread.Sleep(3000);
//           var duration = (int)(System.DateTime.Now - startTime).TotalMilliseconds;
//           LogAction(platformName, "initialize", "app", "success", duration);
//           return BuildResult(true, "Platform initialized", "", duration);
//       } catch (System.Exception ex) {
//           var duration = (int)(System.DateTime.Now - startTime).TotalMilliseconds;
//           LogAction(platformName, "initialize", "app", "failure", duration);
//           return BuildResult(false, "Failed: " + ex.Message, "", duration);
//       }
//   };

// Platform Browse
// Signature: Func<int, System.Collections.Generic.Dictionary<string, string>>
// Input: scrollCount (number of times to scroll)
// Output: Result dictionary with items browsed count in data field
// Example implementation:
//   Func<int, System.Collections.Generic.Dictionary<string, string>> Browse = (scrollCount) => {
//       var startTime = System.DateTime.Now;
//       int itemsFound = 0;
//       // ... browse logic ...
//       var duration = (int)(System.DateTime.Now - startTime).TotalMilliseconds;
//       LogAction("reddit", "browse", "feed", "success", duration);
//       return BuildResult(true, "Browsed " + itemsFound.ToString() + " items", itemsFound.ToString(), duration);
//   };

// Platform Like
// Signature: Func<string, System.Collections.Generic.Dictionary<string, string>>
// Input: targetId (post/content identifier)
// Output: Result dictionary with success/failure
// Example implementation:
//   Func<string, System.Collections.Generic.Dictionary<string, string>> Like = (targetId) => {
//       var startTime = System.DateTime.Now;
//       // ... like logic ...
//       var duration = (int)(System.DateTime.Now - startTime).TotalMilliseconds;
//       LogAction("reddit", "like", targetId, "success", duration);
//       return BuildResult(true, "Liked " + targetId, "", duration);
//   };

// Platform Comment
// Signature: Func<string, string, System.Collections.Generic.Dictionary<string, string>>
// Input: targetId (post identifier), commentText (text to post)
// Output: Result dictionary with success/failure
// Example implementation:
//   Func<string, string, System.Collections.Generic.Dictionary<string, string>> Comment = (targetId, commentText) => {
//       var startTime = System.DateTime.Now;
//       // ... comment logic ...
//       var duration = (int)(System.DateTime.Now - startTime).TotalMilliseconds;
//       LogAction("reddit", "comment", targetId, "success", duration);
//       return BuildResult(true, "Commented on " + targetId, "", duration);
//   };

// Platform Follow
// Signature: Func<string, System.Collections.Generic.Dictionary<string, string>>
// Input: targetId (user/account identifier)
// Output: Result dictionary with success/failure
// Example implementation:
//   Func<string, System.Collections.Generic.Dictionary<string, string>> Follow = (targetId) => {
//       var startTime = System.DateTime.Now;
//       // ... follow logic ...
//       var duration = (int)(System.DateTime.Now - startTime).TotalMilliseconds;
//       LogAction("reddit", "follow", targetId, "success", duration);
//       return BuildResult(true, "Followed " + targetId, "", duration);
//   };

// Platform Share
// Signature: Func<string, string, System.Collections.Generic.Dictionary<string, string>>
// Input: targetId (content identifier), shareTarget (where to share)
// Output: Result dictionary with success/failure
// Example implementation:
//   Func<string, string, System.Collections.Generic.Dictionary<string, string>> Share = (targetId, shareTarget) => {
//       var startTime = System.DateTime.Now;
//       // ... share logic ...
//       var duration = (int)(System.DateTime.Now - startTime).TotalMilliseconds;
//       LogAction("reddit", "share", targetId, "success", duration);
//       return BuildResult(true, "Shared " + targetId, "", duration);
//   };

// ========== Initialization Pattern ==========
// Each platform module should:
// 1. Define implementations for all 6 operations
// 2. Use BuildResult to return standardized results
// 3. Use LogAction to log all operations
// 4. Handle errors with try/catch and return failure results
// 5. Track duration for all operations
