// =====================================================
// Intent.cs - 操作意图抽象（DPS 决策层输出）
// ⚠️ C# 5.0 语法 - 禁止使用 $""、?.、nameof() 等
// v4.5.2 - 2026-02-27 - 架构重构：DPS 决策层与执行层分离
// =====================================================
// 核心职责：
//   定义高层操作意图，描述"做什么"而不是"怎么做"
//   例如："点赞这条帖子" 而不是 "点击坐标 (500, 800)"
// =====================================================
using System;
using System.Collections.Generic;

/// <summary>
/// 操作意图 - DPS 决策层的输出
/// 描述用户行为的语义，不包含物理执行细节
/// </summary>
public class Intent
{
    /// <summary>
    /// 意图类型（tap, swipe, scroll, type, find, verify, delay, back）
    /// </summary>
    public string Action { get; set; }
    
    /// <summary>
    /// 目标元素标识（selector key，如 "like_button"）
    /// </summary>
    public string Target { get; set; }
    
    /// <summary>
    /// 高层参数（方向、文本内容等语义参数）
    /// </summary>
    public Dictionary<string, string> Parameters { get; set; }
    
    /// <summary>
    /// 失败处理策略（skip, retry, abort）
    /// </summary>
    public string OnFail { get; set; }
    
    /// <summary>
    /// 是否启用拟人化执行
    /// </summary>
    public bool Humanized { get; set; }
    
    /// <summary>
    /// 上下文引用（引用之前 find 的结果）
    /// </summary>
    public string ContextRef { get; set; }
    
    /// <summary>
    /// 结果保存位置（用于 find 操作）
    /// </summary>
    public string SaveAs { get; set; }

    /// <summary>
    /// 回退意图链
    /// </summary>
    public List<Intent> FallbackIntents { get; set; }
     
    public Intent()
    {
        Parameters = new Dictionary<string, string>();
        FallbackIntents = new List<Intent>();
        OnFail = "skip";
        Humanized = false;
    }

    public Intent(string action, string target)
        : this()
    {
        Action = action;
        Target = target;
    }
    
    /// <summary>
    /// 从 JSON 步骤构造 Intent
    /// </summary>
    public static Intent FromStepJson(string stepJson)
    {
        Intent intent = new Intent();
        
        intent.Action = JsonHelper.Get(stepJson, "action");
        intent.Target = JsonHelper.Get(stepJson, "selector");
        intent.ContextRef = JsonHelper.Get(stepJson, "context_ref");
        intent.SaveAs = JsonHelper.Get(stepJson, "save_as");
        intent.OnFail = JsonHelper.Get(stepJson, "on_fail");
        
        string humanizedStr = JsonHelper.Get(stepJson, "humanized");
        intent.Humanized = (humanizedStr == "true");
        
        // 提取高层参数
        string direction = JsonHelper.Get(stepJson, "direction");
        if (!string.IsNullOrEmpty(direction))
        {
            intent.Parameters["direction"] = direction;
        }
        
        string distance = JsonHelper.Get(stepJson, "distance");
        if (!string.IsNullOrEmpty(distance))
        {
            intent.Parameters["distance"] = distance;
        }
        
        string text = JsonHelper.Get(stepJson, "text");
        if (!string.IsNullOrEmpty(text))
        {
            intent.Parameters["text"] = text;
        }
        
        string varName = JsonHelper.Get(stepJson, "var");
        if (!string.IsNullOrEmpty(varName))
        {
            intent.Parameters["var"] = varName;
        }
        
        string minMs = JsonHelper.Get(stepJson, "min_ms");
        if (!string.IsNullOrEmpty(minMs))
        {
            intent.Parameters["min_ms"] = minMs;
        }
        
        string maxMs = JsonHelper.Get(stepJson, "max_ms");
        if (!string.IsNullOrEmpty(maxMs))
        {
            intent.Parameters["max_ms"] = maxMs;
        }
        
        string maxRetries = JsonHelper.Get(stepJson, "max_retries");
        if (!string.IsNullOrEmpty(maxRetries))
        {
            intent.Parameters["max_retries"] = maxRetries;
        }
        
        string retryDelay = JsonHelper.Get(stepJson, "retry_delay_ms");
        if (!string.IsNullOrEmpty(retryDelay))
        {
            intent.Parameters["retry_delay_ms"] = retryDelay;
        }
        
        return intent;
    }

    public bool IsValid()
    {
        return !string.IsNullOrEmpty(Action);
    }

    public string GetParameter(string key, string defaultValue)
    {
        if (Parameters == null || string.IsNullOrEmpty(key) || !Parameters.ContainsKey(key))
        {
            return defaultValue;
        }

        string value = Parameters[key];
        return string.IsNullOrEmpty(value) ? defaultValue : value;
    }

    public int GetParameter(string key, int defaultValue)
    {
        string value = GetParameter(key, "");
        if (string.IsNullOrEmpty(value))
        {
            return defaultValue;
        }

        int parsed;
        if (int.TryParse(value, out parsed))
        {
            return parsed;
        }
        return defaultValue;
    }
     
    /// <summary>
    /// 转换为日志字符串
    /// </summary>
    public string ToLogString()
    {
        string log = string.Format("Intent[action={0}", Action);
        
        if (!string.IsNullOrEmpty(Target))
        {
            log += string.Format(", target={0}", Target);
        }
        
        if (!string.IsNullOrEmpty(ContextRef))
        {
            log += string.Format(", context_ref={0}", ContextRef);
        }
        
        if (Parameters.Count > 0)
        {
            log += ", params={";
            bool first = true;
            foreach (KeyValuePair<string, string> kvp in Parameters)
            {
                if (!first) log += ", ";
                log += string.Format("{0}={1}", kvp.Key, kvp.Value);
                first = false;
            }
            log += "}";
        }
        
        if (Humanized)
        {
            log += ", humanized=true";
        }
        
        log += "]";
        return log;
    }
}
