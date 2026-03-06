// =====================================================
// AppExplorer_v2.cs - APP 自动探索与 Manifest 生成
// ⚠️ C# 5.0 语法 - 禁止使用 $""、?.、nameof() 等
// =====================================================
// v4.5.2 - Phase 7: 自动探索实现
//
// 核心功能：
//   1. 随机操作探索（点击、滑动、返回）
//   2. 截图并用 Gemini Flash 分析状态
//   3. 构建状态转移图
//   4. 提取功能能力列表
//   5. 生成 YAML Manifest
// =====================================================
using System;
using System.IO;
using System.Collections.Generic;
using System.Text;


/// <summary>
/// APP 自动探索器 v2
/// 通过随机操作 + AI 视觉分析自动生成 APP Manifest
/// </summary>
public class AppExplorer_v2
{
    private const string TAG = "AppExplorer_v2";
    private static Random _random = new Random();

    // ========== 内部数据结构 ==========


    /// <summary>
    /// 探索步骤记录
    /// </summary>
    public class ExplorationStep
{
        public int StepNumber { get; set; }
        public string Action { get; set; }          // tap, swipe, back
        public string Description { get; set; }     // 操作描述
        public string ScreenshotPath { get; set; }  // 截图路径
        public string DetectedState { get; set; }   // AI 识别的状态
        public double Confidence { get; set; }      // 识别置信度
        public List<string> UIElements { get; set; } // 检测到的 UI 元素
        public string LayoutXml { get; set; }       // UI 布局 XML


        public ExplorationStep()
{
            UIElements = new List<string>();
        }
}


    /// <summary>
/// 状态转移节点
    /// </summary>
    public class StateNode
{
        public string StateName { get; set; }       // 状态名称
        public int VisitCount { get; set; }         // 访问次数
        public string FirstSeenAt { get; set; }     // 首次发现时的步骤
        public List<string> VisualMarkers { get; set; } // 视觉标记
        public List<string> AvailableActions { get; set; } // 可执行操作
        public List<string> Screenshots { get; set; }     // 相关截图


        public StateNode()
{
            VisualMarkers = new List<string>();
            AvailableActions = new List<string>();
            Screenshots = new List<string>();
}
}


    /// <summary>
/// 探索会话配置
/// </summary>
    public class ExplorationConfig
{
        public int MaxSteps { get; set; }           // 最大探索步数
        public int AnalysisInterval { get; set; }   // AI 分析间隔（步数）
        public string ScreenshotDir { get; set; }   // 截图保存目录
        public string OutputDir { get; set; }       // 输出目录
        public bool SaveScreenshots { get; set; }   // 是否保存截图
        public bool SaveLayouts { get; set; }       // 是否保存布局 XML
        public int ScreenWidth { get; set; }        // 屏幕宽度
        public int ScreenHeight { get; set; }       // 屏幕高度


        public ExplorationConfig()
{
            MaxSteps = 100;
            AnalysisInterval = 10;
            ScreenshotDir = "Screenshots/Exploration";
            OutputDir = "Configs/Manifests";
            SaveScreenshots = true;
            SaveLayouts = false;
            ScreenWidth = 1080;
            ScreenHeight = 2400;
}
}


    // ========== 核心探索方法 ==========


