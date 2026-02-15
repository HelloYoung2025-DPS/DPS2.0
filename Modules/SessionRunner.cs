// =====================================================
// SessionRunner.cs - 会话执行模块
// ⚠️ C# 5.0 语法 - 禁止使用 $""、?.、nameof() 等
// ⚠️ 从 BehaviorConfig.json 动态读取行为配置
// ⚠️ v4.5 - 使用 ActionExecutor 统一引擎替代旧平台模块
// ⚠️ v4.5.1 - 接入 MemoryManager 实现交互记录、去重、清理
// =====================================================
using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Generic;

/// <summary>
/// 会话执行模块
/// 模拟用户行为，执行会话动作
/// v4.5: 通过 ActionExecutor + JSON 步骤配置驱动所有平台操作
/// </summary>
public class SessionRunner
{
    private static dynamic _project;
    private const string TAG = "SessionRunner";
    private static Random _random = new Random();
    
    // 疲劳模型状态
    private static bool _fatigueEnabled = false;
    private static double _energy = 1.0;
    private static double _decayBrowse = 0.02;
    private static double _decayRead = 0.05;
    private static double _decayLike = 0.03;
    private static double _decayComment = 0.10;
    private static double _recoveryPerPauseSec = 0.01;
    private static double _minEnergyToComment = 0.3;
    private static double _minEnergyToLike = 0.15;
    
    // 规则引擎数据
    private static string _interestsJson = "";
    private static string _triggersJson = "";
    private static string _decisionConfigJson = "";
    
    // 统一引擎数据（v4.5）
    private static string _operationsJson = "";
    private static string _platformConfig = "";
    
    // 页面状态追踪
    private static string _currentPage = "unknown";
    
