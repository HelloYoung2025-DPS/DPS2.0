// =====================================================
// ⚠️ [已废弃] UIHelper.cs - Android UI Hierarchy XML Parser
// 废弃原因：依赖 System.Xml.Linq，在 ZennoDroid 脚本环境中不可靠
// 替代方案：Core/ScriptHelpers.cs（纯字符串解析，由 ModuleLoader 自动注入）
// 对应函数映射：
//   UIHelper.ParseLayout()        → 不再需要（直接操作 XML 字符串）
//   UIHelper.FindByResourceId()   → FindBoundsByResourceId() / FindNodesByResourceId()
//   UIHelper.ParseBounds()        → ParseBounds()
//   UIHelper.GetCenter()          → GetCenter()
//   UIHelper.GetBounds()          → ExtractBounds()
//   UIHelper.FindRedditPosts()    → FindBoundsByResourceId(xml, "post_unit")
//   UIHelper.FindPostFooter()     → FindNodesByResourceId(xml, "post_footer")
//   UIHelper.GetUpvoteButtonCenter() → FindUpvoteBoundsForPost()（Reddit_Like.cs）
//   UIHelper.GetCommentButtonCenter() → FindBoundsByResourceId(xml, "post_comment_button")
// 计划：后续版本移除此文件
// =====================================================

public static class UIHelper
{
    // ========== XML Parsing ==========
    
    /// <summary>
    /// Parse Android UI hierarchy XML string
    /// </summary>
    public static System.Xml.Linq.XDocument ParseLayout(string xmlString)
    {
        try {
            return System.Xml.Linq.XDocument.Parse(xmlString);
        }
        catch (System.Exception ex) {
            throw new System.Exception("Failed to parse XML: " + ex.Message);
        }
    }
    
    /// <summary>
    /// Find all elements with specific resource-id
    /// </summary>
    public static System.Collections.Generic.List<System.Xml.Linq.XElement> FindByResourceId(
        System.Xml.Linq.XDocument doc, 
        string resourceId)
    {
        var results = new System.Collections.Generic.List<System.Xml.Linq.XElement>();
        
        foreach (var element in doc.Descendants())
        {
            var attr = element.Attribute("resource-id");
            if (attr != null && attr.Value == resourceId)
            {
                results.Add(element);
            }
        }
        
        return results;
    }
    
    /// <summary>
    /// Parse bounds attribute "[x1,y1][x2,y2]" to coordinates
    /// Returns: [x1, y1, x2, y2]
    /// </summary>
    public static int[] ParseBounds(string boundsStr)
    {
        try {
            // Remove brackets and split
            // "[42,1676][306,1765]" -> "42,1676" and "306,1765"
            boundsStr = boundsStr.Replace("[", "").Replace("]", ",");
            string[] parts = boundsStr.Split(new char[] {','}, System.StringSplitOptions.RemoveEmptyEntries);
            
            if (parts.Length != 4) {
                throw new System.Exception("Invalid bounds format: " + boundsStr);
            }
            
            return new int[] {
                int.Parse(parts[0]),
                int.Parse(parts[1]),
                int.Parse(parts[2]),
                int.Parse(parts[3])
            };
        }
        catch (System.Exception ex) {
            throw new System.Exception("Failed to parse bounds '" + boundsStr + "': " + ex.Message);
        }
    }
    
    /// <summary>
    /// Calculate center point from bounds
    /// </summary>
    public static System.Tuple<int, int> GetCenter(int[] bounds)
    {
        if (bounds.Length != 4) {
            throw new System.Exception("Bounds array must have 4 elements");
        }
        
        int centerX = (bounds[0] + bounds[2]) / 2;
        int centerY = (bounds[1] + bounds[3]) / 2;
        
        return new System.Tuple<int, int>(centerX, centerY);
    }
    
    /// <summary>
    /// Get bounds attribute from element
    /// </summary>
    public static string GetBounds(System.Xml.Linq.XElement element)
    {
        var attr = element.Attribute("bounds");
        if (attr == null) {
            throw new System.Exception("Element has no bounds attribute");
        }
        return attr.Value;
    }
    