    /// <summary>
    /// 自动探索 APP 并生成 Manifest
    /// </summary>
    public static string ExploreAndGenerateManifest(
        object project,
        object instance,
        string packageName,
        string outputPath)
{
        // 初始化日志
        Action<string> Log = (m) => CoreHelper.Log(TAG, m);
        Action<string> LogErr = (m) => CoreHelper.LogErr(TAG, m);

        Log("开始探索 APP: " + packageName);


        // 初始化 CoreHelper
        CoreHelper.Init(project, instance);


        // 创建配置
        ExplorationConfig config = new ExplorationConfig();
        if (string.IsNullOrEmpty(outputPath))
{
            outputPath = Path.Combine(config.OutputDir, packageName.Replace(".", "_") + "_manifest.yaml");
}

        // 准备截图目录
        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string sessionDir = Path.Combine(config.ScreenshotDir, timestamp);
        CoreHelper.EnsureDir(sessionDir);

        // 初始化 VisionCorrector
        string aiConfigPath = Path.Combine("Configs", "AIConfig.json");
        if (!File.Exists(aiConfigPath))
{
            aiConfigPath = Path.Combine("Config", "AIConfig.json");
}
        VisionCorrector.Init(project, instance, sessionDir, aiConfigPath);

        // 探索数据
        List<ExplorationStep> steps = new List<ExplorationStep>();
        Dictionary<string, StateNode> stateGraph = new Dictionary<string, StateNode>();

        try
{
            // 1. 启动 APP
            Log("启动 APP: " + packageName);
            dynamic droid = instance.DroidInstance;
            dynamic app = droid.App;

            app.Open(packageName);
            System.Threading.Thread.Sleep(3000);


            // 2. 随机探索
            Log("开始随机探索，最大步数: " + config.MaxSteps);
            for (int step = 0; step < config.MaxSteps; step++)
{
                Log(string.Format("步骤 {0}/{1}", step + 1, config.MaxSteps));

                // 执行一步探索
                ExplorationStep explorationStep = ExploreOneStep(
                    project, instance, step, sessionDir, config);


                if (explorationStep != null)
{
                    steps.Add(explorationStep);


                    // 定期 AI 分析
                    if (step % config.AnalysisInterval == 0)
{
                        AnalyzeStepWithAI(project, instance, explorationStep, aiConfigPath);


                        // 更新状态图
                        UpdateStateGraph(stateGraph, explorationStep);
                    }
}
}

            // 3. 最终分析：汇总所有截图，生成完整报告
            Log("执行最终分析...");
            string finalReport = GenerateFinalReport(project, steps, stateGraph, packageName);


            // 4. 生成 YAML Manifest
            Log("生成 YAML Manifest...");
            string manifestPath = GenerateYAMLManifest(
                packageName, steps, stateGraph, finalReport, outputPath);

            // 5. 保存探索数据
            SaveExplorationData(steps, stateGraph, sessionDir);

            Log("探索完成！Manifest 保存至: " + manifestPath);
            return manifestPath;
}
        catch (Exception ex)
#MRS|{
            LogErr("探索过程异常: " + ex.Message);
            return "ERROR: " + ex.Message;
}
}


    /// <summary>
/// 执行单步探索
    /// </summary>
    private static ExplorationStep ExploreOneStep(
        object project,
        object instance,
        int stepNumber,
        string sessionDir,
        ExplorationConfig config)
{
        ExplorationStep step = new ExplorationStep();
        step.StepNumber = stepNumber;


        try
{
            dynamic droid = instance.DroidInstance;
            dynamic input = droid.Input;
            dynamic hierarchy = droid.Hierarchy;


            // 获取布局（操作前）
            string layoutBefore = hierarchy.GetLayout();
            step.LayoutXml = layoutBefore;


            // 随机选择操作类型
            int actionType = _random.Next(100);
            int centerX = config.ScreenWidth / 2;
            int centerY = config.ScreenHeight / 2;


            if (actionType < 60)
{
                // 点击（60% 概率）
                int x = _random.Next(100, config.ScreenWidth - 100);
                int y = _random.Next(200, config.ScreenHeight - 200);

                input.Tap(x, y);
                System.Threading.Thread.Sleep(_random.Next(500, 1500));

                step.Action = "tap";
                step.Description = string.Format("点击 ({0}, {1})", x, y);
}
            else if (actionType < 85)
{
                // 滑动（25% 概率）
                int startY = _random.Next(config.ScreenHeight / 2, config.ScreenHeight - 200);
                int endY = _random.Next(200, config.ScreenHeight / 2);


                input.Swipe(centerX, startY, centerX, endY, _random.Next(300, 600));
                System.Threading.Thread.Sleep(_random.Next(500, 1000));


                step.Action = "swipe";
                step.Description = string.Format("滑动 ({0}, {1}) → ({2}, {3})",
                    centerX, startY, centerX, endY);
}
            else
{
                // 返回（15% 概率）
                input.Shell("input keyevent 4");
                System.Threading.Thread.Sleep(_random.Next(500, 1000));


                step.Action = "back";
                step.Description = "按返回键";
}


            // 截图
            if (config.SaveScreenshots)
{
                string screenshotPath = Path.Combine(sessionDir,
                    string.Format("step_{0:D3}.png", stepNumber));

                droid.SaveScreenshot(screenshotPath);
                step.ScreenshotPath = screenshotPath;
}
            else
{
                step.ScreenshotPath = "";
}

            return step;
}
        catch (Exception ex)
{
            CoreHelper.LogErr(TAG, string.Format("步骤 {0} 探索失败: {1}",
                stepNumber, ex.Message));
            step.Action = "error";
            step.Description = "错误: " + ex.Message;
            return step;
}
}


    /// <summary>
