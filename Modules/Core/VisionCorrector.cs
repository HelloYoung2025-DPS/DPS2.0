// =====================================================
// VisionCorrector.cs - 视觉分析与错误恢复模块
// ⚠️ C# 5.0 语法 - 禁止使用 $""、?.、nameof() 等
// =====================================================
using System;
using System.IO;
using System.Collections.Generic;

/// <summary>
/// 视觉分析与错误恢复模块
/// 使用 Gemini Flash 视觉模型分析截图并执行恢复操作
/// </summary>
public class VisionCorrector
{
    private static dynamic _project;
    private static dynamic _instance;
    private static string _screenshotDir = "";
    private static string _aiConfigPath = "";

    /// <summary>
    /// 初始化 VisionCorrector
    /// </summary>
    public static void Init(dynamic project, dynamic instanceObj, string screenshotDir, string aiConfigPath)
    {
        _project = project;
        _instance = instanceObj;
        _screenshotDir = screenshotDir;
        _aiConfigPath = aiConfigPath;

        // 确保截图目录存在
        if (!string.IsNullOrEmpty(_screenshotDir) && !Directory.Exists(_screenshotDir))
        {
            Directory.CreateDirectory(_screenshotDir);
        }
    }

    /// <summary>
    /// 检查是否已初始化
    /// </summary>
    public static bool IsInitialized()
    {
        return _project != null && _instance != null;
    }

    // ========== 日志辅助函数 ==========

    private static void Log(string message)
    {
        if (_project != null)
        {
            _project.SendInfoToLog("[VisionCorrector] " + message);
        }
    }

    private static void LogErr(string message)
    {
        if (_project != null)
        {
            _project.SendErrorToLog("[VisionCorrector] " + message);
        }
    }

    private static void LogWarn(string message)
    {
        if (_project != null)
        {
            _project.SendWarningToLog("[VisionCorrector] " + message);
        }
    }

    // ========== 截图捕获 ==========

    /// <summary>
    /// 捕获屏幕截图并保存到指定路径
    /// </summary>
    /// <param name="outputPath">输出路径，如果为空则自动生成</param>
    /// <returns>截图文件路径，失败返回空字符串</returns>
    public static string CaptureScreenshot(string outputPath)
    {
        if (!IsInitialized())
        {
            LogErr("模块未初始化，请先调用 Init()");
            return "";
        }

        try
        {
            // 如果未指定路径，自动生成
            if (string.IsNullOrEmpty(outputPath))
            {
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                outputPath = Path.Combine(_screenshotDir, "screen_" + timestamp + ".png");
            }

            // 确保目录存在
            string dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // 调用 ZennoDroid 截图 API
            dynamic droid = _instance.DroidInstance;
            droid.SaveScreenshot(outputPath);

            // 验证文件是否创建成功
            if (File.Exists(outputPath))
            {
                Log("截图保存成功: " + outputPath);
                return outputPath;
            }
            else
            {
                LogErr("截图文件未创建: " + outputPath);
                return "";
            }
        }
        catch (Exception ex)
        {
            LogErr("截图捕获失败: " + ex.Message);
            return "";
        }
    }

    /// <summary>
    /// 捕获屏幕截图（自动生成文件名）
    /// </summary>
    public static string CaptureScreenshot()
    {
        return CaptureScreenshot(null);
    }

    // ========== AI 配置加载 ==========

    /// <summary>
    /// 加载 AI 配置文件
    /// </summary>
    private static string LoadAIConfig()
    {
        if (string.IsNullOrEmpty(_aiConfigPath))
        {
            LogErr("AI 配置路径未设置");
            return "";
        }

        if (!File.Exists(_aiConfigPath))
        {
            LogErr("AI 配置文件不存在: " + _aiConfigPath);
            return "";
        }

        try
        {
            return File.ReadAllText(_aiConfigPath);
        }
        catch (Exception ex)
        {
            LogErr("加载 AI 配置失败: " + ex.Message);
            return "";
        }
    }

    // ========== 视觉分析 ==========

