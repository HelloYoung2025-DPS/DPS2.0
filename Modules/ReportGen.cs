// =====================================================
// ReportGen.cs - 报告生成模块
// ⚠️ C# 5.0 语法 - 禁止使用 $""、?.、nameof() 等
// v4.0.1 - 修复文件名一致性，使用 CoreHelper.CountOccurrences
// =====================================================
using System;
using System.IO;
using System.Text;

/// <summary>
/// 报告生成模块
/// 生成统计报告和 CSV 导出
/// </summary>
public class ReportGen
{
    private static dynamic _project;
    private const string TAG = "ReportGen";
    
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
            
            if (string.IsNullOrEmpty(projectRoot) || string.IsNullOrEmpty(deviceId))
            {
                CoreHelper.LogErr(TAG, "project_root 或 device_id 未设置");
                return "ERROR: 变量未设置";
            }
            
            if (!CoreHelper.ValidateDeviceId(deviceId))
            {
                CoreHelper.LogErr(TAG, "device_id 包含非法字符");
                return "ERROR: device_id 无效";
            }
            
            projectRoot = CoreHelper.NormalizePath(projectRoot);
            string today = CoreHelper.GetToday();
            
            int currentHour = DateTime.Now.Hour;
            if (currentHour < 17)
            {
                CoreHelper.Log(TAG, "当前时间 " + currentHour + ":00，未到报告生成时间（17:00后）");
                CoreHelper.SetVar("report_result", "SKIPPED");
                return "SKIPPED: 未到报告生成时间";
            }
            
            string reportDir = projectRoot + "Reports\\" + deviceId;
            CoreHelper.EnsureDir(reportDir);
            string todayReportPath = reportDir + "\\" + today + "_weekly.json";
            
            if (File.Exists(todayReportPath))
            {
                CoreHelper.Log(TAG, "今日报告已存在，跳过生成");
                CoreHelper.SetVar("report_result", "SKIPPED");
                return "SKIPPED: 今日报告已存在";
            }
            
            CoreHelper.Log(TAG, "生成报告 - " + today);
            
            string memoryDir = projectRoot + "Memory\\" + deviceId;
            
            int totalActions = 0;
            int browseCount = 0;
            int readCount = 0;
            int likeCount = 0;
            int commentCount = 0;
            int postCount = 0;
            int activeDays = 0;
            
            for (int i = 0; i < 7; i++)
            {
                string dateStr = DateTime.Now.AddDays(-i).ToString("yyyy-MM-dd");
                string memoryFile = memoryDir + "\\" + dateStr + ".json";
                
                if (File.Exists(memoryFile))
                {
                    activeDays++;
                    string content = CoreHelper.ReadFile(memoryFile);
                    
                    browseCount += CountAction(content, "browse");
                    readCount += CountAction(content, "read_post");
                    likeCount += CountAction(content, "like");
                    commentCount += CountAction(content, "comment");
                    postCount += CountAction(content, "post");
                    totalActions += CoreHelper.CountOccurrences(content, "\"action_type\"");
                }
            }
            
            // v4.5.1: 补充 MemoryManager 结构化交互数据
            // TODO: 当 MemoryManager 支持按日期范围查询时，替换为更精确的查询
            string currentPlatform = CoreHelper.GetVar("current_platform", "reddit");
            int mmInteractions = 0;
            string mmMemoryPath = projectRoot + "Memory\\" + deviceId + "\\" + currentPlatform + "\\interactions.json";
            if (File.Exists(mmMemoryPath))
            {
                string mmContent = CoreHelper.ReadFile(mmMemoryPath);
                mmInteractions = CoreHelper.CountOccurrences(mmContent, "\"action\"");
                CoreHelper.Log(TAG, string.Format("MemoryManager 交互记录数: {0}", mmInteractions));
            }
            
            int avgPerDay = activeDays > 0 ? totalActions / activeDays : 0;
            
            string report = "{"
                + "\"report_type\": \"weekly\","
                + "\"device_id\": \"" + deviceId + "\","
                + "\"generated_at\": \"" + CoreHelper.GetNowISO() + "\","
                + "\"period\": \"past_7_days\","
                + "\"summary\": {"
                    + "\"total_actions\": " + totalActions + ","
                    + "\"active_days\": " + activeDays + ","
                    + "\"avg_actions_per_day\": " + avgPerDay
                + "},"
                + "\"actions\": {"
                    + "\"browse\": " + browseCount + ","
                    + "\"read_post\": " + readCount + ","
                    + "\"like\": " + likeCount + ","
                    + "\"comment\": " + commentCount + ","
                    + "\"post\": " + postCount
                + "},"
                + "\"memory_manager\": {"
                    + "\"total_interactions\": " + mmInteractions
                + "}"
            + "}";
            
            CoreHelper.WriteFile(todayReportPath, report);
            
            CoreHelper.Log(TAG, "报告已保存: " + todayReportPath);
            CoreHelper.Log(TAG, string.Format("统计: 总动作 {0}, 活跃天数 {1}", totalActions, activeDays));
            
            string csvDir = reportDir + "\\csv";
            CoreHelper.EnsureDir(csvDir);
            
            StringBuilder csv = new StringBuilder();
            csv.AppendLine("Metric,Value");
            csv.AppendLine("TotalActions," + totalActions);
            csv.AppendLine("ActiveDays," + activeDays);
            csv.AppendLine("AvgPerDay," + avgPerDay);
            csv.AppendLine("Browse," + browseCount);
            csv.AppendLine("Read," + readCount);
            csv.AppendLine("Like," + likeCount);
            csv.AppendLine("Comment," + commentCount);
            csv.AppendLine("Post," + postCount);
            
            string csvFile = csvDir + "\\" + today + "_summary.csv";
            CoreHelper.WriteFile(csvFile, csv.ToString());
            
            CoreHelper.Log(TAG, "CSV已保存: " + csvFile);
            
            CoreHelper.SetVar("report_result", "SUCCESS");
            return "SUCCESS";
        }
        catch (Exception ex)
        {
            CoreHelper.LogErr(TAG, "异常: " + ex.Message);
            CoreHelper.SetVar("last_error", ex.Message);
            CoreHelper.SetVar("report_result", "ERROR");
            return "ERROR: " + ex.Message;
        }
    }
    
    /// <summary>
    /// 计算特定动作出现次数
    /// </summary>
    private static int CountAction(string content, string actionType)
    {
        return CoreHelper.CountOccurrences(content, "\"action_type\":\"" + actionType + "\"")
             + CoreHelper.CountOccurrences(content, "\"action_type\": \"" + actionType + "\"");
    }
}
