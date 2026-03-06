// =====================================================
// Maintenance.cs - 系统维护模块
// ⚠️ C# 5.0 语法 - 禁止使用 $""、?.、nameof() 等
// ⚠️ 保留期限从配置读取（如存在）
// =====================================================
using System;
using System.IO;
using System.Text;

/// <summary>
/// 系统维护模块
/// 清理旧数据、压缩记忆、验证配置
/// </summary>
public class Maintenance
{
    private static dynamic _project;
    private const string TAG = "Maintenance";
    
    // 默认保留期限（可被配置覆盖）
    private static int LOG_RETENTION_DAYS = 30;
    private static int MEMORY_RETENTION_DAYS = 180;
    private static int BACKUP_RETENTION_DAYS = 30;
    
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
            
            if (string.IsNullOrEmpty(projectRoot))
            {
                CoreHelper.LogErr(TAG, "project_root 未设置");
                return "ERROR: 变量未设置";
            }
            
            projectRoot = CoreHelper.NormalizePath(projectRoot);
            
            // 尝试读取维护配置
            string maintenanceConfigPath = projectRoot + "Config\\MaintenanceConfig.json";
            if (File.Exists(maintenanceConfigPath))
            {
                string configJson = CoreHelper.ReadFile(maintenanceConfigPath);
                
                // 读取保留期限配置（嵌套在 retention 对象下）
                string retentionJson = JsonHelper.ExtractObject(configJson, "retention");
                if (!string.IsNullOrEmpty(retentionJson))
                {
                    int val;
                    val = JsonHelper.GetInt(retentionJson, "log_retention_days", LOG_RETENTION_DAYS);
                    if (val > 0) LOG_RETENTION_DAYS = val;
                    
                    val = JsonHelper.GetInt(retentionJson, "memory_retention_days", MEMORY_RETENTION_DAYS);
                    if (val > 0) MEMORY_RETENTION_DAYS = val;
                    
                    val = JsonHelper.GetInt(retentionJson, "backup_retention_days", BACKUP_RETENTION_DAYS);
                    if (val > 0) BACKUP_RETENTION_DAYS = val;
                }
                
                CoreHelper.Log(TAG, "已加载维护配置");
            }
            
            CoreHelper.Log(TAG, "开始系统维护");
            CoreHelper.Log(TAG, string.Format("保留期限: 日志{0}天, 记忆{1}天, 备份{2}天", 
                LOG_RETENTION_DAYS, MEMORY_RETENTION_DAYS, BACKUP_RETENTION_DAYS));
            
            int cleanedFiles = 0;
            int compressedFiles = 0;
            
            // 1. 清理旧日志
            string logsDir = projectRoot + "Logs";
            if (Directory.Exists(logsDir))
            {
                string[] logFiles = Directory.GetFiles(logsDir, "*.log");
                DateTime cutoff = DateTime.Now.AddDays(-LOG_RETENTION_DAYS);
                
                foreach (string file in logFiles)
                {
                    try
                    {
                        FileInfo fileInfo = new FileInfo(file);
                        if (fileInfo.LastWriteTime < cutoff)
                        {
                            File.Delete(file);
                            cleanedFiles++;
                        }
                    }
                    catch (Exception ex)
                    {
                        CoreHelper.LogWarn(TAG, "清理日志失败: " + file + " - " + ex.Message);
                    }
                }
            }
            
            CoreHelper.Log(TAG, "清理旧日志: " + cleanedFiles + " 个");
            