/// 使用 AI 分析单步探索结果
    /// </summary>
    private static void AnalyzeStepWithAI(
        object project,
        object instance,
        ExplorationStep step,
        string aiConfigPath)
{
        try
{
            if (string.IsNullOrEmpty(step.ScreenshotPath) || !File.Exists(step.ScreenshotPath))
{
                return;
}

            // 读取 AI 配置
            string aiConfig = "";
            if (File.Exists(aiConfigPath))
{
                aiConfig = File.ReadAllText(aiConfigPath);
            }

            if (string.IsNullOrEmpty(aiConfig))
{
                CoreHelper.LogWarn(TAG, "AI 配置文件为空，跳过分析");
                return;
}


            // 构建分析提示词
            string prompt = BuildStateAnalysisPrompt(step);


            // 调用 Gemini Flash 分析
            string response = AIService.CallWithRetryAndImage(prompt, step.ScreenshotPath, aiConfig);


            if (!response.StartsWith("ERROR:"))
{
                // 解析结果
                string jsonStr = AIService.ExtractJson(response);
                if (!string.IsNullOrEmpty(jsonStr))
{
                    step.DetectedState = JsonHelper.Get(jsonStr, "state_name");
                    string confStr = JsonHelper.Get(jsonStr, "confidence");
                    double conf = 0.7;
                    double.TryParse(confStr, out conf);
                    step.Confidence = conf;

                    // 提取检测到的 UI 元素
                    string elementsStr = JsonHelper.Get(jsonStr, "ui_elements");
                    if (!string.IsNullOrEmpty(elementsStr) && elementsStr.StartsWith("["))
{
                        string[] elements = JsonHelper.GetArray(elementsStr, "");
                        if (elements != null)
{
                            foreach (string elem in elements)
{
                                if (!string.IsNullOrEmpty(elem))
{
                                    step.UIElements.Add(elem);
}
}
}
}

                    CoreHelper.Log(TAG, string.Format("步骤 {0}: 状态={1}, 置信度={2:F2}",
                        step.StepNumber, step.DetectedState, step.Confidence));
}
}
            else
{
                CoreHelper.LogWarn(TAG, "AI 分析失败: " + response);
}
}
        catch (Exception ex)
{
            CoreHelper.LogErr(TAG, "AI 分析异常: " + ex.Message);
}
}


    /// <summary>
/// 构建状态分析提示词
    /// </summary>
    private static string BuildStateAnalysisPrompt(ExplorationStep step)
{
    System.Text.StringBuilder sb = new System.Text.StringBuilder();
    sb.Append("你是一个 Android APP UI 分析专家。请分析截图并返回 JSON 格式结果。\n\n");
    sb.Append("【操作】\n");
    sb.Append(step.Action + ": " + step.Description + "\n\n");

    sb.Append("【任务】\n");
    sb.Append("1. 识别当前页面是 APP 的哪个功能模块或状态\n");
    sb.Append("2. 列出可见的主要 UI 元素（按钮、输入框、列表等）\n");
    sb.Append("3. 评估用户可能执行的操作\n\n");

    sb.Append("【返回格式】（严格按 JSON 格式，不要添加其他文字）\n");
    sb.Append("{\n");
    sb.Append("  \"state_name\": \"页面名称（如：首页、帖子详情、个人资料等）\",\n");
    sb.Append("  \"confidence\": 0.0-1.0,\n");
    sb.Append("  \"ui_elements\": [\"元素1\", \"元素2\", ...],\n");
    sb.Append("  \"available_actions\": [\"操作1\", \"操作2\", ...],\n");
    sb.Append("  \"visual_markers\": [\"视觉标记1\", \"视觉标记2\", ...]\n");
    sb.Append("}");


    return sb.ToString();
}


    /// <summary>
