// =====================================================
// ActionExecutor.cs - JSON 步骤解释执行器
// ⚠️ C# 5.0 语法 - 禁止使用 $""、?.、nameof() 等
// =====================================================
// 核心职责：
//   读取 operations JSON 中的步骤序列，逐步执行。
//   每个步骤是一个指令（find/tap/swipe/delay/type/verify/require/back/scroll/log）
//
// 操作 JSON 格式（来自 reddit_operations.json 等）：
//   {
//     "browse": {
//       "description": "浏览 feed 并滚动",
//       "require_page": "feed",
//       "steps": [
//         { "action": "find", "selector": "post_unit", "save_as": "posts" },
//         { "action": "scroll", "direction": "down", "distance": 500 },
//         { "action": "delay", "min_ms": 2000, "max_ms": 5000 },
//         { "action": "verify", "selector": "post_unit", "on_fail": "retry" }
//       ]
//     }
//   }
//
// 步骤指令：
//   find     — 查找元素，结果存入上下文
//   tap      — 点击元素（支持 humanized）
//   swipe    — 滑动（支持 humanized）
//   scroll   — 简化滑动（direction + distance）
//   delay    — 随机延迟
//   type     — 输入文本
//   verify   — 验证元素存在，on_fail: retry/skip/abort
//   require  — 检查页面类型，不匹配则 abort
//   back     — 按返回键
//   log      — 输出日志
//   set_var  — 设置 ZD 变量
// =====================================================
using System;
using System.Collections.Generic;

/// <summary>
/// JSON 步骤解释执行器
/// 从 operations JSON 读取步骤并逐步执行
/// </summary>
public class ActionExecutor
{
    private const string TAG = "ActionExecutor";
    private static Random _random = new Random();

    // 执行上下文：步骤间共享数据
    // key → value（bounds 字符串、元素计数等）
    private static Dictionary<string, string> _context = new Dictionary<string, string>();

    // ========== 主入口 ==========

    /// <summary>
    /// 执行一个操作（如 browse/like/comment）
    /// </summary>
    /// <param name="operationsJson">完整 operations JSON（包含所有操作定义）</param>
    /// <param name="operationName">要执行的操作名（如 "browse"）</param>
    /// <param name="platformConfig">平台配置 JSON（含 ui_selectors、page_signatures）</param>
    /// <returns>SUCCESS / ERROR:原因 / SKIP:原因</returns>
    public static string Execute(string operationsJson, string operationName, string platformConfig)
    {
        _context.Clear();

        if (string.IsNullOrEmpty(operationsJson))
        {
            return "ERROR:operations JSON 为空";
        }

        // 提取操作定义
        string opDef = JsonHelper.ExtractObject(operationsJson, operationName);
        if (string.IsNullOrEmpty(opDef))
        {
            CoreHelper.LogErr(TAG, "操作未定义: " + operationName);
            return "ERROR:操作未定义 " + operationName;
        }

        CoreHelper.Log(TAG, "开始执行操作: " + operationName);

        // 检查页面前置条件
        string requirePage = JsonHelper.Get(opDef, "require_page");
        if (!string.IsNullOrEmpty(requirePage))
        {
            string xml = CoreHelper.GetLayout();
            string signaturesJson = JsonHelper.ExtractObject(platformConfig, "page_signatures");
            string currentPage = PageDetector.Detect(xml, signaturesJson);

            if (currentPage != requirePage)
            {
                CoreHelper.Log(TAG, string.Format("页面不匹配: 需要 {0}, 当前 {1}", requirePage, currentPage));
                return "SKIP:页面不匹配 需要" + requirePage + " 当前" + currentPage;
            }
        }

        // 提取步骤数组
        string stepsArray = JsonHelper.ExtractArray(opDef, "steps");
        if (string.IsNullOrEmpty(stepsArray))
        {
            CoreHelper.LogWarn(TAG, "操作无步骤: " + operationName);
            return "SUCCESS";
        }

        // 解析并执行每个步骤
        string[] steps = ParseStepArray(stepsArray);
        string selectorsJson = JsonHelper.ExtractObject(platformConfig, "ui_selectors");

        for (int i = 0; i < steps.Length; i++)
        {
            string stepJson = steps[i];
            string action = JsonHelper.Get(stepJson, "action");

            if (string.IsNullOrEmpty(action))
            {
                CoreHelper.LogWarn(TAG, string.Format("步骤 {0} 缺少 action，跳过", i));
                continue;
            }

            CoreHelper.Log(TAG, string.Format("步骤 {0}/{1}: {2}", i + 1, steps.Length, action));

            string result = ExecuteStep(action, stepJson, selectorsJson, platformConfig);

            if (result.StartsWith("ABORT"))
            {
                CoreHelper.LogErr(TAG, "操作中止: " + result);
                return "ERROR:" + result;
            }
            // SKIP 只跳过当前步骤，继续下一步
        }

        CoreHelper.Log(TAG, "操作完成: " + operationName);
        return "SUCCESS";
    }

