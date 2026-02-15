// =====================================================
// WeeklyEvolve.cs - 每周画像进化模块
// ⚠️ C# 5.0 语法 - 禁止使用 $""、?.、nameof() 等
// ⚠️ 从 AIConfig.json 动态读取 API 配置
// v4.0.1 - 实现 AI 建议的实际应用逻辑
// =====================================================
using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// 每周画像进化模块
/// 分析行为模式，使用 AI 优化画像
/// </summary>
public class WeeklyEvolve
{
    private static dynamic _project;
    private const string TAG = "WeeklyEvolve";
    
    /// <summary>
    /// 模块入口点
    /// </summary>
    public static string Run(object projectObj)
    {
        _project = projectObj;
        
        try
        {
            CoreHelper.Init(projectObj);
            
            string projectRoot = CoreHelper.GetVar("project_root", "");
            string deviceId = CoreHelper.GetVar("device_id", "");
            string personaJson = CoreHelper.GetVar("persona_json", "{}");
            string aiConfigJson = CoreHelper.GetVar("ai_config_json", "");
            
            if (string.IsNullOrEmpty(projectRoot) || string.IsNullOrEmpty(deviceId))
            {
                CoreHelper.LogErr(TAG, "project_root 或 device_id 未设置");
                return "ERROR: 变量未设置";
            }
            
            // 验证 deviceId 安全性
            if (!CoreHelper.ValidateDeviceId(deviceId))
            {
                CoreHelper.LogErr(TAG, "device_id 包含非法字符");
                return "ERROR: device_id 无效";
            }
            
            projectRoot = CoreHelper.NormalizePath(projectRoot);
            
            // 如果 aiConfigJson 为空，从文件读取
            if (string.IsNullOrEmpty(aiConfigJson) || aiConfigJson == "{}")
            {
                string configPath = projectRoot + "Config\\AIConfig.json";
                if (File.Exists(configPath))
                {
                    aiConfigJson = CoreHelper.ReadFile(configPath);
                    CoreHelper.SetVar("ai_config_json", aiConfigJson);
                }
                else
                {
                    CoreHelper.Log(TAG, "跳过AI分析（配置文件不存在）");
                    CoreHelper.SetVar("evolve_result", "SKIPPED");
                    return "SKIPPED";
                }
            }
            
            // 如果 personaJson 为空，从文件读取
            if (string.IsNullOrEmpty(personaJson) || personaJson == "{}")
            {
                string personaPath = projectRoot + "Persons\\" + deviceId + ".json";
                if (File.Exists(personaPath))
                {
                    personaJson = CoreHelper.ReadFile(personaPath);
                    CoreHelper.SetVar("persona_json", personaJson);
                }
                else
                {
                    CoreHelper.Log(TAG, "跳过AI分析（画像文件不存在）");
                    CoreHelper.SetVar("evolve_result", "SKIPPED");
                    return "SKIPPED";
                }
            }
            
            CoreHelper.Log(TAG, "开始每周进化分析");
            
            // 1. 收集过去一周的记忆
            string memoryDir = projectRoot + "Memory\\" + deviceId;
            StringBuilder memorySummary = new StringBuilder();
            int totalActions = 0;
            
            DateTime weekAgo = DateTime.Now.AddDays(-7);
            for (int i = 0; i < 7; i++)
            {
                string dateStr = weekAgo.AddDays(i).ToString("yyyy-MM-dd");
                string memoryFile = memoryDir + "\\" + dateStr + ".json";
                
                if (File.Exists(memoryFile))
                {
                    string content = CoreHelper.ReadFile(memoryFile);
                    int actionCount = CoreHelper.CountOccurrences(content, "\"action_type\"");
                    totalActions += actionCount;
                    memorySummary.AppendLine("日期 " + dateStr + ": " + actionCount + " 个动作");
                }
            }
            
            CoreHelper.Log(TAG, "收集了 7 天的记忆数据，共 " + totalActions + " 个动作");
            
            // 2. 构建进化分析 Prompt
            string stageCode = JsonHelper.Get(personaJson, "stage_code");
            if (string.IsNullOrEmpty(stageCode)) stageCode = "T2";
            
            string currentAge = JsonHelper.Get(personaJson, "current_age");
            if (string.IsNullOrEmpty(currentAge)) currentAge = "30";
            
            string prompt = "你是一个用户画像进化专家。根据以下信息分析画像应该如何自然进化。\n\n"
                + "## 当前画像摘要\n阶段码: " + stageCode + "\n年龄: " + currentAge + "\n\n"
                + "## 过去一周行为记录\n" + memorySummary.ToString() + "\n"
                + "总动作数: " + totalActions + "\n\n"
                + "请给出简短的进化建议，输出JSON格式：\n"
                + "{\"should_evolve\": true/false, \"changes\": [{\"field\": \"字段名\", \"direction\": \"increase/decrease\", \"reason\": \"原因\"}], \"confidence\": 0.0-1.0}\n\n只输出JSON。";
            
            // 3. 调用 AI 分析（使用动态配置，带重试和备用模型）
            CoreHelper.Log(TAG, "调用AI进行进化分析...");
            string responseText = AIService.CallWithRetry(prompt, aiConfigJson);
            
            if (responseText.StartsWith("ERROR:"))
            {
                CoreHelper.LogErr(TAG, "API调用失败: " + responseText);
                return responseText;
            }
            
            CoreHelper.Log(TAG, "AI分析完成");
            
            // 提取 JSON 响应
            string aiJson = AIService.ExtractJson(responseText);
            
            // 检查是否需要进化
            if (aiJson.Contains("\"should_evolve\": true") || 
                aiJson.Contains("\"should_evolve\":true"))
            {
                CoreHelper.Log(TAG, "AI建议进行进化，开始应用修改...");
                
                // 备份当前画像
                string today = CoreHelper.GetToday();
                string backupDir = projectRoot + "Persons\\Backups\\" + deviceId;
                CoreHelper.EnsureDir(backupDir);
                string backupPath = backupDir + "\\" + today + "_evolve.json";
                CoreHelper.WriteFile(backupPath, personaJson);
                CoreHelper.Log(TAG, "已备份画像到: " + backupPath);
                
                // 解析并应用 changes 数组
                personaJson = ApplyEvolutionChanges(personaJson, aiJson);
                
                // 保存更新后的画像
                string personaPath = projectRoot + "Persons\\" + deviceId + ".json";
                CoreHelper.WriteFile(personaPath, personaJson);
                CoreHelper.SetVar("persona_json", personaJson);
                
                CoreHelper.Log(TAG, "画像进化完成并已保存");
            }
            else
            {
                CoreHelper.Log(TAG, "本周无需进化");
            }
            
            CoreHelper.SetVar("evolve_result", "SUCCESS");
            return "SUCCESS";
        }
        catch (Exception ex)
        {
            CoreHelper.LogErr(TAG, "异常: " + ex.Message);
            CoreHelper.SetVar("last_error", ex.Message);
            CoreHelper.SetVar("evolve_result", "ERROR");
            return "ERROR: " + ex.Message;
        }
    }
    