/// 更新状态转移图
    /// </summary>
    private static void UpdateStateGraph(
        Dictionary<string, StateNode> stateGraph,
        ExplorationStep step)
{
        if (string.IsNullOrEmpty(step.DetectedState))
{
            step.DetectedState = "unknown_state_" + step.StepNumber;
}


        if (!stateGraph.ContainsKey(step.DetectedState))
{
            StateNode node = new StateNode();
            node.StateName = step.DetectedState;
            node.VisitCount = 1;
            node.FirstSeenAt = "步骤 " + step.StepNumber;

            if (step.UIElements.Count > 0)
{
                node.VisualMarkers.AddRange(step.UIElements);
}


            if (!string.IsNullOrEmpty(step.ScreenshotPath))
{
                node.Screenshots.Add(step.ScreenshotPath);
}


            stateGraph[step.DetectedState] = node;
}
#        else
{
            stateGraph[step.DetectedState].VisitCount++;

            if (!string.IsNullOrEmpty(step.ScreenshotPath))
{
                if (!stateGraph[step.DetectedState].Screenshots.Contains(step.ScreenshotPath))
{
                    stateGraph[step.DetectedState].Screenshots.Add(step.ScreenshotPath);
}
}
}
}


    /// <summary>
/// 生成最终分析报告
    /// </summary>
    private static string GenerateFinalReport(
        object project,
        List<ExplorationStep> steps,
        Dictionary<string, StateNode> stateGraph,
        string packageName)
{
    System.Text.StringBuilder sb = new System.Text.StringBuilder();

    sb.AppendLine("# APP 探索报告");
    sb.AppendLine("# 包名: " + packageName);
    sb.AppendLine("# 生成时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
    sb.AppendLine();


    sb.AppendLine("## 探索统计");
    sb.AppendLine("- 总步数: " + steps.Count);
    sb.AppendLine("- 发现状态数: " + stateGraph.Count);
    sb.AppendLine();


    sb.AppendLine("## 发现的状态");
    foreach (KeyValuePair<string, StateNode> kvp in stateGraph)
{
        StateNode node = kvp.Value;
        sb.AppendLine("- " + node.StateName + " (访问 " + node.VisitCount + " 次)");
        if (node.VisualMarkers.Count > 0)
{
            sb.AppendLine("  视觉标记: " + string.Join(", ", node.VisualMarkers.ToArray()));
}
}

    sb.AppendLine();
    sb.AppendLine("## 操作分布");
    Dictionary<string, int> actionCounts = new Dictionary<string, int>();
    foreach (ExplorationStep step in steps)
{
        if (actionCounts.ContainsKey(step.Action))
{
            actionCounts[step.Action]++;
}
#        else
{
            actionCounts[step.Action] = 1;
}
}

    foreach (KeyValuePair<string, int> kvp in actionCounts)
{
        sb.AppendLine("- " + kvp.Key + ": " + kvp.Value + " 次");
}


    return sb.ToString();
}


    /// <summary>
/// 生成 YAML Manifest
    /// </summary>
    private static string GenerateYAMLManifest(
        string packageName,
        List<ExplorationStep> steps,
        Dictionary<string, StateNode> stateGraph,
        string finalReport,
        string outputPath)
{
        try
{
            // 确保输出目录存在
            string outputDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outputDir))
{
                CoreHelper.EnsureDir(outputDir);
}


            System.Text.StringBuilder yaml = new System.Text.StringBuilder();

            // YAML 头部
            yaml.AppendLine("# =====================================================");
            yaml.AppendLine("# APP Manifest - 自动生成");
            yaml.AppendLine("# 包名: " + packageName);
            yaml.AppendLine("# 生成时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            yaml.AppendLine("# 探索步数: " + steps.Count);
            yaml.AppendLine("# =====================================================");
            yaml.AppendLine();


            // APP 信息
            yaml.AppendLine("app:");
            yaml.AppendLine("  id: \"" + packageName.Replace(".", "_") + "\"");
            yaml.AppendLine("  name: \"" + ExtractAppName(packageName) + "\"");
            yaml.AppendLine("  package: \"" + packageName + "\"");
            yaml.AppendLine("  explored_at: \"" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\"");
            yaml.AppendLine("  exploration_steps: " + steps.Count);
            yaml.AppendLine();


            // 功能能力（从状态图推断）
            yaml.AppendLine("# DPS 用于决策：这个 APP 能做什么");
            yaml.AppendLine("capabilities:");
            List<string> inferredCaps = InferCapabilities(stateGraph);
    foreach (string cap in inferredCaps)
{
        yaml.AppendLine("  - " + cap);
}
    yaml.AppendLine();


            // 状态定义
            yaml.AppendLine("# DPS 用于感知：如何识别当前状态");
            yaml.AppendLine("states:");
    foreach (KeyValuePair<string, StateNode> kvp in stateGraph)
{
        StateNode node = kvp.Value;
        if (node.VisitCount >= 2) // 只记录访问 2 次以上的稳定状态
{
            yaml.AppendLine("  - name: \"" + node.StateName + "\"");
            yaml.AppendLine("    visit_count: " + node.VisitCount);
            if (node.VisualMarkers.Count > 0)
{
                yaml.AppendLine("    visual_markers:");
                foreach (string marker in node.VisualMarkers)
{
                    yaml.AppendLine("      - \"" + marker + "\"");
}
}
            yaml.AppendLine("    gemini_prompt: \"这是 " + ExtractAppName(packageName) + " 的" + node.StateName + "页面吗？\"");
            yaml.AppendLine();
}
}


            // 意图映射（简化版本）
            yaml.AppendLine("# DPS → ZennoDroid 的翻译层");
            yaml.AppendLine("intent_mappings:");
            yaml.AppendLine("  explore:");
    yaml.AppendLine("    zd_action: \"random_explore\"");
    yaml.AppendLine("    description: \"随机探索 APP\"");
    yaml.AppendLine();


            // 速率限制（默认值）
            yaml.AppendLine("# DPS 用于决策：速率限制");
            yaml.AppendLine("rate_limits:");
            yaml.AppendLine("  explore:");
    yaml.AppendLine("    per_hour: 100");
    yaml.AppendLine("    cooldown_seconds: 1");
    yaml.AppendLine();


            // 写入文件
            CoreHelper.WriteFileAtomic(outputPath, yaml.ToString());

            return outputPath;
}
        catch (Exception ex)
{
            CoreHelper.LogErr(TAG, "生成 Manifest 失败: " + ex.Message);
            return "ERROR: " + ex.Message;
}
}


    /// <summary>
