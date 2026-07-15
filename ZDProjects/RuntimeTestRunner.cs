// =====================================================
// RuntimeTestRunner.cs - DPS v4.5 运行时测试脚本
// 用于在 ZennoDroid 中执行完整的运行时测试
// ⚠️ C# 5.0 语法 - 禁止使用 $""、?.、nameof() 等
// =====================================================
using System;
using System.IO;
using System.Collections.Generic;

/// <summary>
/// DPS v4.5 Universal Framework 运行时测试执行器
/// 在 Android 模拟器环境中验证所有修复和新功能
/// </summary>
public class RuntimeTestRunner
{
    private static dynamic _project;
    private static dynamic _instance;
    private const string TAG = "RuntimeTestRunner";
    
    /// <summary>
    /// 测试结果记录
    /// </summary>
    private static List<string> _testResults = new List<string>();
    
    /// <summary>
    /// 主入口点
    /// </summary>
    public static string Run(object projectObj, object instanceObj)
    {
        _project = projectObj;
        _instance = instanceObj;
        _testResults.Clear();

        try
        {
            CoreHelper.Init(projectObj);
        }
        catch (Exception ex)
        {
            return "FAILED: INFRA_ERROR - CoreHelper 初始化失败: " + ex.Message;
        }

        string projectRoot = CoreHelper.GetVar("project_root", "");
        if (string.IsNullOrEmpty(projectRoot))
        {
            CoreHelper.SetVar("test_result", "NOT_RUN");
            CoreHelper.SetVar("test_evidence_result", "NOT_RUN");
            return "FAILED: NOT_RUN - project_root 未设置";
        }
        
        CoreHelper.Log(TAG, "========================================");
        CoreHelper.Log(TAG, "  DPS v4.5 运行时测试套件");
        CoreHelper.Log(TAG, "  测试环境: Android 模拟器 + ZennoDroid");
        CoreHelper.Log(TAG, "========================================");
        CoreHelper.Log(TAG, "");
        
        int passed = 0;
        int failed = 0;
        
        // 测试1: Android 模拟器连接
        if (Test1_AndroidEmulatorConnection())
        {
            passed++;
            RecordResult("✅ 测试1: Android 模拟器连接 - PASS");
        }
        else
        {
            failed++;
            RecordResult("❌ 测试1: Android 模拟器连接 - FAIL");
        }
        
        // 测试2: 项目初始化
        if (Test2_ProjectInitialization(projectRoot))
        {
            passed++;
            RecordResult("✅ 测试2: 项目初始化 - PASS");
        }
        else
        {
            failed++;
            RecordResult("❌ 测试2: 项目初始化 - FAIL");
        }
        
        // 测试3: AppExplorer Instagram 探索
        if (Test3_AppExplorerInstagram(projectRoot))
        {
            passed++;
            RecordResult("✅ 测试3: AppExplorer Instagram 探索 - PASS");
        }
        else
        {
            failed++;
            RecordResult("❌ 测试3: AppExplorer Instagram 探索 - FAIL");
        }
        
        // 测试4: Instagram 导航路径验证
        if (Test4_InstagramNavigation(projectRoot))
        {
            passed++;
            RecordResult("✅ 测试4: Instagram 导航路径验证 - PASS");
        }
        else
        {
            failed++;
            RecordResult("❌ 测试4: Instagram 导航路径验证 - FAIL");
        }
        
        // 测试5: 速率限制验证
        if (Test5_RateLimitVerification(projectRoot))
        {
            passed++;
            RecordResult("✅ 测试5: 速率限制验证 - PASS");
        }
        else
        {
            failed++;
            RecordResult("❌ 测试5: 速率限制验证 - FAIL");
        }
        
        // 测试6: VisionCorrector 视觉修正
        if (Test6_VisionCorrector(projectRoot))
        {
            passed++;
            RecordResult("✅ 测试6: VisionCorrector 视觉修正 - PASS");
        }
        else
        {
            failed++;
            RecordResult("❌ 测试6: VisionCorrector 视觉修正 - FAIL");
        }
        
        // 输出测试结果
        CoreHelper.Log(TAG, "");
        CoreHelper.Log(TAG, "========================================");
        CoreHelper.Log(TAG, "  测试结果汇总");
        CoreHelper.Log(TAG, "========================================");
        
        foreach (string result in _testResults)
        {
            CoreHelper.Log(TAG, result);
        }
        
        CoreHelper.Log(TAG, "");
        CoreHelper.Log(TAG, string.Format("总计: {0} 通过, {1} 失败", passed, failed));
        CoreHelper.Log(TAG, "========================================");
        
        // 保存测试报告。报告缺失属于基础设施失败，不能由日志中的 PASS 覆盖。
        bool reportSaved = SaveTestReport(projectRoot, passed, failed);

        if (!reportSaved)
        {
            CoreHelper.SetVar("test_result", "INFRA_ERROR");
            CoreHelper.SetVar("test_evidence_result", "INFRA_ERROR");
            return "FAILED: INFRA_ERROR - 运行时测试报告未保存";
        }

        if (passed + failed == 0)
        {
            CoreHelper.SetVar("test_result", "NOT_RUN");
            CoreHelper.SetVar("test_evidence_result", "NOT_RUN");
            return "FAILED: NOT_RUN - 没有执行任何测试";
        }

        if (failed == 0 && passed > 0)
        {
            CoreHelper.SetVar("test_result", "ALL_PASS");
            CoreHelper.SetVar("test_evidence_result", "PASS");
            return "SUCCESS: 所有测试通过";
        }

        string evidenceResult = passed > 0 ? "PARTIAL" : "FAIL";
        CoreHelper.SetVar("test_result", evidenceResult);
        CoreHelper.SetVar("test_evidence_result", evidenceResult);
        return string.Format("FAILED: {0} - {1} 通过, {2} 失败", evidenceResult, passed, failed);
    }
    
