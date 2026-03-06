// =====================================================
// RedditExplorerTest.cs - Reddit APP 自动探索测试脚本
// 用于在 ZennoDroid 中探索 Reddit APP 并生成 manifest
// ⚠️ C# 5.0 语法 - 禁止使用 $""、?.、nameof() 等
// =====================================================
using System;
using System.IO;

/// <summary>
/// Reddit APP 自动探索测试
/// 使用 AppExplorer 自动分析 Reddit APP 的 UI 结构并生成 manifest
/// </summary>
public class RedditExplorerTest
{
    private static dynamic _project;
    private static dynamic _instance;
    private const string TAG = "RedditExplorerTest";
    
    /// <summary>
    /// 主入口点
    /// </summary>
    public static string Run(object projectObj, object instanceObj)
    {
        _project = projectObj;
        _instance = instanceObj;
        
        CoreHelper.Init(projectObj);
        
        string projectRoot = CoreHelper.GetVar("project_root", "");
        if (string.IsNullOrEmpty(projectRoot))
        {
            return "ERROR: project_root 未设置";
        }
        
        CoreHelper.Log(TAG, "========================================");
        CoreHelper.Log(TAG, "  Reddit APP 自动探索测试");
        CoreHelper.Log(TAG, "  使用 AppExplorer 生成 manifest");
        CoreHelper.Log(TAG, "========================================");
        CoreHelper.Log(TAG, "");
        
        try
        {
            // 步骤1: 验证 Reddit APP 已安装
            CoreHelper.Log(TAG, "[步骤1] 验证 Reddit APP 已安装...");
            dynamic droid = _instance.DroidInstance;
            
            string packageName = "com.reddit.frontpage";
            bool isInstalled = CheckAppInstalled(droid, packageName);
            
            if (!isInstalled)
            {
                CoreHelper.LogErr(TAG, "Reddit APP 未安装: " + packageName);
                CoreHelper.LogErr(TAG, "请先安装 Reddit APP 后再运行此测试");
                return "ERROR: Reddit APP 未安装";
            }
            
            CoreHelper.Log(TAG, "✓ Reddit APP 已安装");
            CoreHelper.Log(TAG, "");
            
            // 步骤2: 启动 Reddit APP
            CoreHelper.Log(TAG, "[步骤2] 启动 Reddit APP...");
            droid.StartApp(packageName);
            
            // 等待 APP 完全启动
            CoreHelper.Log(TAG, "等待 APP 启动（10秒）...");
            System.Threading.Thread.Sleep(10000);
            
            // 验证 APP 是否在前台
            string currentPackage = droid.GetCurrentPackage();
            if (currentPackage != packageName)
            {
                CoreHelper.LogWarn(TAG, "当前前台 APP: " + currentPackage);
                CoreHelper.LogWarn(TAG, "预期: " + packageName);
                CoreHelper.LogWarn(TAG, "尝试继续探索...");
            }
            else
            {
                CoreHelper.Log(TAG, "✓ Reddit APP 已启动");
            }
            CoreHelper.Log(TAG, "");
            
            // 步骤3: 运行 AppExplorer
            CoreHelper.Log(TAG, "[步骤3] 运行 AppExplorer 自动探索...");
            string outputPath = projectRoot + "Configs\\Manifests\\reddit_explored.json";
            
            CoreHelper.Log(TAG, "输出路径: " + outputPath);
            CoreHelper.Log(TAG, "开始探索（这可能需要 2-5 分钟）...");
            
            string result = AppExplorer.Explore(_project, _instance, packageName, outputPath);
            
            CoreHelper.Log(TAG, "AppExplorer 结果: " + result);
            CoreHelper.Log(TAG, "");
            
            // 步骤4: 验证生成的 manifest
            CoreHelper.Log(TAG, "[步骤4] 验证生成的 manifest...");
            
            if (!File.Exists(outputPath))
            {
                CoreHelper.LogErr(TAG, "Manifest 文件未生成: " + outputPath);
                return "ERROR: Manifest 文件未生成";
            }
            
            string manifestContent = File.ReadAllText(outputPath);
            CoreHelper.Log(TAG, "Manifest 文件大小: " + manifestContent.Length + " 字节");
            
            // 验证 manifest 包含必需字段
            bool hasScreens = manifestContent.Contains("\"screens\"");
            bool hasNavigation = manifestContent.Contains("\"navigation\"");
            bool hasOperations = manifestContent.Contains("\"operations\"");
            bool hasSelectors = manifestContent.Contains("\"selectors\"");
            
            CoreHelper.Log(TAG, "包含 screens: " + hasScreens);
            CoreHelper.Log(TAG, "包含 navigation: " + hasNavigation);
            CoreHelper.Log(TAG, "包含 operations: " + hasOperations);
            CoreHelper.Log(TAG, "包含 selectors: " + hasSelectors);
            
            if (hasScreens && hasNavigation && hasOperations)
            {
                CoreHelper.Log(TAG, "✓ Manifest 结构完整");
            }
            else
            {
                CoreHelper.LogWarn(TAG, "⚠ Manifest 可能不完整");
            }
            CoreHelper.Log(TAG, "");
            
            // 步骤5: 分析探索结果
            CoreHelper.Log(TAG, "[步骤5] 分析探索结果...");
            AnalyzeManifest(manifestContent);
            CoreHelper.Log(TAG, "");
            
            // 步骤6: 生成对比报告
            CoreHelper.Log(TAG, "[步骤6] 生成对比报告...");
            GenerateComparisonReport(projectRoot, manifestContent);
            CoreHelper.Log(TAG, "");
            
            CoreHelper.Log(TAG, "========================================");
            CoreHelper.Log(TAG, "  Reddit APP 探索完成");
            CoreHelper.Log(TAG, "========================================");
            CoreHelper.Log(TAG, "");
            CoreHelper.Log(TAG, "生成的文件:");
            CoreHelper.Log(TAG, "1. " + outputPath);
            CoreHelper.Log(TAG, "2. " + projectRoot + "Reports\\reddit_exploration_report.txt");
            CoreHelper.Log(TAG, "");
            CoreHelper.Log(TAG, "下一步:");
            CoreHelper.Log(TAG, "1. 查看生成的 manifest 文件");
            CoreHelper.Log(TAG, "2. 对比 reddit.json 和 reddit_explored.json");
            CoreHelper.Log(TAG, "3. 合并有用的发现到 reddit.json");
            CoreHelper.Log(TAG, "4. 测试 Reddit 操作流程");
            
            CoreHelper.SetVar("reddit_exploration_result", "SUCCESS");
            return "SUCCESS: Reddit APP 探索完成";
        }
        catch (Exception ex)
        {
            CoreHelper.LogErr(TAG, "异常: " + ex.Message);
            CoreHelper.LogErr(TAG, "堆栈跟踪: " + ex.StackTrace);
            CoreHelper.SetVar("reddit_exploration_result", "ERROR");
            return "ERROR: " + ex.Message;
        }
    }
    