/// 从包名推断 APP 名称
    /// </summary>
    private static string ExtractAppName(string packageName)
{
        if (string.IsNullOrEmpty(packageName)) return "Unknown";

        string[] parts = packageName.Split('.');
        if (parts.Length > 0)
{
            return parts[parts.Length - 1];
}
        return packageName;
}


    /// <summary>
/// 从状态图推断功能能力
    /// </summary>
    private static List<string> InferCapabilities(Dictionary<string, StateNode> stateGraph)
{
        List<string> caps = new List<string>();


        // 基础能力
        caps.Add("open_app");
        caps.Add("navigate");

        // 从状态名称推断能力
        foreach (KeyValuePair<string, StateNode> kvp in stateGraph)
{
            string stateName = kvp.Key.ToLower();


            if (stateName.Contains("home") || stateName.Contains("feed") || stateName.Contains("main"))
{
                if (!caps.Contains("browse_feed")) caps.Add("browse_feed");
}
            if (stateName.Contains("post") || stateName.Contains("detail"))
{
                if (!caps.Contains("view_post")) caps.Add("view_post");
}
            if (stateName.Contains("profile") || stateName.Contains("user"))
{
                if (!caps.Contains("view_profile")) caps.Add("view_profile");
}
            if (stateName.Contains("search"))
{
                if (!caps.Contains("search")) caps.Add("search");
}
            if (stateName.Contains("comment") || stateName.Contains("reply"))
{
                if (!caps.Contains("comment")) caps.Add("comment");
}
}


        return caps;
}


    /// <summary>