    /// <summary>
    /// 测试1: Android 模拟器连接
    /// </summary>
    private static bool Test1_AndroidEmulatorConnection()
    {
        CoreHelper.Log(TAG, "[测试1] 验证 Android 模拟器连接...");
        
        try
        {
            dynamic droid = _instance.DroidInstance;
            if (droid == null)
            {
                CoreHelper.LogErr(TAG, "[测试1] DroidInstance 为 null");
                return false;
            }
            
            // 获取设备信息
            string deviceInfo = droid.GetDeviceInfo();
            CoreHelper.Log(TAG, "[测试1] 设备信息: " + deviceInfo);
            
            // 获取屏幕分辨率
            int width = droid.Screen.Width;
            int height = droid.Screen.Height;
            CoreHelper.Log(TAG, string.Format("[测试1] 屏幕分辨率: {0}x{1}", width, height));
            
            if (width > 0 && height > 0)
            {
                CoreHelper.Log(TAG, "[测试1] ✓ Android 模拟器连接正常");
                return true;
            }
            else
            {
                CoreHelper.LogErr(TAG, "[测试1] 屏幕分辨率无效");
                return false;
            }
        }
        catch (Exception ex)
        {
            CoreHelper.LogErr(TAG, "[测试1] 异常: " + ex.Message);
            return false;
        }
    }
    
    /// <summary>
    /// 测试2: 项目初始化
    /// </summary>
    private static bool Test2_ProjectInitialization(string projectRoot)
    {
        CoreHelper.Log(TAG, "[测试2] 验证项目初始化...");
        
        try
        {
            // 运行 Initializer
            string initResult = Initializer.Run(_project);
            CoreHelper.Log(TAG, "[测试2] Initializer 结果: " + initResult);
            
            // 验证 Screenshots 目录
            string screenshotDir = projectRoot + "Screenshots\\";
            if (!Directory.Exists(screenshotDir))
            {
                CoreHelper.LogErr(TAG, "[测试2] Screenshots 目录不存在");
                return false;
            }
            
            // 验证 VisionCorrector 初始化
            string visionStatus = CoreHelper.GetVar("vision_corrector_status", "");
            CoreHelper.Log(TAG, "[测试2] VisionCorrector 状态: " + visionStatus);
            
            if (initResult == "SUCCESS" || initResult.StartsWith("SUCCESS:"))
            {
                CoreHelper.Log(TAG, "[测试2] ✓ 项目初始化成功");
                return true;
            }
            else
            {
                CoreHelper.LogErr(TAG, "[测试2] 初始化失败");
                return false;
            }
        }
        catch (Exception ex)
        {
            CoreHelper.LogErr(TAG, "[测试2] 异常: " + ex.Message);
            return false;
        }
    }
    
