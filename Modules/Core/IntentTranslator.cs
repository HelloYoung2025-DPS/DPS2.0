// =====================================================
// IntentTranslator.cs - 意图翻译器（"大脑"到"手"的桥梁）
// ⚠️ C# 5.0 语法 - 禁止使用 $""、?.、nameof() 等
// =====================================================
// v4.5.2 架构重构 - DPS "翻译"层
//
// IntentTranslator 是 DPS 架构中的"翻译层"，
// 将高层 Intent 翻译为具体的 ZDCommand。
//
// 设计原则：
//   1. 保留元素定位逻辑（SelectorEngine 调用）
//   2. 保留坐标计算逻辑（ParseBounds 等）
//   3. 输出 ZDCommand 而非直接执行
//   4. 支持回退链（fallback chain）
// =====================================================

using System;
using System.Collections.Generic;

/// <summary>
/// 意图翻译器 - 将 Intent 转换为 ZDCommand
/// </summary>
public class IntentTranslator
{
    private const string TAG = "IntentTranslator";
    private static Random _random = new Random();

    // ========== 核心翻译方法 ==========

    /// <summary>
    /// 将 Intent 翻译为 ZDCommand
    /// </summary>
    /// <param name="intent">要翻译的意图</param>
    /// <param name="selectorsJson">UI 选择器配置</param>
    /// <param name="context">操作上下文</param>
    /// <returns>翻译后的命令（可能为 null）</returns>
    public static ZDCommand Translate(Intent intent, string selectorsJson, OperationContext context)
    {
        if (intent == null)
        {
            CoreHelper.LogErr(TAG, "Intent 为 null");
            return null;
        }

        if (!intent.IsValid())
        {
            CoreHelper.LogErr(TAG, "Intent 无效: " + intent.ToLogString());
            return null;
        }

        CoreHelper.Log(TAG, "翻译意图: " + intent.ToLogString());

        ZDCommand command = null;

        switch (intent.Action)
        {
            case "tap":
                command = TranslateTap(intent, selectorsJson, context);
                break;

            case "long_press":
                command = TranslateLongPress(intent, selectorsJson, context);
                break;

            case "swipe":
            case "scroll":
                command = TranslateSwipe(intent, selectorsJson, context);
                break;

            case "type_text":
                command = TranslateTypeText(intent, context);
                break;

            case "back":
                command = TranslateBack();
                break;

            case "delay":
            case "wait":
                command = TranslateDelay(intent);
                break;

            case "verify":
                command = TranslateVerify(intent, selectorsJson, context);
                break;

            case "find":
                command = TranslateFind(intent, selectorsJson, context);
                break;

            default:
                CoreHelper.LogWarn(TAG, "未知意图类型: " + intent.Action);
                break;
        }

        if (command != null)
        {
            command.Humanized = intent.Humanized;
            command.MaxRetries = GetMaxRetries(intent);
            command.RetryDelay = GetRetryDelay(intent);
        }

        return command;
    }

    /// <summary>
    /// 将 Intent 翻译为 ZDCommand 序列
    /// </summary>
    public static ZDCommand[] TranslateSequence(Intent[] intents, string selectorsJson, OperationContext context)
    {
        if (intents == null || intents.Length == 0)
        {
            return new ZDCommand[0];
        }

        List<ZDCommand> commands = new List<ZDCommand>();

        for (int i = 0; i < intents.Length; i++)
        {
            Intent intent = intents[i];
            ZDCommand cmd = Translate(intent, selectorsJson, context);
            if (cmd != null)
            {
                commands.Add(cmd);
            }
        }

        return commands.ToArray();
    }

    // ========== 具体意图翻译 ==========

    /// <summary>
    /// 翻译点击意图
    /// </summary>
    private static ZDCommand TranslateTap(Intent intent, string selectorsJson, OperationContext context)
    {
        // 优先级 1: 从上下文引用获取坐标
        if (!string.IsNullOrEmpty(intent.ContextRef))
        {
            string boundsStr = context.GetVariable(intent.ContextRef, "");
            if (!string.IsNullOrEmpty(boundsStr))
            {
                int[] bounds = SelectorEngine.ParseBounds(boundsStr);
                if (bounds != null)
                {
                    int centerX = (bounds[0] + bounds[2]) / 2;
                    int centerY = (bounds[1] + bounds[3]) / 2;
                    return ZDCommand.CreateTap(centerX, centerY, intent.Humanized);
                }
            }
        }

        // 优先级 2: 通过选择器查找
        if (!string.IsNullOrEmpty(intent.Target))
        {
            string selectorJson = ResolveSelector(intent.Target, selectorsJson);
            if (!string.IsNullOrEmpty(selectorJson))
            {
                string xml = CoreHelper.GetLayout();
                int[] center = SelectorEngine.FindCenter(xml, selectorJson);
                if (center != null)
                {
                    return ZDCommand.CreateTap(center[0], center[1], intent.Humanized);
                }
            }
        }

        // 优先级 3: 从参数获取坐标
        string xStr = intent.GetParameter("x", "");
        string yStr = intent.GetParameter("y", "");
        if (!string.IsNullOrEmpty(xStr) && !string.IsNullOrEmpty(yStr))
        {
            int x, y;
            if (int.TryParse(xStr, out x) && int.TryParse(yStr, out y))
            {
                return ZDCommand.CreateTap(x, y, intent.Humanized);
            }
        }

        CoreHelper.LogWarn(TAG, "无法解析 tap 目标");
        return null;
    }

