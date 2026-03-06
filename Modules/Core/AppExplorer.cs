// =====================================================
// AppExplorer.cs - APP 自动探索器
// ⚠️ C# 5.0 语法 - 禁止使用 $""、?.、nameof() 等
// =====================================================
//
// 探索原理：
//   1. 启动目标 APP
//   2. 获取当前屏幕 UI hierarchy XML
//   3. 提取屏幕签名（低频 resource-id 元素，这些通常是页面特征）
//   4. 查找可点击元素（clickable="true"）
//   5. 优先点击导航元素（nav/tab/bottom/menu 关键字）
//   6. 等待页面加载后重复上述步骤
//   7. 限制探索深度（默认 2 层）
//
// 输出：
//   - 生成 manifest 草稿 JSON 文件
//   - 保存到 Configs/Manifests/{package}_draft.json
//   - 包含发现的屏幕、元素签名、可操作按钮
//   - 标记需要人工补充的部分
// =====================================================
using System;
using System.Collections.Generic;
using System.Text;

/// <summary>
/// APP 自动探索器
/// 自动探索 APP 结构并生成 manifest 草稿
/// </summary>
public class AppExplorer
{
    private const string TAG = "AppExplorer";
    
    // 默认探索深度（1-2 层）
    private const int DEFAULT_MAX_DEPTH = 2;
    
    // 页面加载等待时间（毫秒）
    private const int PAGE_LOAD_WAIT = 2000;
    
    // 点击后等待时间（毫秒）
    private const int CLICK_WAIT = 1500;
    
    // 敏感操作关键字（避免触发）
    private static readonly string[] SENSITIVE_KEYWORDS = new string[]
    {
        "delete", "remove", "uninstall", "logout", "signout",
        "post", "publish", "share", "upload", "submit",
        "purchase", "buy", "pay", "checkout"
    };
    
    // 导航元素关键字（优先点击）
    private static readonly string[] NAV_KEYWORDS = new string[]
    {
        "nav", "tab", "bottom", "menu", "home", "feed",
        "profile", "search", "discover", "explore"
    };
    
    // ========== 内部数据结构 ==========
    
    /// <summary>
    /// 屏幕信息
    /// </summary>
    private class ScreenInfo
    {
        public string Id;              // 屏幕唯一标识
        public string Name;            // 屏幕名称（推测）
        public List<string> Signatures; // 屏幕签名（低频 resource-id）
        public List<ElementInfo> Clickables; // 可点击元素
        public int Depth;              // 探索深度
        public string ParentId;        // 父屏幕 ID
        
        public ScreenInfo()
        {
            Id = "";
            Name = "";
            Signatures = new List<string>();
            Clickables = new List<ElementInfo>();
            Depth = 0;
            ParentId = "";
        }
    }
    
    /// <summary>
    /// 元素信息
    /// </summary>
    private class ElementInfo
    {
        public string ResourceId;      // resource-id
        public string Text;            // text 内容
        public string ContentDesc;     // content-desc
        public string Bounds;          // bounds 坐标
        public bool IsNavigation;      // 是否为导航元素
        
        public ElementInfo()
        {
            ResourceId = "";
            Text = "";
            ContentDesc = "";
            Bounds = "";
            IsNavigation = false;
        }
    }
    
    // ========== 探索状态 ==========
    
    private static List<ScreenInfo> _discoveredScreens;
    private static HashSet<string> _visitedScreenHashes;
    private static string _currentPackage;
    private static string _currentAppId;
    
    // ========== 公共方法 ==========
    
    /// <summary>
    /// 探索指定 APP
    /// </summary>
    /// <param name="packageName">APP 包名（如 com.reddit.frontpage）</param>
    /// <param name="maxDepth">最大探索深度（默认 2）</param>
    /// <returns>探索结果 JSON 字符串，失败时返回以 "ERROR:" 开头的错误信息</returns>
    public static string Explore(string packageName, int maxDepth)
    {
        return Explore(packageName, maxDepth, "");
    }
    
