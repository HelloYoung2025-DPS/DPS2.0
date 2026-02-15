// =====================================================
// PageDetector.cs - 加权指纹页面检测引擎
// ⚠️ C# 5.0 语法 - 禁止使用 $""、?.、nameof() 等
// =====================================================
// 页面检测原理：
//   每个页面类型定义一组"指纹"（UI 元素特征），每个指纹有权重。
//   扫描当前 XML hierarchy，累加匹配指纹的权重，
//   得分最高且超过阈值的页面类型即为当前页面。
//
// 指纹配置来自 PlatformsConfig.json → platforms.{name}.page_signatures
// 格式：
//   "page_signatures": {
//     "feed": {
//       "threshold": 0.5,
//       "signals": [
//         { "strategy": "resource-id", "value": "post_unit", "weight": 0.6 },
//         { "strategy": "resource-id", "value": "toolbar", "weight": 0.2 },
//         { "strategy": "text-contains", "value": "Home", "weight": 0.2 }
//       ]
//     },
//     "post_detail": { ... },
//     "comment": { ... },
//     "profile": { ... }
//   }
//
// 返回值：页面类型字符串（feed/post_detail/comment/profile/unknown）
// =====================================================
using System;
using System.Collections.Generic;

/// <summary>
/// 加权指纹页面检测引擎
/// 通过 UI 元素特征指纹识别当前所在页面
/// </summary>
public class PageDetector
{
    private const string TAG = "PageDetector";

    /// <summary>
    /// 检测当前页面类型
    /// </summary>
    /// <param name="xml">当前 UI hierarchy XML</param>
    /// <param name="signaturesJson">页面指纹配置 JSON（page_signatures 对象）</param>
    /// <returns>页面类型字符串，无法识别返回 "unknown"</returns>
    public static string Detect(string xml, string signaturesJson)
    {
        if (string.IsNullOrEmpty(xml) || string.IsNullOrEmpty(signaturesJson))
        {
            return "unknown";
        }

        string bestPage = "unknown";
        double bestScore = 0.0;

        // 遍历所有已知页面类型
        string[] pageTypes = GetPageTypes(signaturesJson);

        for (int i = 0; i < pageTypes.Length; i++)
        {
            string pageType = pageTypes[i];
            string pageConfig = JsonHelper.ExtractObject(signaturesJson, pageType);
            if (string.IsNullOrEmpty(pageConfig))
            {
                continue;
            }

            double threshold = JsonHelper.GetDouble(pageConfig, "threshold", 0.5);
            string signalsArray = JsonHelper.ExtractArray(pageConfig, "signals");
            if (string.IsNullOrEmpty(signalsArray))
            {
                continue;
            }

            double score = CalculateScore(xml, signalsArray);

            CoreHelper.Log(TAG, string.Format("页面 {0}: score={1:F3}, threshold={2:F2}",
                pageType, score, threshold));

            if (score >= threshold && score > bestScore)
            {
                bestScore = score;
                bestPage = pageType;
            }
        }

        CoreHelper.Log(TAG, "检测结果: " + bestPage + " (score=" + bestScore.ToString("F3") + ")");
        return bestPage;
    }

    /// <summary>
    /// 检测当前页面并返回详细结果（包含所有页面得分）
    /// </summary>
    public static string DetectWithScores(string xml, string signaturesJson)
    {
        if (string.IsNullOrEmpty(xml) || string.IsNullOrEmpty(signaturesJson))
        {
            return "{\"page\":\"unknown\",\"scores\":{}}";
        }

        string bestPage = "unknown";
        double bestScore = 0.0;
        var scoreEntries = new List<string>();

        string[] pageTypes = GetPageTypes(signaturesJson);

        for (int i = 0; i < pageTypes.Length; i++)
        {
            string pageType = pageTypes[i];
            string pageConfig = JsonHelper.ExtractObject(signaturesJson, pageType);
            if (string.IsNullOrEmpty(pageConfig)) continue;

            double threshold = JsonHelper.GetDouble(pageConfig, "threshold", 0.5);
            string signalsArray = JsonHelper.ExtractArray(pageConfig, "signals");
            if (string.IsNullOrEmpty(signalsArray)) continue;

            double score = CalculateScore(xml, signalsArray);
            scoreEntries.Add("\"" + pageType + "\":" + score.ToString("F3"));

            if (score >= threshold && score > bestScore)
            {
                bestScore = score;
                bestPage = pageType;
            }
        }

        string scoresJson = "{" + string.Join(",", scoreEntries.ToArray()) + "}";
        return "{\"page\":\"" + bestPage + "\",\"score\":" + bestScore.ToString("F3")
            + ",\"scores\":" + scoresJson + "}";
    }

    /// <summary>
    /// 快速检查是否在指定页面
    /// </summary>
    public static bool IsPage(string xml, string signaturesJson, string expectedPage)
    {
        string detected = Detect(xml, signaturesJson);
        return detected == expectedPage;
    }

    // ========== 内部方法 ==========

