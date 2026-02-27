// =====================================================
// ZennoDroidAdapter.cs - ZennoDroid API 适配器
// ⚠️ C# 5.0 语法 - 禁止使用 $""、?.、nameof() 等
// =====================================================
// v4.5.2 架构重构 - DPS "手"层
//
// ZennoDroidAdapter 是 DPS 与 ZennoDroid API 之间的唯一接口，
// 负责将 ZDCommand 转换为实际的 ZennoDroid API 调用。
//
// 设计原则：
//   1. 封装所有 ZennoDroid API 调用
//   2. 提供统一的错误处理
//   3. 支持重试机制
//   4. 返回标准化的 ZDResult
// =====================================================

using System;
using System.Threading;

/// <summary>
/// ZennoDroid API 适配器
/// DPS 架构中的"手"层，负责执行物理操作
/// </summary>
public class ZennoDroidAdapter
{
    private const string TAG = "ZennoDroidAdapter";
    private static Random _random = new Random();
    private static bool _fastMode = true; // 默认开启快速模式，成功时不截图节省性能

    // ========== 核心执行方法 ==========

    /// <summary>
    /// 设置快速模式
    /// </summary>
    public static void SetFastMode(bool fastMode)
    {
        _fastMode = fastMode;
    }

    /// <summary>
    /// 执行单个 ZDCommand
    /// </summary>
    /// <param name="command">要执行的命令</param>
    /// <param name="captureOnSuccess">是否在成功时也捕获截图（即使在快速模式下）</param>
    /// <returns>执行结果</returns>
    public static ZDResult Execute(ZDCommand command, bool captureOnSuccess = false)

    /// <returns>执行结果</returns>
    public static ZDResult Execute(ZDCommand command)
    {
        if (command == null)
        {
            return ZDResult.FailedRetryable("命令为 null");
        }

        if (!command.IsValid())
        {
            return ZDResult.FailedRetryable("命令参数无效");
        }

        DateTime startTime = DateTime.Now;
        ZDResult result = null;

        try
        {
            CoreHelper.Log(TAG, "执行命令: " + command.ToLogString());

            switch (command.Type)
            {
                case ZDCommandType.Tap:
                    result = ExecuteTap(command, captureOnSuccess);
                    break;

                case ZDCommandType.LongPress:
                    result = ExecuteLongPress(command, captureOnSuccess);
                    break;

                case ZDCommandType.Swipe:
                    result = ExecuteSwipe(command, captureOnSuccess);
                    break;

                case ZDCommandType.SendText:
                    result = ExecuteSendText(command, captureOnSuccess);
                    break;

                case ZDCommandType.PressBack:
                    result = ExecutePressBack(command, captureOnSuccess);
                    break;

                case ZDCommandType.Delay:
                    result = ExecuteDelay(command);
                    break;

                case ZDCommandType.ShellCommand:
                    result = ExecuteShell(command, captureOnSuccess);
                    break;

                case ZDCommandType.GetLayout:
                    result = ExecuteGetLayout(command);
                    break;

                case ZDCommandType.Screenshot:
                    result = ExecuteScreenshot(command);
                    break;

                default:
                    result = ZDResult.FailedRetryable("未知命令类型: " + command.Type.ToString());
                    break;
            }

            {
                case ZDCommandType.Tap:
                    result = ExecuteTap(command);
                    break;

                case ZDCommandType.LongPress:
                    result = ExecuteLongPress(command);
                    break;

                case ZDCommandType.Swipe:
                    result = ExecuteSwipe(command);
                    break;

                case ZDCommandType.SendText:
                    result = ExecuteSendText(command);
                    break;

                case ZDCommandType.PressBack:
                    result = ExecutePressBack(command);
                    break;

                case ZDCommandType.Delay:
                    result = ExecuteDelay(command);
                    break;

                case ZDCommandType.ShellCommand:
                    result = ExecuteShell(command);
                    break;

                case ZDCommandType.GetLayout:
                    result = ExecuteGetLayout(command);
                    break;

                case ZDCommandType.Screenshot:
                    result = ExecuteScreenshot(command);
                    break;

                default:
                    result = ZDResult.FailedRetryable("未知命令类型: " + command.Type.ToString());
                    break;
            }
        }
        catch (Exception ex)
        {
            CoreHelper.LogErr(TAG, "命令执行异常: " + ex.Message);
            result = ZDResult.FromException(ex);
            
            // 失败时总是尝试捕获截图用于分析
            try
            {
                string screenshotPath = VisionCorrector.CaptureScreenshot();
                if (!string.IsNullOrEmpty(screenshotPath))
                {
                    result.ScreenshotPath = screenshotPath;
                }
            }
            catch {}
        }

        {
            CoreHelper.LogErr(TAG, "命令执行异常: " + ex.Message);
            result = ZDResult.FromException(ex);
        }

        // 计算执行时间
        TimeSpan elapsed = DateTime.Now - startTime;
        result.ElapsedMilliseconds = (long)elapsed.TotalMilliseconds;
        result.AttemptCount = 1;

        CoreHelper.Log(TAG, "命令完成: " + result.ToLogString());

        return result;
    }