    /// <summary>
    /// 测试3: AppExplorer Instagram 探索
    /// </summary>
    private static bool Test3_AppExplorerInstagram(string projectRoot)
    {
        CoreHelper.Log(TAG, "[测试3] 使用 AppExplorer 探索 Instagram APP...");
        
        try
        {
            // 启动 Instagram
            dynamic droid = _instance.DroidInstance;
            droid.StartApp("com.instagram.android");
            
            System.Threading.Thread.Sleep(5000); // 等待 APP 启动
            
            // 运行 AppExplorer
            string manifestPath = projectRoot + "Configs\\Manifests\\instagram_explored.json";
            string result = AppExplorer.Explore(_project, _instance, "com.instagram.android", manifestPath);
            
            CoreHelper.Log(TAG, "[测试3] AppExplorer 结果: " + result);

            if (string.IsNullOrEmpty(result) ||
                (result != "SUCCESS" && !result.StartsWith("SUCCESS:")))
            {
                CoreHelper.LogErr(TAG, "[测试3] AppExplorer 未返回明确 SUCCESS");
                return false;
            }
            
            // 验证生成的 manifest 文件
            if (File.Exists(manifestPath))
            {
                string manifestContent = File.ReadAllText(manifestPath);
                CoreHelper.Log(TAG, "[测试3] Manifest 文件大小: " + manifestContent.Length + " 字节");
                
                // 验证 manifest 包含必需字段
                if (manifestContent.Contains("\"screens\"") && 
                    manifestContent.Contains("\"navigation\"") &&
                    manifestContent.Contains("\"operations\""))
                {
                    CoreHelper.Log(TAG, "[测试3] ✓ Manifest 生成成功");
                    return true;
                }
                else
                {
                    CoreHelper.LogErr(TAG, "[测试3] Manifest 缺少必需字段");
                    return false;
                }
            }
            else
            {
                CoreHelper.LogErr(TAG, "[测试3] Manifest 文件未生成");
                return false;
            }
        }
        catch (Exception ex)
        {
            CoreHelper.LogErr(TAG, "[测试3] 异常: " + ex.Message);
            return false;
        }
    }
    
    /// <summary>
    /// 测试4: Instagram 导航路径验证
    /// </summary>
    private static bool Test4_InstagramNavigation(string projectRoot)
    {
        CoreHelper.Log(TAG, "[测试4] 验证 Instagram 导航路径...");
        
        try
        {
            // 加载 instagram.json
            string manifestPath = projectRoot + "Configs\\Manifests\\instagram.json";
            string manifestJson = File.ReadAllText(manifestPath);
            
            // 初始化 NavigationResolver
            bool loaded = NavigationResolver.LoadManifest(manifestJson);
            if (!loaded)
            {
                CoreHelper.LogErr(TAG, "[测试4] Manifest 加载失败");
                return false;
            }
            
            // 测试导航路径: home → notifications
            string[] path1 = NavigationResolver.FindPath("home", "notifications");
            if (path1 == null || path1.Length == 0)
            {
                CoreHelper.LogErr(TAG, "[测试4] home → notifications 路径不存在");
                return false;
            }
            CoreHelper.Log(TAG, "[测试4] home → notifications 路径: " + string.Join(" → ", path1));
            
            // 测试导航路径: home → direct_messages
            string[] path2 = NavigationResolver.FindPath("home", "direct_messages");
            if (path2 == null || path2.Length == 0)
            {
                CoreHelper.LogErr(TAG, "[测试4] home → direct_messages 路径不存在");
                return false;
            }
            CoreHelper.Log(TAG, "[测试4] home → direct_messages 路径: " + string.Join(" → ", path2));
            
            CoreHelper.Log(TAG, "[测试4] ✓ 导航路径验证成功");
            return true;
        }
        catch (Exception ex)
        {
            CoreHelper.LogErr(TAG, "[测试4] 异常: " + ex.Message);
            return false;
        }
    }
    