    /// <summary>
    /// 模块入口点
    /// </summary>
    public static string Run(object projectObj)
    {
        _project = projectObj;
        
        try
        {
            CoreHelper.Init(projectObj);
            
            string projectRoot = CoreHelper.GetVar("project_root", "");
            string deviceId = CoreHelper.GetVar("device_id", "");
            string personaJson = CoreHelper.GetVar("persona_json", "{}");
            string sessionPlanJson = CoreHelper.GetVar("session_plan_json", "{}");
            string behaviorConfigJson = CoreHelper.GetVar("behavior_config_json", "");
            
            if (string.IsNullOrEmpty(projectRoot) || string.IsNullOrEmpty(deviceId))
            {
                CoreHelper.LogErr(TAG, "project_root 或 device_id 未设置");
                CoreHelper.SetVar("session_result", "ERROR");
                return "ERROR: 变量未设置";
            }
            
            projectRoot = CoreHelper.NormalizePath(projectRoot);
            
            // 如果 behaviorConfigJson 为空，从文件读取
            if (string.IsNullOrEmpty(behaviorConfigJson) || behaviorConfigJson == "{}")
            {
                string configPath = projectRoot + "Config\\BehaviorConfig.json";
                if (File.Exists(configPath))
                {
                    behaviorConfigJson = CoreHelper.ReadFile(configPath);
                    CoreHelper.SetVar("behavior_config_json", behaviorConfigJson);
                    CoreHelper.Log(TAG, "已从文件加载行为配置");
                }
            }
            
            CoreHelper.Log(TAG, "开始执行会话");
            
            // ========== 加载决策配置 ==========
            string decisionConfigPath = projectRoot + "Config\\DecisionConfig.json";
            if (File.Exists(decisionConfigPath))
            {
                _decisionConfigJson = CoreHelper.ReadFile(decisionConfigPath);
                RuleEngine.LoadConfig(_decisionConfigJson);
                CoreHelper.Log(TAG, "已加载 DecisionConfig.json");
            }
            else
            {
                CoreHelper.LogWarn(TAG, "DecisionConfig.json 不存在，使用默认规则");
            }
            
            // interests/triggers 在平台确定后加载（见下方）
            
            // ========== 初始化疲劳模型 ==========
            string fatigueSection = JsonHelper.ExtractObject(_decisionConfigJson, "fatigue_model");
            if (!string.IsNullOrEmpty(fatigueSection))
            {
                string enabledFatigue = JsonHelper.Get(fatigueSection, "enabled");
                _fatigueEnabled = (enabledFatigue == "true");
                _energy = JsonHelper.GetDouble(fatigueSection, "initial_energy", 1.0);
                _recoveryPerPauseSec = JsonHelper.GetDouble(fatigueSection, "recovery_per_pause_sec", 0.01);
                _minEnergyToComment = JsonHelper.GetDouble(fatigueSection, "min_energy_to_comment", 0.3);
                _minEnergyToLike = JsonHelper.GetDouble(fatigueSection, "min_energy_to_like", 0.15);
                
                string decaySection = JsonHelper.ExtractObject(fatigueSection, "decay_per_action");
                if (!string.IsNullOrEmpty(decaySection))
                {
                    _decayBrowse = JsonHelper.GetDouble(decaySection, "browse", 0.02);
                    _decayRead = JsonHelper.GetDouble(decaySection, "read", 0.05);
                    _decayLike = JsonHelper.GetDouble(decaySection, "like", 0.03);
                    _decayComment = JsonHelper.GetDouble(decaySection, "comment", 0.10);
                }
                
                CoreHelper.Log(TAG, string.Format("疲劳模型: enabled={0}, energy={1:F2}", _fatigueEnabled, _energy));
            }
            
            // ========== Multi-Platform Support ==========
            // Determine which platform to use based on device_app_mapping
            string platformName = DeterminePlatform(projectRoot, deviceId);
            CoreHelper.Log(TAG, "选择平台: " + platformName);
            CoreHelper.SetVar("current_platform", platformName);
            
            // Load platform-specific configuration
            string platformsConfigPath = projectRoot + "Config\\PlatformsConfig.json";
            string platformsConfigJson = "";
            if (File.Exists(platformsConfigPath))
            {
                platformsConfigJson = CoreHelper.ReadFile(platformsConfigPath);
                CoreHelper.SetVar("platforms_config_json", platformsConfigJson);
            }
            
            // Extract platform-specific config
            string platformsSection = JsonHelper.ExtractObject(platformsConfigJson, "platforms");
            string platformConfig = JsonHelper.ExtractObject(platformsSection, platformName);
            
            if (string.IsNullOrEmpty(platformConfig))
            {
                CoreHelper.LogErr(TAG, "未找到平台配置: " + platformName);
                CoreHelper.SetVar("session_result", "ERROR");
                return "ERROR: 平台配置不存在";
            }
            
            // Check if platform is enabled
            string enabledStr = JsonHelper.Get(platformConfig, "enabled");
            if (enabledStr == "false")
            {
                CoreHelper.LogErr(TAG, "平台未启用: " + platformName);
                CoreHelper.SetVar("session_result", "ERROR");
                return "ERROR: 平台未启用";
            }
            
            // 保存平台配置供 ActionExecutor 使用
            _platformConfig = platformConfig;
            
            CoreHelper.Log(TAG, "平台配置已加载: " + platformName);
            
            // ========== 初始化 MemoryManager (v4.5.1) ==========
            string memoryBasePath = projectRoot + "Memory";
            MemoryManager.Init(memoryBasePath);
            CoreHelper.Log(TAG, "MemoryManager 已初始化: " + memoryBasePath);
            
            // ========== 加载操作配置 JSON (v4.5 统一引擎) ==========
            string opsPath = projectRoot + "Config\\Operations\\" + platformName + "_operations.json";
            if (File.Exists(opsPath))
            {
                string rawOpsJson = CoreHelper.ReadFile(opsPath);
                // 提取 operations 对象（操作定义在 "operations" key 下）
                string opsSection = JsonHelper.ExtractObject(rawOpsJson, "operations");
                _operationsJson = string.IsNullOrEmpty(opsSection) ? rawOpsJson : opsSection;
                CoreHelper.Log(TAG, "已加载操作配置: " + opsPath);
            }
            else
            {
                _operationsJson = "";
                CoreHelper.LogWarn(TAG, "操作配置不存在: " + opsPath + "，将回退到旧模块模式");
            }
            
            // 初始化页面状态
            _currentPage = "unknown";
            
            // 按平台加载 interests 和 triggers
            string interestsPath = projectRoot + "Data\\Keywords\\" + platformName + "\\interests.json";
            if (File.Exists(interestsPath))
            {
                _interestsJson = CoreHelper.ReadFile(interestsPath);
                CoreHelper.Log(TAG, "已加载 interests.json (" + platformName + ")");
            }
            else
            {
                _interestsJson = "";
                CoreHelper.LogWarn(TAG, "interests.json 不存在: " + interestsPath);
            }
            
            string triggersPath = projectRoot + "Data\\Keywords\\" + platformName + "\\triggers.json";
            if (File.Exists(triggersPath))
            {
                _triggersJson = CoreHelper.ReadFile(triggersPath);
                CoreHelper.Log(TAG, "已加载 triggers.json (" + platformName + ")");
            }
            else
            {
                _triggersJson = "";
                CoreHelper.LogWarn(TAG, "triggers.json 不存在: " + triggersPath);
            }
            
            // 获取会话时长
            int sessionDuration = JsonHelper.GetInt(sessionPlanJson, "session_duration_minutes", 12);
            
            // 从配置获取会话限制
            string sessionSection = JsonHelper.ExtractObject(behaviorConfigJson, "session");
            int minDuration = JsonHelper.GetInt(sessionSection, "min_duration_minutes", 3);
            int maxDuration = JsonHelper.GetInt(sessionSection, "max_duration_minutes", 30);
            
            if (sessionDuration < minDuration) sessionDuration = minDuration;
            if (sessionDuration > maxDuration) sessionDuration = maxDuration;
            
            CoreHelper.Log(TAG, "计划会话时长: " + sessionDuration + " 分钟");
            
            // 从配置获取动作权重
            string actionsSection = JsonHelper.ExtractObject(behaviorConfigJson, "actions");
            
            // 定义动作类型列表
            string[] actionTypes = new string[] { "browse", "read_post", "like", "comment", "post" };
            double[] weights = new double[5];
            
            for (int i = 0; i < actionTypes.Length; i++)
            {
                string actionSection = JsonHelper.ExtractObject(actionsSection, actionTypes[i]);
                double weight = JsonHelper.GetDouble(actionSection, "weight_base", 0.2);
                weights[i] = weight;
            }
            
            CoreHelper.Log(TAG, string.Format("动作权重: browse={0:F2}, read={1:F2}, like={2:F2}, comment={3:F2}, post={4:F2}", 
                weights[0], weights[1], weights[2], weights[3], weights[4]));
            
            // 从配置获取打字速度
            string typingSection = JsonHelper.ExtractObject(behaviorConfigJson, "typing");
            int typingSpeed = 40; // 默认
            
            string typingLevel = JsonHelper.Get(personaJson, "TypingSkillLevel");
            if (string.IsNullOrEmpty(typingLevel)) typingLevel = "regular";
            
            string levelSection = JsonHelper.ExtractObject(typingSection, typingLevel);
            if (!string.IsNullOrEmpty(levelSection))
            {
                int wpmMin = JsonHelper.GetInt(levelSection, "wpm_min", 30);
                int wpmMax = JsonHelper.GetInt(levelSection, "wpm_max", 50);
                typingSpeed = (wpmMin + wpmMax) / 2;
            }
            CoreHelper.Log(TAG, "打字速度: " + typingSpeed + " WPM (级别: " + typingLevel + ")");
            
            // 准备记忆目录
            string today = CoreHelper.GetToday();
            string memoryDir = projectRoot + "Memory\\" + deviceId;
            CoreHelper.EnsureDir(memoryDir);
            string memoryFile = memoryDir + "\\" + today + ".json";
            
            // 模拟会话
            DateTime sessionStart = DateTime.Now;
            DateTime sessionEnd = sessionStart.AddMinutes(sessionDuration);
            int actionCount = 0;
            int failedActions = 0;
            List<string> memoryEntries = new List<string>();
            
            while (DateTime.Now < sessionEnd)
            {
                // BUG-02 fix: 每轮迭代开始前清空上一轮的帖子数据，避免残留
                CoreHelper.SetVar("current_post_json", "");
                CoreHelper.SetVar("current_post_id", "");
                
                // Step 1: 疲劳调权 — 能量不足时自动禁用高消耗动作
                double[] adjustedWeights = AdjustWeightsForFatigue(actionTypes, weights);
                
                // 加权随机选择动作
                string selectedAction = WeightedChoice(actionTypes, adjustedWeights);
                
                // Step 2: RuleEngine 帖子评估门控
                // 读取当前帖子数据（由平台模块 Browse 操作或 unified engine 写入）
                string currentPostJson = CoreHelper.GetVar("current_post_json", "");
                string currentPostId = CoreHelper.GetVar("current_post_id", "");
                
                // Step 2a: MemoryManager 去重检测 (v4.5.1)
                // 对非浏览动作，检查是否在窗口期内已交互过
                if (selectedAction != "browse" && !string.IsNullOrEmpty(currentPostId))
                {
                    string currentPlatformForDedup = CoreHelper.GetVar("current_platform", "reddit");
                    if (MemoryManager.IsDuplicate(deviceId, currentPlatformForDedup, currentPostId, 0))
                    {
                        CoreHelper.Log(TAG, string.Format("MemoryManager 去重: 帖子 {0} 已交互过，降级为 browse", currentPostId));
                        selectedAction = "browse";
                    }
                }
                
                // Step 2b: RuleEngine 评估
                if (!EvaluatePostForAction(selectedAction, currentPostJson))
                {
                    // RuleEngine 拒绝 → 降级为 browse
                    CoreHelper.Log(TAG, string.Format("RuleEngine 拒绝 {0}，降级为 browse", selectedAction));
                    selectedAction = "browse";
                }
                
                CoreHelper.Log(TAG, string.Format("执行动作: {0} (energy={1:F2})", selectedAction, _energy));
                
                // 设置当前动作类型供子项目使用
                CoreHelper.SetVar("current_action", selectedAction);
                
                // 执行实际动作
                string actionResult = "PENDING";
                try {
                    string currentPlatform = CoreHelper.GetVar("current_platform", "reddit");
                    
                    // v4.5: 使用统一引擎执行
                    if (!string.IsNullOrEmpty(_operationsJson))
                    {
                        actionResult = ExecuteWithUnifiedEngine(selectedAction, currentPlatform, projectRoot);
                    }
                    else
                    {
                        // 回退: 无操作配置时使用旧模块模式
                        CoreHelper.SetVar("pending_action", selectedAction);
                        CoreHelper.SetVar("pending_action_type", selectedAction);
                        CoreHelper.SetVar("pending_platform", currentPlatform);
                        
                        string moduleResult = LoadPlatformModule(projectRoot, currentPlatform, selectedAction);
                        
                        if (moduleResult.StartsWith("ERROR"))
                        {
                            actionResult = "ERROR";
                            CoreHelper.LogErr(TAG, "平台模块返回错误: " + moduleResult);
                        }
                        else
                        {
                            actionResult = CoreHelper.GetVar("action_result", "SUCCESS");
                        }
                    }
                } catch (System.Exception ex) {
                    actionResult = "ERROR";
                    CoreHelper.LogErr(TAG, "动作执行失败: " + ex.Message);
                }
                
                // BUG-04 fix: SKIP 不算失败（页面不匹配等正常情况）
                if (actionResult != "SUCCESS" && !actionResult.StartsWith("SKIP")) {
                    failedActions++;
                }
                
                // v4.5.1: 通过 MemoryManager 记录结构化交互
                if (actionResult == "SUCCESS" && selectedAction != "browse" && !string.IsNullOrEmpty(currentPostId))
                {
                    string currentPlatformForRecord = CoreHelper.GetVar("current_platform", "reddit");
                    MemoryManager.RecordInteraction(deviceId, currentPlatformForRecord, currentPostId, selectedAction);
                    CoreHelper.Log(TAG, string.Format("MemoryManager 已记录: {0} on {1}", selectedAction, currentPostId));
                }
                
                // 记录到日记忆文件（兼容 WeeklyEvolve 的文件读取）
                // BUG-13 fix: 使用 JsonHelper.Escape 防止特殊字符破坏 JSON
                string timestamp = DateTime.Now.ToString("HH:mm:ss");
                string logEntry = "{\"time\":\"" + JsonHelper.Escape(timestamp) + "\",\"action_type\":\"" + JsonHelper.Escape(selectedAction) + "\"}";
                memoryEntries.Add(logEntry);
                
                actionCount++;
                
                // 从配置计算动作耗时
                int delayMs = GetActionDelay(selectedAction, actionsSection);
                
                // BUG-05 fix: 分离实际等待时间和能量恢复计算
                // 实际 Thread.Sleep 限制在合理范围（避免阻塞），
                // 但能量恢复用完整的配置延迟（模拟真实操作耗时）
                int sleepMs = Math.Min(delayMs, 5000); // 实际等待最多5秒
                if (sleepMs < 1000) sleepMs = 1000;    // 最少等待1秒
                System.Threading.Thread.Sleep(sleepMs);
                
                // Step 3: 更新能量 — 用完整配置延迟计算恢复（非截断后的 sleepMs）
                UpdateEnergy(selectedAction, delayMs);
                
                // 防止无限循环：设置最大动作数
                if (actionCount >= 50)
                {
                    CoreHelper.Log(TAG, "达到最大动作数限制 (50)，结束会话");
                    break;
                }
            }
            
            // 保存记忆
            string entriesJson = "[" + string.Join(",", memoryEntries.ToArray()) + "]";
            string memoryJson = "{"
                + "\"device_id\":\"" + deviceId + "\","
                + "\"date\":\"" + today + "\","
                + "\"session_start\":\"" + sessionStart.ToString("HH:mm:ss") + "\","
                + "\"session_end\":\"" + DateTime.Now.ToString("HH:mm:ss") + "\","
                + "\"entries\":" + entriesJson
                + "}";
            CoreHelper.WriteFileAtomic(memoryFile, memoryJson);
            
            // v4.5.1: 会话结束时清理过期记忆
            string currentPlatformForCleanup = CoreHelper.GetVar("current_platform", "reddit");
            MemoryManager.CleanupOldInteractions(deviceId, currentPlatformForCleanup, 0);
            MemoryManager.EnforceMemoryLimits(deviceId, currentPlatformForCleanup, _decisionConfigJson);
            CoreHelper.Log(TAG, "MemoryManager 清理完成");
            
            CoreHelper.Log(TAG, "会话结束，执行动作数: " + actionCount + ", 失败动作数: " + failedActions);
            
            // 如果失败动作超过一定比例（例如 50%），则认为会话失败
            bool isSuccess = actionCount > 0 && failedActions <= (actionCount / 2);
            
            if (isSuccess)
            {
                CoreHelper.SetVar("run_result", "SUCCESS");
                CoreHelper.SetVar("action_count", actionCount.ToString());
                CoreHelper.SetVar("session_result", "SUCCESS");
                return "SUCCESS";
            }
            else
            {
                CoreHelper.LogErr(TAG, "会话执行失败: 成功动作数不足");
                CoreHelper.SetVar("run_result", "ERROR");
                CoreHelper.SetVar("session_result", "ERROR");
                return "ERROR: 动作执行失败率过高";
            }
        }
        catch (Exception ex)
        {
            CoreHelper.LogErr(TAG, "异常: " + ex.Message);
            CoreHelper.SetVar("last_error", ex.Message);
            CoreHelper.SetVar("session_result", "ERROR");
            return "ERROR: " + ex.Message;
        }
    }
    