    // ========== 步骤分发 ==========

    /// <summary>
    /// 执行单个步骤
    /// </summary>
    private static string ExecuteStep(string action, string stepJson, string selectorsJson, string platformConfig)
    {
        try
        {
            if (action == "find") return StepFind(stepJson, selectorsJson);
            if (action == "tap") return StepTap(stepJson, selectorsJson);
            if (action == "swipe") return StepSwipe(stepJson);
            if (action == "scroll") return StepScroll(stepJson);
            if (action == "delay") return StepDelay(stepJson);
            if (action == "type") return StepType(stepJson);
            if (action == "verify") return StepVerify(stepJson, selectorsJson);
            if (action == "require") return StepRequire(stepJson, platformConfig);
            if (action == "back") return StepBack();
            if (action == "log") return StepLog(stepJson);
            if (action == "set_var") return StepSetVar(stepJson);
            if (action == "refresh_layout") return StepRefreshLayout();

            CoreHelper.LogWarn(TAG, "未知步骤类型: " + action);
            return "SKIP";
        }
        catch (Exception ex)
        {
            CoreHelper.LogErr(TAG, string.Format("步骤 {0} 异常: {1}", action, ex.Message));
            string onFail = JsonHelper.Get(stepJson, "on_fail");
            if (onFail == "abort") return "ABORT:" + ex.Message;
            if (onFail == "skip") return "SKIP";
            return "SKIP";
        }
    }

    // ========== 步骤实现 ==========

    /// <summary>
    /// find — 查找元素，结果存入上下文
    /// { "action": "find", "selector": "post_unit", "save_as": "posts", "on_fail": "skip" }
    /// selector 引用 ui_selectors 中的 key，或内联选择器 JSON
    /// </summary>
    private static string StepFind(string stepJson, string selectorsJson)
    {
        string selectorJson = ResolveSelector(stepJson, selectorsJson);
        if (string.IsNullOrEmpty(selectorJson))
        {
            return HandleOnFail(stepJson, "选择器解析失败");
        }

        string xml = CoreHelper.GetLayout();
        List<string> results = SelectorEngine.Find(xml, selectorJson);

        string saveAs = JsonHelper.Get(stepJson, "save_as");
        if (!string.IsNullOrEmpty(saveAs))
        {
            // 存储第一个结果的 bounds 和总数
            if (results.Count > 0)
            {
                _context[saveAs] = results[0];
                _context[saveAs + "_count"] = results.Count.ToString();
                // 存储所有结果（用 | 分隔）
                _context[saveAs + "_all"] = string.Join("|", results.ToArray());
            }
            else
            {
                _context[saveAs] = "";
                _context[saveAs + "_count"] = "0";
            }
        }

        if (results.Count == 0)
        {
            return HandleOnFail(stepJson, "元素未找到");
        }

        CoreHelper.Log(TAG, string.Format("find: 找到 {0} 个元素", results.Count));
        return "OK";
    }

    /// <summary>
    /// tap — 点击元素
    /// { "action": "tap", "selector": "like_button" }
    /// { "action": "tap", "context_ref": "posts" }  — 使用上下文中已找到的元素
    /// { "action": "tap", "x": 540, "y": 1200 }     — 绝对坐标
    /// </summary>
    private static string StepTap(string stepJson, string selectorsJson)
    {
        int[] center = ResolveTapTarget(stepJson, selectorsJson);
        if (center == null)
        {
            return HandleOnFail(stepJson, "tap 目标解析失败");
        }

        dynamic input = CoreHelper.GetInput();
        if (input == null)
        {
            return "ABORT:DroidInstance.Input 未初始化";
        }

        input.Tap(center[0], center[1]);
        CoreHelper.Log(TAG, string.Format("tap: ({0}, {1})", center[0], center[1]));
        return "OK";
    }

