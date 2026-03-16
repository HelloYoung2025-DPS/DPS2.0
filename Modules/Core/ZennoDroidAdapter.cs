// =====================================================
// ZennoDroidAdapter.cs - ZennoDroid API 适配器
// ⚠️ C# 5.0 语法 - 禁止使用 $""、?.、nameof() 等
// v4.5.11 - 清理重复方法块并统一执行/截图逻辑
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
    private static bool _fastMode = true;

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
    public static ZDResult Execute(ZDCommand command, bool captureOnSuccess)
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
        }
        catch (Exception ex)
        {
            CoreHelper.LogErr(TAG, "命令执行异常: " + ex.Message);
            result = ZDResult.FromException(ex);
            AttachScreenshot(result, false, true);
        }

        if (result == null)
        {
            result = ZDResult.FailedRetryable("命令执行未返回结果");
        }

        TimeSpan elapsed = DateTime.Now - startTime;
        result.ElapsedMilliseconds = (long)elapsed.TotalMilliseconds;
        result.AttemptCount = 1;

        CoreHelper.Log(TAG, "命令完成: " + result.ToLogString());
        return result;
    }

    /// <summary>
    /// 执行单个 ZDCommand（默认成功时不截图）
    /// </summary>
    public static ZDResult Execute(ZDCommand command)
    {
        return Execute(command, false);
    }

    /// <summary>
    /// 执行命令（带重试）
    /// </summary>
    public static ZDResult ExecuteWithRetry(ZDCommand command)
    {
        if (command == null)
        {
            return ZDResult.FailedRetryable("命令为 null");
        }

        int maxRetries = command.MaxRetries > 0 ? command.MaxRetries : 1;
        int retryDelay = command.RetryDelay > 0 ? command.RetryDelay : 1000;
        ZDResult lastResult = null;

        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            lastResult = Execute(command, false);
            if (lastResult != null)
            {
                lastResult.AttemptCount = attempt + 1;
            }

            if (lastResult == null || lastResult.IsSuccess())
            {
                return lastResult;
            }

            if (!lastResult.CanRetry())
            {
                return lastResult;
            }

            if (attempt < maxRetries - 1)
            {
                CoreHelper.Log(TAG, string.Format("重试 {0}/{1}: {2}",
                    attempt + 1, maxRetries, lastResult.ErrorMessage));
                Thread.Sleep(retryDelay);
            }
        }

        return lastResult;
    }

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
            int offset = _random.Next(5, 16);
            x += _random.Next(-offset, offset + 1);
            y += _random.Next(-offset, offset + 1);
            Thread.Sleep(_random.Next(50, 201));
        }

        if (x < 0) x = 0;
        if (y < 0) y = 0;

        input.Tap(x, y);

        ZDResult result = ZDResult.Success(0);
        AttachScreenshot(result, captureOnSuccess, false);
        return result;
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

        int duration = command.Duration > 0 ? command.Duration : 1000;
        if (command.Humanized)
        {
            Thread.Sleep(_random.Next(50, 151));
        }

        input.LongTap(command.X1, command.Y1);
        Thread.Sleep(duration);

        ZDResult result = ZDResult.Success(duration);
        AttachScreenshot(result, captureOnSuccess, false);
        return result;
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
            int jitter = _random.Next(3, 16);
            x1 += _random.Next(-jitter, jitter + 1);
            y1 += _random.Next(-jitter, jitter + 1);
            x2 += _random.Next(-jitter, jitter + 1);
            y2 += _random.Next(-jitter, jitter + 1);
            Thread.Sleep(_random.Next(50, 151));
        }

        input.Swipe(x1, y1, x2, y2, duration);

        ZDResult result = ZDResult.Success(duration);
        AttachScreenshot(result, captureOnSuccess, false);
        return result;
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
            for (int i = 0; i < text.Length; i++)
            {
                input.SendText(text[i].ToString());
                Thread.Sleep(_random.Next(50, 151));
            }
        }
        else
        {
            input.SendText(text);
        }

        ZDResult result = ZDResult.Success(0);
        AttachScreenshot(result, captureOnSuccess, false);
        return result;
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
        AttachScreenshot(result, captureOnSuccess, false);
        return result;
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
            int parsed = 0;
            if (int.TryParse(msStr, out parsed))
            {
                ms = parsed;
            }
        }

        if (ms < 0) ms = 0;

        if (command.Humanized)
        {
            int variance = ms / 10;
            if (variance > 0)
            {
                ms += _random.Next(-variance, variance + 1);
            }
            if (ms < 0) ms = 0;
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

        if (string.IsNullOrEmpty(command.Text))
        {
            return ZDResult.FailedRetryable("Shell 命令为空");
        }

        input.Shell(command.Text);

        ZDResult result = ZDResult.Success(0);
        AttachScreenshot(result, captureOnSuccess, false);
        return result;
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
        string path = VisionCorrector.CaptureScreenshot();
        if (string.IsNullOrEmpty(path))
        {
            return ZDResult.FailedRetryable("截图失败");
        }

        ZDResult result = ZDResult.Success(0);
        result.ScreenshotPath = path;
        return result;
    }

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
            if (results[i] != null && results[i].IsFailed() && !results[i].CanRetry())
            {
                CoreHelper.LogErr(TAG, string.Format("批量执行在步骤 {0} 中止", i));
                break;
            }
        }

        return results;
    }

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
        string xml = CoreHelper.GetLayout();
        if (string.IsNullOrEmpty(xml))
        {
            return false;
        }

        int pos = 0;
        while (pos < xml.Length)
        {
            int boundsPos = xml.IndexOf("bounds=\"", pos, StringComparison.Ordinal);
            if (boundsPos < 0)
            {
                break;
            }

            int valueStart = boundsPos + 8;
            int valueEnd = xml.IndexOf("\"", valueStart, StringComparison.Ordinal);
            if (valueEnd <= valueStart)
            {
                break;
            }

            string bounds = xml.Substring(valueStart, valueEnd - valueStart);
            int[] parsed = SelectorEngine.ParseBounds(bounds);
            if (parsed != null &&
                x >= parsed[0] && x <= parsed[2] &&
                y >= parsed[1] && y <= parsed[3])
            {
                return true;
            }

            pos = valueEnd + 1;
        }

        return false;
    }

    /// <summary>
    /// 根据模式附加截图
    /// </summary>
    private static void AttachScreenshot(ZDResult result, bool captureOnSuccess, bool forceCapture)
    {
        if (result == null)
        {
            return;
        }

        if (!forceCapture && _fastMode && !captureOnSuccess)
        {
            return;
        }

        string path = VisionCorrector.CaptureScreenshot();
        if (!string.IsNullOrEmpty(path))
        {
            result.ScreenshotPath = path;
        }
    }
}
