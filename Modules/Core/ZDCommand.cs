// =====================================================
// ZDCommand.cs - ZennoDroid 命令类（"怎么做"）
// ⚠️ C# 5.0 语法 - 禁止使用 $""、?.、nameof() 等
// =====================================================
// v4.5.2 架构重构 - DPS "手"层
//
// ZDCommand 是对 ZennoDroid API 的抽象封装，
// 描述"怎么做"的具体物理操作。
//
// 设计原则：
//   1. ZDCommand 是 IntentTranslator 的输出
//   2. ZennoDroidAdapter 接收 ZDCommand 并执行
//   3. 命令执行失败时可重试或回退
// =====================================================

using System;
using System.Collections.Generic;

/// <summary>
/// ZennoDroid 命令类型
/// </summary>
public enum ZDCommandType
{
    /// <summary>点击操作</summary>
    Tap,
    /// <summary>长按操作</summary>
    LongPress,
    /// <summary>滑动操作</summary>
    Swipe,
    /// <summary>输入文本</summary>
    SendText,
    /// <summary>按返回键</summary>
    PressBack,
    /// <summary>等待/延迟</summary>
    Delay,
    /// <summary>执行 Shell 命令</summary>
    ShellCommand,
    /// <summary>获取 UI 布局</summary>
    GetLayout,
    /// <summary>截图</summary>
    Screenshot
}

/// <summary>
/// ZennoDroid 命令类 - 描述物理操作的参数
/// </summary>
public class ZDCommand
{
    private const string TAG = "ZDCommand";

    // ========== 属性 ==========

    /// <summary>
    /// 命令类型
    /// </summary>
    public ZDCommandType Type { get; set; }

    /// <summary>
    /// 命令描述（用于日志）
    /// </summary>
    public string Description { get; set; }

    /// <summary>
    /// 坐标参数（Tap, Swipe 使用）
    /// </summary>
    public int X1 { get; set; }
    public int Y1 { get; set; }
    public int X2 { get; set; }
    public int Y2 { get; set; }

    /// <summary>
    /// 持续时间（毫秒，Swipe 使用）
    /// </summary>
    public int Duration { get; set; }

    /// <summary>
    /// 文本内容（SendText, ShellCommand 使用）
    /// </summary>
    public string Text { get; set; }

    /// <summary>
    /// 是否启用人性化执行（随机偏移、可变速率）
    /// </summary>
    public bool Humanized { get; set; }

    /// <summary>
    /// 最大重试次数
    /// </summary>
    public int MaxRetries { get; set; }

    /// <summary>
    /// 重试延迟（毫秒）
    /// </summary>
    public int RetryDelay { get; set; }

    /// <summary>
    /// 扩展参数
    /// </summary>
    public Dictionary<string, string> ExtraParams { get; set; }

    // ========== 构造函数 ==========

    /// <summary>
    /// 默认构造函数
    /// </summary>
    public ZDCommand()
    {
        Type = ZDCommandType.Tap;
        Description = "";
        X1 = 0;
        Y1 = 0;
        X2 = 0;
        Y2 = 0;
        Duration = 300;
        Text = "";
        Humanized = false;
        MaxRetries = 1;
        RetryDelay = 1000;
        ExtraParams = new Dictionary<string, string>();
    }

    // ========== 工厂方法 ==========

    /// <summary>
    /// 创建点击命令
    /// </summary>
    public static ZDCommand CreateTap(int x, int y, bool humanized)
    {
        ZDCommand cmd = new ZDCommand();
        cmd.Type = ZDCommandType.Tap;
        cmd.Description = string.Format("点击 ({0}, {1})", x, y);
        cmd.X1 = x;
        cmd.Y1 = y;
        cmd.Humanized = humanized;
        return cmd;
    }

    /// <summary>
    /// 创建长按命令
    /// </summary>
    public static ZDCommand CreateLongPress(int x, int y, int duration)
    {
        ZDCommand cmd = new ZDCommand();
        cmd.Type = ZDCommandType.LongPress;
        cmd.Description = string.Format("长按 ({0}, {1}) {2}ms", x, y, duration);
        cmd.X1 = x;
        cmd.Y1 = y;
        cmd.Duration = duration;
        return cmd;
    }

    /// <summary>
    /// 创建滑动命令
    /// </summary>
    public static ZDCommand CreateSwipe(int x1, int y1, int x2, int y2, int duration, bool humanized)
    {
        ZDCommand cmd = new ZDCommand();
        cmd.Type = ZDCommandType.Swipe;
        cmd.Description = string.Format("滑动 ({0},{1})->({2},{3})", x1, y1, x2, y2);
        cmd.X1 = x1;
        cmd.Y1 = y1;
        cmd.X2 = x2;
        cmd.Y2 = y2;
        cmd.Duration = duration;
        cmd.Humanized = humanized;
        return cmd;
    }