    /// <summary>
    /// 验证错误：专用于操作失败时的视觉验证
    /// 判断是真的失败了，还是 ZennoDroid 误报（实际成功了）
    /// </summary>
    /// <param name="intent">操作意图</param>
    /// <param name="failedResult">ZennoDroid 返回的失败结果</param>
    /// <param name="screenshotPath">失败时的截图路径</param>
    /// <returns>如果视觉验证显示实际已成功，返回 true；否则返回 false</returns>
    public static bool VerifyError(string intent, ZDResult failedResult, string screenshotPath)
    {
        if (string.IsNullOrEmpty(screenshotPath) || !File.Exists(screenshotPath))
        {
            LogWarn("视觉验证失败：缺失截图文件");
            return false;
        }

        string aiConfig = LoadAIConfig();
        if (string.IsNullOrEmpty(aiConfig)) return false;

        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        sb.Append("你是一个 Android 自动化专家。刚刚执行了一个操作但系统报错了，请通过截图判断该操作是否【实际上已经成功】。\n\n");
        sb.Append("【操作意图】: " + intent + "\n");
        sb.Append("【报错信息】: " + (failedResult != null ? failedResult.ErrorMessage : "未知错误") + "\n\n");
        sb.Append("【任务】: 分析截图，如果看到操作已经生效（例如：点赞按钮变红、评论已发出、页面已跳转到目标位置），请判定为成功。\n\n");
        sb.Append("【返回格式】: {\"actually_success\": true/false, \"reason\": \"简短说明\"}");

        string aiResponse = AIService.CallWithRetryAndImage(sb.ToString(), screenshotPath, aiConfig);
        string json = AIService.ExtractJson(aiResponse);

        if (string.IsNullOrEmpty(json)) return false;

        string successStr = JsonHelper.Get(json, "actually_success");
        bool verified = (successStr == "true" || successStr == "True");

        if (verified)
        {
            Log("视觉验证发现 ZennoDroid 误报，操作实际已成功: " + intent);
        }

        return verified;
    }

    /// <summary>
    /// 分析当前屏幕状态并返回恢复建议
    /// </summary>
    /// <param name="context">上下文描述</param>
    /// <param name="expectedPage">期望页面描述</param>
    /// <returns>JSON 格式的分析结果</returns>
    public static string AnalyzeAndRecover(string context, string expectedPage)
    {
        if (!IsInitialized())
        {
            LogErr("模块未初始化，请先调用 Init()");
            return "{\"success\":false,\"error\":\"模块未初始化\"}";
        }

        // 1. 捕获截图
        string screenshotPath = CaptureScreenshot();
        if (string.IsNullOrEmpty(screenshotPath))
        {
            return "{\"success\":false,\"error\":\"截图捕获失败\"}";
        }

        // 2. 加载 AI 配置
        string aiConfig = LoadAIConfig();
        if (string.IsNullOrEmpty(aiConfig))
        {
            return "{\"success\":false,\"error\":\"AI 配置加载失败\"}";
        }

        // 3. 构建 AI 提示词
        string prompt = BuildAnalysisPrompt(context, expectedPage);

        // 4. 调用 AI 视觉分析
        string aiResponse = AIService.CallWithRetryAndImage(prompt, screenshotPath, aiConfig);

        // 5. 解析响应
        if (string.IsNullOrEmpty(aiResponse) || aiResponse.StartsWith("ERROR:"))
        {
            LogErr("AI 分析失败: " + (aiResponse ?? "null"));
            return "{\"success\":false,\"error\":\"AI 分析失败\",\"details\":\"" + (aiResponse ?? "null") + "\"}";
        }

        // 6. 提取 JSON 结果
        string jsonResult = AIService.ExtractJson(aiResponse);

        // 7. 记录结果
        Log("分析完成: " + jsonResult.Substring(0, Math.Min(200, jsonResult.Length)));

        return jsonResult;
    }

