// =====================================================
// Main.cs - 主项目入口模块
// ⚠️ C# 5.0 语法 - 禁止使用 $""、?.、nameof() 等
// ⚠️ 支持 force_regenerate 强制重新生成所有数据
// =====================================================
using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// 主项目入口
/// 负责加载配置、检查画像、生成会话计划
/// </summary>
public class Main
{
    private static dynamic _project;
    private const string TAG = "Main";
    
    /// <summary>
    /// 模块入口点
    /// </summary>
    public static string Run(object projectObj)
    {
        _project = projectObj;
        
        try
        {
            CoreHelper.Init(projectObj);
            
            // 读取基础变量
            string projectRoot = CoreHelper.GetVar("project_root", "");
            string deviceId = CoreHelper.GetVar("device_id", "");
            
            // 读取强制重新生成标志
            string forceRegen = CoreHelper.GetVar("force_regenerate", "false").ToLower();
            bool forceRegenerate = (forceRegen == "true" || forceRegen == "1" || forceRegen == "yes");
            
            if (string.IsNullOrEmpty(projectRoot) || string.IsNullOrEmpty(deviceId))
            {
                CoreHelper.LogErr(TAG, "project_root 或 device_id 未设置");
                CoreHelper.SetVar("main_result", "ERROR");
                return "ERROR: 变量未设置";
            }
            
            projectRoot = CoreHelper.NormalizePath(projectRoot);
            
            CoreHelper.Log(TAG, "========================================");
            CoreHelper.Log(TAG, "  DPS v4.5 - 会话开始");
            CoreHelper.Log(TAG, "  设备: " + deviceId);
            CoreHelper.Log(TAG, "  时间: " + CoreHelper.GetNow());
            if (forceRegenerate)
            {
                CoreHelper.Log(TAG, "  ⚠️ 强制重新生成模式已启用");
            }
            CoreHelper.Log(TAG, "========================================");
            
            // 强制重新生成：清理所有运行时数据
            if (forceRegenerate)
            {
                ClearRuntimeData(projectRoot, deviceId);
            }
            
            // 加载配置文件
            string aiConfigPath = projectRoot + "Config\\AIConfig.json";
            string stageConfigPath = projectRoot + "Config\\StageConfig.json";
            string behaviorConfigPath = projectRoot + "Config\\BehaviorConfig.json";
            
            if (!File.Exists(aiConfigPath))
            {
                CoreHelper.LogErr(TAG, "配置文件不存在: " + aiConfigPath);
                CoreHelper.SetVar("main_result", "ERROR");
                return "ERROR: 配置文件缺失";
            }
            
            string aiConfigJson = CoreHelper.ReadFile(aiConfigPath);
            string stageConfigJson = CoreHelper.ReadFile(stageConfigPath);
            string behaviorConfigJson = CoreHelper.ReadFile(behaviorConfigPath);
            
            CoreHelper.SetVar("ai_config_json", aiConfigJson);
            CoreHelper.SetVar("stage_config_json", stageConfigJson);
            CoreHelper.SetVar("behavior_config_json", behaviorConfigJson);
            
            // 清空缓存变量（强制从文件重新读取）
            if (forceRegenerate)
            {
                CoreHelper.SetVar("persona_json", "");
                CoreHelper.SetVar("session_plan_json", "");
            }
            
            CoreHelper.Log(TAG, "[配置] 已加载所有配置文件");
            
            // 加载或创建画像
            string personaPath = projectRoot + "Persons\\" + deviceId + ".json";
            string personaJson = "";
            
            if (File.Exists(personaPath))
            {
                personaJson = CoreHelper.ReadFile(personaPath);
                CoreHelper.Log(TAG, "[画像] 已加载现有画像");
            }
            else
            {
                CoreHelper.Log(TAG, "[画像] 画像不存在，需要先运行 PersonaCreate");
                CoreHelper.SetVar("main_result", "NEED_CREATE_PERSONA");
                return "NEED_CREATE_PERSONA";
            }
            
            CoreHelper.SetVar("persona_json", personaJson);
            
            // 每日更新检查
            string today = CoreHelper.GetToday();
            
            // 使用 JsonHelper 提取嵌套字段 _meta.last_updated
            string metaJson = JsonHelper.ExtractObject(personaJson, "_meta");
            string lastUpdated = "";
            if (!string.IsNullOrEmpty(metaJson))
            {
                lastUpdated = JsonHelper.Get(metaJson, "last_updated");
                if (lastUpdated == null) lastUpdated = "";
            }
            
            CoreHelper.Log(TAG, "[检查] last_updated=" + lastUpdated + ", today=" + today);
            
            // 强制重新生成模式：总是执行每日更新
            if (forceRegenerate || lastUpdated != today)
            {
                CoreHelper.Log(TAG, "[更新] 需要执行每日更新");
                CoreHelper.SetVar("effective_date", today);
                CoreHelper.SetVar("main_result", "NEED_DAILY_UPDATE");
                return "NEED_DAILY_UPDATE";
            }
            
            // 生成会话计划
            CoreHelper.Log(TAG, "[会话] 生成会话计划...");
            
            string stageCode = JsonHelper.Get(personaJson, "stage_code");
            if (string.IsNullOrEmpty(stageCode)) stageCode = "T2";
            
            // 从配置获取阶段参数
            int sessionsPerDay = 6;
            int avgSessionMinutes = 12;
            
            var sessionsMatch = Regex.Match(stageConfigJson,
                "\"" + stageCode + "\"[^}]*\"sessions_per_day\"\\s*:\\s*(\\d+)");
            if (sessionsMatch.Success)
            {
                int.TryParse(sessionsMatch.Groups[1].Value, out sessionsPerDay);
            }
            
            var avgMatch = Regex.Match(stageConfigJson,
                "\"" + stageCode + "\"[^}]*\"avg_session_minutes\"\\s*:\\s*(\\d+)");
            if (avgMatch.Success)
            {
                int.TryParse(avgMatch.Groups[1].Value, out avgSessionMinutes);
            }
            
            // 生成随机会话时长
            var rnd = new Random();
            int duration = avgSessionMinutes + rnd.Next(-3, 4);
            if (duration < 3) duration = 3;
            if (duration > 30) duration = 30;
            
            string sessionPlan = "{"
                + "\"created_at\": \"" + CoreHelper.GetNowISO() + "\","
                + "\"stage_code\": \"" + stageCode + "\","
                + "\"session_duration_minutes\": " + duration + ","
                + "\"planned_actions\": [\"browse\", \"read_post\", \"like\"]"
                + "}";
            
            CoreHelper.SetVar("session_plan_json", sessionPlan);
            CoreHelper.Log(TAG, "[会话] 计划时长: " + duration + " 分钟");
            
            CoreHelper.Log(TAG, "========================================");
            CoreHelper.Log(TAG, "  会话准备完成，可以执行 SessionRunner");
            CoreHelper.Log(TAG, "========================================");
            
            // 重置强制重新生成标志
            if (forceRegenerate)
            {
                CoreHelper.SetVar("force_regenerate", "false");
                CoreHelper.Log(TAG, "[升级] 已重置 force_regenerate 标志");
            }
            
            CoreHelper.SetVar("main_result", "READY");
            return "READY";
        }
        catch (Exception ex)
        {
            CoreHelper.LogErr(TAG, "异常: " + ex.Message);
            CoreHelper.SetVar("last_error", ex.Message);
            CoreHelper.SetVar("main_result", "ERROR");
            return "ERROR: " + ex.Message;
        }
    }
    
