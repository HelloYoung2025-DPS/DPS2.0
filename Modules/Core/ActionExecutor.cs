// =====================================================
// ActionExecutor.cs - JSON 步骤解释执行器
// ⚠️ C# 5.0 语法 - 禁止使用 $""、?.、nameof() 等
// =====================================================
// 核心职责：
//   读取 operations JSON 中的步骤序列，逐步执行。
//   每个步骤是一个指令（find/tap/swipe/delay/type/verify/require/back/scroll/log/foreach/call_operation）
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
//   foreach  — 循环遍历多个元素
//   call_operation — 调用其他操作（支持递归，最大深度 5）
// =====================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

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
    /// 执行一个操作（如 browse/like/comment）- 向后兼容版本（无 OperationContext）
    /// </summary>
    /// <param name="operationsJson">完整 operations JSON（包含所有操作定义）</param>
    /// <param name="operationName">要执行的操作名（如 "browse"）</param>
    /// <param name="platformConfig">平台配置 JSON（含 ui_selectors、page_signatures）</param>
    /// <returns>SUCCESS / ERROR:原因 / SKIP:原因</returns>
    public static string Execute(string operationsJson, string operationName, string platformConfig)
    {
        // 创建默认 OperationContext
        string deviceId = CoreHelper.GetVar("device_id", "default");
        OperationContext context = new OperationContext(deviceId);
        return Execute(operationsJson, operationName, platformConfig, context);
    }

    /// <summary>
    /// 执行一个操作（如 browse/like/comment）- 新签名（支持 OperationContext）
    /// </summary>
    /// <summary>
    /// 执行一个操作（如 browse/like/comment）
    /// </summary>
    /// <param name="operationsJson">完整 operations JSON（包含所有操作定义）</param>
    /// <param name="operationName">要执行的操作名（如 "browse"）</param>
    /// <param name="platformConfig">平台配置 JSON（含 ui_selectors、page_signatures）</param>
    /// <returns>SUCCESS / ERROR:原因 / SKIP:原因</returns>
    public static string Execute(string operationsJson, string operationName, string platformConfig, OperationContext context)
    {
        if (context == null)
        {
            return "ERROR:OperationContext 为 null";
        }

        // 顶层调用清理上下文；递归 call_operation 复用当前上下文变量
        if (context.CallDepth == 0)
        {
            context.ClearVariables();
        }

        if (string.IsNullOrEmpty(operationsJson))
        {
            SyncLegacyContext(context);
            return "ERROR:operations JSON 为空";
        }

        // 提取操作定义
        string opDef = JsonHelper.ExtractObject(operationsJson, operationName);
        if (string.IsNullOrEmpty(opDef))
        {
            CoreHelper.LogErr(TAG, "操作未定义: " + operationName);
            SyncLegacyContext(context);
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
                SyncLegacyContext(context);
                return "SKIP:页面不匹配 需要" + requirePage + " 当前" + currentPage;
            }
        }

        // 提取步骤数组
        string stepsArray = JsonHelper.ExtractArray(opDef, "steps");
        if (string.IsNullOrEmpty(stepsArray))
        {
            CoreHelper.LogWarn(TAG, "操作无步骤: " + operationName);
            SyncLegacyContext(context);
            return "SUCCESS";
        }

        // 解析并执行每个步骤
        string[] steps = ParseStepArray(stepsArray);
        string selectorsJson = JsonHelper.ExtractObject(platformConfig, "ui_selectors");

        int skippedSteps = 0;
        int totalSteps = steps.Length;

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

            string result = ExecuteStep(action, stepJson, selectorsJson, platformConfig, operationsJson, context);

            if (result.StartsWith("ABORT"))
            {
                CoreHelper.LogErr(TAG, "操作中止: " + result);
                SyncLegacyContext(context);
                return "ERROR:" + result;
            }

            if (result.StartsWith("SKIP"))
            {
                skippedSteps++;
                CoreHelper.LogWarn(TAG, string.Format("步骤 {0} 跳过: {1}", i + 1, result));
                
                // 关键步骤（require）的 SKIP 会中断操作
                if (action == "require")
                {
                    CoreHelper.LogWarn(TAG, "require 步骤失败，中止操作");
                    SyncLegacyContext(context);
                    return result; // 返回 SKIP:原因
                }
                // 非关键步骤的 SKIP 继续执行
            }

            if (result.StartsWith("ERROR"))
            {
                CoreHelper.LogErr(TAG, string.Format("步骤 {0} 失败: {1}", i + 1, result));
                string onFail = JsonHelper.Get(stepJson, "on_fail");
                if (string.IsNullOrEmpty(onFail)) onFail = "abort";
                if (onFail == "abort")
                {
                    SyncLegacyContext(context);
                    return result;
                }
                else if (onFail == "skip")
                {
                    skippedSteps++;
                    CoreHelper.LogWarn(TAG, "步骤失败但配置为跳过，继续执行");
                    continue;
                }
            }
        }

        // 如果所有步骤都被跳过，返回 SKIP
        if (skippedSteps == totalSteps && totalSteps > 0)
        {
            CoreHelper.LogWarn(TAG, string.Format("操作 {0} 的所有步骤都被跳过", operationName));
            SyncLegacyContext(context);
            return "SKIP:all_steps_skipped";
        }

        CoreHelper.Log(TAG, string.Format("操作完成: {0} (跳过 {1}/{2} 步骤)", operationName, skippedSteps, totalSteps));
        SyncLegacyContext(context);
        return "SUCCESS";
    }

    // ========== 步骤分发 ==========

    /// <summary>
    /// 执行单个步骤
    /// </summary>
    private static string ExecuteStep(string action, string stepJson, string selectorsJson, string platformConfig, string operationsJson, OperationContext context)
    {
        try
        {
            if (action == "find") return StepFind(stepJson, selectorsJson, context);
            if (action == "tap") return StepTap(stepJson, selectorsJson, context);
            if (action == "swipe") return StepSwipe(stepJson);
            if (action == "scroll") return StepScroll(stepJson);
            if (action == "delay") return StepDelay(stepJson);
            if (action == "type") return StepType(stepJson);
            if (action == "verify") return StepVerify(stepJson, selectorsJson);
            if (action == "require") return StepRequire(stepJson, platformConfig);
            if (action == "refresh_layout") return StepRefreshLayout();
            if (action == "foreach") return ExecuteForeach(stepJson, selectorsJson, platformConfig, operationsJson, context);
            if (action == "back") return StepBack();
            if (action == "log") return StepLog(stepJson);
            if (action == "set_var") return StepSetVar(stepJson, context);
            if (action == "call_operation") return ExecuteCallOperation(stepJson, operationsJson, platformConfig, context);
            if (action == "if_exists") return ExecuteIfExists(stepJson, selectorsJson, platformConfig, operationsJson, context);
            if (action == "random_pick") return ExecuteRandomPick(stepJson, selectorsJson, platformConfig, operationsJson, context);
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
    private static string StepFind(string stepJson, string selectorsJson, OperationContext context)
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
                context.SetVariable(saveAs, results[0]);
                context.SetVariable(saveAs + "_count", results.Count.ToString());
                // 存储所有结果（用 | 分隔）
                context.SetVariable(saveAs + "_all", string.Join("|", results.ToArray()));

                // 同步节点语义文本，供 SessionRunner/RuleEngine 读取
                string firstNode = FindNodeByBounds(xml, results[0]);
                string nodeText = "";
                string nodeDesc = "";
                if (!string.IsNullOrEmpty(firstNode))
                {
                    nodeText = SelectorEngine.ExtractAttr(firstNode, "text");
                    nodeDesc = SelectorEngine.ExtractAttr(firstNode, "content-desc");
                }
                context.SetVariable(saveAs + "_text", nodeText);
                context.SetVariable(saveAs + "_desc", nodeDesc);
                SyncSemanticAliases(context, saveAs, nodeText, nodeDesc);
            }
            else
            {
                context.SetVariable(saveAs, "");
                context.SetVariable(saveAs + "_count", "0");
                context.SetVariable(saveAs + "_all", "");
                context.SetVariable(saveAs + "_text", "");
                context.SetVariable(saveAs + "_desc", "");
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
    private static string StepTap(string stepJson, string selectorsJson, OperationContext context)
    {
        int[] center = ResolveTapTarget(stepJson, selectorsJson, context);
        if (center == null)
        {
            return HandleOnFail(stepJson, "tap 目标解析失败");
        }

        dynamic input = CoreHelper.GetInput();
        if (input == null)
        {
            return "ABORT:DroidInstance.Input 未初始化";
        }

        bool humanized = JsonHelper.Get(stepJson, "humanized") == "true";
        if (humanized)
        {
            string profileName = CoreHelper.GetVar("humanization_profile", "casual");
            var profile = ScriptHelpers.GetProfileConfig(profileName);
            int[] bounds = ResolveBounds(stepJson, selectorsJson, context);
            if (bounds != null)
            {
                ScriptHelpers.HumanizedTap(bounds, profile, _random);
                CoreHelper.Log(TAG, string.Format("humanized tap: ({0}, {1}) bounds={2}", (bounds[0]+bounds[2])/2, (bounds[1]+bounds[3])/2, bounds[0] + "," + bounds[1] + "," + bounds[2] + "," + bounds[3]));
                return "OK";
            }
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

        bool humanized = JsonHelper.Get(stepJson, "humanized") == "true";
        if (humanized)
        {
            string profileName = CoreHelper.GetVar("humanization_profile", "casual");
            var profile = ScriptHelpers.GetProfileConfig(profileName);
            ScriptHelpers.HumanizedSwipe(x1, y1, x2, y2, profile, _random);
            CoreHelper.Log(TAG, string.Format("humanized swipe: ({0},{1})->({2},{3})", x1, y1, x2, y2));
            return "OK";
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

        int centerX = JsonHelper.GetInt(stepJson, "center_x", 540);
        int centerY = JsonHelper.GetInt(stepJson, "center_y", 1200);

        int x1 = centerX, y1 = centerY, x2 = centerX, y2 = centerY;

        if (direction == "down") { y1 = centerY + distance / 2; y2 = centerY - distance / 2; }
        else if (direction == "up") { y1 = centerY - distance / 2; y2 = centerY + distance / 2; }
        else if (direction == "left") { x1 = centerX + distance / 2; x2 = centerX - distance / 2; }
        else if (direction == "right") { x1 = centerX - distance / 2; x2 = centerX + distance / 2; }

        dynamic input = CoreHelper.GetInput();
        if (input == null) return "ABORT:DroidInstance.Input 未初始化";

        bool humanized = JsonHelper.Get(stepJson, "humanized") == "true";
        if (humanized)
        {
            string profileName = CoreHelper.GetVar("humanization_profile", "casual");
            var profile = ScriptHelpers.GetProfileConfig(profileName);
            ScriptHelpers.HumanizedSwipe(x1, y1, x2, y2, profile, _random);
            CoreHelper.Log(TAG, string.Format("humanized scroll {0}: distance={1}", direction, distance));
            return "OK";
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

        bool humanized = JsonHelper.Get(stepJson, "humanized") == "true";
        int delayMs = _random.Next(minMs, maxMs + 1);
        if (humanized) {
            string profileName = CoreHelper.GetVar("humanization_profile", "casual");
            var profile = ScriptHelpers.GetProfileConfig(profileName);
            delayMs = ScriptHelpers.HumanizedDelay(delayMs, profile, _random);
        }
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
    private static string StepSetVar(string stepJson, OperationContext context)
    {
        string name = JsonHelper.Get(stepJson, "name");
        if (string.IsNullOrEmpty(name)) return "SKIP";

        string value = JsonHelper.Get(stepJson, "value");
        if (string.IsNullOrEmpty(value))
        {
            string ctxRef = JsonHelper.Get(stepJson, "context_ref");
            if (!string.IsNullOrEmpty(ctxRef))
            {
                value = context.GetVariable(ctxRef, "");
            }
        }

        CoreHelper.SetVar(name, value ?? "");
        CoreHelper.Log(TAG, string.Format("set_var: {0}={1}", name, value));
        return "OK";
    }

    /// <summary>
    /// random_pick — 加权随机选择并执行
    /// { "action": "random_pick", "choices": [
    ///     { "weight": 3.0, "steps": [...] },
    ///     { "weight": 1.0, "steps": [...] }
    /// ] }
    /// 权重默认值：1.0
    /// 算法：累加权重法（cumulative weight method）
    /// </summary>
    private static string ExecuteRandomPick(string stepJson, string selectorsJson, string platformConfig, string operationsJson, OperationContext context)
    {
        // 提取 choices 数组
        string choicesArray = JsonHelper.ExtractArray(stepJson, "choices");
        if (string.IsNullOrEmpty(choicesArray))
        {
            return HandleOnFail(stepJson, "random_pick 缺少 choices 数组");
        }

        // 解析 choices 数组
        string[] choices = ParseStepArray(choicesArray);
        if (choices.Length == 0)
        {
            return HandleOnFail(stepJson, "random_pick choices 为空");
        }

        // 计算总权重
        double totalWeight = 0.0;
        for (int i = 0; i < choices.Length; i++)
        {
            string choiceJson = choices[i];
            double weight = 1.0; // 默认权重
            string weightStr = JsonHelper.Get(choiceJson, "weight");
            if (!string.IsNullOrEmpty(weightStr))
            {
                double parsedWeight = 0.0;
                if (double.TryParse(weightStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out parsedWeight))
                {
                    weight = parsedWeight;
                }
            }
            totalWeight += weight;
        }

        if (totalWeight <= 0.0)
        {
            return HandleOnFail(stepJson, "random_pick 总权重为 0");
        }

        // 生成随机数：[0, totalWeight)
        double randomValue = _random.NextDouble() * totalWeight;

        // 累加权重法选择
        double cumulative = 0.0;
        int selectedIndex = -1;

        for (int i = 0; i < choices.Length; i++)
        {
            string choiceJson = choices[i];
            double weight = 1.0;
            string weightStr = JsonHelper.Get(choiceJson, "weight");
            if (!string.IsNullOrEmpty(weightStr))
            {
                double parsedWeight = 0.0;
                if (double.TryParse(weightStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out parsedWeight))
                {
                    weight = parsedWeight;
                }
            }

            cumulative += weight;
            if (randomValue < cumulative)
            {
                selectedIndex = i;
                break;
            }
        }

        // 兜底：如果因浮点误差未选中，选择最后一个
        if (selectedIndex < 0)
        {
            selectedIndex = choices.Length - 1;
        }

        CoreHelper.Log(TAG, string.Format("random_pick: 选中 choice {0}/{1}", selectedIndex + 1, choices.Length));

        // 提取选中 choice 的 steps 数组
        string selectedChoiceJson = choices[selectedIndex];
        string stepsArray = JsonHelper.ExtractArray(selectedChoiceJson, "steps");
        if (string.IsNullOrEmpty(stepsArray))
        {
            CoreHelper.LogWarn(TAG, "random_pick: 选中的 choice 无 steps，跳过");
            return "OK";
        }

        // 执行选中的 steps
        string[] steps = ParseStepArray(stepsArray);
        for (int i = 0; i < steps.Length; i++)
        {
            string subStepJson = steps[i];
            string action = JsonHelper.Get(subStepJson, "action");

            if (string.IsNullOrEmpty(action))
            {
                CoreHelper.LogWarn(TAG, string.Format("random_pick 子步骤 {0} 缺少 action，跳过", i));
                continue;
            }

            CoreHelper.Log(TAG, string.Format("random_pick 子步骤 {0}/{1}: {2}", i + 1, steps.Length, action));

            string result = ExecuteStep(action, subStepJson, selectorsJson, platformConfig, operationsJson, context);

            if (result.StartsWith("ABORT"))
            {
                CoreHelper.LogErr(TAG, "random_pick 子步骤中止: " + result);
                return result;
            }
            // SKIP 只跳过当前子步骤，继续下一步
        }

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
    /// <summary>
    /// foreach — 循环遍历多个元素
    /// { "action": "foreach", "loop": { "selector": "post_unit", "max_items": 5, "body": [...] } }
    /// 遍历找到的元素，对每个元素执行 body 中的步骤
    /// 循环变量：loop_index（当前索引）、loop_element（当前元素 bounds）
    /// </summary>
    private static string ExecuteForeach(string stepJson, string selectorsJson, string platformConfig, string operationsJson, OperationContext context)
    {
        // 提取 loop 对象
        string loopJson = JsonHelper.ExtractObject(stepJson, "loop");
        if (string.IsNullOrEmpty(loopJson))
        {
            return HandleOnFail(stepJson, "foreach 缺少 loop 对象");
        }

        // 解析 selector
        string selectorJson = ResolveSelector(loopJson, selectorsJson);
        if (string.IsNullOrEmpty(selectorJson))
        {
            return HandleOnFail(stepJson, "foreach 选择器解析失败");
        }

        // 查找所有匹配元素
        string xml = CoreHelper.GetLayout();
        List<string> elements = SelectorEngine.Find(xml, selectorJson);

        if (elements.Count == 0)
        {
            CoreHelper.Log(TAG, "foreach: 未找到元素，跳过循环");
            return "OK";
        }

        // 获取 max_items 限制（默认 10）
        int maxItems = JsonHelper.GetInt(loopJson, "max_items", 10);
        int loopCount = Math.Min(elements.Count, maxItems);

        CoreHelper.Log(TAG, string.Format("foreach: 找到 {0} 个元素，将遍历 {1} 个", elements.Count, loopCount));

        // 提取 body 步骤数组
        string bodyArray = JsonHelper.ExtractArray(loopJson, "body");
        if (string.IsNullOrEmpty(bodyArray))
        {
            CoreHelper.LogWarn(TAG, "foreach: body 为空，跳过");
            return "OK";
        }

        string[] bodySteps = ParseStepArray(bodyArray);
        if (bodySteps.Length == 0)
        {
            CoreHelper.LogWarn(TAG, "foreach: body 步骤为空");
            return "OK";
        }

        // 遍历元素并执行 body 步骤
        int successCount = 0;
        int failCount = 0;

        for (int i = 0; i < loopCount; i++)
        {
            CoreHelper.Log(TAG, string.Format("foreach: 循环 {0}/{1}", i + 1, loopCount));

            // 设置循环变量
            context.SetVariable("loop_index", i.ToString());
            context.SetVariable("loop_element", elements[i]);

            // 执行 body 中的每个步骤
            bool loopSuccess = true;
            for (int j = 0; j < bodySteps.Length; j++)
            {
                string bodyStepJson = bodySteps[j];
                string bodyAction = JsonHelper.Get(bodyStepJson, "action");

                if (string.IsNullOrEmpty(bodyAction))
                {
                    continue;
                }

                CoreHelper.Log(TAG, string.Format("  步骤 {0}/{1}: {2}", j + 1, bodySteps.Length, bodyAction));

                string result = ExecuteStep(bodyAction, bodyStepJson, selectorsJson, platformConfig, operationsJson, context);

                // 如果步骤中止，记录错误但继续下一个元素
                if (result.StartsWith("ABORT"))
                {
                    CoreHelper.LogErr(TAG, string.Format("  循环 {0} 步骤失败: {1}", i + 1, result));
                    loopSuccess = false;
                    break;
                }
            }

            if (loopSuccess)
            {
                successCount++;
            }
            else
            {
                failCount++;
            }
        }

        // 清理循环变量
        context.Variables.Remove("loop_index");
        context.Variables.Remove("loop_element");

        CoreHelper.Log(TAG, string.Format("foreach: 完成，成功 {0}/{1}", successCount, loopCount));

        // 只要有成功的就返回 OK
        if (successCount > 0)
        {
            return "OK";
        }

        return HandleOnFail(stepJson, "foreach 所有循环都失败");
    }

    /// <summary>
    /// if_exists — 条件分支（根据元素是否存在执行不同步骤）
    /// { "action": "if_exists", "condition": { "selector": "post_unit", "then": [...], "else": [...] } }
    /// then: 元素存在时执行的步骤数组
    /// else: 元素不存在时执行的步骤数组（可选）
    /// </summary>
    private static string ExecuteIfExists(string stepJson, string selectorsJson, string platformConfig, string operationsJson, OperationContext context)
    {
        // 提取 condition 对象
        string conditionJson = JsonHelper.ExtractObject(stepJson, "condition");
        if (string.IsNullOrEmpty(conditionJson))
        {
            return HandleOnFail(stepJson, "if_exists 缺少 condition 对象");
        }

        // 解析选择器
        string selectorJson = ResolveSelector(conditionJson, selectorsJson);
        if (string.IsNullOrEmpty(selectorJson))
        {
            return HandleOnFail(stepJson, "if_exists 选择器解析失败");
        }

        // 检查元素是否存在（复用 SelectorEngine）
        string xml = CoreHelper.GetLayout();
        bool exists = SelectorEngine.Exists(xml, selectorJson);
        
        CoreHelper.Log(TAG, string.Format("if_exists: 元素{0}存在", exists ? "" : "不"));

        // 根据存在性选择分支
        string branchKey = exists ? "then" : "else";
        string branchArray = JsonHelper.ExtractArray(conditionJson, branchKey);
        
        // else 分支可选
        if (string.IsNullOrEmpty(branchArray))
        {
            if (!exists)
            {
                CoreHelper.Log(TAG, "if_exists: 元素不存在且无 else 分支，跳过");
                return "OK";
            }
            return HandleOnFail(stepJson, "if_exists 缺少 " + branchKey + " 分支");
        }

        // 执行分支步骤数组
        CoreHelper.Log(TAG, string.Format("if_exists: 执行 {0} 分支", branchKey));
        return ExecuteStepsArray(branchArray, selectorsJson, platformConfig, operationsJson, context);
    }

    /// <summary>
    /// 执行步骤数组（递归调用 ExecuteStep）
    /// </summary>
    private static string ExecuteStepsArray(string stepsArrayJson, string selectorsJson, string platformConfig, string operationsJson, OperationContext context)
    {
        if (string.IsNullOrEmpty(stepsArrayJson))
        {
            return "OK";
        }

        // 解析步骤数组
        string[] steps = ParseStepArray(stepsArrayJson);
        
        for (int i = 0; i < steps.Length; i++)
        {
            string stepJson = steps[i];
            string action = JsonHelper.Get(stepJson, "action");
            
            if (string.IsNullOrEmpty(action))
            {
                CoreHelper.LogWarn(TAG, string.Format("分支步骤 {0} 缺少 action，跳过", i));
                continue;
            }
            
            CoreHelper.Log(TAG, string.Format("分支步骤 {0}/{1}: {2}", i + 1, steps.Length, action));
            
            string result = ExecuteStep(action, stepJson, selectorsJson, platformConfig, operationsJson, context);
            
            if (result.StartsWith("ABORT"))
            {
                CoreHelper.LogErr(TAG, "分支步骤中止: " + result);
                return result;
            }
            // SKIP 只跳过当前步骤，继续下一步
        }
        
        return "OK";
    }

    /// <summary>
    /// call_operation — 调用其他操作（支持递归组合）
    /// { "action": "call_operation", "operation": "browse", "on_fail": "skip" }
    /// 递归深度限制为 5 层（通过 OperationContext.CanEnterCall 检查）
    /// </summary>
    private static string ExecuteCallOperation(string stepJson, string operationsJson, string platformConfig, OperationContext context)
    {
        // 检查递归深度限制
        if (!context.CanEnterCall())
        {
            CoreHelper.LogErr(TAG, "call_operation: 递归深度超过限制（最大 5 层）");
            return HandleOnFail(stepJson, "递归深度超限");
        }

        // 获取要调用的操作名称
        string operationName = JsonHelper.Get(stepJson, "operation");
        if (string.IsNullOrEmpty(operationName))
        {
            CoreHelper.LogErr(TAG, "call_operation: 缺少 operation 字段");
            return HandleOnFail(stepJson, "缺少 operation 字段");
        }

        // 检查操作是否存在
        string opDef = JsonHelper.ExtractObject(operationsJson, operationName);
        if (string.IsNullOrEmpty(opDef))
        {
            CoreHelper.LogErr(TAG, "call_operation: 操作未定义 " + operationName);
            return HandleOnFail(stepJson, "操作未定义 " + operationName);
        }

        CoreHelper.Log(TAG, string.Format("call_operation: 调用 {0} (深度 {1})", operationName, context.CallDepth));

        // 进入递归调用
        context.EnterCall();

        try
        {
            // 递归调用 Execute
            string result = Execute(operationsJson, operationName, platformConfig, context);

            // 退出递归调用
            context.ExitCall();

            // 检查结果
            if (result.StartsWith("ERROR") || result.StartsWith("ABORT"))
            {
                CoreHelper.LogErr(TAG, string.Format("call_operation: {0} 失败 - {1}", operationName, result));
                return HandleOnFail(stepJson, operationName + " 失败");
            }

            CoreHelper.Log(TAG, string.Format("call_operation: {0} 完成", operationName));
            return "OK";
        }
        catch (Exception ex)
        {
            // 确保退出递归调用
            context.ExitCall();
            CoreHelper.LogErr(TAG, string.Format("call_operation: {0} 异常 - {1}", operationName, ex.Message));
            return HandleOnFail(stepJson, operationName + " 异常: " + ex.Message);
        }
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
    private static int[] ResolveTapTarget(string stepJson, string selectorsJson, OperationContext context)
    {
        // 方式1：从上下文引用
        string ctxRef = JsonHelper.Get(stepJson, "context_ref");
        if (!string.IsNullOrEmpty(ctxRef))
        {
            string boundsStr = context.GetVariable(ctxRef, "");
            if (!string.IsNullOrEmpty(boundsStr))
            {
                int[] bounds = SelectorEngine.ParseBounds(boundsStr);
                if (bounds != null)
                {
                    return new int[] { (bounds[0] + bounds[2]) / 2, (bounds[1] + bounds[3]) / 2 };
                }
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
    /// 解析 tap 目标 Bounds (矩形区域)
    /// </summary>
    private static int[] ResolveBounds(string stepJson, string selectorsJson, OperationContext context)
    {
        // 方式1：从上下文引用
        string ctxRef = JsonHelper.Get(stepJson, "context_ref");
        if (!string.IsNullOrEmpty(ctxRef))
        {
            string boundsStr = context.GetVariable(ctxRef, "");
            if (!string.IsNullOrEmpty(boundsStr))
            {
                return SelectorEngine.ParseBounds(boundsStr);
            }
        }

        // 方式2：通过选择器实时查找
        string selectorJson = ResolveSelector(stepJson, selectorsJson);
        if (!string.IsNullOrEmpty(selectorJson))
        {
            string xml = CoreHelper.GetLayout();
            return SelectorEngine.FindBounds(xml, selectorJson);
        }

        // 方式3：绝对坐标 (构造一个小矩形)
        string xStr = JsonHelper.Get(stepJson, "x");
        string yStr = JsonHelper.Get(stepJson, "y");
        if (!string.IsNullOrEmpty(xStr) && !string.IsNullOrEmpty(yStr))
        {
            int x = 0, y = 0;
            if (int.TryParse(xStr, out x) && int.TryParse(yStr, out y))
            {
                return new int[] { x - 5, y - 5, x + 5, y + 5 };
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
    /// 按 bounds 反查节点字符串，用于提取 text/content-desc 等语义信息
    /// </summary>
    private static string FindNodeByBounds(string xml, string bounds)
    {
        if (string.IsNullOrEmpty(xml) || string.IsNullOrEmpty(bounds))
        {
            return "";
        }

        string marker = "bounds=\"" + bounds + "\"";
        int foundPos = xml.IndexOf(marker);
        if (foundPos < 0)
        {
            return "";
        }

        int nodeStart = xml.LastIndexOf('<', foundPos);
        if (nodeStart < 0)
        {
            return "";
        }

        int nodeEnd = xml.IndexOf('>', foundPos);
        if (nodeEnd < 0)
        {
            return "";
        }

        return xml.Substring(nodeStart, nodeEnd - nodeStart + 1);
    }

    /// <summary>
    /// 将常见 save_as 字段映射为统一语义 key，便于 RuleEngine 使用
    /// </summary>
    private static void SyncSemanticAliases(OperationContext context, string saveAs, string nodeText, string nodeDesc)
    {
        if (context == null || string.IsNullOrEmpty(saveAs))
        {
            return;
        }

        string key = saveAs.ToLowerInvariant();
        string semanticText = !string.IsNullOrEmpty(nodeText) ? nodeText : nodeDesc;

        if (key == "title" || key == "caption" || key == "headline" || key == "post_title")
        {
            if (!string.IsNullOrEmpty(semanticText))
            {
                context.SetVariable("post_title", semanticText);
            }
            return;
        }

        if (key == "body" || key == "post_body")
        {
            if (!string.IsNullOrEmpty(semanticText))
            {
                context.SetVariable("post_body", semanticText);
            }
            return;
        }

        if (key == "subreddit" || key == "subreddit_name" || key == "community")
        {
            if (!string.IsNullOrEmpty(semanticText))
            {
                context.SetVariable("post_subreddit", semanticText);
            }
            return;
        }

        if (key == "upvotes" || key == "score" || key == "likes" || key == "like_count")
        {
            string normalized = NormalizeMetricValue(semanticText);
            if (!string.IsNullOrEmpty(normalized))
            {
                context.SetVariable("post_upvotes", normalized);
            }
            return;
        }

        if (key == "comments" || key == "comment_count" || key == "reply_count")
        {
            string normalized = NormalizeMetricValue(semanticText);
            if (!string.IsNullOrEmpty(normalized))
            {
                context.SetVariable("post_comment_count", normalized);
            }
            return;
        }

        if (key == "timestamp" || key == "time" || key == "post_time")
        {
            if (!string.IsNullOrEmpty(semanticText))
            {
                context.SetVariable("post_timestamp", semanticText);
            }
            return;
        }

        if (key == "target_post")
        {
            // 某些 APP 列表项节点 text 可直接作为标题兜底
            if (!string.IsNullOrEmpty(semanticText) && string.IsNullOrEmpty(context.GetVariable("post_title", "")))
            {
                context.SetVariable("post_title", semanticText);
            }
        }
    }

    /// <summary>
    /// 将 "1.2k"/"3,421" 等数字文本标准化为纯数字字符串
    /// </summary>
    private static string NormalizeMetricValue(string raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return "";
        }

        string s = raw.Trim().ToLowerInvariant().Replace(",", "");
        double multiplier = 1.0;
        if (s.EndsWith("k"))
        {
            multiplier = 1000.0;
            s = s.Substring(0, s.Length - 1);
        }
        else if (s.EndsWith("m"))
        {
            multiplier = 1000000.0;
            s = s.Substring(0, s.Length - 1);
        }

        StringBuilder numeric = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if ((c >= '0' && c <= '9') || c == '.')
            {
                numeric.Append(c);
            }
        }

        if (numeric.Length == 0)
        {
            return "";
        }

        double val;
        if (double.TryParse(numeric.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out val))
        {
            long n = (long)Math.Round(val * multiplier);
            return n.ToString();
        }

        return "";
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
    /// 将 OperationContext 同步到旧版静态上下文（兼容 SessionRunner）
    /// </summary>
    private static void SyncLegacyContext(OperationContext context)
    {
        _context.Clear();
        if (context == null || context.Variables == null)
        {
            return;
        }

        foreach (KeyValuePair<string, string> kvp in context.Variables)
        {
            _context[kvp.Key] = kvp.Value ?? "";
        }
    }

    /// <summary>
    /// 获取上下文变量（兼容旧调用）
    /// </summary>
    public static string GetContextVariable(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return "";
        }

        if (_context.ContainsKey(key))
        {
            return _context[key];
        }
        return "";
    }

    /// <summary>
    /// 设置上下文变量（兼容旧调用）
    /// </summary>
    public static void SetContextVariable(string key, string value)
    {
        if (string.IsNullOrEmpty(key))
        {
            return;
        }
        _context[key] = value ?? "";
    }

    /// <summary>
    /// 清空上下文（兼容旧调用）
    /// </summary>
    public static void ClearContext()
    {
        _context.Clear();
    }

    /// <summary>
    /// 获取上下文变量（供 SessionRunner 调用）
    /// </summary>
    public static string GetContext(string key)
    {
        return GetContextVariable(key);
    }
}