    /// <summary>
    /// 构建视觉分析提示词
    /// </summary>
    private static string BuildAnalysisPrompt(string context, string expectedPage)
    {
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        sb.Append("你是一个 Android APP 自动化测试的错误恢复专家。\n\n");
        sb.Append("【上下文】\n");
        sb.Append(context);
        sb.Append("\n\n");
        sb.Append("【期望页面】\n");
        sb.Append(expectedPage);
        sb.Append("\n\n");
        sb.Append("【任务】\n");
        sb.Append("1. 分析当前截图，判断是否在期望页面上\n");
        sb.Append("2. 如果不在，识别当前页面是什么\n");
        sb.Append("3. 提供具体的恢复操作建议\n\n");
        sb.Append("【返回格式】（严格按 JSON 格式返回，不要添加其他文字）\n");
        sb.Append("{\n");
        sb.Append("  \"on_expected_page\": true/false,\n");
        sb.Append("  \"current_page\": \"页面名称\",\n");
        sb.Append("  \"confidence\": 0.0-1.0,\n");
        sb.Append("  \"recovery_action\": \"swipe_back/tap_home/close_reopen/press_back/wait/no_action\",\n");
        sb.Append("  \"recovery_params\": {\n");
        sb.Append("    \"x\": 0,\n");
        sb.Append("    \"y\": 0,\n");
        sb.Append("    \"duration\": 0\n");
        sb.Append("  },\n");
        sb.Append("  \"reason\": \"原因说明\"\n");
        sb.Append("}");

        return sb.ToString();
    }

    // ========== 恢复操作执行 ==========

    /// <summary>
    /// 执行恢复操作
    /// </summary>
    /// <param name="actionJson">AI 返回的 JSON 结果</param>
    /// <returns>是否成功执行</returns>
    public static bool ExecuteRecovery(string actionJson)
    {
        if (string.IsNullOrEmpty(actionJson))
        {
            LogErr("恢复操作 JSON 为空");
            return false;
        }

        try
        {
            // 解析恢复动作
            string action = JsonHelper.Get(actionJson, "recovery_action");
            string paramsStr = JsonHelper.ExtractObject(actionJson, "recovery_params");

            Log("执行恢复操作: " + action);

            bool result = false;

            switch (action)
            {
                case "swipe_back":
                    result = DoSwipeBack(paramsStr);
                    break;
                case "tap_home":
                    result = DoTapHome(paramsStr);
                    break;
                case "close_reopen":
                    result = DoCloseReopen(paramsStr);
                    break;
                case "press_back":
                    result = DoPressBack(paramsStr);
                    break;
                case "wait":
                    result = DoWait(paramsStr);
                    break;
                case "no_action":
                    Log("无需执行恢复操作");
                    result = true;
                    break;
                default:
                    LogWarn("未知的恢复操作: " + action);
                    result = false;
                    break;
            }

            if (result)
            {
                Log("恢复操作执行成功");
            }
            else
            {
                LogErr("恢复操作执行失败");
            }

            return result;
        }
        catch (Exception ex)
        {
            LogErr("执行恢复操作异常: " + ex.Message);
            return false;
        }
    }

    // ========== 具体恢复操作 ==========