    /// <summary>
    /// 测试5: 速率限制验证
    /// </summary>
    private static bool Test5_RateLimitVerification(string projectRoot)
    {
        CoreHelper.Log(TAG, "[测试5] 验证速率限制配置...");
        
        try
        {
            // 加载 instagram.json
            string manifestPath = projectRoot + "Configs\\Manifests\\instagram.json";
            string manifestJson = File.ReadAllText(manifestPath);
            
            // 提取 like_feed_posts 操作
            string operationsJson = JsonHelper.ExtractObject(manifestJson, "operations");
            string likePostsJson = JsonHelper.ExtractObject(operationsJson, "like_feed_posts");
            
            if (string.IsNullOrEmpty(likePostsJson))
            {
                CoreHelper.LogErr(TAG, "[测试5] like_feed_posts 操作不存在");
                return false;
            }
            
            // 提取速率限制
            string rateLimitJson = JsonHelper.ExtractObject(likePostsJson, "rate_limit");
            if (string.IsNullOrEmpty(rateLimitJson))
            {
                CoreHelper.LogErr(TAG, "[测试5] rate_limit 配置不存在");
                return false;
            }
            
            int perHour = JsonHelper.GetInt(rateLimitJson, "per_hour", 0);
            int cooldownSeconds = JsonHelper.GetInt(rateLimitJson, "cooldown_seconds", 0);
            
            CoreHelper.Log(TAG, string.Format("[测试5] 速率限制: {0}/hour, 冷却 {1}秒", perHour, cooldownSeconds));
            
            // 验证速率限制符合安全标准（30/hour, 120秒冷却）
            if (perHour == 30 && cooldownSeconds == 120)
            {
                CoreHelper.Log(TAG, "[测试5] ✓ 速率限制配置正确");
                return true;
            }
            else
            {
                CoreHelper.LogErr(TAG, string.Format("[测试5] 速率限制不正确: 期望 30/hour, 120秒，实际 {0}/hour, {1}秒", perHour, cooldownSeconds));
                return false;
            }
        }
        catch (Exception ex)
        {
            CoreHelper.LogErr(TAG, "[测试5] 异常: " + ex.Message);
            return false;
        }
    }
    
    /// <summary>
    /// 测试6: VisionCorrector 视觉修正
    /// </summary>
    private static bool Test6_VisionCorrector(string projectRoot)
    {
        CoreHelper.Log(TAG, "[测试6] 测试 VisionCorrector 视觉修正...");
        
        try
        {
            // 捕获当前屏幕截图
            string screenshotPath = projectRoot + "Screenshots\\test_screenshot.png";
            dynamic droid = _instance.DroidInstance;
            
            byte[] screenshotBytes = droid.Screen.ScreenshotAsArray();
            File.WriteAllBytes(screenshotPath, screenshotBytes);
            
            CoreHelper.Log(TAG, "[测试6] 截图已保存: " + screenshotPath);
            
            // 调用 VisionCorrector 分析
            string prompt = "分析这个 Instagram 屏幕截图，识别当前页面类型（首页/搜索/个人资料等）";
            string result = VisionCorrector.AnalyzeAndRecover(_project, _instance, prompt);
            
            CoreHelper.Log(TAG, "[测试6] VisionCorrector 结果: " + result);
            
            // 只有明确 SUCCESS 才能放行；未知、PARTIAL 或其他状态全部失败关闭。
            if (result == "SUCCESS" || result.StartsWith("SUCCESS:"))
            {
                CoreHelper.Log(TAG, "[测试6] ✓ VisionCorrector 视觉分析成功");
                return true;
            }
            else
            {
                CoreHelper.LogErr(TAG, "[测试6] VisionCorrector 返回错误");
                return false;
            }
        }
        catch (Exception ex)
        {
            CoreHelper.LogErr(TAG, "[测试6] 异常: " + ex.Message);
            return false;
        }
    }
    
    /// <summary>
    /// 记录测试结果
    /// </summary>
    private static void RecordResult(string result)
    {
        _testResults.Add(result);
    }
    
    /// <summary>
    /// 保存测试报告
    /// </summary>
    private static bool SaveTestReport(string projectRoot, int passed, int failed)
    {
        try
        {
            string reportPath = projectRoot + "Reports\\runtime_test_report.txt";
            string reportDir = Path.GetDirectoryName(reportPath);
            
            if (!Directory.Exists(reportDir))
            {
                Directory.CreateDirectory(reportDir);
            }
            
            string report = "DPS v4.5 运行时测试报告\n";
            report += "生成时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\n";
            report += "========================================\n\n";
            
            foreach (string result in _testResults)
            {
                report += result + "\n";
            }
            
            report += "\n========================================\n";
            report += string.Format("总计: {0} 通过, {1} 失败\n", passed, failed);
            
            File.WriteAllText(reportPath, report);
            CoreHelper.Log(TAG, "测试报告已保存: " + reportPath);
            return true;
        }
        catch (Exception ex)
        {
            CoreHelper.LogErr(TAG, "保存测试报告失败: " + ex.Message);
            return false;
        }
    }
}
