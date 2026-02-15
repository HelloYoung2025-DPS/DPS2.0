// =====================================================
// ⚠️ [已废弃] UIHelper Test - Verify XML Parsing Works
// 废弃原因：UIHelper.cs 已废弃，改用 Core/ScriptHelpers.cs 纯字符串解析
// 参考：MultiPlatform_IntegrationTest.cs 为当前有效测试
// =====================================================

Action<string> Log = (m) => project.SendInfoToLog("[UIHelper Test] " + m, true);
Action<string> LogErr = (m) => project.SendErrorToLog("[UIHelper Test] " + m, true);

try {
    Log("Starting UIHelper test...");
    
    var droid = instance.DroidInstance;
    var hierarchy = droid.Hierarchy;
    var input = droid.Input;
    var app = droid.App;
    
    // Open Reddit
    Log("Opening Reddit...");
    app.Open("com.reddit.frontpage");
    System.Threading.Thread.Sleep(5000);
    
    // Get layout
    Log("Getting UI hierarchy...");
    string layout = hierarchy.GetLayout();
    Log("Layout length: " + layout.Length.ToString());
    
    // Test 1: Parse XML
    Log("Test 1: Parsing XML...");
    var doc = UIHelper.ParseLayout(layout);
    Log("✓ XML parsed successfully");
    
    // Test 2: Find all posts
    Log("Test 2: Finding all posts...");
    var posts = UIHelper.FindRedditPosts(doc);
    Log("Found " + posts.Count.ToString() + " posts");
    
    if (posts.Count == 0) {
        LogErr("No posts found!");
        return "ERROR: No posts found";
    }
    
    // Test 3: Process first post
    Log("Test 3: Processing first post...");
    var firstPost = posts[0];
    
    // Get post center
    var postCenter = UIHelper.GetPostCenter(firstPost);
    Log("Post center: (" + postCenter.Item1.ToString() + ", " + postCenter.Item2.ToString() + ")");
    
    // Find post_footer
    var postFooter = UIHelper.FindPostFooter(firstPost);
    if (postFooter == null) {
        LogErr("post_footer not found!");
        return "ERROR: post_footer not found";
    }
    Log("✓ post_footer found");
    
    // Test 4: Get button coordinates
    Log("Test 4: Getting button coordinates...");
    
    var upvoteCenter = UIHelper.GetUpvoteButtonCenter(postFooter);
    Log("Upvote button: (" + upvoteCenter.Item1.ToString() + ", " + upvoteCenter.Item2.ToString() + ")");
    
    var commentCenter = UIHelper.GetCommentButtonCenter(postFooter);
    Log("Comment button: (" + commentCenter.Item1.ToString() + ", " + commentCenter.Item2.ToString() + ")");
    
    var shareCenter = UIHelper.GetShareButtonCenter(postFooter);
    Log("Share button: (" + shareCenter.Item1.ToString() + ", " + shareCenter.Item2.ToString() + ")");
    
    // Test 5: Click upvote button using dynamic coordinates
    Log("Test 5: Clicking upvote button at dynamic position...");
    input.Tap(upvoteCenter.Item1, upvoteCenter.Item2);
    System.Threading.Thread.Sleep(2000);
    
    // Get new layout and check if it changed
    string newLayout = hierarchy.GetLayout();
    bool changed = newLayout != layout;
    Log("UI changed after upvote: " + changed.ToString());
    
    // Test 6: Process all visible posts
    Log("Test 6: Processing all visible posts...");
    for (int i = 0; i < posts.Count; i++) {
        var post = posts[i];
        var footer = UIHelper.FindPostFooter(post);
        
        if (footer != null) {
            var upvote = UIHelper.GetUpvoteButtonCenter(footer);
            Log("Post " + (i + 1).ToString() + " upvote button: (" + upvote.Item1.ToString() + ", " + upvote.Item2.ToString() + ")");
        } else {
            Log("Post " + (i + 1).ToString() + " has no footer");
        }
    }
    
    Log("=== All Tests Passed ===");
    return "SUCCESS";
}
catch (System.Exception ex) {
    LogErr("Exception: " + ex.Message);
    LogErr("Stack trace: " + ex.StackTrace);
    return "ERROR: " + ex.Message;
}