    /// <summary>
    /// 探索指定 APP（带 appId）
    /// </summary>
    /// <param name="packageName">APP 包名</param>
    /// <param name="maxDepth">最大探索深度</param>
    /// <param name="appId">应用标识符（可选，如 reddit）</param>
    /// <returns>探索结果 JSON 字符串</returns>
    public static string Explore(string packageName, int maxDepth, string appId)
    {
        try
        {
            // 参数验证
            if (string.IsNullOrEmpty(packageName))
            {
                CoreHelper.LogErr(TAG, "packageName 参数为空");
                return "ERROR: packageName cannot be empty";
            }
            
            if (maxDepth < 1) maxDepth = DEFAULT_MAX_DEPTH;
            if (maxDepth > 3) maxDepth = 3; // 硬限制最多 3 层
            
            // 初始化状态
            _discoveredScreens = new List<ScreenInfo>();
            _visitedScreenHashes = new HashSet<string>();
            _currentPackage = packageName;
            _currentAppId = string.IsNullOrEmpty(appId) ? ExtractAppId(packageName) : appId;
            
            CoreHelper.Log(TAG, "开始探索 APP: " + packageName);
            CoreHelper.Log(TAG, "最大深度: " + maxDepth.ToString());
            
            // 获取 DroidInstance
            dynamic droid = CoreHelper.GetDroid();
            if (droid == null)
            {
                CoreHelper.LogErr(TAG, "DroidInstance 未初始化");
                return "ERROR: DroidInstance not initialized";
            }
            
            // 启动 APP
            CoreHelper.Log(TAG, "正在启动 APP...");
            droid.App.Open(packageName);
            System.Threading.Thread.Sleep(PAGE_LOAD_WAIT);
            
            // 从主屏幕开始探索
            ExploreScreen("root", 0);
            
            // 生成 manifest
            string manifestJson = GenerateManifest();
            
            // 保存到文件
            SaveManifest(manifestJson);
            
            CoreHelper.Log(TAG, "探索完成，发现 " + _discoveredScreens.Count.ToString() + " 个屏幕");
            
            return manifestJson;
        }
        catch (Exception ex)
        {
            CoreHelper.LogErr(TAG, "探索异常: " + ex.Message);
            return "ERROR: Exception during exploration: " + ex.Message;
        }
    }
    
    // ========== 内部方法 ==========
    
    /// <summary>
    /// 探索单个屏幕
    /// </summary>
    /// <param name="screenId">屏幕 ID</param>
    /// <param name="depth">当前深度</param>
    private static void ExploreScreen(string screenId, int depth)
    {
        if (depth > DEFAULT_MAX_DEPTH)
        {
            CoreHelper.Log(TAG, "达到最大探索深度，停止");
            return;
        }
        
        // 获取当前 UI hierarchy
        string layoutXml = CoreHelper.GetLayout();
        if (string.IsNullOrEmpty(layoutXml))
        {
            CoreHelper.LogWarn(TAG, "无法获取 UI hierarchy");
            return;
        }
        
        // 计算屏幕哈希（用于去重）
        string screenHash = ComputeScreenHash(layoutXml);
        if (_visitedScreenHashes.Contains(screenHash))
        {
            CoreHelper.Log(TAG, "屏幕已访问过，跳过: " + screenId);
            return;
        }
        _visitedScreenHashes.Add(screenHash);
        
        // 创建屏幕信息
        ScreenInfo screen = new ScreenInfo();
        screen.Id = "screen_" + _discoveredScreens.Count.ToString();
        screen.Name = InferScreenName(layoutXml, depth);
        screen.Depth = depth;
        screen.ParentId = (depth == 0) ? "" : screenId;
        
        // 提取屏幕签名
        screen.Signatures = ExtractSignatures(layoutXml);
        
        // 查找可点击元素
        screen.Clickables = FindClickableElements(layoutXml);
        
        _discoveredScreens.Add(screen);
        
        CoreHelper.Log(TAG, string.Format("发现屏幕 [{0}]: {1}, 签名数: {2}, 可点击数: {3}",
            screen.Id, screen.Name, screen.Signatures.Count, screen.Clickables.Count));
        
        // 递归探索：点击导航元素
        if (depth < DEFAULT_MAX_DEPTH)
        {
            ExploreNavigationElements(screen, depth);
        }
    }
    
