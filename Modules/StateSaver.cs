// =====================================================
// StateSaver.cs - 状态保存模块
// ⚠️ C# 5.0 语法 - 禁止使用 $""、?.、nameof() 等
// ⚠️ 保存画像和统计数据（记忆由 SessionRunner 保存）
// v4.0.1 - 移除未使用的 SaveMemory 方法
// =====================================================
using System;
using System.IO;
using System.Text;

/// <summary>
/// 状态保存模块
/// 负责保存画像和运行统计
/// </summary>
public class StateSaver
{
    private static dynamic _project;
    private const string TAG = "StateSaver";
    
    /// <summary>
    /// 模块入口点
    /// </summary>
    public static string Run(object projectObj)
    {
        _project = projectObj;
        
        try
        {
            CoreHelper.Init(projectObj);
            
            CoreHelper.Log(TAG, "========================================");
            CoreHelper.Log(TAG, "  保存状态");
            CoreHelper.Log(TAG, "========================================");
            
            string projectRoot = CoreHelper.GetVar("project_root", "");
            string deviceId = CoreHelper.GetVar("device_id", "");
            
            if (string.IsNullOrEmpty(projectRoot) || string.IsNullOrEmpty(deviceId))
            {
                CoreHelper.LogErr(TAG, "project_root 或 device_id 未设置");
                CoreHelper.SetVar("state_result", "ERROR");
                return "ERROR: 必需变量未设置";
            }
            
            if (!CoreHelper.ValidateDeviceId(deviceId))
            {
                CoreHelper.LogErr(TAG, "device_id 包含非法字符");
                CoreHelper.SetVar("state_result", "ERROR");
                return "ERROR: device_id 无效";
            }
            
            projectRoot = CoreHelper.NormalizePath(projectRoot);
            string today = CoreHelper.GetToday();
            int savedCount = 0;
            
            string personaJson = CoreHelper.GetVar("persona_json", "");
            if (!string.IsNullOrEmpty(personaJson))
            {
                SavePersona(projectRoot, deviceId, personaJson);
                savedCount++;
            }
            
            CoreHelper.Log(TAG, "[记忆] 由 SessionRunner 已保存，跳过");
            
            UpdateStatistics(projectRoot, deviceId, today);
            savedCount++;
            
            CoreHelper.Log(TAG, "状态保存完成，共保存 " + savedCount + " 项");
            CoreHelper.SetVar("state_result", "SUCCESS");
            return "SUCCESS";
        }
        catch (Exception ex)
        {
            CoreHelper.LogErr(TAG, "异常: " + ex.Message);
            CoreHelper.SetVar("last_error", ex.Message);
            CoreHelper.SetVar("state_result", "ERROR");
            return "ERROR: " + ex.Message;
        }
    }
    
    /// <summary>
    /// 保存画像到文件
    /// </summary>
    private static void SavePersona(string projectRoot, string deviceId, string personaJson)
    {
        try
        {
            string personaDir = projectRoot + "Persons";
            CoreHelper.EnsureDir(personaDir);
            
            string personaPath = personaDir + "\\" + deviceId + ".json";
            CoreHelper.WriteFile(personaPath, personaJson);
            
            CoreHelper.Log(TAG, "[画像] 已保存: " + deviceId + ".json");
        }
        catch (Exception ex)
        {
            CoreHelper.LogErr(TAG, "[画像] 保存失败: " + ex.Message);
        }
    }
    
    /// <summary>
    /// 更新运行统计
    /// </summary>
    private static void UpdateStatistics(string projectRoot, string deviceId, string date)
    {
        try
        {
            string statsDir = projectRoot + "Stats";
            CoreHelper.EnsureDir(statsDir);
            
            string statsPath = statsDir + "\\" + deviceId + "_stats.json";
            
            string statsJson = "";
            int totalRuns = 0;
            int totalActions = 0;
            
            if (File.Exists(statsPath))
            {
                statsJson = CoreHelper.ReadFile(statsPath);
                string totalRunsStr = CoreHelper.JGet(statsJson, "total_runs");
                string totalActionsStr = CoreHelper.JGet(statsJson, "total_actions");
                
                if (!string.IsNullOrEmpty(totalRunsStr))
                {
                    int.TryParse(totalRunsStr, out totalRuns);
                }
                if (!string.IsNullOrEmpty(totalActionsStr))
                {
                    int.TryParse(totalActionsStr, out totalActions);
                }
            }
            
            totalRuns++;
            
            string actionCountStr = CoreHelper.GetVar("action_count", "");
            int actionCount = 0;
            if (!string.IsNullOrEmpty(actionCountStr))
            {
                int.TryParse(actionCountStr, out actionCount);
            }
            totalActions += actionCount;
            
            StringBuilder sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"device_id\": \"" + deviceId + "\",\n");
            sb.Append("  \"total_runs\": " + totalRuns + ",\n");
            sb.Append("  \"total_actions\": " + totalActions + ",\n");
            sb.Append("  \"last_run_date\": \"" + date + "\",\n");
            sb.Append("  \"last_updated\": \"" + DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss") + "\"\n");
            sb.Append("}");
            
            CoreHelper.WriteFile(statsPath, sb.ToString());
            
            CoreHelper.Log(TAG, "[统计] 已更新: 总运行 " + totalRuns + " 次, 总操作 " + totalActions + " 次");
        }
        catch (Exception ex)
        {
            CoreHelper.LogErr(TAG, "[统计] 更新失败: " + ex.Message);
        }
    }
}
