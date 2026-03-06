// =====================================================
// RateLimiter.cs - 通用速率限制模块
// ⚠️ C# 5.0 语法 - 禁止使用 $""、?.、nameof() 等
// v4.5.1 - 2026-02-27 修复路径获取并集成 CoreHelper
// =====================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

public static class RateLimiter
{
    private const string TAG = "RateLimiter";
    private const string LOG_SUBDIR = "Logs\\rate_limits";

    /// <summary>
    /// 核心入口方法
    /// </summary>
    public static string Run(object projectObj)
    {
        CoreHelper.Init(projectObj);
        return "READY";
    }

    /// <summary>
    /// 检查是否触发速率限制 (基于 manifest 配置)
    /// </summary>
    public static bool CheckRateLimit(string appId, string operation, string platformConfigJson)
    {
        try {
            // 1. 获取配置 (platformConfig -> rate_limits)
            string rateLimitsJson = JsonHelper.ExtractObject(platformConfigJson, "rate_limits");
            if (string.IsNullOrEmpty(rateLimitsJson)) return true;

            // 2. 根据操作类型选择字段
            int maxPerHour = 0;
            if (operation == "like")
            {
                maxPerHour = JsonHelper.GetInt(rateLimitsJson, "max_likes_per_hour", 0);
            }
            else if (operation == "comment")
            {
                maxPerHour = JsonHelper.GetInt(rateLimitsJson, "max_comments_per_hour", 0);
            }
            else if (operation == "follow")
            {
                maxPerHour = JsonHelper.GetInt(rateLimitsJson, "max_follows_per_hour", 0);
            }
            else
            {
                maxPerHour = JsonHelper.GetInt(rateLimitsJson, "max_actions_per_hour", 0);
            }

            // 3. 检查每小时最大操作数
            if (maxPerHour > 0) {
                int hourCount = GetOperationCount(appId, operation, TimeSpan.FromHours(1));
                if (hourCount >= maxPerHour) {
                    CoreHelper.LogWarn(TAG, string.Format("[{0}] {1} 触发每小时限流: {2}/{3}", appId, operation, hourCount, maxPerHour));
                    return false;
                }
            }

            // 4. 检查冷却时间 (使用 min_action_delay_ms 转秒)
            int minDelayMs = JsonHelper.GetInt(rateLimitsJson, "min_action_delay_ms", 0);
            if (minDelayMs > 0) {
                int cooldownSeconds = minDelayMs / 1000;
                DateTime lastTime = GetLastOperationTime(appId, operation);
                if (lastTime != DateTime.MinValue) {
                    double elapsedSeconds = (DateTime.Now - lastTime).TotalSeconds;
                    if (elapsedSeconds < cooldownSeconds) {
                        CoreHelper.LogWarn(TAG, string.Format("[{0}] {1} 仍在冷却中: {2:F1}/{3}s", appId, operation, elapsedSeconds, cooldownSeconds));
                        return false;
                    }
                }
            }
            return true;
        } catch (Exception ex) {
            CoreHelper.LogErr(TAG, "CheckRateLimit 异常: " + ex.Message);
            return true; // 异常时放行，不阻断业务
        }
    }

    /// <summary>
    /// 增加操作计数 (原子追加到文件)
    /// </summary>
    public static void IncrementRateLimit(string appId, string operation)
    {
        try {
            string logPath = GetLogPath(appId, operation);
            if (string.IsNullOrEmpty(logPath)) return;

            string dir = Path.GetDirectoryName(logPath);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            File.AppendAllText(logPath, timestamp + Environment.NewLine);
        } catch (Exception ex) {
            CoreHelper.LogErr(TAG, "IncrementRateLimit 异常: " + ex.Message);
        }
    }

    /// <summary>
    /// 获取指定时间段内的操作计数
    /// </summary>
    public static int GetOperationCount(string appId, string operation, TimeSpan period)
    {
        try {
            string logPath = GetLogPath(appId, operation);
            if (!File.Exists(logPath)) return 0;

            DateTime cutoff = DateTime.Now - period;
            int count = 0;

            using (StreamReader reader = new StreamReader(logPath)) {
                string line;
                while ((line = reader.ReadLine()) != null) {
                    DateTime dt;
                    if (DateTime.TryParse(line, out dt)) {
                        if (dt > cutoff) count++;
                    }
                }
            }
            return count;
        } catch (Exception) {
            return 0;
        }
    }

    /// <summary>
    /// 获取最后一次操作的时间戳
    /// </summary>
    public static DateTime GetLastOperationTime(string appId, string operation)
    {
        try {
            string logPath = GetLogPath(appId, operation);
            if (!File.Exists(logPath)) return DateTime.MinValue;

            string[] lines = File.ReadAllLines(logPath);
            if (lines == null || lines.Length == 0) return DateTime.MinValue;

            DateTime lastTime;
            if (DateTime.TryParse(lines[lines.Length - 1], out lastTime)) {
                return lastTime;
            }
            return DateTime.MinValue;
        } catch (Exception) {
            return DateTime.MinValue;
        }
    }

    /// <summary>
    /// 计算日志路径 (集成 project_root)
    /// </summary>
    private static string GetLogPath(string appId, string operation)
    {
        string projectRoot = CoreHelper.GetVar("project_root", "");
        if (string.IsNullOrEmpty(projectRoot)) return null;
        
        if (!projectRoot.EndsWith("\\")) projectRoot += "\\";
        return Path.Combine(projectRoot + LOG_SUBDIR, string.Format("{0}_{1}.log", appId, operation));
    }
}