    /// <summary>
    /// 提取屏幕签名（低频 resource-id 元素）
    /// </summary>
    /// <param name="layoutXml">UI hierarchy XML</param>
    /// <returns>签名列表（resource-id 列表）</returns>
    private static List<string> ExtractSignatures(string layoutXml)
    {
        var signatures = new List<string>();
        if (string.IsNullOrEmpty(layoutXml)) return signatures;
        
        // 统计每个 resource-id 出现的次数
        var resourceIdCounts = new Dictionary<string, int>();
        
        // 查找所有 resource-id 属性
        // 格式：resource-id="com.app:id/xxx"
        int searchPos = 0;
        while (true)
        {
            int idx = layoutXml.IndexOf("resource-id=\"", searchPos);
            if (idx < 0) break;
            
            int start = idx + "resource-id=\"".Length;
            int end = layoutXml.IndexOf("\"", start);
            if (end <= start) break;
            
            string resourceId = layoutXml.Substring(start, end - start);
            
            // 只统计包名内的 resource-id（过滤系统资源）
            if (!resourceId.StartsWith("android:") && 
                resourceId.Contains(":id/"))
            {
                if (!resourceIdCounts.ContainsKey(resourceId))
                {
                    resourceIdCounts[resourceId] = 0;
                }
                resourceIdCounts[resourceId]++;
            }
            
            searchPos = end + 1;
        }
        
        // 计算平均出现次数
        int total = 0;
        int count = 0;
        foreach (var kvp in resourceIdCounts)
        {
            total += kvp.Value;
            count++;
        }
        
        if (count == 0) return signatures;
        
        double avg = (double)total / (double)count;
        
        // 选择出现次数低于平均值的 resource-id 作为签名
        // 这些元素通常更具页面特征性
        foreach (var kvp in resourceIdCounts)
        {
            if (kvp.Value < avg)
            {
                signatures.Add(kvp.Key);
            }
        }
        
        return signatures;
    }
    
    /// <summary>
    /// 查找可点击元素
    /// </summary>
    /// <param name="layoutXml">UI hierarchy XML</param>
    /// <returns>可点击元素列表</returns>
    private static List<ElementInfo> FindClickableElements(string layoutXml)
    {
        var elements = new List<ElementInfo>();
        if (string.IsNullOrEmpty(layoutXml)) return elements;
        
        // 解析 XML 中的 clickable 元素
        // 格式：<node clickable="true" resource-id="..." text="..." content-desc="..." bounds="[...]"/>
        
        int searchPos = 0;
        while (true)
        {
            // 查找 clickable="true"
            int clickableIdx = layoutXml.IndexOf("clickable=\"true\"", searchPos);
            if (clickableIdx < 0) break;
            
            // 向前找到节点开始
            int nodeStart = layoutXml.LastIndexOf("<node", clickableIdx);
            if (nodeStart < 0)
            {
                searchPos = clickableIdx + 1;
                continue;
            }
            
            // 向后找到节点结束
            int nodeEnd = layoutXml.IndexOf("/>", clickableIdx);
            if (nodeEnd < 0)
            {
                searchPos = clickableIdx + 1;
                continue;
            }
            
            // 提取节点内容
            string nodeContent = layoutXml.Substring(nodeStart, nodeEnd - nodeStart + 2);
            
            // 解析元素信息
            ElementInfo elem = ParseElement(nodeContent);
            if (!string.IsNullOrEmpty(elem.ResourceId) || 
                !string.IsNullOrEmpty(elem.Text))
            {
                elements.Add(elem);
            }
            
            searchPos = nodeEnd + 2;
        }
        
        return elements;
    }
    
    /// <summary>
    /// 探索导航元素（递归）
    /// </summary>
    /// <param name="screen">当前屏幕信息</param>
    /// <param name="depth">当前深度</param>
    private static void ExploreNavigationElements(ScreenInfo screen, int depth)
    {
        dynamic droid = CoreHelper.GetDroid();
        if (droid == null) return;
        
        int clickCount = 0;
        int maxClicks = 5; // 每个屏幕最多点击 5 个导航元素
        
        foreach (ElementInfo elem in screen.Clickables)
        {
            if (clickCount >= maxClicks) break;
            
            // 只点击导航元素
            if (!elem.IsNavigation) continue;
            
            // 检查敏感操作
            if (IsSensitiveOperation(elem))
            {
                CoreHelper.Log(TAG, "跳过敏感操作: " + elem.Text);
                continue;
            }
            
            // 点击元素
            CoreHelper.Log(TAG, string.Format("点击导航元素: {0} ({1})", 
                elem.Text, elem.ResourceId));
            
            if (TapElement(elem))
            {
                clickCount++;
                System.Threading.Thread.Sleep(CLICK_WAIT);
                
                // 探索新屏幕
                ExploreScreen(screen.Id, depth + 1);
                
                // 返回上一屏幕
                GoBack();
                System.Threading.Thread.Sleep(CLICK_WAIT);
            }
        }
    }
    
