// =====================================================
// Reddit Dynamic Click Test - ZennoDroid Own Code
// 使用动态坐标提取点击点赞按钮
// =====================================================

// ========== Helper Functions ==========
Action<string> Log = (m) => project.SendInfoToLog("[Dynamic Test] " + m, true);
Action<string> LogErr = (m) => project.SendErrorToLog("[Dynamic Test] " + m, true);

// ========== XML Parsing Functions ==========

// Parse bounds "[x1,y1][x2,y2]" to int array [x1, y1, x2, y2]
Func<string, int[]> ParseBounds = (boundsStr) => {
    try {
        // "[42,1676][306,1765]" -> "42,1676,306,1765"
        boundsStr = boundsStr.Replace("[", "").Replace("]", ",");
        string[] parts = boundsStr.Split(new char[] {','}, System.StringSplitOptions.RemoveEmptyEntries);
        
        if (parts.Length != 4) {
            throw new System.Exception("Invalid bounds format");
        }
        
        return new int[] {
            int.Parse(parts[0]),
            int.Parse(parts[1]),
            int.Parse(parts[2]),
            int.Parse(parts[3])
        };
    }
    catch (System.Exception ex) {
        throw new System.Exception("ParseBounds failed: " + ex.Message);
    }
};

// Calculate center point from bounds array
Func<int[], System.Tuple<int, int>> GetCenter = (bounds) => {
    int centerX = (bounds[0] + bounds[2]) / 2;
    int centerY = (bounds[1] + bounds[3]) / 2;
    return new System.Tuple<int, int>(centerX, centerY);
};

// Find all elements with specific resource-id
Func<System.Xml.Linq.XDocument, string, System.Collections.Generic.List<System.Xml.Linq.XElement>> FindByResourceId = null;
FindByResourceId = (doc, resourceId) => {
    var results = new System.Collections.Generic.List<System.Xml.Linq.XElement>();
    
    foreach (var element in doc.Descendants()) {
        var attr = element.Attribute("resource-id");
        if (attr != null && attr.Value == resourceId) {
            results.Add(element);
        }
    }
    
    return results;
};

// Find post_footer within a post element
Func<System.Xml.Linq.XElement, System.Xml.Linq.XElement> FindPostFooter = null;
FindPostFooter = (postElement) => {
    foreach (var descendant in postElement.Descendants()) {
        var attr = descendant.Attribute("resource-id");
        if (attr != null && attr.Value == "post_footer") {
            return descendant;
        }
    }
    return null;
};

// Get upvote button center from post_footer (first child)
Func<System.Xml.Linq.XElement, System.Tuple<int, int>> GetUpvoteCenter = (postFooter) => {
    var children = new System.Collections.Generic.List<System.Xml.Linq.XElement>();
    foreach (var child in postFooter.Elements()) {
        children.Add(child);
    }
    
    if (children.Count == 0) {
        throw new System.Exception("post_footer has no children");
    }
    
    // First child is upvote button
    var upvoteButton = children[0];
    var boundsAttr = upvoteButton.Attribute("bounds");
    if (boundsAttr == null) {
        throw new System.Exception("Upvote button has no bounds");
    }
    
    int[] bounds = ParseBounds(boundsAttr.Value);
    return GetCenter(bounds);
};

// ========== Main Logic ==========
try {
    Log("Starting dynamic click test...");
    
    var droid = instance.DroidInstance;
    var input = droid.Input;
    var app = droid.App;
    var hierarchy = droid.Hierarchy;
    
    // Open Reddit
    Log("Opening Reddit app...");
    app.Open("com.reddit.frontpage");
    System.Threading.Thread.Sleep(5000);
    
    // Get UI hierarchy
    Log("Getting UI hierarchy...");
    string layout = hierarchy.GetLayout();
    Log("Layout length: " + layout.Length.ToString());
    
    // Parse XML
    Log("Parsing XML...");
    var doc = System.Xml.Linq.XDocument.Parse(layout);
    Log("✓ XML parsed successfully");
    
    // Find all posts
    Log("Finding all posts...");
    var posts = FindByResourceId(doc, "post_unit");
    Log("Found " + posts.Count.ToString() + " posts");
    
    if (posts.Count == 0) {
        LogErr("No posts found!");
        return "ERROR: No posts found";
    }
    
    // Process all visible posts
    Log("=== Processing All Visible Posts ===");
    int successCount = 0;
    
    for (int i = 0; i < posts.Count; i++) {
        Log("--- Post " + (i + 1).ToString() + " ---");
        
        var post = posts[i];
        var postFooter = FindPostFooter(post);
        
        if (postFooter == null) {
            Log("Post " + (i + 1).ToString() + " has no footer, skipping");
            continue;
        }
        
        try {
            var upvoteCenter = GetUpvoteCenter(postFooter);
            Log("Upvote button at: (" + upvoteCenter.Item1.ToString() + ", " + upvoteCenter.Item2.ToString() + ")");
            successCount++;
        }
        catch (System.Exception ex) {
            Log("Failed to get upvote button: " + ex.Message);
        }
    }
    
    Log("Successfully located " + successCount.ToString() + " upvote buttons");
    
    // Test clicking first post's upvote button
    if (successCount > 0) {
        Log("=== Testing Click on First Post ===");
        var firstPost = posts[0];
        var firstFooter = FindPostFooter(firstPost);
        var firstUpvote = GetUpvoteCenter(firstFooter);
        
        Log("Clicking upvote at (" + firstUpvote.Item1.ToString() + ", " + firstUpvote.Item2.ToString() + ")...");
        input.Tap(firstUpvote.Item1, firstUpvote.Item2);
        System.Threading.Thread.Sleep(2000);
        
        // Check if UI changed
        string newLayout = hierarchy.GetLayout();
        bool changed = (newLayout != layout);
        Log("UI changed after click: " + changed.ToString());
        
        if (changed) {
            Log("✓ Click successful - UI changed");
        } else {
            Log("⚠ Click may have failed - UI unchanged");
        }
    }
    
    Log("=== Test Complete ===");
    return "SUCCESS: Located " + successCount.ToString() + " upvote buttons";
}
catch (System.Exception ex) {
    LogErr("Exception: " + ex.Message);
    LogErr("Stack trace: " + ex.StackTrace);
    return "ERROR: " + ex.Message;
}