/// 保存探索数据到 JSON
    /// </summary>
    private static void SaveExplorationData(
        List<ExplorationStep> steps,
        Dictionary<string, StateNode> stateGraph,
        string sessionDir)
{
        try
{
            // 保存步骤数据
            System.Text.StringBuilder stepsJson = new System.Text.StringBuilder();
            stepsJson.AppendLine("{");
            stepsJson.AppendLine("  \"steps\": [");
            for (int i = 0; i < steps.Count; i++)
{
                ExplorationStep step = steps[i];
                stepsJson.AppendLine("    {");
                stepsJson.AppendLine("      \"step_number\": " + step.StepNumber + ",");
                stepsJson.AppendLine("      \"action\": \"" + step.Action + "\",");
                stepsJson.AppendLine("      \"description\": \"" + JsonHelper.Escape(step.Description) + "\",");
                stepsJson.AppendLine("      \"detected_state\": \"" + JsonHelper.Escape(step.DetectedState) + "\",");
                stepsJson.AppendLine("      \"confidence\": " + step.Confidence.ToString("F2") + ",");
                stepsJson.AppendLine("      \"screenshot\": \"" + JsonHelper.Escape(step.ScreenshotPath) + "\"");
                stepsJson.Append("    }");
                if (i < steps.Count - 1) stepsJson.AppendLine(",");
                else stepsJson.AppendLine();
}
            stepsJson.AppendLine("  ]");
            stepsJson.AppendLine("}");

            string stepsPath = Path.Combine(sessionDir, "exploration_steps.json");
            CoreHelper.WriteFileAtomic(stepsPath, stepsJson.ToString());


            // 保存状态图数据
            System.Text.StringBuilder graphJson = new System.Text.StringBuilder();
            graphJson.AppendLine("{");
            graphJson.AppendLine("  \"states\": [");
            int idx = 0;
            foreach (KeyValuePair<string, StateNode> kvp in stateGraph)
{
                StateNode node = kvp.Value;
                graphJson.AppendLine("    {");
                graphJson.AppendLine("      \"name\": \"" + JsonHelper.Escape(node.StateName) + "\",");
                graphJson.AppendLine("      \"visit_count\": " + node.VisitCount + ",");
                graphJson.AppendLine("      \"first_seen\": \"" + JsonHelper.Escape(node.FirstSeenAt) + "\",");
                graphJson.AppendLine("      \"visual_markers\": " + JsonHelper.CreateArray(node.VisualMarkers.ToArray()) + ",");
                graphJson.AppendLine("      \"screenshots\": " + JsonHelper.CreateArray(node.Screenshots.ToArray()) + "");
                graphJson.Append("    }");
                if (idx < stateGraph.Count - 1) graphJson.AppendLine(",");
                else graphJson.AppendLine();
                idx++;
}
            graphJson.AppendLine("  ]");
            graphJson.AppendLine("}");


            string graphPath = Path.Combine(sessionDir, "state_graph.json");
            CoreHelper.WriteFileAtomic(graphPath, graphJson.ToString());

            CoreHelper.Log(TAG, "探索数据已保存至: " + sessionDir);
}
        catch (Exception ex)
{
            CoreHelper.LogErr(TAG, "保存探索数据失败: " + ex.Message);
}
}


    // ========== 便捷方法 ==========


    /// <summary>
/// 快速探索（使用默认配置）
    /// </summary>
    public static string QuickExplore(
        object project,
        object instance,
        string packageName)
{
        return ExploreAndGenerateManifest(project, instance, packageName, null);
}
}