    /// <summary>
    /// 加权随机选择
    /// </summary>
    private static string WeightedChoice(string[] items, double[] weights)
    {
        // BUG-10 fix: 先计算权重总和，用 r * total 归一化
        // 防止权重之和 != 1.0 时出现偏差
        double total = 0;
        for (int i = 0; i < weights.Length; i++)
        {
            total += weights[i];
        }
        if (total <= 0)
        {
            return items[items.Length - 1];
        }
        
        double r = _random.NextDouble() * total;
        double sum = 0;
        for (int i = 0; i < weights.Length; i++)
        {
            sum += weights[i];
            if (r <= sum && i < items.Length)
            {
                return items[i];
            }
        }
        return items[items.Length - 1];
    }
    
    /// <summary>
    /// 从配置获取动作延迟时间（毫秒）
    /// </summary>
    private static int GetActionDelay(string action, string actionsSection)
    {
        string actionSection = JsonHelper.ExtractObject(actionsSection, action);
        
        int minSec = JsonHelper.GetInt(actionSection, "duration_sec_min", 5);
        int maxSec = JsonHelper.GetInt(actionSection, "duration_sec_max", 30);
        
        // 转换为毫秒并加入随机变化
        int delayMs = _random.Next(minSec * 1000, maxSec * 1000);
        return delayMs;
    }
    