    /// <summary>
    /// 检查 APP 是否已安装
    /// </summary>
    private static bool CheckAppInstalled(dynamic droid, string packageName)
    {
        try
        {
            // 尝试获取 APP 信息
            string appInfo = droid.GetAppInfo(packageName);
            return !string.IsNullOrEmpty(appInfo);
        }
        catch
        {
            // 如果获取失败，尝试启动 APP
            try
            {
                droid.StartApp(packageName);
                System.Threading.Thread.Sleep(2000);
                string currentPackage = droid.GetCurrentPackage();
                return currentPackage == packageName;
            }
            catch
            {
                return false;
            }
        }
    }
    
    /// <summary>
    /// 分析 manifest 内容
    /// </summary>
    private static void AnalyzeManifest(string manifestContent)
    {
        try
        {
            // 提取 screens 数组
            string screensJson = JsonHelper.ExtractArray(manifestContent, "screens");
            if (!string.IsNullOrEmpty(screensJson))
            {
                // 简单计数（通过查找 "id" 字段出现次数）
                int screenCount = CountOccurrences(screensJson, "\"id\"");
                CoreHelper.Log(TAG, "发现屏幕数量: " + screenCount);
            }
            
            // 提取 navigation.edges 数组
            string navigationJson = JsonHelper.ExtractObject(manifestContent, "navigation");
            if (!string.IsNullOrEmpty(navigationJson))
            {
                string edgesJson = JsonHelper.ExtractArray(navigationJson, "edges");
                if (!string.IsNullOrEmpty(edgesJson))
                {
                    int edgeCount = CountOccurrences(edgesJson, "\"from\"");
                    CoreHelper.Log(TAG, "发现导航边数量: " + edgeCount);
                }
            }
            
            // 提取 operations 对象
            string operationsJson = JsonHelper.ExtractObject(manifestContent, "operations");
            if (!string.IsNullOrEmpty(operationsJson))
            {
                int operationCount = CountOccurrences(operationsJson, "\"description\"");
                CoreHelper.Log(TAG, "发现操作数量: " + operationCount);
            }
        }
        catch (Exception ex)
        {
            CoreHelper.LogWarn(TAG, "分析 manifest 失败: " + ex.Message);
        }
    }
    