    /// <summary>
    /// swipe — 滑动
    /// { "action": "swipe", "x1": 540, "y1": 1600, "x2": 540, "y2": 800, "duration": 500 }
    /// </summary>
    private static string StepSwipe(string stepJson)
    {
        int x1 = JsonHelper.GetInt(stepJson, "x1", 540);
        int y1 = JsonHelper.GetInt(stepJson, "y1", 1600);
        int x2 = JsonHelper.GetInt(stepJson, "x2", 540);
        int y2 = JsonHelper.GetInt(stepJson, "y2", 800);
        int duration = JsonHelper.GetInt(stepJson, "duration", 500);

        dynamic input = CoreHelper.GetInput();
        if (input == null)
        {
            return "ABORT:DroidInstance.Input 未初始化";
        }

        input.Swipe(x1, y1, x2, y2, duration);
        CoreHelper.Log(TAG, string.Format("swipe: ({0},{1})->({2},{3}) {4}ms", x1, y1, x2, y2, duration));
        return "OK";
    }

    /// <summary>
    /// scroll — 简化滑动（方向 + 距离）
    /// { "action": "scroll", "direction": "down", "distance": 500, "duration": 400 }
    /// </summary>
    private static string StepScroll(string stepJson)
    {
        string direction = JsonHelper.Get(stepJson, "direction");
        if (string.IsNullOrEmpty(direction)) direction = "down";

        int distance = JsonHelper.GetInt(stepJson, "distance", 500);
        int duration = JsonHelper.GetInt(stepJson, "duration", 400);

        // 屏幕中心作为起点（默认 1080x2400 分辨率）
        int centerX = JsonHelper.GetInt(stepJson, "center_x", 540);
        int centerY = JsonHelper.GetInt(stepJson, "center_y", 1200);

        int x1 = centerX, y1 = centerY, x2 = centerX, y2 = centerY;

        if (direction == "down") { y1 = centerY + distance / 2; y2 = centerY - distance / 2; }
        else if (direction == "up") { y1 = centerY - distance / 2; y2 = centerY + distance / 2; }
        else if (direction == "left") { x1 = centerX + distance / 2; x2 = centerX - distance / 2; }
        else if (direction == "right") { x1 = centerX - distance / 2; x2 = centerX + distance / 2; }

        dynamic input = CoreHelper.GetInput();
        if (input == null)
        {
            return "ABORT:DroidInstance.Input 未初始化";
        }

        input.Swipe(x1, y1, x2, y2, duration);
        CoreHelper.Log(TAG, string.Format("scroll {0}: distance={1}", direction, distance));
        return "OK";
    }

    /// <summary>
    /// delay — 随机延迟
    /// { "action": "delay", "min_ms": 1000, "max_ms": 3000 }
    /// </summary>
    private static string StepDelay(string stepJson)
    {
        int minMs = JsonHelper.GetInt(stepJson, "min_ms", 1000);
        int maxMs = JsonHelper.GetInt(stepJson, "max_ms", 3000);
        if (maxMs < minMs) maxMs = minMs;

        int delayMs = _random.Next(minMs, maxMs + 1);
        System.Threading.Thread.Sleep(delayMs);
        CoreHelper.Log(TAG, string.Format("delay: {0}ms", delayMs));
        return "OK";
    }

    /// <summary>
    /// type — 输入文本
    /// { "action": "type", "text": "Hello world" }
    /// { "action": "type", "var": "comment_text" }  — 从 ZD 变量读取
    /// </summary>
    private static string StepType(string stepJson)
    {
        string text = JsonHelper.Get(stepJson, "text");
        if (string.IsNullOrEmpty(text))
        {
            string varName = JsonHelper.Get(stepJson, "var");
            if (!string.IsNullOrEmpty(varName))
            {
                text = CoreHelper.GetVar(varName, "");
            }
        }

        if (string.IsNullOrEmpty(text))
        {
            return HandleOnFail(stepJson, "type 无文本内容");
        }

        dynamic input = CoreHelper.GetInput();
        if (input == null)
        {
            return "ABORT:DroidInstance.Input 未初始化";
        }

        input.SendText(text);
        CoreHelper.Log(TAG, "type: " + text.Substring(0, Math.Min(text.Length, 30)) + "...");
        return "OK";
    }