    /// <summary>
    /// 根据疲劳模型调整动作权重
    /// 能量不足时，高消耗动作（comment、like）权重降为 0
    /// </summary>
    private static double[] AdjustWeightsForFatigue(string[] actionTypes, double[] baseWeights)
    {
        if (!_fatigueEnabled)
        {
            return baseWeights;
        }
        
        double[] adjusted = new double[baseWeights.Length];
        double removedWeight = 0.0;
        int activeCount = 0;
        
        for (int i = 0; i < actionTypes.Length; i++)
        {
            string action = actionTypes[i];
            bool blocked = false;
            
            // 能量不足时禁用高消耗动作
            if (action == "comment" && _energy < _minEnergyToComment)
            {
                blocked = true;
            }
            else if (action == "like" && _energy < _minEnergyToLike)
            {
                blocked = true;
            }
            
            if (blocked)
            {
                adjusted[i] = 0.0;
                removedWeight += baseWeights[i];
                CoreHelper.Log(TAG, string.Format("疲劳禁用动作: {0} (energy={1:F2})", action, _energy));
            }
            else
            {
                adjusted[i] = baseWeights[i];
                activeCount++;
            }
        }
        
        // 将被禁用的权重按比例分配给剩余动作
        if (removedWeight > 0.0 && activeCount > 0)
        {
            double totalActive = 0.0;
            for (int i = 0; i < adjusted.Length; i++)
            {
                totalActive += adjusted[i];
            }
            
            if (totalActive > 0.0)
            {
                for (int i = 0; i < adjusted.Length; i++)
                {
                    if (adjusted[i] > 0.0)
                    {
                        adjusted[i] = adjusted[i] + (removedWeight * adjusted[i] / totalActive);
                    }
                }
            }
        }
        
        // 归一化确保总和为 1.0
        double sum = 0.0;
        for (int i = 0; i < adjusted.Length; i++)
        {
            sum += adjusted[i];
        }
        if (sum > 0.0)
        {
            for (int i = 0; i < adjusted.Length; i++)
            {
                adjusted[i] = adjusted[i] / sum;
            }
        }
        
        return adjusted;
    }
    