    /// <summary>
    /// 翻译长按意图
    /// </summary>
    private static ZDCommand TranslateLongPress(Intent intent, string selectorsJson, OperationContext context)
    {
        ZDCommand tapCmd = TranslateTap(intent, selectorsJson, context);
        if (tapCmd == null)
        {
            return null;
        }

        int duration = intent.GetParameter("duration", 1000);
        return ZDCommand.CreateLongPress(tapCmd.X1, tapCmd.Y1, duration);
    }

    /// <summary>
    /// 翻译滑动意图
    /// </summary>
    private static ZDCommand TranslateSwipe(Intent intent, string selectorsJson, OperationContext context)
    {
        // 从参数获取滑动参数
        string direction = intent.GetParameter("direction", "down");
        int distance = intent.GetParameterInt("distance", 500);
        int duration = intent.GetParameterInt("duration", 400);

        // 默认中心点
        int centerX = intent.GetParameterInt("center_x", 540);
        int centerY = intent.GetParameterInt("center_y", 1200);

        int x1 = centerX, y1 = centerY, x2 = centerX, y2 = centerY;

        if (direction == "down")
        {
            y1 = centerY + distance / 2;
            y2 = centerY - distance / 2;
        }
        else if (direction == "up")
        {
            y1 = centerY - distance / 2;
            y2 = centerY + distance / 2;
        }
        else if (direction == "left")
        {
            x1 = centerX + distance / 2;
            x2 = centerX - distance / 2;
        }
        else if (direction == "right")
        {
            x1 = centerX - distance / 2;
            x2 = centerX + distance / 2;
        }

        return ZDCommand.CreateSwipe(x1, y1, x2, y2, duration, intent.Humanized);
    }

    /// <summary>
    /// 翻译输入文本意图
    /// </summary>
    private static ZDCommand TranslateTypeText(Intent intent, OperationContext context)
    {
        string text = intent.GetParameter("text", "");

        // 如果没有 text，尝试从 var 参数读取
        if (string.IsNullOrEmpty(text))
        {
            string varName = intent.GetParameter("var", "");
            if (!string.IsNullOrEmpty(varName))
            {
                text = CoreHelper.GetVar(varName, "");
            }
        }

        if (string.IsNullOrEmpty(text))
        {
            CoreHelper.LogWarn(TAG, "type_text 缺少文本内容");
            return null;
        }

        return ZDCommand.CreateSendText(text, intent.Humanized);
    }

    /// <summary>
    /// 翻译返回意图
    /// </summary>
    private static ZDCommand TranslateBack()
    {
        return ZDCommand.CreatePressBack();
    }

    /// <summary>
    /// 翻译延迟意图
    /// </summary>
    private static ZDCommand TranslateDelay(Intent intent)
    {
        int minMs = intent.GetParameterInt("min_ms", 1000);
        int maxMs = intent.GetParameterInt("max_ms", 3000);

        if (maxMs < minMs) maxMs = minMs;

        int delayMs = _random.Next(minMs, maxMs + 1);

        return ZDCommand.CreateDelay(delayMs);
    }

    /// <summary>
    /// 翻译验证意图
    /// </summary>
    private static ZDCommand TranslateVerify(Intent intent, string selectorsJson, OperationContext context)
    {
        string selectorJson = ResolveSelector(intent.Target, selectorsJson);
        if (string.IsNullOrEmpty(selectorJson))
        {
            return null;
        }

        // verify 转换为 find + 延迟（模拟验证后的等待）
        string xml = CoreHelper.GetLayout();
        bool exists = SelectorEngine.Exists(xml, selectorJson);

        if (!exists)
        {
            CoreHelper.Log(TAG, "verify: 元素不存在: " + intent.Target);
        }

        // 返回一个短延迟，表示验证完成
        return ZDCommand.CreateDelay(100);
    }