    /// <summary>
    /// 创建输入文本命令
    /// </summary>
    public static ZDCommand CreateSendText(string text, bool humanized)
    {
        ZDCommand cmd = new ZDCommand();
        cmd.Type = ZDCommandType.SendText;
        cmd.Description = "输入: " + (text.Length > 20 ? text.Substring(0, 20) + "..." : text);
        cmd.Text = text;
        cmd.Humanized = humanized;
        return cmd;
    }

    /// <summary>
    /// 创建按返回键命令
    /// </summary>
    public static ZDCommand CreatePressBack()
    {
        ZDCommand cmd = new ZDCommand();
        cmd.Type = ZDCommandType.PressBack;
        cmd.Description = "按返回键";
        return cmd;
    }

    /// <summary>
    /// 创建延迟命令
    /// </summary>
    public static ZDCommand CreateDelay(int milliseconds)
    {
        ZDCommand cmd = new ZDCommand();
        cmd.Type = ZDCommandType.Delay;
        cmd.Description = "等待 " + milliseconds + "ms";
        cmd.ExtraParams["milliseconds"] = milliseconds.ToString();
        return cmd;
    }

    /// <summary>
    /// 创建 Shell 命令
    /// </summary>
    public static ZDCommand CreateShell(string shellCmd)
    {
        ZDCommand cmd = new ZDCommand();
        cmd.Type = ZDCommandType.ShellCommand;
        cmd.Description = "Shell: " + shellCmd;
        cmd.Text = shellCmd;
        return cmd;
    }

    /// <summary>
    /// 创建获取布局命令
    /// </summary>
    public static ZDCommand CreateGetLayout()
    {
        ZDCommand cmd = new ZDCommand();
        cmd.Type = ZDCommandType.GetLayout;
        cmd.Description = "获取 UI 布局";
        return cmd;
    }

    /// <summary>
    /// 创建截图命令
    /// </summary>
    public static ZDCommand CreateScreenshot()
    {
        ZDCommand cmd = new ZDCommand();
        cmd.Type = ZDCommandType.Screenshot;
        cmd.Description = "截图";
        return cmd;
    }

    // ========== 工具方法 ==========

    /// <summary>
    /// 获取扩展参数
    /// </summary>
    public string GetExtraParam(string key, string defaultValue)
    {
        if (ExtraParams == null)
        {
            return defaultValue;
        }
        if (ExtraParams.ContainsKey(key))
        {
            return ExtraParams[key];
        }
        return defaultValue;
    }

    /// <summary>
    /// 设置扩展参数
    /// </summary>
    public void SetExtraParam(string key, string value)
    {
        if (ExtraParams == null)
        {
            ExtraParams = new Dictionary<string, string>();
        }
        ExtraParams[key] = value;
    }

    /// <summary>
    /// 转换为日志字符串
    /// </summary>
    public string ToLogString()
    {
        if (!string.IsNullOrEmpty(Description))
        {
            return Description;
        }
        return string.Format("ZDCommand[{0}]", Type.ToString());
    }

    /// <summary>
    /// 深拷贝命令
    /// </summary>
    public ZDCommand Clone()
    {
        ZDCommand clone = new ZDCommand();
        clone.Type = Type;
        clone.Description = Description;
        clone.X1 = X1;
        clone.Y1 = Y1;
        clone.X2 = X2;
        clone.Y2 = Y2;
        clone.Duration = Duration;
        clone.Text = Text;
        clone.Humanized = Humanized;
        clone.MaxRetries = MaxRetries;
        clone.RetryDelay = RetryDelay;

        if (ExtraParams != null)
        {
            clone.ExtraParams = new Dictionary<string, string>();
            foreach (KeyValuePair<string, string> kvp in ExtraParams)
            {
                clone.ExtraParams[kvp.Key] = kvp.Value;
            }
        }

        return clone;
    }

    /// <summary>
    /// 检查命令是否有效
    /// </summary>
    public bool IsValid()
    {
        switch (Type)
        {
            case ZDCommandType.Tap:
            case ZDCommandType.LongPress:
                return X1 >= 0 && Y1 >= 0;

            case ZDCommandType.Swipe:
                return X1 >= 0 && Y1 >= 0 && X2 >= 0 && Y2 >= 0;

            case ZDCommandType.SendText:
                return !string.IsNullOrEmpty(Text);

            default:
                return true;
        }
    }
}