    /// <summary>
    /// 清理所有运行时生成的数据
    /// </summary>
    private static void ClearRuntimeData(string projectRoot, string deviceId)
    {
        CoreHelper.Log(TAG, "[升级] 开始清理运行时数据...");
        
        int backupCount = 0;
        int deleteCount = 0;
        string today = CoreHelper.GetToday();
        
        // 创建升级备份目录
        string upgradeBackupDir = projectRoot + "Backups\\Upgrade_" + today;
        CoreHelper.EnsureDir(upgradeBackupDir);
        
        // 1. 备份并删除画像
        string personaPath = projectRoot + "Persons\\" + deviceId + ".json";
        if (File.Exists(personaPath))
        {
            string backupPath = upgradeBackupDir + "\\persona_" + deviceId + ".json";
            File.Copy(personaPath, backupPath, true);
            File.Delete(personaPath);
            backupCount++;
            deleteCount++;
            CoreHelper.Log(TAG, "[升级] 画像已备份并删除");
        }
        
        // 2. 备份并清空记忆目录
        string memoryDir = projectRoot + "Memory\\" + deviceId;
        if (Directory.Exists(memoryDir))
        {
            string[] memoryFiles = Directory.GetFiles(memoryDir, "*.json");
            if (memoryFiles.Length > 0)
            {
                string memoryBackupDir = upgradeBackupDir + "\\Memory_" + deviceId;
                CoreHelper.EnsureDir(memoryBackupDir);
                
                foreach (string file in memoryFiles)
                {
                    string fileName = Path.GetFileName(file);
                    File.Copy(file, memoryBackupDir + "\\" + fileName, true);
                    File.Delete(file);
                    backupCount++;
                    deleteCount++;
                }
                CoreHelper.Log(TAG, "[升级] 记忆文件已备份并删除: " + memoryFiles.Length + " 个");
            }
        }
        
        // 3. 备份并清空报告目录
        string reportsDir = projectRoot + "Reports\\" + deviceId;
        if (Directory.Exists(reportsDir))
        {
            string[] reportFiles = Directory.GetFiles(reportsDir, "*.json");
            if (reportFiles.Length > 0)
            {
                string reportsBackupDir = upgradeBackupDir + "\\Reports_" + deviceId;
                CoreHelper.EnsureDir(reportsBackupDir);
                
                foreach (string file in reportFiles)
                {
                    string fileName = Path.GetFileName(file);
                    File.Copy(file, reportsBackupDir + "\\" + fileName, true);
                    File.Delete(file);
                    backupCount++;
                    deleteCount++;
                }
                CoreHelper.Log(TAG, "[升级] 报告文件已备份并删除: " + reportFiles.Length + " 个");
            }
        }
        
        CoreHelper.Log(TAG, string.Format("[升级] 清理完成: 备份 {0} 个文件, 删除 {1} 个文件", backupCount, deleteCount));
        CoreHelper.Log(TAG, "[升级] 备份位置: " + upgradeBackupDir);
    }
}