    /// <summary>
    /// 生成 manifest JSON
    /// </summary>
    /// <returns>manifest JSON 字符串</returns>
    private static string GenerateManifest()
    {
        var jsonBuilder = new StringBuilder();
        
        jsonBuilder.Append("{\n");
        jsonBuilder.Append("  \"_meta\": {\n");
        jsonBuilder.Append("    \"version\": \"4.5.0\",\n");
        jsonBuilder.Append("    \"generated_by\": \"AppExplorer\",\n");
        jsonBuilder.Append("    \"generated_at\": \"" + CoreHelper.GetNowISO() + "\",\n");
        jsonBuilder.Append("    \"status\": \"draft\",\n");
        jsonBuilder.Append("    \"notes\": \"This is an auto-generated draft. Please review and complete manually.\"\n");
        jsonBuilder.Append("  },\n");
        jsonBuilder.Append("  \"app_id\": \"" + _currentAppId + "\",\n");
        jsonBuilder.Append("  \"package\": \"" + _currentPackage + "\",\n");
        jsonBuilder.Append("  \"display_name\": \"" + InferDisplayName() + "\",\n");
        jsonBuilder.Append("  \n");
        jsonBuilder.Append("  // ========== SCREENS (Auto-discovered) ==========\n");
        jsonBuilder.Append("  \"screens\": [\n");
        
        for (int i = 0; i < _discoveredScreens.Count; i++)
        {
            ScreenInfo screen = _discoveredScreens[i];
            jsonBuilder.Append("    {\n");
            jsonBuilder.Append("      \"id\": \"" + screen.Id + "\",\n");
            jsonBuilder.Append("      \"name\": \"" + EscapeJson(screen.Name) + "\",\n");
            jsonBuilder.Append("      \"depth\": " + screen.Depth.ToString() + ",\n");
            jsonBuilder.Append("      \"parent_id\": \"" + EscapeJson(screen.ParentId) + "\",\n");
            jsonBuilder.Append("      \n");
            jsonBuilder.Append("      // Signatures: low-frequency resource-ids that identify this screen\n");
            jsonBuilder.Append("      \"signatures\": [\n");
            
            for (int j = 0; j < screen.Signatures.Count; j++)
            {
                jsonBuilder.Append("        \"" + EscapeJson(screen.Signatures[j]) + "\"");
                if (j < screen.Signatures.Count - 1)
                    jsonBuilder.Append(",");
                jsonBuilder.Append("\n");
            }
            
            jsonBuilder.Append("      ],\n");
            jsonBuilder.Append("      \n");
            jsonBuilder.Append("      // TODO: Add page_signatures for PageDetector\n");
            jsonBuilder.Append("      // Format: { \"threshold\": 0.5, \"signals\": [...] }\n");
            jsonBuilder.Append("      \"page_signatures\": null,\n");
            jsonBuilder.Append("      \n");
            jsonBuilder.Append("      // TODO: Add entry_actions (how to reach this screen)\n");
            jsonBuilder.Append("      \"entry_actions\": []\n");
            jsonBuilder.Append("    }");
            
            if (i < _discoveredScreens.Count - 1)
                jsonBuilder.Append(",");
            jsonBuilder.Append("\n");
        }
        
        jsonBuilder.Append("  ],\n");
        jsonBuilder.Append("  \n");
        jsonBuilder.Append("  // ========== SELECTORS (TODO: Complete manually) ==========\n");
        jsonBuilder.Append("  // Selector format: { \"strategy\": \"resource-id|text|xpath\", \"value\": \"...\" }\n");
        jsonBuilder.Append("  \"selectors\": {\n");
        jsonBuilder.Append("    \"TODO\": \"Add element selectors for key UI elements\"\n");
        jsonBuilder.Append("  },\n");
        jsonBuilder.Append("  \n");
        jsonBuilder.Append("  // ========== OPERATIONS (TODO: Complete manually) ==========\n");
        jsonBuilder.Append("  // Define operations available in this app\n");
        jsonBuilder.Append("  \"operations\": [\n");
        jsonBuilder.Append("    {\n");
        jsonBuilder.Append("      \"name\": \"browse_feed\",\n");
        jsonBuilder.Append("      \"display_name\": \"Browse Feed\",\n");
        jsonBuilder.Append("      \"screen\": \"TODO\",\n");
        jsonBuilder.Append("      \"actions\": []\n");
        jsonBuilder.Append("    }\n");
        jsonBuilder.Append("  ]\n");
        jsonBuilder.Append("}\n");
        
        return jsonBuilder.ToString();
    }
    