    /// <summary>
    /// 计算字符串出现次数
    /// </summary>
    private static int CountOccurrences(string text, string pattern)
    {
        int count = 0;
        int index = 0;
        
        while ((index = text.IndexOf(pattern, index)) != -1)
        {
            count++;
            index += pattern.Length;
        }
        
        return count;
    }
    
    /// <summary>
    /// 生成对比报告
    /// </summary>
    private static void GenerateComparisonReport(string projectRoot, string exploredManifest)
    {
        try
        {
            string reportPath = projectRoot + "Reports\\reddit_exploration_report.txt";
            string reportDir = Path.GetDirectoryName(reportPath);
            
            if (!Directory.Exists(reportDir))
            {
                Directory.CreateDirectory(reportDir);
            }
            
            string report = "Reddit APP 探索报告\n";
            report += "生成时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\n";
            report += "========================================\n\n";
            
            report += "## 探索结果\n\n";
            
            // 分析探索的 manifest
            report += "### 自动探索生成的 manifest\n";
            report += "文件大小: " + exploredManifest.Length + " 字节\n";
            
            string screensJson = JsonHelper.ExtractArray(exploredManifest, "screens");
            if (!string.IsNullOrEmpty(screensJson))
            {
                int screenCount = CountOccurrences(screensJson, "\"id\"");
                report += "发现屏幕: " + screenCount + " 个\n";
            }
            
            string navigationJson = JsonHelper.ExtractObject(exploredManifest, "navigation");
            if (!string.IsNullOrEmpty(navigationJson))
            {
                string edgesJson = JsonHelper.ExtractArray(navigationJson, "edges");
                if (!string.IsNullOrEmpty(edgesJson))
                {
                    int edgeCount = CountOccurrences(edgesJson, "\"from\"");
                    report += "发现导航边: " + edgeCount + " 条\n";
                }
            }
            
            report += "\n";
            
            // 对比现有的 reddit.json
            string existingManifestPath = projectRoot + "Configs\\Manifests\\reddit.json";
            if (File.Exists(existingManifestPath))
            {
                report += "### 现有的 reddit.json\n";
                string existingManifest = File.ReadAllText(existingManifestPath);
                report += "文件大小: " + existingManifest.Length + " 字节\n";
                
                string existingScreensJson = JsonHelper.ExtractArray(existingManifest, "screens");
                if (!string.IsNullOrEmpty(existingScreensJson))
                {
                    int screenCount = CountOccurrences(existingScreensJson, "\"id\"");
                    report += "定义屏幕: " + screenCount + " 个\n";
                }
                
                report += "\n";
                report += "### 建议\n";
                report += "1. 对比两个文件，查看自动探索是否发现了新的屏幕或导航路径\n";
                report += "2. 将有用的发现合并到 reddit.json\n";
                report += "3. 验证自动生成的选择器是否准确\n";
                report += "4. 测试导航路径是否可达\n";
            }
            else
            {
                report += "### 注意\n";
                report += "现有的 reddit.json 不存在，可以直接使用探索生成的 manifest\n";
            }
            
            report += "\n========================================\n";
            
            File.WriteAllText(reportPath, report);
            CoreHelper.Log(TAG, "对比报告已保存: " + reportPath);
        }
        catch (Exception ex)
        {
            CoreHelper.LogErr(TAG, "生成对比报告失败: " + ex.Message);
        }
    }
}