    /// <summary>
    /// 执行命令（带重试）
    /// </summary>
    public static ZDResult ExecuteWithRetry(ZDCommand command)
    {
        int maxRetries = command.MaxRetries > 0 ? command.MaxRetries : 1;
        int retryDelay = command.RetryDelay > 0 ? command.RetryDelay : 1000;

        ZDResult lastResult = null;

        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            lastResult = Execute(command);
            lastResult.AttemptCount = attempt + 1;

            if (lastResult.IsSuccess())
            {
                return lastResult;
            }

            if (!lastResult.CanRetry())
            {
                // 不可重试的失败，直接返回
                return lastResult;
            }

            // 还有重试机会，等待后重试
            if (attempt < maxRetries - 1)
            {
                CoreHelper.Log(TAG, string.Format("重试 {0}/{1}: {2}",
                    attempt + 1, maxRetries, lastResult.ErrorMessage));
                Thread.Sleep(retryDelay);
            }
        }

        return lastResult;
    }

    // ========== 具体命令实现 ==========

    /// <summary>
    /// 执行点击命令
    /// </summary>
    private static ZDResult ExecuteTap(ZDCommand command, bool captureOnSuccess)
    {
        dynamic input = CoreHelper.GetInput();
        if (input == null)
        {
            return ZDResult.FailedFatal("DroidInstance.Input 未初始化");
        }

        int x = command.X1;
        int y = command.Y1;

        if (command.Humanized)
        {
            // 人性化点击：添加随机偏移
            int offset = _random.Next(5, 15);
            x += _random.Next(-offset, offset);
            y += _random.Next(-offset, offset);

            // 添加随机延迟
            int thinkTime = _random.Next(50, 200);
            Thread.Sleep(thinkTime);
        }

        input.Tap(x, y);
        
        ZDResult result = ZDResult.Success(0);
        
        // 性能优化：快速模式且不要求成功截图时，直接返回
        if (_fastMode && !captureOnSuccess)
        {
            return result;
        }
        
        // 捕获截图
        string path = VisionCorrector.CaptureScreenshot();
        if (!string.IsNullOrEmpty(path))
        {
            result.ScreenshotPath = path;
        }
        
        return result;
    }

    {
        dynamic input = CoreHelper.GetInput();
        if (input == null)
        {
            return ZDResult.FailedFatal("DroidInstance.Input 未初始化");
        }

        int x = command.X1;
        int y = command.Y1;

        if (command.Humanized)
        {
            // 人性化点击：添加随机偏移
            int offset = _random.Next(5, 15);
            x += _random.Next(-offset, offset);
            y += _random.Next(-offset, offset);

            // 添加随机延迟
            int thinkTime = _random.Next(50, 200);
            Thread.Sleep(thinkTime);
        }

        input.Tap(x, y);
        return ZDResult.Success(0);
    }

    /// <summary>
    /// 执行长按命令
    /// </summary>
    private static ZDResult ExecuteLongPress(ZDCommand command, bool captureOnSuccess)
    {
        dynamic input = CoreHelper.GetInput();
        if (input == null)
        {
            return ZDResult.FailedFatal("DroidInstance.Input 未初始化");
        }

        int x = command.X1;
        int y = command.Y1;
        int duration = command.Duration > 0 ? command.Duration : 1000;

        input.LongTap(x, y);
        Thread.Sleep(duration);

        ZDResult result = ZDResult.Success(duration);
        
        if (_fastMode && !captureOnSuccess) return result;
        
        string path = VisionCorrector.CaptureScreenshot();
        if (!string.IsNullOrEmpty(path)) result.ScreenshotPath = path;
        
        return result;
    }

    {
        dynamic input = CoreHelper.GetInput();
        if (input == null)
        {
            return ZDResult.FailedFatal("DroidInstance.Input 未初始化");
        }

        int x = command.X1;
        int y = command.Y1;
        int duration = command.Duration > 0 ? command.Duration : 1000;

        input.LongTap(x, y);
        Thread.Sleep(duration);

        return ZDResult.Success(duration);
    }

    /// <summary>
    /// 执行滑动命令
    /// </summary>
    private static ZDResult ExecuteSwipe(ZDCommand command, bool captureOnSuccess)
    {
        dynamic input = CoreHelper.GetInput();
        if (input == null)
        {
            return ZDResult.FailedFatal("DroidInstance.Input 未初始化");
        }

        int x1 = command.X1;
        int y1 = command.Y1;
        int x2 = command.X2;
        int y2 = command.Y2;
        int duration = command.Duration > 0 ? command.Duration : 300;

        if (command.Humanized)
        {
            int thinkTime = _random.Next(50, 150);
            Thread.Sleep(thinkTime);
        }

        input.Swipe(x1, y1, x2, y2, duration);
        
        ZDResult result = ZDResult.Success(duration);
        if (_fastMode && !captureOnSuccess) return result;
        
        string path = VisionCorrector.CaptureScreenshot();
        if (!string.IsNullOrEmpty(path)) result.ScreenshotPath = path;
        
        return result;
    }

    {
        dynamic input = CoreHelper.GetInput();
        if (input == null)
        {
            return ZDResult.FailedFatal("DroidInstance.Input 未初始化");
        }

        int x1 = command.X1;
        int y1 = command.Y1;
        int x2 = command.X2;
        int y2 = command.Y2;
        int duration = command.Duration > 0 ? command.Duration : 300;

        if (command.Humanized)
        {
            // 调用 Core/ScriptHelpers 中的人性化滑动
            // 注意：这里需要访问 ScriptHelpers 的方法
            // 由于 C# 5.0 的限制，我们手动实现简化版本
            int thinkTime = _random.Next(50, 150);
            Thread.Sleep(thinkTime);
        }

        input.Swipe(x1, y1, x2, y2, duration);
        return ZDResult.Success(duration);
    }

    /// <summary>
    /// 执行输入文本命令
    /// </summary>
    private static ZDResult ExecuteSendText(ZDCommand command, bool captureOnSuccess)
    {
        dynamic input = CoreHelper.GetInput();
        if (input == null)
        {
            return ZDResult.FailedFatal("DroidInstance.Input 未初始化");
        }

        string text = command.Text;
        if (string.IsNullOrEmpty(text))
        {
            return ZDResult.FailedRetryable("文本内容为空");
        }

        if (command.Humanized)
        {
            // 人性化输入：逐字符输入，带随机延迟
            foreach (char c in text)
            {
                input.SendText(c.ToString());
                int delay = _random.Next(50, 150);
                Thread.Sleep(delay);
            }
        }
        else
        {
            input.SendText(text);
        }

        ZDResult result = ZDResult.Success(0);
        if (_fastMode && !captureOnSuccess) return result;
        
        string path = VisionCorrector.CaptureScreenshot();
        if (!string.IsNullOrEmpty(path)) result.ScreenshotPath = path;
        
        return result;
    }

    {
        dynamic input = CoreHelper.GetInput();
        if (input == null)
        {
            return ZDResult.FailedFatal("DroidInstance.Input 未初始化");
        }

        string text = command.Text;
        if (string.IsNullOrEmpty(text))
        {
            return ZDResult.FailedRetryable("文本内容为空");
        }

        if (command.Humanized)
        {
            // 人性化输入：逐字符输入，带随机延迟
            foreach (char c in text)
            {
                input.SendText(c.ToString());
                int delay = _random.Next(50, 150);
                Thread.Sleep(delay);
            }
        }
        else
        {
            input.SendText(text);
        }

        return ZDResult.Success(0);
    }

    /// <summary>
    /// 执行按返回键命令
    /// </summary>
    private static ZDResult ExecutePressBack(ZDCommand command, bool captureOnSuccess)
    {
        dynamic input = CoreHelper.GetInput();
        if (input == null)
        {
            return ZDResult.FailedFatal("DroidInstance.Input 未初始化");
        }

        input.Shell("input keyevent 4");
        
        ZDResult result = ZDResult.Success(0);
        if (_fastMode && !captureOnSuccess) return result;
        
        string path = VisionCorrector.CaptureScreenshot();
        if (!string.IsNullOrEmpty(path)) result.ScreenshotPath = path;
        
        return result;
    }

    {
        dynamic input = CoreHelper.GetInput();
        if (input == null)
        {
            return ZDResult.FailedFatal("DroidInstance.Input 未初始化");
        }

        input.Shell("input keyevent 4");
        return ZDResult.Success(0);
    }

    /// <summary>
    /// 执行延迟命令
    /// </summary>
    private static ZDResult ExecuteDelay(ZDCommand command)
    {
        int ms = 1000;
        string msStr = command.GetExtraParam("milliseconds", "");
        if (!string.IsNullOrEmpty(msStr))
        {
            int.TryParse(msStr, out ms);
        }

        if (command.Humanized)
        {
            // 添加随机变化
            int variance = ms / 10;
            ms += _random.Next(-variance, variance);
        }

        Thread.Sleep(ms);
        return ZDResult.Success(ms);
    }

    /// <summary>
    /// 执行 Shell 命令
    /// </summary>
    private static ZDResult ExecuteShell(ZDCommand command, bool captureOnSuccess)
    {
        dynamic input = CoreHelper.GetInput();
        if (input == null)
        {
            return ZDResult.FailedFatal("DroidInstance.Input 未初始化");
        }

        string shellCmd = command.Text;
        if (string.IsNullOrEmpty(shellCmd))
        {
            return ZDResult.FailedRetryable("Shell 命令为空");
        }

        input.Shell(shellCmd);
        
        ZDResult result = ZDResult.Success(0);
        if (_fastMode && !captureOnSuccess) return result;
        
        string path = VisionCorrector.CaptureScreenshot();
        if (!string.IsNullOrEmpty(path)) result.ScreenshotPath = path;
        
        return result;
    }

    {
        dynamic input = CoreHelper.GetInput();
        if (input == null)
        {
            return ZDResult.FailedFatal("DroidInstance.Input 未初始化");
        }

        string shellCmd = command.Text;
        if (string.IsNullOrEmpty(shellCmd))
        {
            return ZDResult.FailedRetryable("Shell 命令为空");
        }

        input.Shell(shellCmd);
        return ZDResult.Success(0);
    }

    /// <summary>
    /// 执行获取布局命令
    /// </summary>
    private static ZDResult ExecuteGetLayout(ZDCommand command)
    {
        string xml = CoreHelper.GetLayout();
        ZDResult result = ZDResult.Success(0);
        result.AfterLayoutXml = xml;
        return result;
    }

    /// <summary>
    /// 执行截图命令
    /// </summary>
    private static ZDResult ExecuteScreenshot(ZDCommand command)
    {
        // 截图功能需要在 CoreHelper 中实现
        // 这里返回占位结果
        ZDResult result = ZDResult.Success(0);
        result.SetExtraData("screenshot_saved", "false");
        return result;
    }

    // ========== 批量执行 ==========

    /// <summary>
    /// 批量执行命令序列
    /// </summary>
    public static ZDResult[] ExecuteBatch(ZDCommand[] commands)
    {
        if (commands == null || commands.Length == 0)
        {
            return new ZDResult[0];
        }

        ZDResult[] results = new ZDResult[commands.Length];

        for (int i = 0; i < commands.Length; i++)
        {
            results[i] = ExecuteWithRetry(commands[i]);

            // 如果某个命令失败且是致命错误，停止执行
            if (results[i].IsFailed() && !results[i].CanRetry())
            {
                CoreHelper.LogErr(TAG, string.Format("批量执行在步骤 {0} 中止", i));
                break;
            }
        }

        return results;
    }

    // ========== 辅助方法 ==========

    /// <summary>
    /// 获取当前 UI 布局
    /// </summary>
    public static string GetCurrentLayout()
    {
        return CoreHelper.GetLayout();
    }

    /// <summary>
    /// 检查元素是否存在（通过坐标）
    /// </summary>
    public static bool ElementExistsAt(int x, int y)
    {
        // 简单实现：检查坐标是否有可点击元素
        string xml = CoreHelper.GetLayout();
        if (string.IsNullOrEmpty(xml))
        {
            return false;
        }

        // 检查 bounds 是否包含该坐标
        string pattern = string.Format("bounds=\"[{0},{1}]", x, y);
        return xml.IndexOf(pattern) >= 0;
    }
}
