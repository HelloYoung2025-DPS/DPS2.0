// =====================================================
// SelectorEngine.cs - 多策略 UI 元素查找引擎
// ⚠️ C# 5.0 语法 - 禁止使用 $""、?.、nameof() 等
// =====================================================
// 查找策略优先级（fallback 链）：
//   1. resource-id  — 最快最可靠
//   2. text         — 按文本内容匹配
//   3. content-desc — 无障碍描述匹配
//   4. class        — 按控件类型匹配（最宽泛）
//
// 选择器 JSON 格式（来自 PlatformsConfig.json）：
//   {
//     "strategy": "resource-id",       // 主策略
//     "value": "com.reddit:id/post",   // 主值
//     "fallback_strategy": "text",     // 备选策略（可选）
//     "fallback_value": "Post",        // 备选值（可选）
//     "index": 0                       // 匹配第几个（可选，默认0=第一个）
//   }
// =====================================================
using System;
using System.Collections.Generic;

/// <summary>
/// 多策略 UI 元素查找引擎
/// 从 XML hierarchy 中按多种策略定位元素
/// </summary>
public class SelectorEngine
{
    private const string TAG = "SelectorEngine";

    // ========== 核心查找方法 ==========

    /// <summary>
    /// 按选择器 JSON 查找元素，返回 bounds 字符串列表
    /// selectorJson 格式见文件头注释
    /// </summary>
    public static List<string> Find(string xml, string selectorJson)
    {
        if (string.IsNullOrEmpty(xml) || string.IsNullOrEmpty(selectorJson))
        {
            return new List<string>();
        }

        string strategy = JsonHelper.Get(selectorJson, "strategy");
        string value = JsonHelper.Get(selectorJson, "value");

        if (string.IsNullOrEmpty(strategy) || string.IsNullOrEmpty(value))
        {
            CoreHelper.LogErr(TAG, "选择器缺少 strategy 或 value");
            return new List<string>();
        }

        // 主策略查找
        List<string> results = FindByStrategy(xml, strategy, value);

        // 主策略无结果 → 尝试 fallback
        if (results.Count == 0)
        {
            string fbStrategy = JsonHelper.Get(selectorJson, "fallback_strategy");
            string fbValue = JsonHelper.Get(selectorJson, "fallback_value");

            if (!string.IsNullOrEmpty(fbStrategy) && !string.IsNullOrEmpty(fbValue))
            {
                CoreHelper.Log(TAG, string.Format("主策略 {0}={1} 无结果，尝试 fallback {2}={3}",
                    strategy, value, fbStrategy, fbValue));
                results = FindByStrategy(xml, fbStrategy, fbValue);
            }
        }

        return results;
    }

    /// <summary>
    /// 按选择器 JSON 查找单个元素，返回 bounds 字符串
    /// 支持 index 字段指定匹配第几个（默认0）
    /// </summary>
    public static string FindOne(string xml, string selectorJson)
    {
        List<string> results = Find(xml, selectorJson);
        if (results.Count == 0)
        {
            return "";
        }

        int index = JsonHelper.GetInt(selectorJson, "index", 0);
        if (index < 0 || index >= results.Count)
        {
            index = 0;
        }
        return results[index];
    }

    /// <summary>
    /// 按选择器 JSON 查找并返回解析后的 int[4] 坐标
    /// 失败返回 null
    /// </summary>
    public static int[] FindBounds(string xml, string selectorJson)
    {
        string boundsStr = FindOne(xml, selectorJson);
        if (string.IsNullOrEmpty(boundsStr))
        {
            return null;
        }
        return ParseBounds(boundsStr);
    }

    /// <summary>
    /// 按选择器 JSON 查找并返回中心点坐标
    /// 失败返回 null
    /// </summary>
    public static int[] FindCenter(string xml, string selectorJson)
    {
        int[] bounds = FindBounds(xml, selectorJson);
        if (bounds == null)
        {
            return null;
        }
        return new int[] { (bounds[0] + bounds[2]) / 2, (bounds[1] + bounds[3]) / 2 };
    }

    // ========== 策略分发 ==========

    /// <summary>
    /// 按指定策略查找元素，返回 bounds 字符串列表
    /// </summary>
    public static List<string> FindByStrategy(string xml, string strategy, string value)
    {
        if (strategy == "resource-id")
        {
            return FindByAttribute(xml, "resource-id", value);
        }
        if (strategy == "text")
        {
            return FindByAttribute(xml, "text", value);
        }
        if (strategy == "content-desc")
        {
            return FindByAttribute(xml, "content-desc", value);
        }
        if (strategy == "class")
        {
            return FindByAttribute(xml, "class", value);
        }
        if (strategy == "text-contains")
        {
            return FindByAttributeContains(xml, "text", value);
        }
        if (strategy == "desc-contains")
        {
            return FindByAttributeContains(xml, "content-desc", value);
        }

        CoreHelper.LogWarn(TAG, "未知选择器策略: " + strategy);
        return new List<string>();
    }

    // ========== 底层 XML 查找 ==========

    /// <summary>
    /// 按属性精确匹配查找节点的 bounds
    /// </summary>
    public static List<string> FindByAttribute(string xml, string attrName, string attrValue)
    {
        var results = new List<string>();
        string searchPattern = attrName + "=\"" + attrValue + "\"";

        int pos = 0;
        while (pos < xml.Length)
        {
            int foundPos = xml.IndexOf(searchPattern, pos);
            if (foundPos < 0) break;

            int nodeStart = xml.LastIndexOf('<', foundPos);
            if (nodeStart < 0) { pos = foundPos + 1; continue; }

            int nodeEnd = xml.IndexOf('>', foundPos);
            if (nodeEnd < 0) { pos = foundPos + 1; continue; }

            string nodeStr = xml.Substring(nodeStart, nodeEnd - nodeStart + 1);

            // 提取 bounds
            string bounds = ExtractAttr(nodeStr, "bounds");
            if (!string.IsNullOrEmpty(bounds))
            {
                results.Add(bounds);
            }

            pos = nodeEnd + 1;
        }

        return results;
    }