    /// <summary>
    /// 将 SessionRunner 动作名映射到 RuleEngine 决策类型
    /// </summary>
    private static string MapActionToRuleAction(string action)
    {
        if (action == "browse") return "continue_browsing";
        if (action == "read_post") return "click_post";
        if (action == "like") return "like_post";
        if (action == "comment") return "comment_post";
        // post 没有对应的 RuleEngine 阈值，用 comment_post 的高门槛
        if (action == "post") return "comment_post";
        return "continue_browsing";
    }
    
    /// <summary>
    /// 评估当前帖子是否值得执行指定动作
    /// 返回 true = 执行，false = 跳过（降级为 browse）
    /// 无帖子数据时优雅降级：始终返回 true
    /// </summary>
    private static bool EvaluatePostForAction(string action, string postJson)
    {
        // 无帖子数据 → 降级到纯加权随机（兼容旧流程）
        if (string.IsNullOrEmpty(postJson) || postJson == "{}")
        {
            return true;
        }
        
        // browse 不需要评估
        if (action == "browse")
        {
            return true;
        }
        
        // 检查避免话题
        if (RuleEngine.CheckAvoidTopics(postJson, _triggersJson))
        {
            CoreHelper.Log(TAG, "帖子命中避免话题，跳过: " + action);
            return false;
        }
        
        // 计算综合评分
        double hotScore = RuleEngine.CalculateHotScore(postJson);
        double activityScore = RuleEngine.CalculateActivityScore(postJson, "");
        double relevanceScore = RuleEngine.CalculateRelevanceScore(postJson, _interestsJson, _triggersJson);
        double compositeScore = RuleEngine.CalculateCompositeScore(hotScore, activityScore, relevanceScore);
        
        string ruleAction = MapActionToRuleAction(action);
        bool shouldInteract = RuleEngine.ShouldInteract(compositeScore, ruleAction, _decisionConfigJson);
        
        CoreHelper.Log(TAG, string.Format(
            "RuleEngine 评估: action={0}, hot={1:F2}, activity={2:F2}, relevance={3:F2}, composite={4:F2}, interact={5}",
            action, hotScore, activityScore, relevanceScore, compositeScore, shouldInteract));
        
        return shouldInteract;
    }
    