            // 2. 压缩旧记忆
            if (!string.IsNullOrEmpty(deviceId))
            {
                string memoryDir = projectRoot + "Memory\\" + deviceId;
                if (Directory.Exists(memoryDir))
                {
                    string[] memoryFiles = Directory.GetFiles(memoryDir, "????-??-??.json");
                    DateTime retentionCutoff = DateTime.Now.AddDays(-MEMORY_RETENTION_DAYS);
                    
                    foreach (string file in memoryFiles)
                    {
                        try
                        {
                            string fileName = Path.GetFileNameWithoutExtension(file);
                            DateTime fileDate;
                            
                            if (DateTime.TryParse(fileName, out fileDate))
                            {
                                if (fileDate < retentionCutoff)
                                {
                                    string content = CoreHelper.ReadFile(file);
                                    int actionCount = CoreHelper.CountOccurrences(content, "\"action_type\"");
                                    
                                    // 追加到长期记忆
                                    string longTermFile = memoryDir + "\\_long_term.txt";
                                    string summary = fileName + ": " + actionCount + " actions\n";
                                    File.AppendAllText(longTermFile, summary, Encoding.UTF8);
                                    
                                    // 删除原文件
                                    File.Delete(file);
                                    compressedFiles++;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            CoreHelper.LogWarn(TAG, "压缩记忆失败: " + file + " - " + ex.Message);
                        }
                    }
                }
                
                CoreHelper.Log(TAG, "压缩旧记忆: " + compressedFiles + " 个");
                
                // 3. 清理旧备份
                string backupDir = projectRoot + "Persons\\Backups\\" + deviceId;
                if (Directory.Exists(backupDir))
                {
                    string[] backupFiles = Directory.GetFiles(backupDir, "*.json");
                    DateTime backupCutoff = DateTime.Now.AddDays(-BACKUP_RETENTION_DAYS);
                    int deletedBackups = 0;
                    
                    foreach (string file in backupFiles)
                    {
                        try
                        {
                            FileInfo fileInfo = new FileInfo(file);
                            if (fileInfo.LastWriteTime < backupCutoff)
                            {
                                File.Delete(file);
                                deletedBackups++;
                            }
                        }
                        catch (Exception ex)
                        {
                            CoreHelper.LogWarn(TAG, "清理备份失败: " + file + " - " + ex.Message);
                        }
                    }
                    
                    CoreHelper.Log(TAG, "清理旧备份: " + deletedBackups + " 个");
                }
            }
            
            // 4. 验证配置完整性
            string[] requiredConfigs = new string[] {
                "Config\\AIConfig.json",
                "Config\\StageConfig.json",
                "Config\\BehaviorConfig.json",
                "Config\\PersonaPrompt.txt"
            };
            
            int missingConfigs = 0;
            foreach (string config in requiredConfigs)
            {
                if (!File.Exists(projectRoot + config))
                {
                    CoreHelper.LogErr(TAG, "缺失配置: " + config);
                    missingConfigs++;
                }
            }
            
            if (missingConfigs == 0)
            {
                CoreHelper.Log(TAG, "配置完整性检查: ✓ 全部通过");
            }
            else
            {
                CoreHelper.LogErr(TAG, "配置完整性检查: " + missingConfigs + " 个缺失");
            }
            
            // 5. 检查磁盘空间
            try
            {
                string driveName = Path.GetPathRoot(projectRoot);
                if (string.IsNullOrEmpty(driveName))
                {
                    driveName = projectRoot.Substring(0, 1);
                }
                DriveInfo driveInfo = new DriveInfo(driveName);
                long freeGB = driveInfo.AvailableFreeSpace / (1024 * 1024 * 1024);
                
                if (freeGB < 1)
                {
                    CoreHelper.LogErr(TAG, "警告: 磁盘空间不足 (" + freeGB + " GB)");
                }
                else
                {
                    CoreHelper.Log(TAG, "磁盘空间: " + freeGB + " GB 可用");
                }
            }
            catch (Exception ex)
            {
                CoreHelper.LogWarn(TAG, "磁盘空间检查失败: " + ex.Message);
            }
            
            CoreHelper.Log(TAG, "系统维护完成");
            
            return "SUCCESS";
        }
        catch (Exception ex)
        {
            CoreHelper.LogErr(TAG, "异常: " + ex.Message);
            CoreHelper.SetVar("last_error", ex.Message);
            return "ERROR: " + ex.Message;
        }
    }
}