    /// <summary>
    /// Get resource-id attribute from element
    /// </summary>
    public static string GetResourceId(System.Xml.Linq.XElement element)
    {
        var attr = element.Attribute("resource-id");
        return attr != null ? attr.Value : "";
    }
    
    /// <summary>
    /// Get all child elements of an element
    /// </summary>
    public static System.Collections.Generic.List<System.Xml.Linq.XElement> GetChildren(
        System.Xml.Linq.XElement element)
    {
        var children = new System.Collections.Generic.List<System.Xml.Linq.XElement>();
        foreach (var child in element.Elements())
        {
            children.Add(child);
        }
        return children;
    }
    
    // ========== Reddit-Specific Helpers ==========
    
    /// <summary>
    /// Find all post units in Reddit feed
    /// </summary>
    public static System.Collections.Generic.List<System.Xml.Linq.XElement> FindRedditPosts(
        System.Xml.Linq.XDocument doc)
    {
        return FindByResourceId(doc, "post_unit");
    }
    
    /// <summary>
    /// Find post_footer element within a post
    /// </summary>
    public static System.Xml.Linq.XElement FindPostFooter(System.Xml.Linq.XElement postElement)
    {
        foreach (var descendant in postElement.Descendants())
        {
            var attr = descendant.Attribute("resource-id");
            if (attr != null && attr.Value == "post_footer")
            {
                return descendant;
            }
        }
        return null;
    }
    
    /// <summary>
    /// Get upvote button coordinates from post_footer
    /// Upvote button is the FIRST child of post_footer (no resource-id)
    /// </summary>
    public static System.Tuple<int, int> GetUpvoteButtonCenter(System.Xml.Linq.XElement postFooter)
    {
        var children = GetChildren(postFooter);
        if (children.Count == 0) {
            throw new System.Exception("post_footer has no children");
        }
        
        // Try to find element with content-desc containing "upvote" or "vote"
        foreach (var child in children) {
            var contentDesc = child.Attribute("content-desc");
            if (contentDesc != null) {
                string desc = contentDesc.Value.ToLower();
                if (desc.Contains("upvote") || desc.Contains("vote")) {
                    string boundsStr = GetBounds(child);
                    int[] bounds = ParseBounds(boundsStr);
                    return GetCenter(bounds);
                }
            }
        }
        
        // Fallback to first child (backward compatibility)
        var upvoteButton = children[0];
        string fallbackBoundsStr = GetBounds(upvoteButton);
        int[] fallbackBounds = ParseBounds(fallbackBoundsStr);
        return GetCenter(fallbackBounds);
    }
    
    /// <summary>
    /// Get comment button coordinates from post_footer
    /// </summary>
    public static System.Tuple<int, int> GetCommentButtonCenter(System.Xml.Linq.XElement postFooter)
    {
        foreach (var child in postFooter.Elements())
        {
            var attr = child.Attribute("resource-id");
            if (attr != null && attr.Value == "post_comment_button")
            {
                string boundsStr = GetBounds(child);
                int[] bounds = ParseBounds(boundsStr);
                return GetCenter(bounds);
            }
        }
        throw new System.Exception("Comment button not found in post_footer");
    }
    
    /// <summary>
    /// Get share button coordinates from post_footer
    /// </summary>
    public static System.Tuple<int, int> GetShareButtonCenter(System.Xml.Linq.XElement postFooter)
    {
        foreach (var child in postFooter.Elements())
        {
            var attr = child.Attribute("resource-id");
            if (attr != null && attr.Value == "post_share_button")
            {
                string boundsStr = GetBounds(child);
                int[] bounds = ParseBounds(boundsStr);
                return GetCenter(bounds);
            }
        }
        throw new System.Exception("Share button not found in post_footer");
    }
    
    /// <summary>
    /// Get post center coordinates (for clicking to open post)
    /// </summary>
    public static System.Tuple<int, int> GetPostCenter(System.Xml.Linq.XElement postElement)
    {
        string boundsStr = GetBounds(postElement);
        int[] bounds = ParseBounds(boundsStr);
        return GetCenter(bounds);
    }
}