    // ========== 辅助方法 ==========
    
    /// <summary>
    /// 从包名提取 appId
    /// </summary>
    private static string ExtractAppId(string packageName)
    {
        if (string.IsNullOrEmpty(packageName)) return "unknown";
        
        // 移除常见前缀
        string result = packageName.Replace("com.", "")
                                   .Replace("android.", "")
                                   .Replace("app.", "");
        
        // 移除 .frontpage, .mobile 等后缀
        int dotIdx = result.IndexOf('.');
        if (dotIdx > 0)
        {
            result = result.Substring(0, dotIdx);
        }
        
        return result.ToLower();
    }
    
    /// <summary>
    /// 推测显示名称
    /// </summary>
    private static string InferDisplayName()
    {
        string appId = _currentAppId;
        if (string.IsNullOrEmpty(appId)) return "Unknown App";
        
        // 首字母大写
        if (appId.Length > 0)
        {
            return char.ToUpper(appId[0]) + appId.Substring(1);
        }
        return appId;
    }
    
    /// <summary>
    /// 推测屏幕名称
    /// </summary>
    private static string InferScreenName(string layoutXml, int depth)
    {
        if (string.IsNullOrEmpty(layoutXml)) return "unknown_screen";
        
        // 根据深度推测屏幕类型
        switch (depth)
        {
            case 0:
                return "main";
            case 1:
                return "sub_screen_1";
            case 2:
                return "sub_screen_2";
            default:
                return "depth_" + depth.ToString();
        }
    }
    
    /// <summary>
    /// 计算屏幕哈希（用于去重）
    /// </summary>
    private static string ComputeScreenHash(string layoutXml)
    {
        if (string.IsNullOrEmpty(layoutXml)) return "";
        
        // 简单哈希：提取所有 resource-id 并排序
        var resourceIds = new List<string>();
        
        int searchPos = 0;
        while (true)
        {
            int idx = layoutXml.IndexOf("resource-id=\"", searchPos);
            if (idx < 0) break;
            
            int start = idx + "resource-id=\"".Length;
            int end = layoutXml.IndexOf("\"", start);
            if (end <= start) break;
            
            string resourceId = layoutXml.Substring(start, end - start);
            resourceIds.Add(resourceId);
            
            searchPos = end + 1;
        }
        
        // 排序后拼接
        resourceIds.Sort();
        
        var combined = new StringBuilder();
        foreach (string id in resourceIds)
        {
            combined.Append(id);
            combined.Append("|");
        }
        
        return combined.ToString();
    }
    
    /// <summary>
    /// 解析元素信息
    /// </summary>
    private static ElementInfo ParseElement(string nodeContent)
    {
        ElementInfo elem = new ElementInfo();
        
        if (string.IsNullOrEmpty(nodeContent)) return elem;
        
        // 解析 resource-id
        elem.ResourceId = ExtractXmlAttribute(nodeContent, "resource-id");
        
        // 解析 text
        elem.Text = ExtractXmlAttribute(nodeContent, "text");
        
        // 解析 content-desc
        elem.ContentDesc = ExtractXmlAttribute(nodeContent, "content-desc");
        
        // 解析 bounds
        elem.Bounds = ExtractXmlAttribute(nodeContent, "bounds");
        
        // 判断是否为导航元素
        elem.IsNavigation = IsNavigationElement(elem);
        
        return elem;
    }
    
