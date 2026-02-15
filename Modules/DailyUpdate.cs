// =====================================================
// DailyUpdate.cs - 每日画像更新模块
// ⚠️ C# 5.0 语法 - 禁止使用 $""、?.、nameof() 等
// =====================================================
using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// 每日画像更新模块
/// 更新日期、年龄、孕周等时间相关属性
/// </summary>
public class DailyUpdate
{
    private static dynamic _project;
    private const string TAG = "DailyUpdate";
    
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
            string personaJson = CoreHelper.GetVar("persona_json", "");
            
            if (string.IsNullOrEmpty(projectRoot) || string.IsNullOrEmpty(deviceId))
            {
                CoreHelper.LogErr(TAG, "project_root 或 device_id 未设置");
                CoreHelper.SetVar("daily_result", "ERROR");
                return "ERROR: 变量未设置";
            }
            
            if (string.IsNullOrEmpty(personaJson))
            {
                CoreHelper.LogErr(TAG, "persona_json 为空");
                CoreHelper.SetVar("daily_result", "ERROR");
                return "ERROR: 画像未加载";
            }
            
            projectRoot = CoreHelper.NormalizePath(projectRoot);
            string today = CoreHelper.GetToday();
            
            CoreHelper.Log(TAG, "开始每日更新 - " + today);
            
            // 1. 备份当前画像
            string backupDir = projectRoot + "Persons\\Backups\\" + deviceId;
            CoreHelper.EnsureDir(backupDir);
            string backupPath = backupDir + "\\" + today + ".json";
            CoreHelper.WriteFile(backupPath, personaJson);
            CoreHelper.Log(TAG, "备份完成");
            
            // 2. 更新 _meta.last_updated（注意：是嵌套在 _meta 对象中）
            // 先尝试更新 _meta 中的 last_updated
            if (personaJson.Contains("\"_meta\""))
            {
                // 匹配 _meta 对象中的 last_updated
                personaJson = Regex.Replace(personaJson,
                    "(\"_meta\"\\s*:\\s*\\{[^}]*\"last_updated\"\\s*:\\s*)\"[^\"]+\"",
                    "$1\"" + today + "\"");
            }
            else
            {
                // 兼容旧格式：顶层的 last_updated
                personaJson = Regex.Replace(personaJson,
                    "\"last_updated\"\\s*:\\s*\"[^\"]+\"",
                    "\"last_updated\": \"" + today + "\"");
            }
            CoreHelper.Log(TAG, "last_updated 已更新为: " + today);
            
            // 3. 更新年龄
            var birthMatch = Regex.Match(personaJson, 
                "\"birth_date\"\\s*:\\s*\"([^\"]+)\"");
            if (birthMatch.Success)
            {
                DateTime birthDate;
                if (DateTime.TryParse(birthMatch.Groups[1].Value, out birthDate))
                {
                    int age = DateTime.Now.Year - birthDate.Year;
                    if (DateTime.Now.DayOfYear < birthDate.DayOfYear) age--;
                    
                    personaJson = Regex.Replace(personaJson,
                        "\"current_age\"\\s*:\\s*\\d+",
                        "\"current_age\": " + age);
                }
            }
            
            // 4. 更新孕周
            if (personaJson.Contains("\"is_pregnant\": true") || personaJson.Contains("\"is_pregnant\":true"))
            {
                var conceptionMatch = Regex.Match(personaJson,
                    "\"conception_date\"\\s*:\\s*\"([^\"]+)\"");
                if (conceptionMatch.Success)
                {
                    DateTime conceptionDate;
                    if (DateTime.TryParse(conceptionMatch.Groups[1].Value, out conceptionDate))
                    {
                        int totalDays = (int)(DateTime.Now - conceptionDate).TotalDays;
                        int weeks = totalDays / 7;
                        int days = totalDays % 7;
                        int trimester = weeks <= 13 ? 1 : (weeks <= 27 ? 2 : 3);
                        
                        personaJson = Regex.Replace(personaJson,
                            "\"current_week\"\\s*:\\s*\\d+",
                            "\"current_week\": " + weeks);
                        personaJson = Regex.Replace(personaJson,
                            "\"current_days\"\\s*:\\s*\\d+",
                            "\"current_days\": " + days);
                        personaJson = Regex.Replace(personaJson,
                            "\"trimester\"\\s*:\\s*\\d+",
                            "\"trimester\": " + trimester);
                        
                        // 更新阶段码
                        string newStage = weeks <= 13 ? "T1" : (weeks <= 27 ? "T2" : "T3");
                        var oldStageMatch = Regex.Match(personaJson,
                            "\"stage_code\"\\s*:\\s*\"([^\"]+)\"");
                        if (oldStageMatch.Success && oldStageMatch.Groups[1].Value != newStage)
                        {
                            CoreHelper.Log(TAG, "阶段转换: " + oldStageMatch.Groups[1].Value + " -> " + newStage);
                            personaJson = Regex.Replace(personaJson,
                                "\"stage_code\"\\s*:\\s*\"[^\"]+\"",
                                "\"stage_code\": \"" + newStage + "\"");
                        }
                        
                        CoreHelper.Log(TAG, string.Format("孕周更新: {0}周{1}天, 孕期{2}", weeks, days, trimester));
                    }
                }
            }
            
            // 5. 更新季节
            string season = "winter";
            int month = DateTime.Now.Month;
            if (month >= 3 && month <= 5) season = "spring";
            else if (month >= 6 && month <= 8) season = "summer";
            else if (month >= 9 && month <= 11) season = "fall";
            
            personaJson = Regex.Replace(personaJson,
                "\"current_season\"\\s*:\\s*\"[^\"]+\"",
                "\"current_season\": \"" + season + "\"");
            personaJson = Regex.Replace(personaJson,
                "\"current_month\"\\s*:\\s*\\d+",
                "\"current_month\": " + month);
            
            // 6. 保存更新后的画像
            string personaPath = projectRoot + "Persons\\" + deviceId + ".json";
            CoreHelper.WriteFile(personaPath, personaJson);
            
            CoreHelper.SetVar("persona_json", personaJson);
            
            CoreHelper.Log(TAG, "每日更新完成");
            CoreHelper.SetVar("daily_result", "SUCCESS");
            return "SUCCESS";
        }
        catch (Exception ex)
        {
            CoreHelper.LogErr(TAG, "异常: " + ex.Message);
            CoreHelper.SetVar("last_error", ex.Message);
            CoreHelper.SetVar("daily_result", "ERROR");
            return "ERROR: " + ex.Message;
        }
    }
}