    /// <summary>
    /// 更新能量值（执行动作后消耗，等待时恢复）
    /// </summary>
    private static void UpdateEnergy(string action, int pauseMs)
    {
        if (!_fatigueEnabled)
        {
            return;
        }
        
        // 消耗能量
        double decay = 0.0;
        if (action == "browse") decay = _decayBrowse;
        else if (action == "read_post") decay = _decayRead;
        else if (action == "like") decay = _decayLike;
        else if (action == "comment") decay = _decayComment;
        else decay = _decayBrowse; // post 等其他动作用 browse 的消耗
        
        _energy -= decay;
        
        // 等待期间恢复能量
        double pauseSec = pauseMs / 1000.0;
        _energy += pauseSec * _recoveryPerPauseSec;
        
        // 钳制到 [0, 1]
        if (_energy < 0.0) _energy = 0.0;
        if (_energy > 1.0) _energy = 1.0;
        
        CoreHelper.Log(TAG, string.Format("能量更新: action={0}, decay={1:F3}, recovery={2:F3}, energy={3:F2}",
            action, decay, pauseSec * _recoveryPerPauseSec, _energy));
    }
    
    /// <summary>
    /// 根据设备ID确定使用哪个平台
    /// 从 device_app_mapping.json 读取映射关系
    /// </summary>
    private static string DeterminePlatform(string projectRoot, string deviceId)
    {
        string mappingPath = projectRoot + "Config\\device_app_mapping.json";
        
        if (!File.Exists(mappingPath))
        {
            CoreHelper.Log(TAG, "device_app_mapping.json 不存在，默认使用 reddit");
            return "reddit";
        }
        
        try
        {
            string mappingJson = CoreHelper.ReadFile(mappingPath);
            string devicesSection = JsonHelper.ExtractObject(mappingJson, "devices");
            string deviceConfig = JsonHelper.ExtractObject(devicesSection, deviceId);
            
            if (string.IsNullOrEmpty(deviceConfig))
            {
                CoreHelper.Log(TAG, "设备 " + deviceId + " 未配置，默认使用 reddit");
                return "reddit";
            }
            
            string platform = JsonHelper.Get(deviceConfig, "platform");
            
            if (string.IsNullOrEmpty(platform))
            {
                CoreHelper.Log(TAG, "设备 " + deviceId + " 未指定平台，默认使用 reddit");
                return "reddit";
            }
            
            return platform.ToLower();
        }
        catch (Exception ex)
        {
            CoreHelper.LogErr(TAG, "读取 device_app_mapping.json 失败: " + ex.Message);
            return "reddit";
        }
    }
    