    /// <summary>
    /// 提取 XML 属性值
    /// </summary>
    private static string ExtractXmlAttribute(string nodeContent, string attrName)
    {
        if (string.IsNullOrEmpty(nodeContent) || string.IsNullOrEmpty(attrName))
            return "";
        
        string pattern = attrName + "=\"";
        int idx = nodeContent.IndexOf(pattern);
        if (idx < 0) return "";
        
        int start = idx + pattern.Length;
        int end = nodeContent.IndexOf("\"", start);
        if (end <= start) return "";
        
        return nodeContent.Substring(start, end - start);
    }
    
    /// <summary>
    /// 判断是否为导航元素
    /// </summary>
    private static bool IsNavigationElement(ElementInfo elem)
    {
        string combined = (elem.ResourceId + " " + elem.Text + " " + elem.ContentDesc).ToLower();
        
        foreach (string keyword in NAV_KEYWORDS)
        {
            if (combined.Contains(keyword))
                return true;
        }
        
        return false;
    }
    
    /// <summary>
    /// 判断是否为敏感操作
    /// </summary>
    private static bool IsSensitiveOperation(ElementInfo elem)
    {
        string combined = (elem.ResourceId + " " + elem.Text + " " + elem.ContentDesc).ToLower();
        
        foreach (string keyword in SENSITIVE_KEYWORDS)
        {
            if (combined.Contains(keyword))
                return true;
        }
        
        return false;
    }
    
    /// <summary>
    /// 点击元素
    /// </summary>
    private static bool TapElement(ElementInfo elem)
    {
        try
        {
            dynamic input = CoreHelper.GetInput();
            if (input == null) return false;
            
            // 解析 bounds 坐标
            // 格式：[x1,y1][x2,y2]
            if (string.IsNullOrEmpty(elem.Bounds)) return false;
            
            // 移除方括号
            string bounds = elem.Bounds.Replace("[", "").Replace("]", ",");
            string[] parts = bounds.Split(',');
            
            if (parts.Length >= 4)
            {
                int x1 = CoreHelper.SafeParseInt(parts[0], 0);
                int y1 = CoreHelper.SafeParseInt(parts[1], 0);
                int x2 = CoreHelper.SafeParseInt(parts[2], 0);
                int y2 = CoreHelper.SafeParseInt(parts[3], 0);
                
                // 计算中心点
                int centerX = (x1 + x2) / 2;
                int centerY = (y1 + y2) / 2;
                
                input.Tap(centerX, centerY);
                return true;
            }
            
            return false;
        }
        catch (Exception ex)
        {
            CoreHelper.LogErr(TAG, "TapElement 失败: " + ex.Message);
            return false;
        }
    }
    
    /// <summary>
    /// 返回上一屏幕
    /// </summary>
    private static void GoBack()
    {
        try
        {
            dynamic input = CoreHelper.GetInput();
            if (input == null) return;
            
            input.Shell("input keyevent 4");
        }
        catch (Exception ex)
        {
            CoreHelper.LogWarn(TAG, "GoBack 失败: " + ex.Message);
        }
    }
    
    /// <summary>
    /// 保存 manifest 到文件
    /// </summary>
    private static void SaveManifest(string manifestJson)
    {
        try
        {
            string projectRoot = CoreHelper.GetVar("project_root", "");
            if (string.IsNullOrEmpty(projectRoot))
            {
                projectRoot = System.IO.Directory.GetCurrentDirectory();
            }
            
            projectRoot = CoreHelper.NormalizePath(projectRoot);
            string dirPath = projectRoot + "Configs\\Manifests\\";
            string fileName = _currentPackage + "_draft.json";
            string filePath = dirPath + fileName;
            
            CoreHelper.EnsureDir(dirPath);
            CoreHelper.WriteFileAtomic(filePath, manifestJson);
            
            CoreHelper.Log(TAG, "Manifest 已保存: " + filePath);
        }
        catch (Exception ex)
        {
            CoreHelper.LogErr(TAG, "SaveManifest 失败: " + ex.Message);
        }
    }
    
    /// <summary>
    /// JSON 字符串转义
    /// </summary>
    private static string EscapeJson(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        
        return value.Replace("\\", "\\\\")
                    .Replace("\"", "\\\"")
                    .Replace("\n", "\\n")
                    .Replace("\r", "\\r")
                    .Replace("\t", "\\t");
    }
}