    /// <summary>
    /// verify — 验证元素存在
    /// { "action": "verify", "selector": "post_unit", "on_fail": "retry", "max_retries": 3, "retry_delay_ms": 1000 }
    /// on_fail: skip（跳过）/ abort（中止）/ retry（重试，需配合 max_retries）
    /// </summary>
    private static string StepVerify(string stepJson, string selectorsJson)
    {
        string selectorJson = ResolveSelector(stepJson, selectorsJson);
        if (string.IsNullOrEmpty(selectorJson))
        {
            return HandleOnFail(stepJson, "verify 选择器解析失败");
        }

        string onFail = JsonHelper.Get(stepJson, "on_fail");
        int maxRetries = JsonHelper.GetInt(stepJson, "max_retries", 1);
        int retryDelay = JsonHelper.GetInt(stepJson, "retry_delay_ms", 1000);

        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            string xml = CoreHelper.GetLayout();
            if (SelectorEngine.Exists(xml, selectorJson))
            {
                CoreHelper.Log(TAG, "verify: 元素存在");
                return "OK";
            }

            if (attempt < maxRetries - 1)
            {
                CoreHelper.Log(TAG, string.Format("verify: 重试 {0}/{1}", attempt + 1, maxRetries));
                System.Threading.Thread.Sleep(retryDelay);
            }
        }