    /// <summary>
    /// 计算一组信号在 XML 中的加权得分
    /// </summary>
    private static double CalculateScore(string xml, string signalsArrayJson)
    {
        double totalScore = 0.0;

        // 手动解析信号数组中的每个对象
        // signalsArrayJson 格式: [{ ... }, { ... }, ...]
        string[] elements = ParseObjectArray(signalsArrayJson);

        for (int i = 0; i < elements.Length; i++)
        {
            string signal = elements[i];
            string strategy = JsonHelper.Get(signal, "strategy");
            string value = JsonHelper.Get(signal, "value");
            double weight = JsonHelper.GetDouble(signal, "weight", 0.0);

            if (string.IsNullOrEmpty(strategy) || string.IsNullOrEmpty(value))
            {
                continue;
            }

            // 用 SelectorEngine 检查元素是否存在
            List<string> matches = SelectorEngine.FindByStrategy(xml, strategy, value);
            if (matches.Count > 0)
            {
                totalScore += weight;
            }
        }

        return totalScore;
    }

    /// <summary>
    /// 从 page_signatures JSON 中提取所有页面类型名称
    /// 手动解析顶层 key（不依赖完整 JSON 解析器）
    /// </summary>
    private static string[] GetPageTypes(string signaturesJson)
    {
        var types = new List<string>();
        if (string.IsNullOrEmpty(signaturesJson)) return types.ToArray();

        // 找到第一个 { 之后的所有顶层 key
        int i = signaturesJson.IndexOf('{');
        if (i < 0) return types.ToArray();
        i++;

        int depth = 1;
        while (i < signaturesJson.Length && depth > 0)
        {
            // 跳过空白
            while (i < signaturesJson.Length && char.IsWhiteSpace(signaturesJson[i])) i++;
            if (i >= signaturesJson.Length) break;

            char c = signaturesJson[i];

            if (c == '}')
            {
                depth--;
                i++;
                continue;
            }

            if (c == ',')
            {
                i++;
                continue;
            }

            // 在 depth==1 时遇到引号 → 这是一个顶层 key
            if (depth == 1 && c == '"')
            {
                int keyStart = i + 1;
                int keyEnd = signaturesJson.IndexOf('"', keyStart);
                if (keyEnd <= keyStart) break;

                string key = signaturesJson.Substring(keyStart, keyEnd - keyStart);
                types.Add(key);

                // 跳过 key、冒号、value
                i = keyEnd + 1;
                while (i < signaturesJson.Length && char.IsWhiteSpace(signaturesJson[i])) i++;
                if (i < signaturesJson.Length && signaturesJson[i] == ':') i++;

                // 跳过 value（可能是嵌套对象）
                i = SkipValue(signaturesJson, i);
            }
            else if (c == '{')
            {
                depth++;
                i++;
            }
            else
            {
                i++;
            }
        }

        return types.ToArray();
    }

    /// <summary>
    /// 解析 JSON 数组中的对象元素
    /// 输入: [{ ... }, { ... }]
    /// 输出: 每个对象的 JSON 字符串
    /// </summary>
    private static string[] ParseObjectArray(string arrayJson)
    {
        var results = new List<string>();
        if (string.IsNullOrEmpty(arrayJson)) return results.ToArray();

        int i = arrayJson.IndexOf('[');
        if (i < 0) return results.ToArray();
        i++;

        while (i < arrayJson.Length)
        {
            // 跳过空白和逗号
            while (i < arrayJson.Length && (char.IsWhiteSpace(arrayJson[i]) || arrayJson[i] == ',')) i++;
            if (i >= arrayJson.Length) break;

            if (arrayJson[i] == ']') break;

            if (arrayJson[i] == '{')
            {
                int objStart = i;
                int depth = 1;
                i++;
                while (i < arrayJson.Length && depth > 0)
                {
                    char c = arrayJson[i];
                    if (c == '"')
                    {
                        // 跳过字符串
                        i++;
                        while (i < arrayJson.Length)
                        {
                            if (arrayJson[i] == '"') { i++; break; }
                            if (arrayJson[i] == '\\' && i + 1 < arrayJson.Length) i++;
                            i++;
                        }
                    }
                    else if (c == '{')
                    {
                        depth++;
                        i++;
                    }
                    else if (c == '}')
                    {
                        depth--;
                        i++;
                    }
                    else
                    {
                        i++;
                    }
                }
                results.Add(arrayJson.Substring(objStart, i - objStart));
            }
            else
            {
                i++;
            }
        }

        return results.ToArray();
    }

    /// <summary>
    /// 跳过一个 JSON 值（字符串/对象/数组/原始值）
    /// </summary>
    private static int SkipValue(string json, int startIndex)
    {
        int i = startIndex;
        while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
        if (i >= json.Length) return i;

        char c = json[i];

        // 字符串
        if (c == '"')
        {
            i++;
            while (i < json.Length)
            {
                if (json[i] == '"') return i + 1;
                if (json[i] == '\\' && i + 1 < json.Length) i++;
                i++;
            }
            return i;
        }

        // 对象或数组
        if (c == '{' || c == '[')
        {
            char open = c;
            char close = (c == '{') ? '}' : ']';
            int depth = 1;
            i++;
            while (i < json.Length && depth > 0)
            {
                char ch = json[i];
                if (ch == '"')
                {
                    i++;
                    while (i < json.Length)
                    {
                        if (json[i] == '"') { i++; break; }
                        if (json[i] == '\\' && i + 1 < json.Length) i++;
                        i++;
                    }
                }
                else if (ch == open)
                {
                    depth++;
                    i++;
                }
                else if (ch == close)
                {
                    depth--;
                    i++;
                }
                else
                {
                    i++;
                }
            }
            return i;
        }

        // 原始值（数字、布尔、null）
        while (i < json.Length)
        {
            c = json[i];
            if (c == ',' || c == '}' || c == ']' || char.IsWhiteSpace(c)) break;
            i++;
        }
        return i;
    }
}
