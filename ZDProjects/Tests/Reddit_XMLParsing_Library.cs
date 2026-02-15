// =====================================================
// Reddit_XMLParsing_Library.cs
// 可复用的 XML 解析函数库（复制到其他脚本中使用）
// =====================================================
// 
// 使用说明：
// 1. 将此文件中的函数复制到你的 Own Code 脚本中
// 2. 调用相应函数解析 Reddit UI 元素
// 3. 所有函数都是 Func<>/Action<> 委托，符合 ZennoDroid 限制
//
// =====================================================

// ========== 基础 XML 解析函数 ==========

// 解析 bounds 属性 "[x1,y1][x2,y2]" 返回 [x1, y1, x2, y2]
Func<string, int[]> ParseBounds = (boundsStr) => {
    try {
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
        throw new System.Exception("ParseBounds failed: " + ex.Message);
    }
};

// 从 bounds 数组计算中心点
Func<int[], System.Tuple<int, int>> GetCenter = (bounds) => {
    int centerX = (bounds[0] + bounds[2]) / 2;
    int centerY = (bounds[1] + bounds[3]) / 2;
    return new System.Tuple<int, int>(centerX, centerY);
};

// 根据 resource-id 查找所有元素
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

// 获取元素的 bounds 属性值
Func<System.Xml.Linq.XElement, string> GetBounds = (element) => {
    var attr = element.Attribute("bounds");
    if (attr == null) {
        throw new System.Exception("Element has no bounds attribute");
    }
    return attr.Value;
};

// 获取元素的 resource-id 属性值
Func<System.Xml.Linq.XElement, string> GetResourceId = (element) => {
    var attr = element.Attribute("resource-id");
    return attr != null ? attr.Value : "";
};

// 获取元素的所有子元素
Func<System.Xml.Linq.XElement, System.Collections.Generic.List<System.Xml.Linq.XElement>> GetChildren = (element) => {
    var children = new System.Collections.Generic.List<System.Xml.Linq.XElement>();
    foreach (var child in element.Elements()) {
        children.Add(child);
    }
    return children;
};

// ========== Reddit 专用函数 ==========

// 查找所有帖子单元
Func<System.Xml.Linq.XDocument, System.Collections.Generic.List<System.Xml.Linq.XElement>> FindRedditPosts = (doc) => {
    return FindByResourceId(doc, "post_unit");
};

// 在帖子元素中查找 post_footer
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

// 从 post_footer 获取点赞按钮中心坐标（第一个子元素）
Func<System.Xml.Linq.XElement, System.Tuple<int, int>> GetUpvoteCenter = (postFooter) => {
    var children = GetChildren(postFooter);
    if (children.Count == 0) {
        throw new System.Exception("post_footer has no children");
    }
    
    var upvoteButton = children[0];
    string boundsStr = GetBounds(upvoteButton);
    int[] bounds = ParseBounds(boundsStr);
    return GetCenter(bounds);
};

// 从 post_footer 获取评论按钮中心坐标
Func<System.Xml.Linq.XElement, System.Tuple<int, int>> GetCommentCenter = (postFooter) => {
    foreach (var child in postFooter.Elements()) {
        var attr = child.Attribute("resource-id");
        if (attr != null && attr.Value == "post_comment_button") {
            string boundsStr = GetBounds(child);
            int[] bounds = ParseBounds(boundsStr);
            return GetCenter(bounds);
        }
    }
    throw new System.Exception("Comment button not found");
};

// 从 post_footer 获取分享按钮中心坐标
Func<System.Xml.Linq.XElement, System.Tuple<int, int>> GetShareCenter = (postFooter) => {
    foreach (var child in postFooter.Elements()) {
        var attr = child.Attribute("resource-id");
        if (attr != null && attr.Value == "post_share_button") {
            string boundsStr = GetBounds(child);
            int[] bounds = ParseBounds(boundsStr);
            return GetCenter(bounds);
        }
    }
    throw new System.Exception("Share button not found");
};

// 获取帖子中心坐标（用于点击打开帖子）
Func<System.Xml.Linq.XElement, System.Tuple<int, int>> GetPostCenter = (postElement) => {
    string boundsStr = GetBounds(postElement);
    int[] bounds = ParseBounds(boundsStr);
    return GetCenter(bounds);
};

// 获取帖子的 Y 坐标范围（用于判断是否在屏幕内）
Func<System.Xml.Linq.XElement, System.Tuple<int, int>> GetPostYRange = (postElement) => {
    string boundsStr = GetBounds(postElement);
    int[] bounds = ParseBounds(boundsStr);
    return new System.Tuple<int, int>(bounds[1], bounds[3]); // (top, bottom)
};

// 检查帖子是否在屏幕可见区域内
Func<System.Xml.Linq.XElement, int, int, bool> IsPostVisible = (postElement, screenTop, screenBottom) => {
    var yRange = GetPostYRange(postElement);
    int postTop = yRange.Item1;
    int postBottom = yRange.Item2;
    
    // 帖子至少有一部分在屏幕内
    return postBottom > screenTop && postTop < screenBottom;
};

// ========== 示例：完整的帖子信息提取 ==========

// 提取单个帖子的所有按钮坐标
Func<System.Xml.Linq.XElement, System.Collections.Generic.Dictionary<string, System.Tuple<int, int>>> ExtractPostButtons = (postElement) => {
    var buttons = new System.Collections.Generic.Dictionary<string, System.Tuple<int, int>>();
    
    var postFooter = FindPostFooter(postElement);
    if (postFooter == null) {
        return buttons; // 返回空字典
    }
    
    try {
        buttons["upvote"] = GetUpvoteCenter(postFooter);
    } catch { }
    
    try {
        buttons["comment"] = GetCommentCenter(postFooter);
    } catch { }
    
    try {
        buttons["share"] = GetShareCenter(postFooter);
    } catch { }
    
    try {
        buttons["post_center"] = GetPostCenter(postElement);
    } catch { }
    
    return buttons;
};