    /// <summary>
    /// 按属性包含匹配查找节点的 bounds（模糊匹配）
    /// </summary>
    public static List<string> FindByAttributeContains(string xml, string attrName, string partialValue)
    {
        var results = new List<string>();
        string attrPrefix = attrName + "=\"";

        int pos = 0;
        while (pos < xml.Length)
        {
            int attrPos = xml.IndexOf(attrPrefix, pos);
            if (attrPos < 0) break;

            int valueStart = attrPos + attrPrefix.Length;
            int valueEnd = xml.IndexOf("\"", valueStart);
            if (valueEnd <= valueStart) { pos = attrPos + 1; continue; }

            string fullValue = xml.Substring(valueStart, valueEnd - valueStart);

            if (fullValue.IndexOf(partialValue, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // 找到匹配，回溯找节点起始
                int nodeStart = xml.LastIndexOf('<', attrPos);
                if (nodeStart < 0) { pos = valueEnd + 1; continue; }

                int nodeEnd = xml.IndexOf('>', valueEnd);
                if (nodeEnd < 0) { pos = valueEnd + 1; continue; }

                string nodeStr = xml.Substring(nodeStart, nodeEnd - nodeStart + 1);
                string bounds = ExtractAttr(nodeStr, "bounds");
                if (!string.IsNullOrEmpty(bounds))
                {
                    results.Add(bounds);
                }

                pos = nodeEnd + 1;
            }
            else
            {
                pos = valueEnd + 1;
            }
        }

        return results;
    }

    /// <summary>
    /// 查找完整节点字符串（不仅仅是 bounds）
    /// </summary>
    public static List<string> FindNodes(string xml, string selectorJson)
    {
        var results = new List<string>();
        if (string.IsNullOrEmpty(xml) || string.IsNullOrEmpty(selectorJson))
        {
            return results;
        }

        string strategy = JsonHelper.Get(selectorJson, "strategy");
        string value = JsonHelper.Get(selectorJson, "value");

        if (string.IsNullOrEmpty(strategy) || string.IsNullOrEmpty(value))
        {
            return results;
        }

        string searchPattern = "";
        if (strategy == "resource-id" || strategy == "text" || strategy == "content-desc" || strategy == "class")
        {
            searchPattern = strategy + "=\"" + value + "\"";
        }
        else
        {
            return results;
        }

        int pos = 0;
        while (pos < xml.Length)
        {
            int foundPos = xml.IndexOf(searchPattern, pos);
            if (foundPos < 0) break;

            int nodeStart = xml.LastIndexOf('<', foundPos);
            if (nodeStart < 0) { pos = foundPos + 1; continue; }

            int nodeEnd = xml.IndexOf('>', foundPos);
            if (nodeEnd < 0) { pos = foundPos + 1; continue; }

            string nodeStr = xml.Substring(nodeStart, nodeEnd - nodeStart + 1);
            results.Add(nodeStr);

            pos = nodeEnd + 1;
        }

        return results;
    }

    // ========== 工具方法 ==========

    /// <summary>
    /// 从节点字符串中提取指定属性值
    /// </summary>
    public static string ExtractAttr(string nodeStr, string attrName)
    {
        string prefix = attrName + "=\"";
        int start = nodeStr.IndexOf(prefix);
        if (start < 0) return "";
        start += prefix.Length;
        int end = nodeStr.IndexOf("\"", start);
        if (end <= start) return "";
        return nodeStr.Substring(start, end - start);
    }

    /// <summary>
    /// 解析 bounds 字符串 "[x1,y1][x2,y2]" → int[4]
    /// </summary>
    public static int[] ParseBounds(string boundsStr)
    {
        if (string.IsNullOrEmpty(boundsStr)) return null;
        try
        {
            boundsStr = boundsStr.Replace("[", "").Replace("]", ",");
            string[] parts = boundsStr.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4) return null;
            return new int[] {
                int.Parse(parts[0]), int.Parse(parts[1]),
                int.Parse(parts[2]), int.Parse(parts[3])
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 检查元素是否存在（快速判断）
    /// </summary>
    public static bool Exists(string xml, string selectorJson)
    {
        List<string> results = Find(xml, selectorJson);
        return results.Count > 0;
    }

    /// <summary>
    /// 统计匹配元素数量
    /// </summary>
    public static int Count(string xml, string selectorJson)
    {
        return Find(xml, selectorJson).Count;
    }

    /// <summary>
    /// 构建简单选择器 JSON（便捷方法，避免手动拼 JSON）
    /// </summary>
    public static string BuildSelector(string strategy, string value)
    {
        return "{\"strategy\":\"" + JsonHelper.Escape(strategy)
            + "\",\"value\":\"" + JsonHelper.Escape(value) + "\"}";
    }

    /// <summary>
    /// 构建带 fallback 的选择器 JSON
    /// </summary>
    public static string BuildSelector(string strategy, string value, string fbStrategy, string fbValue)
    {
        return "{\"strategy\":\"" + JsonHelper.Escape(strategy)
            + "\",\"value\":\"" + JsonHelper.Escape(value)
            + "\",\"fallback_strategy\":\"" + JsonHelper.Escape(fbStrategy)
            + "\",\"fallback_value\":\"" + JsonHelper.Escape(fbValue) + "\"}";
    }
}