        return HandleOnFail(stepJson, "verify 失败: 元素不存在");
    }

    /// <summary>
    /// require — 检查当前页面类型
    /// { "action": "require", "page": "feed", "on_fail": "abort" }
    /// </summary>
    private static string StepRequire(string stepJson, string platformConfig)
    {
        string expectedPage = JsonHelper.Get(stepJson, "page");
        if (string.IsNullOrEmpty(expectedPage))
        {
            return "SKIP";
        }

        string xml = CoreHelper.GetLayout();
        string signaturesJson = JsonHelper.ExtractObject(platformConfig, "page_signatures");
        string currentPage = PageDetector.Detect(xml, signaturesJson);

        if (currentPage == expectedPage)
        {
            CoreHelper.Log(TAG, "require: 页面匹配 " + expectedPage);
            return "OK";
        }

        CoreHelper.Log(TAG, string.Format("require: 页面不匹配 需要={0} 当前={1}", expectedPage, currentPage));
        return HandleOnFail(stepJson, "页面不匹配");
    }

    /// <summary>
    /// back — 按返回键
    /// </summary>
    private static string StepBack()
    {
        dynamic input = CoreHelper.GetInput();
        if (input == null)
        {
            return "ABORT:DroidInstance.Input 未初始化";
        }

        input.Shell("input keyevent 4");
        CoreHelper.Log(TAG, "back: 按返回键");
        return "OK";
    }

    /// <summary>
    /// log — 输出日志
    /// { "action": "log", "message": "开始浏览" }
    /// </summary>
    private static string StepLog(string stepJson)
    {
        string message = JsonHelper.Get(stepJson, "message");
        if (!string.IsNullOrEmpty(message))
        {
            CoreHelper.Log(TAG, "[OP] " + message);
        }
        return "OK";
    }

    /// <summary>
    /// set_var — 设置 ZD 变量
    /// { "action": "set_var", "name": "action_result", "value": "SUCCESS" }
    /// { "action": "set_var", "name": "post_count", "context_ref": "posts_count" }
    /// </summary>
    private static string StepSetVar(string stepJson)
    {
        string name = JsonHelper.Get(stepJson, "name");
        if (string.IsNullOrEmpty(name)) return "SKIP";

        string value = JsonHelper.Get(stepJson, "value");
        if (string.IsNullOrEmpty(value))
        {
            string ctxRef = JsonHelper.Get(stepJson, "context_ref");
            if (!string.IsNullOrEmpty(ctxRef) && _context.ContainsKey(ctxRef))
            {
                value = _context[ctxRef];
            }
        }

        CoreHelper.SetVar(name, value ?? "");
        CoreHelper.Log(TAG, string.Format("set_var: {0}={1}", name, value));
        return "OK";
    }

    /// <summary>
    /// refresh_layout — 刷新 UI 布局缓存
    /// </summary>
    private static string StepRefreshLayout()
    {
        CoreHelper.GetLayout(); // 触发重新获取
        CoreHelper.Log(TAG, "refresh_layout: 已刷新");
        return "OK";
    }

    // ========== 辅助方法 ==========

    /// <summary>
    /// 解析选择器：支持引用 ui_selectors 中的 key 或内联 JSON
    /// </summary>
    private static string ResolveSelector(string stepJson, string selectorsJson)
    {
        // 方式1：引用 ui_selectors 中的 key
        string selectorKey = JsonHelper.Get(stepJson, "selector");
        if (!string.IsNullOrEmpty(selectorKey))
        {
            // 先尝试从 ui_selectors 中查找
            if (!string.IsNullOrEmpty(selectorsJson))
            {
                string resolved = JsonHelper.ExtractObject(selectorsJson, selectorKey);
                if (!string.IsNullOrEmpty(resolved))
                {
                    return resolved;
                }
            }

            // 如果 ui_selectors 中没有，当作 resource-id 直接使用
            return SelectorEngine.BuildSelector("resource-id", selectorKey);
        }

        // 方式2：内联选择器
        string inlineSelector = JsonHelper.ExtractObject(stepJson, "selector_inline");
        if (!string.IsNullOrEmpty(inlineSelector))
        {
            return inlineSelector;
        }

        return null;
    }

    /// <summary>
    /// 解析 tap 目标坐标
    /// 优先级：context_ref → selector → 绝对坐标(x,y)
    /// </summary>
    private static int[] ResolveTapTarget(string stepJson, string selectorsJson)
    {
        // 方式1：从上下文引用
        string ctxRef = JsonHelper.Get(stepJson, "context_ref");
        if (!string.IsNullOrEmpty(ctxRef) && _context.ContainsKey(ctxRef))
        {
            string boundsStr = _context[ctxRef];
            int[] bounds = SelectorEngine.ParseBounds(boundsStr);
            if (bounds != null)
            {
                return new int[] { (bounds[0] + bounds[2]) / 2, (bounds[1] + bounds[3]) / 2 };
            }
        }

        // 方式2：通过选择器实时查找
        string selectorJson = ResolveSelector(stepJson, selectorsJson);
        if (!string.IsNullOrEmpty(selectorJson))
        {
            string xml = CoreHelper.GetLayout();
            int[] center = SelectorEngine.FindCenter(xml, selectorJson);
            if (center != null)
            {
                return center;
            }
        }

        // 方式3：绝对坐标
        string xStr = JsonHelper.Get(stepJson, "x");
        string yStr = JsonHelper.Get(stepJson, "y");
        if (!string.IsNullOrEmpty(xStr) && !string.IsNullOrEmpty(yStr))
        {
            int x = 0, y = 0;
            if (int.TryParse(xStr, out x) && int.TryParse(yStr, out y))
            {
                return new int[] { x, y };
            }
        }

        return null;
    }

    /// <summary>
    /// 处理 on_fail 策略
    /// </summary>
    private static string HandleOnFail(string stepJson, string reason)
    {
        string onFail = JsonHelper.Get(stepJson, "on_fail");
        if (string.IsNullOrEmpty(onFail)) onFail = "skip";

        if (onFail == "abort")
        {
            return "ABORT:" + reason;
        }
        // skip 或其他 → 跳过当前步骤
        CoreHelper.Log(TAG, "步骤跳过: " + reason);
        return "SKIP";
    }

    /// <summary>
    /// 解析步骤数组 JSON → 字符串数组
    /// </summary>
    private static string[] ParseStepArray(string arrayJson)
    {
        var results = new List<string>();
        if (string.IsNullOrEmpty(arrayJson)) return results.ToArray();

        int i = arrayJson.IndexOf('[');
        if (i < 0) return results.ToArray();
        i++;

        while (i < arrayJson.Length)
        {
            while (i < arrayJson.Length && (char.IsWhiteSpace(arrayJson[i]) || arrayJson[i] == ',')) i++;
            if (i >= arrayJson.Length) break;
            if (arrayJson[i] == ']') break;

            if (arrayJson[i] == '{')
            {
                int objStart = i;
                int depth = 1;
                i++;
                while (i < arrayJson.Length && depth > 0)
                {
                    char c = arrayJson[i];
                    if (c == '"')
                    {
                        i++;
                        while (i < arrayJson.Length)
                        {
                            if (arrayJson[i] == '"') { i++; break; }
                            if (arrayJson[i] == '\\' && i + 1 < arrayJson.Length) i++;
                            i++;
                        }
                    }
                    else if (c == '{') { depth++; i++; }
                    else if (c == '}') { depth--; i++; }
                    else { i++; }
                }
                results.Add(arrayJson.Substring(objStart, i - objStart));
            }
            else
            {
                i++;
            }
        }

        return results.ToArray();
    }

    /// <summary>
    /// 获取上下文变量（供外部查询）
    /// </summary>
    public static string GetContext(string key)
    {
        if (_context.ContainsKey(key))
        {
            return _context[key];
        }
        return "";
    }

    /// <summary>
    /// 设置上下文变量（供外部注入）
    /// </summary>
    public static void SetContext(string key, string value)
    {
        _context[key] = value;
    }

    /// <summary>
    /// 清空上下文
    /// </summary>
    public static void ClearContext()
    {
        _context.Clear();
    }
}