    /// <summary>
    /// 执行滑动返回操作
    /// </summary>
    private static bool DoSwipeBack(string paramsStr)
    {
        try
        {
            dynamic droid = _instance.DroidInstance;
            dynamic input = droid.Input;

            // 从边缘滑动返回
            int x = 100;
            int y1 = 500;
            int y2 = 1500;
            int duration = 500;

            // 尝试从参数读取
            string xStr = JsonHelper.Get(paramsStr, "x");
            string y1Str = JsonHelper.Get(paramsStr, "y1");
            string y2Str = JsonHelper.Get(paramsStr, "y2");
            string durStr = JsonHelper.Get(paramsStr, "duration");

            int temp;
            if (!string.IsNullOrEmpty(xStr) && int.TryParse(xStr, out temp)) x = temp;
            if (!string.IsNullOrEmpty(y1Str) && int.TryParse(y1Str, out temp)) y1 = temp;
            if (!string.IsNullOrEmpty(y2Str) && int.TryParse(y2Str, out temp)) y2 = temp;
            if (!string.IsNullOrEmpty(durStr) && int.TryParse(durStr, out temp)) duration = temp;

            input.Swipe(x, y1, x, y2, duration);
            System.Threading.Thread.Sleep(500);
            return true;
        }
        catch (Exception ex)
        {
            LogErr("滑动返回失败: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 点击 Home 按钮
    /// </summary>
    private static bool DoTapHome(string paramsStr)
    {
        try
        {
            dynamic droid = _instance.DroidInstance;
            dynamic input = droid.Input;

            // 发送 Home 键事件
            input.SendKeyCode(3); // KEYCODE_HOME = 3
            System.Threading.Thread.Sleep(1000);
            return true;
        }
        catch (Exception ex)
        {
            LogErr("点击 Home 失败: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 关闭并重新打开 APP
    /// </summary>
    private static bool DoCloseReopen(string paramsStr)
    {
        try
        {
            dynamic droid = _instance.DroidInstance;
            dynamic app = droid.App;

            // 获取当前顶层 APP
            string currentPackage = app.Top;
            if (string.IsNullOrEmpty(currentPackage))
            {
                LogErr("无法获取当前 APP 包名");
                return false;
            }

            // 关闭 APP
            app.Close(currentPackage);
            System.Threading.Thread.Sleep(2000);

            // 重新打开
            app.Open(currentPackage);
            System.Threading.Thread.Sleep(3000);

            Log("APP 已重新打开: " + currentPackage);
            return true;
        }
        catch (Exception ex)
        {
            LogErr("关闭重开 APP 失败: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 按 Back 键
    /// </summary>
    private static bool DoPressBack(string paramsStr)
    {
        try
        {
            dynamic droid = _instance.DroidInstance;
            dynamic input = droid.Input;

            input.SendKeyCode(4); // KEYCODE_BACK = 4
            System.Threading.Thread.Sleep(500);
            return true;
        }
        catch (Exception ex)
        {
            LogErr("按 Back 键失败: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 等待指定时间
    /// </summary>
    private static bool DoWait(string paramsStr)
    {
        try
        {
            int waitMs = 1000;

            string waitStr = JsonHelper.Get(paramsStr, "duration");
            int temp;
            if (!string.IsNullOrEmpty(waitStr) && int.TryParse(waitStr, out temp))
            {
                waitMs = temp;
            }

            Log("等待 " + waitMs + "ms");
            System.Threading.Thread.Sleep(waitMs);
            return true;
        }
        catch (Exception ex)
        {
            LogErr("等待失败: " + ex.Message);
            return false;
        }
    }

    // ========== 高级功能 ==========

    /// <summary>
    /// 完整的视觉纠错流程：截图 -> 分析 -> 执行恢复
    /// </summary>
    /// <param name="context">上下文</param>
    /// <param name="expectedPage">期望页面</param>
    /// <returns>是否在期望页面上</returns>
    public static bool CorrectAndWait(string context, string expectedPage)
    {
        // 1. 分析当前状态
        string result = AnalyzeAndRecover(context, expectedPage);

        // 2. 检查是否在期望页面
        string onExpectedStr = JsonHelper.Get(result, "on_expected_page");
        bool onExpected = (onExpectedStr == "true" || onExpectedStr == "True");

        if (onExpected)
        {
            Log("已在期望页面: " + expectedPage);
            return true;
        }

        // 3. 执行恢复操作
        Log("不在期望页面，执行恢复操作");
        bool recoverySuccess = ExecuteRecovery(result);

        if (recoverySuccess)
        {
            // 等待页面稳定
            System.Threading.Thread.Sleep(1000);
        }

        return recoverySuccess;
    }

    /// <summary>
    /// 批量纠错：多次尝试直到到达期望页面
    /// </summary>
    /// <param name="context">上下文</param>
    /// <param name="expectedPage">期望页面</param>
    /// <param name="maxAttempts">最大尝试次数</param>
    /// <returns>是否成功到达期望页面</returns>
    public static bool CorrectWithRetry(string context, string expectedPage, int maxAttempts)
    {
        if (maxAttempts <= 0) maxAttempts = 3;

        for (int i = 0; i < maxAttempts; i++)
        {
            Log("视觉纠错尝试 " + (i + 1) + "/" + maxAttempts);

            bool success = CorrectAndWait(context, expectedPage);
            if (success)
            {
                // 再次验证
                string verifyResult = AnalyzeAndRecover(context, expectedPage);
                string onExpectedStr = JsonHelper.Get(verifyResult, "on_expected_page");
                if (onExpectedStr == "true" || onExpectedStr == "True")
                {
                    Log("验证成功，已到达期望页面");
                    return true;
                }
            }

            if (i < maxAttempts - 1)
            {
                LogErr("未到达期望页面，1秒后重试");
                System.Threading.Thread.Sleep(1000);
            }
        }

        LogErr("视觉纠错失败，已达到最大尝试次数");
        return false;
    }

    /// <summary>
    /// 批量纠错（默认 3 次）
    /// </summary>
    public static bool CorrectWithRetry(string context, string expectedPage)
    {
        return CorrectWithRetry(context, expectedPage, 3);
    }
}