    /// <summary>
    /// 翻译查找意图
    /// </summary>
    private static ZDCommand TranslateFind(Intent intent, string selectorsJson, OperationContext context)
    {
        string selectorJson = ResolveSelector(intent.Target, selectorsJson);
        if (string.IsNullOrEmpty(selectorJson))
        {
            return null;
        }

        string xml = CoreHelper.GetLayout();
        List<string> results = SelectorEngine.Find(xml, selectorJson);

        string saveAs = intent.SaveAs;
        if (!string.IsNullOrEmpty(saveAs))
        {
            if (results.Count > 0)
            {
                context.SetVariable(saveAs, results[0]);
                context.SetVariable(saveAs + "_count", results.Count.ToString());
            }
            else
            {
                context.SetVariable(saveAs, "");
                context.SetVariable(saveAs + "_count", "0");
            }
        }

        CoreHelper.Log(TAG, string.Format("find: 找到 {0} 个元素", results.Count));

        // find 不产生物理命令，返回一个标记命令
        ZDCommand cmd = ZDCommand.CreateDelay(0);
        cmd.SetExtraParam("__find_result__", results.Count.ToString());
        return cmd;
    }

    // ========== 回退链处理 ==========

    /// <summary>
    /// 将 Intent（含回退链）翻译为命令序列
    /// </summary>
    public static ZDCommand[] TranslateWithFallback(Intent intent, string selectorsJson, OperationContext context)
    {
        List<ZDCommand> commands = new List<ZDCommand>();

        // 翻译主意图
        ZDCommand primaryCmd = Translate(intent, selectorsJson, context);
        if (primaryCmd != null)
        {
            commands.Add(primaryCmd);
        }

        // 翻译回退意图
        if (intent.FallbackIntents != null && intent.FallbackIntents.Count > 0)
        {
            foreach (Intent fallback in intent.FallbackIntents)
            {
                ZDCommand fbCmd = Translate(fallback, selectorsJson, context);
                if (fbCmd != null)
                {
                    commands.Add(fbCmd);
                }
            }
        }

        return commands.ToArray();
    }

    // ========== 辅助方法 ==========

    /// <summary>
    /// 解析选择器：支持引用 ui_selectors 中的 key
    /// </summary>
    private static string ResolveSelector(string selectorKey, string selectorsJson)
    {
        if (string.IsNullOrEmpty(selectorKey))
        {
            return null;
        }

        // 尝试从 ui_selectors 中查找
        if (!string.IsNullOrEmpty(selectorsJson))
        {
            string resolved = JsonHelper.ExtractObject(selectorsJson, selectorKey);
            if (!string.IsNullOrEmpty(resolved))
            {
                return resolved;
            }
        }

        // 当作 resource-id 直接使用
        return SelectorEngine.BuildSelector("resource-id", selectorKey);
    }

    /// <summary>
    /// 获取最大重试次数
    /// </summary>
    private static int GetMaxRetries(Intent intent)
    {
        if (intent.OnFail == "retry")
        {
            return intent.GetParameterInt("max_retries", 3);
        }
        return 1;
    }

    /// <summary>
    /// 获取重试延迟
    /// </summary>
    private static int GetRetryDelay(Intent intent)
    {
        return intent.GetParameterInt("retry_delay_ms", 1000);
    }

    // ========== 便捷方法 ==========

    /// <summary>
    /// 从 ActionExecutor stepJson 创建 Intent 并翻译
    /// </summary>
    public static ZDCommand TranslateFromStepJson(string stepJson, string selectorsJson, OperationContext context)
    {
        Intent intent = Intent.FromStepJson(stepJson);
        return Translate(intent, selectorsJson, context);
    }

    /// <summary>
    /// 快速创建点击命令（从选择器）
    /// </summary>
    public static ZDCommand QuickTap(string selectorKey, string selectorsJson, bool humanized)
    {
        Intent intent = new Intent("tap", selectorKey);
        intent.Humanized = humanized;
        return Translate(intent, selectorsJson, new OperationContext("quick"));
    }
}

// Intent 扩展方法（C# 5.0 兼容）
public static class IntentExtensions
{
    /// <summary>
    /// 获取整数参数
    /// </summary>
    public static int GetParameterInt(this Intent intent, string key, int defaultValue)
    {
        string strValue = intent.GetParameter(key, "");
        if (string.IsNullOrEmpty(strValue))
        {
            return defaultValue;
        }

        int result;
        if (int.TryParse(strValue, out result))
        {
            return result;
        }
        return defaultValue;
    }
}