    /// <summary>
    /// v4.5 统一引擎执行入口
    /// 将 SessionRunner 动作名映射到 operations JSON 中的操作名，
    /// 并处理页面状态转换逻辑（如 read_post 需要先 open_post）。
    /// </summary>
    private static string ExecuteWithUnifiedEngine(string sessionAction, string platformName, string projectRoot)
    {
        // 先检测当前页面状态
        string xml = CoreHelper.GetLayout();
        if (!string.IsNullOrEmpty(xml))
        {
            string signaturesJson = JsonHelper.ExtractObject(_platformConfig, "page_signatures");
            if (!string.IsNullOrEmpty(signaturesJson))
            {
                _currentPage = PageDetector.Detect(xml, signaturesJson);
                CoreHelper.Log(TAG, "当前页面: " + _currentPage);
                CoreHelper.SetVar("current_page", _currentPage);
            }
        }
        
        // 映射 SessionRunner 动作名 → operations JSON 操作名序列
        // 某些动作可能需要多步操作（如 read_post = open_post → read_post）
        string[] opSequence = MapActionToOperations(sessionAction, _currentPage);
        
        if (opSequence.Length == 0)
        {
            CoreHelper.Log(TAG, string.Format("动作 {0} 在页面 {1} 无对应操作，跳过", sessionAction, _currentPage));
            return "SKIP";
        }
        
        string lastResult = "SUCCESS";
        
        for (int i = 0; i < opSequence.Length; i++)
        {
            string opName = opSequence[i];
            CoreHelper.Log(TAG, string.Format("统一引擎执行: {0} ({1}/{2})", opName, i + 1, opSequence.Length));
            
            string result = ActionExecutor.Execute(_operationsJson, opName, _platformConfig);
            
            if (result.StartsWith("ERROR"))
            {
                CoreHelper.LogErr(TAG, string.Format("操作 {0} 失败: {1}", opName, result));
                lastResult = "ERROR";
                break;
            }
            
            if (result.StartsWith("SKIP"))
            {
                CoreHelper.Log(TAG, string.Format("操作 {0} 跳过: {1}", opName, result));
                // SKIP 不算失败，但也不继续后续步骤
                lastResult = "SKIP";
                break;
            }
            
            // 操作成功，更新页面状态
            xml = CoreHelper.GetLayout();
            if (!string.IsNullOrEmpty(xml))
            {
                string sigJson = JsonHelper.ExtractObject(_platformConfig, "page_signatures");
                if (!string.IsNullOrEmpty(sigJson))
                {
                    _currentPage = PageDetector.Detect(xml, sigJson);
                    CoreHelper.SetVar("current_page", _currentPage);
                }
            }
            
            lastResult = "SUCCESS";
            
            // v4.5.2: 从 ActionExecutor 上下文中提取帖子数据并设置 ZD 变量
            // BUG-02 fix: 同时设置 current_post_json 供 RuleEngine 使用
            // BUG-06 fix: 使用稳定标识符替代 GetHashCode()
            string foundPost = ActionExecutor.GetContext("target_post");
            if (!string.IsNullOrEmpty(foundPost))
            {
                // BUG-06 fix: 使用内容截断作为稳定标识符（取代不可靠的 GetHashCode）
                string postIdentifier = foundPost.Length > 80 ? foundPost.Substring(0, 80) : foundPost;
                postIdentifier = System.Text.RegularExpressions.Regex.Replace(postIdentifier, "[^a-zA-Z0-9_\\-]", "_");
                CoreHelper.SetVar("current_post_id", postIdentifier);
                
                // BUG-02 fix: 构建最小帖子 JSON 供 RuleEngine 评估
                string ctxTitle = ActionExecutor.GetContext("post_title");
                string ctxSub = ActionExecutor.GetContext("post_subreddit");
                string ctxUp = ActionExecutor.GetContext("post_upvotes");
                string ctxComments = ActionExecutor.GetContext("post_comment_count");
                string ctxTs = ActionExecutor.GetContext("post_timestamp");
                
                StringBuilder pjb = new StringBuilder("{");
                pjb.Append("\"title\":\"").Append(JsonHelper.Escape(ctxTitle != null ? ctxTitle : "")).Append("\"");
                if (!string.IsNullOrEmpty(ctxSub))
                {
                    pjb.Append(",\"subreddit\":\"").Append(JsonHelper.Escape(ctxSub)).Append("\"");
                }
                if (!string.IsNullOrEmpty(ctxUp))
                {
                    pjb.Append(",\"upvotes\":").Append(ctxUp);
                }
                if (!string.IsNullOrEmpty(ctxComments))
                {
                    pjb.Append(",\"comment_count\":").Append(ctxComments);
                }
                if (!string.IsNullOrEmpty(ctxTs))
                {
                    pjb.Append(",\"timestamp\":\"").Append(JsonHelper.Escape(ctxTs)).Append("\"");
                }
                pjb.Append("}");
                CoreHelper.SetVar("current_post_json", pjb.ToString());
            }
        }
        
        // 设置结果变量
        CoreHelper.SetVar("action_result", lastResult);
        return lastResult;
    }
    