    /// <summary>
    /// 应用进化修改到画像
    /// </summary>
    private static string ApplyEvolutionChanges(string personaJson, string aiJson)
    {
        // 提取 changes 数组
        Match changesMatch = Regex.Match(aiJson, "\"changes\"\\s*:\\s*\\[([^\\]]+)\\]", RegexOptions.Singleline);
        if (!changesMatch.Success)
        {
            CoreHelper.Log(TAG, "未找到 changes 数组");
            return personaJson;
        }
        
        string changesContent = changesMatch.Groups[1].Value;
        
        // 解析每个 change 对象
        MatchCollection changeObjects = Regex.Matches(changesContent, "\\{([^{}]+)\\}", RegexOptions.Singleline);
        
        int appliedCount = 0;
        foreach (Match changeObj in changeObjects)
        {
            string entry = changeObj.Groups[1].Value;
            
            // 提取字段名
            string field = ExtractJsonStringValue(entry, "field");
            string direction = ExtractJsonStringValue(entry, "direction");
            string reason = ExtractJsonStringValue(entry, "reason");
            
            if (string.IsNullOrEmpty(field) || string.IsNullOrEmpty(direction))
            {
                continue;
            }
            
            CoreHelper.Log(TAG, string.Format("处理进化: {0} -> {1} (原因: {2})", field, direction, reason));
            
            // 应用修改
            personaJson = ApplyFieldChange(personaJson, field, direction);
            appliedCount++;
        }
        
        CoreHelper.Log(TAG, "共应用 " + appliedCount + " 项进化修改");
        return personaJson;
    }
    
    /// <summary>
    /// 从 JSON 片段中提取字符串值
    /// </summary>
    private static string ExtractJsonStringValue(string json, string key)
    {
        Match m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"([^\"]+)\"");
        return m.Success ? m.Groups[1].Value : "";
    }
    
    /// <summary>
    /// 应用单个字段的进化修改
    /// </summary>
    private static string ApplyFieldChange(string json, string field, string direction)
    {
        // 查找字段的当前数值
        Match valMatch = Regex.Match(json, "\"" + field + "\"\\s*:\\s*(\\d+)");
        if (valMatch.Success)
        {
            int currentVal = int.Parse(valMatch.Groups[1].Value);
            int newVal = currentVal;
            
            // 根据方向调整值（步长为 5）
            if (direction == "increase")
            {
                newVal = currentVal + 5;
            }
            else if (direction == "decrease")
            {
                newVal = currentVal - 5;
            }
            
            // 限制在 1-100 范围内
            if (newVal > 100) newVal = 100;
            if (newVal < 1) newVal = 1;
            
            CoreHelper.Log(TAG, string.Format("  字段 [{0}]: {1} -> {2}", field, currentVal, newVal));
            
            return Regex.Replace(json, "\"" + field + "\"\\s*:\\s*\\d+", 
                "\"" + field + "\": " + newVal);
        }
        else
        {
            CoreHelper.Log(TAG, string.Format("  字段 [{0}] 不是数值类型，跳过", field));
        }
        
        return json;
    }
}