    /// <summary>
    /// 将 SessionRunner 动作名映射到 operations JSON 操作名序列
    /// 根据当前页面状态决定需要哪些步骤
    /// </summary>
    private static string[] MapActionToOperations(string sessionAction, string currentPage)
    {
        // browse: 在 feed 页面滚动浏览
        if (sessionAction == "browse")
        {
            if (currentPage != "feed" && currentPage != "unknown")
            {
                // 不在 feed 页面 → 先返回 feed，再浏览
                return new string[] { "back_to_feed", "browse" };
            }
            return new string[] { "browse" };
        }
        
        // read_post: 需要先打开帖子，再阅读
        if (sessionAction == "read_post")
        {
            if (currentPage == "post_detail")
            {
                // 已经在详情页 → 直接阅读（操作名兼容：read_post 或 view_post）
                return new string[] { GetReadOperationName(), "back_to_feed" };
            }
            if (currentPage == "feed" || currentPage == "unknown")
            {
                // 在 feed 页面 → 打开帖子 → 阅读 → 返回
                return new string[] { "open_post", GetReadOperationName(), "back_to_feed" };
            }
            // 其他页面 → 先返回 feed
            return new string[] { "back_to_feed", "open_post", GetReadOperationName(), "back_to_feed" };
        }
        
        // like: 在 feed 页面点赞
        if (sessionAction == "like")
        {
            if (currentPage != "feed" && currentPage != "unknown")
            {
                return new string[] { "back_to_feed", "like" };
            }
            return new string[] { "like" };
        }
        
        // comment: 需要在详情页评论
        if (sessionAction == "comment")
        {
            if (currentPage == "post_detail")
            {
                return new string[] { "comment", "back_to_feed" };
            }
            if (currentPage == "feed" || currentPage == "unknown")
            {
                return new string[] { "open_post", "comment", "back_to_feed" };
            }
            return new string[] { "back_to_feed", "open_post", "comment", "back_to_feed" };
        }
        
        // post: 不通过统一引擎处理（需要专门的创建帖子流程）
        if (sessionAction == "post")
        {
            CoreHelper.Log(TAG, "post 操作暂不支持统一引擎，跳过");
            return new string[0];
        }
        
        // 其他动作：尝试直接映射
        // 如 follow, share 等直接对应 operations JSON 中的同名操作
        return new string[] { sessionAction };
    }
    
    /// <summary>
    /// 获取阅读操作名（不同平台命名不同：reddit=read_post, instagram=view_post）
    /// 优先使用 operations JSON 中实际存在的名称
    /// </summary>
    private static string GetReadOperationName()
    {
        // 检查 operations JSON 中是否有 read_post
        string readOp = JsonHelper.ExtractObject(_operationsJson, "read_post");
        if (!string.IsNullOrEmpty(readOp))
        {
            return "read_post";
        }
        
        // 检查 view_post (Instagram 用的名称)
        string viewOp = JsonHelper.ExtractObject(_operationsJson, "view_post");
        if (!string.IsNullOrEmpty(viewOp))
        {
            return "view_post";
        }
        
        // 默认
        return "read_post";
    }
    
    /// <summary>
    /// 加载并执行平台模块（旧模式，v4.5 保留作为回退）
    /// 根据平台名称动态加载对应的模块文件
    /// 支持回退到 ZDProjects 脚本
    /// </summary>
    private static string LoadPlatformModule(string projectRoot, string platformName, string operation)
    {
        // 构建平台模块路径
        string capitalName = char.ToUpper(platformName[0]) + platformName.Substring(1);
        string modulePath = projectRoot + "Platforms\\" + capitalName + "\\" + capitalName + "Module.cs";
        
        if (!File.Exists(modulePath))
        {
            // 回退到 ZDProjects 脚本
            string scriptName = capitalName + "_" + char.ToUpper(operation[0]) + operation.Substring(1);
            string scriptPath = projectRoot + "ZDProjects\\" + scriptName + ".cs";
            
            if (!File.Exists(scriptPath))
            {
                CoreHelper.LogErr(TAG, "平台模块和脚本均不存在: " + modulePath + " / " + scriptPath);
                return "ERROR: 模块文件不存在";
            }
            
            modulePath = scriptPath;
        }
        
        CoreHelper.Log(TAG, "加载平台模块: " + modulePath + " 操作: " + operation);
        
        // 设置操作参数供模块读取
        CoreHelper.SetVar("module_operation", operation);
        CoreHelper.SetVar("module_path", modulePath);
        
        // 注意：实际执行需要通过 ZD 的 project.Execute() 或 ModuleLoader 机制
        // 这里设置变量，由外层 ZD 流程读取 module_path 并执行
        // 如果在 ModuleLoader 编译环境中，可以直接调用：
        // return ModuleLoader.RunModule(modulePath, "Run", new object[] { _project });
        
        return "DISPATCHED:" + modulePath;
    }
}
