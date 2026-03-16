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
    
    [System.ThreadStatic]
    private static Random _threadRandom;
    
    private static Random GetRandom()
    {
        if (_threadRandom == null)
        {
            _threadRandom = new Random(unchecked(Environment.TickCount * 31 + System.Threading.Thread.CurrentThread.ManagedThreadId));
        }
        return _threadRandom;
    }
    
    private class SessionState
    {
        public bool FatigueEnabled;
        public double Energy;
        public double DecayBrowse;
        public double DecayRead;
        public double DecayLike;
        public double DecayComment;
        public double RecoveryPerPauseSec;
        public double MinEnergyToComment;
        public double MinEnergyToLike;
    }
    
    // 规则引擎数据
    private static string _interestsJson = "";
    private static string _triggersJson = "";
    private static string _decisionConfigJson = "";
    
    // 统一引擎数据（v4.5）
    private static string _operationsJson = "";
    private static string _platformConfig = "";
    private static string _intentMappingJson = "";
    private static string _userStrategyJson = "";
    
    // 页面状态追踪
    private static string _currentPage = "unknown";
    
    // 连续 SKIP 计数器（导航恢复用）
    private static int _consecutiveSkips = 0;
    private const int MAX_CONSECUTIVE_SKIPS = 3;
    
    // v4.5.12: Vision 恢复调用计数（单次会话限制）
    private static int _visionRecoveryCount = 0;
    private const int MAX_VISION_RECOVERY_PER_SESSION = 5;
    
    // v4.6.0: 智能编排器（ADR-015）
    private static SmartOrchestrator _orchestrator = new SmartOrchestrator();
    
    // AI 控制策略
    private static bool _aiDirectExecution = true;
    private static bool _notifyHumanOnAiControl = false;
    
    // v4.7.0: 操作队列（ZD 外层流程编排用）
    // 一个 sessionAction（如 read_post）可能映射为多个 operation（open_post → read_post → back_to_feed）
    // DecideNextAction 每次返回队列中的下一个 operation
    private static List<string> _operationQueue = new List<string>();
    private static int _operationQueueIndex = 0;
    
    // ==================== ZD 变量 Key 常量（跨 cube 传递） ====================
    // v4.7.0: SessionRunner 拆分为 4 个入口方法后，所有共享状态通过 ZD 变量传递
    // 此处定义 key 名称常量，确保跨文件一致性
    
    private static class VK
    {
        // 会话配置（Init 写入，Decide/Check/Finalize 读取）
        public const string ProjectRoot = "project_root";
        public const string DeviceId = "device_id";
        public const string PlatformName = "current_platform";
        public const string SessionEndTime = "sr_session_end_time";
        public const string BehaviorConfigJson = "behavior_config_json";
        
        // 疲劳模型状态
        public const string SessionStateJson = "sr_session_state";
        
        // 动作权重
        public const string ActionWeightsJson = "sr_action_weights";
        
        // 动作计数器
        public const string ActionCount = "sr_action_count";
        public const string SuccessCount = "sr_success_count";
        public const string FailCount = "sr_fail_count";
        public const string SkipCount = "sr_skip_count";
        public const string ConsecutiveSkips = "sr_consecutive_skips";
        public const string VisionRecoveryCount = "sr_vision_recovery_count";
        
        // SmartOrchestrator 状态
        public const string OrchestratorState = "sr_orchestrator_state";
        
        // 操作队列
        public const string OpQueue = "sr_op_queue";
        public const string OpQueueIndex = "sr_op_queue_index";
        
        // 当前动作决策结果
        public const string CurrentSessionAction = "sr_current_session_action";
        public const string CurrentOpName = "sr_current_op_name";
        
        // 记忆条目（JSON 数组字符串）
        public const string MemoryEntries = "sr_memory_entries";
        public const string MemoryFile = "sr_memory_file";
        public const string SessionStartTime = "sr_session_start_time";
        
        // Step DSL 前置变量（InitSession 写入，StepRunner/Advancer/ZD 原生块读取）
        public const string ZdScreenWidth = "zd_screen_width";
        public const string ZdScreenHeight = "zd_screen_height";
        public const string ZdStepPlan = "zd_step_plan";
        public const string ZdStepIndex = "zd_step_index";
        public const string ZdStepCount = "zd_step_count";
        public const string ZdStepType = "zd_step_type";
        public const string ZdStepParam = "zd_step_param";
        public const string ZdSelectorKey = "zd_selector_key";
        public const string ZdTapX1 = "zd_tap_x1";
        public const string ZdTapX2 = "zd_tap_x2";
        public const string ZdTapY1 = "zd_tap_y1";
        public const string ZdTapY2 = "zd_tap_y2";
        public const string ZdSwipeX1 = "zd_swipe_x1";
        public const string ZdSwipeY1 = "zd_swipe_y1";
        public const string ZdSwipeX2 = "zd_swipe_x2";
        public const string ZdSwipeY2 = "zd_swipe_y2";
        public const string ZdSwipeDuration = "zd_swipe_duration";
        public const string ZdWaitSec = "zd_wait_sec";
        public const string ZdFound = "zd_found";
        public const string ZdVerifyRule = "zd_verify_rule";
        public const string ZdSafety = "zd_safety";
        
        // ZD 原生块执行结果回收变量（ZD 原生块写入，EvaluateActionResult 读取）
        public const string ZdActionResult = "zd_action_result";
        public const string ZdActionDuration = "zd_action_duration";
        public const string ZdActionErrorDetail = "zd_action_error_detail";
        
        // T17: 兼容开关（四入口 vs 旧 Run 整体式执行）
        // sr_use_legacy_run = "true" 时，InitSession 内部委托 Run() 完成整个会话
        // 默认值为空（走新四入口逻辑），不改变当前系统行为
        public const string UseLegacyRun = "sr_use_legacy_run";
        // InitSession 委托 Run() 完成后设置此标志，后续入口方法检测到后直接 no-op
        public const string LegacyRunCompleted = "sr_legacy_run_completed";
        // Run() 通过 InitSession 委托执行时的返回结果
        public const string LegacyRunResult = "sr_legacy_run_result";
    }
    
    // ==================== SessionState 序列化 ====================
    
    /// <summary>
    /// 将 SessionState 序列化为 JSON 字符串，用于跨 cube 传递
    /// </summary>
    private static string SerializeSessionState(SessionState state)
    {
        return "{"
            + "\"fe\":" + (state.FatigueEnabled ? "1" : "0")
            + ",\"en\":" + state.Energy.ToString("F4")
            + ",\"db\":" + state.DecayBrowse.ToString("F4")
            + ",\"dr\":" + state.DecayRead.ToString("F4")
            + ",\"dl\":" + state.DecayLike.ToString("F4")
            + ",\"dc\":" + state.DecayComment.ToString("F4")
            + ",\"rp\":" + state.RecoveryPerPauseSec.ToString("F4")
            + ",\"mec\":" + state.MinEnergyToComment.ToString("F4")
            + ",\"mel\":" + state.MinEnergyToLike.ToString("F4")
            + "}";
    }
    
    /// <summary>
    /// 从 JSON 字符串反序列化 SessionState
    /// </summary>
    private static SessionState DeserializeSessionState(string json)
    {
        SessionState state = new SessionState();
        if (string.IsNullOrEmpty(json))
        {
            state.FatigueEnabled = false;
            state.Energy = 1.0;
            state.DecayBrowse = 0.02;
            state.DecayRead = 0.05;
            state.DecayLike = 0.03;
            state.DecayComment = 0.10;
            state.RecoveryPerPauseSec = 0.01;
            state.MinEnergyToComment = 0.3;
            state.MinEnergyToLike = 0.15;
            return state;
        }
        
        state.FatigueEnabled = (JsonHelper.Get(json, "fe") == "1");
        state.Energy = JsonHelper.GetDouble(json, "en", 1.0);
        state.DecayBrowse = JsonHelper.GetDouble(json, "db", 0.02);
        state.DecayRead = JsonHelper.GetDouble(json, "dr", 0.05);
        state.DecayLike = JsonHelper.GetDouble(json, "dl", 0.03);
        state.DecayComment = JsonHelper.GetDouble(json, "dc", 0.10);
        state.RecoveryPerPauseSec = JsonHelper.GetDouble(json, "rp", 0.01);
        state.MinEnergyToComment = JsonHelper.GetDouble(json, "mec", 0.3);
        state.MinEnergyToLike = JsonHelper.GetDouble(json, "mel", 0.15);
        return state;
    }
    
    /// <summary>
    /// 将动作权重数组序列化为 JSON
    /// </summary>
    private static string SerializeWeights(string[] actionTypes, double[] weights)
    {
        StringBuilder sb = new StringBuilder("{");
        for (int i = 0; i < actionTypes.Length && i < weights.Length; i++)
        {
            if (i > 0) sb.Append(",");
            sb.Append("\"");
            sb.Append(actionTypes[i]);
            sb.Append("\":");
            sb.Append(weights[i].ToString("F4"));
        }
        sb.Append("}");
        return sb.ToString();
    }
    
    /// <summary>
    /// 从 JSON 反序列化动作权重
    /// </summary>
    private static double[] DeserializeWeights(string json, string[] actionTypes)
    {
        double[] weights = new double[actionTypes.Length];
        for (int i = 0; i < actionTypes.Length; i++)
        {
            weights[i] = JsonHelper.GetDouble(json, actionTypes[i], 0.2);
        }
        return weights;
    }
    
    /// <summary>
    /// 将操作队列序列化为管道分隔字符串
    /// </summary>
    private static string SerializeOpQueue(List<string> queue)
    {
        if (queue == null || queue.Count == 0) return "";
        return string.Join("|", queue.ToArray());
    }
    
    /// <summary>
    /// 从管道分隔字符串反序列化操作队列
    /// </summary>
    private static List<string> DeserializeOpQueue(string s)
    {
        List<string> queue = new List<string>();
        if (string.IsNullOrEmpty(s)) return queue;
        string[] parts = s.Split('|');
        for (int i = 0; i < parts.Length; i++)
        {
            if (!string.IsNullOrEmpty(parts[i]))
            {
                queue.Add(parts[i]);
            }
        }
        return queue;
    }
    
    /// <summary>
    /// 保存所有动态状态到 ZD 变量（Decide/Check 方法出口调用）
    /// </summary>
    private static void SaveDynamicState(SessionState sessionState, int actionCount,
        int successCount, int failCount, int skipCount)
    {
        CoreHelper.SetVar(VK.SessionStateJson, SerializeSessionState(sessionState));
        CoreHelper.SetVar(VK.ActionCount, actionCount.ToString());
        CoreHelper.SetVar(VK.SuccessCount, successCount.ToString());
        CoreHelper.SetVar(VK.FailCount, failCount.ToString());
        CoreHelper.SetVar(VK.SkipCount, skipCount.ToString());
        CoreHelper.SetVar(VK.ConsecutiveSkips, _consecutiveSkips.ToString());
        CoreHelper.SetVar(VK.VisionRecoveryCount, _visionRecoveryCount.ToString());
        CoreHelper.SetVar(VK.OrchestratorState, _orchestrator.SaveState());
        CoreHelper.SetVar(VK.OpQueue, SerializeOpQueue(_operationQueue));
        CoreHelper.SetVar(VK.OpQueueIndex, _operationQueueIndex.ToString());
    }
    
    /// <summary>
    /// 从 ZD 变量恢复所有必要的运行时状态（Decide/Check/Finalize 入口调用）
    /// 同时重新加载配置文件到静态字段（因为每个 cube 编译为独立 assembly）
    /// </summary>
    private static SessionState RestoreState(out int actionCount, out int successCount,
        out int failCount, out int skipCount)
    {
        string projectRoot = CoreHelper.GetVar(VK.ProjectRoot, "");
        projectRoot = CoreHelper.NormalizePath(projectRoot);
        string platformName = CoreHelper.GetVar(VK.PlatformName, "reddit");
        
        // 重新加载配置到静态字段（独立 assembly 静态字段不共享）
        ReloadConfigs(projectRoot, platformName);
        
        // 恢复编排器状态
        _orchestrator.LoadState(CoreHelper.GetVar(VK.OrchestratorState, ""));
        
        // 恢复计数器
        actionCount = CoreHelper.GetVarInt(VK.ActionCount, 0);
        successCount = CoreHelper.GetVarInt(VK.SuccessCount, 0);
        failCount = CoreHelper.GetVarInt(VK.FailCount, 0);
        skipCount = CoreHelper.GetVarInt(VK.SkipCount, 0);
        _consecutiveSkips = CoreHelper.GetVarInt(VK.ConsecutiveSkips, 0);
        _visionRecoveryCount = CoreHelper.GetVarInt(VK.VisionRecoveryCount, 0);
        
        // 恢复操作队列
        _operationQueue = DeserializeOpQueue(CoreHelper.GetVar(VK.OpQueue, ""));
        _operationQueueIndex = CoreHelper.GetVarInt(VK.OpQueueIndex, 0);
        
        // 恢复当前页面
        _currentPage = CoreHelper.GetVar("current_page", "unknown");
        
        // 恢复 SessionState
        return DeserializeSessionState(CoreHelper.GetVar(VK.SessionStateJson, ""));
    }
    
    /// <summary>
    /// 重新从磁盘加载所有配置文件到静态字段
    /// 每个 C# code cube 编译为独立 assembly，静态字段不共享，必须每次重新加载
    /// </summary>
    private static void ReloadConfigs(string projectRoot, string platformName)
    {
        if (string.IsNullOrEmpty(projectRoot)) return;
        
        // 加载平台配置
        string platformsConfigPath = projectRoot + "Config\\PlatformsConfig.json";
        if (File.Exists(platformsConfigPath))
        {
            string platformsConfigJson = CoreHelper.ReadFile(platformsConfigPath);
            string platformsSection = JsonHelper.ExtractObject(platformsConfigJson, "platforms");
            _platformConfig = JsonHelper.ExtractObject(platformsSection, platformName);
        }
        
        // 加载操作配置
        string opsPath = projectRoot + "Config\\Operations\\" + platformName + "_operations.json";
        if (File.Exists(opsPath))
        {
            string rawOpsJson = CoreHelper.ReadFile(opsPath);
            string opsSection = JsonHelper.ExtractObject(rawOpsJson, "operations");
            _operationsJson = string.IsNullOrEmpty(opsSection) ? rawOpsJson : opsSection;
        }
        
        // 加载意图映射
        string mappingPath = projectRoot + "Config\\IntentMappings\\" + platformName + "_intents.json";
        if (File.Exists(mappingPath))
        {
            _intentMappingJson = CoreHelper.ReadFile(mappingPath);
        }
        
        // 加载用户策略
        string strategyPath = projectRoot + "Config\\UserStrategy.json";
        if (File.Exists(strategyPath))
        {
            _userStrategyJson = CoreHelper.ReadFile(strategyPath);
            string aiControl = JsonHelper.ExtractObject(_userStrategyJson, "ai_control");
            string aiControlEnabled = JsonHelper.Get(aiControl, "enabled");
            if (aiControlEnabled == "false")
            {
                _aiDirectExecution = false;
            }
            else
            {
                string directExecution = JsonHelper.Get(aiControl, "direct_execution");
                _aiDirectExecution = (directExecution != "false");
            }
        }
        
        // 加载决策配置
        string decisionConfigPath = projectRoot + "Config\\DecisionConfig.json";
        if (File.Exists(decisionConfigPath))
        {
            _decisionConfigJson = CoreHelper.ReadFile(decisionConfigPath);
            RuleEngine.LoadConfig(_decisionConfigJson);
        }
        
        // 加载 interests / triggers
        string interestsPath = projectRoot + "Data\\Keywords\\" + platformName + "\\interests.json";
        if (File.Exists(interestsPath))
        {
            _interestsJson = CoreHelper.ReadFile(interestsPath);
        }
        string triggersPath = projectRoot + "Data\\Keywords\\" + platformName + "\\triggers.json";
        if (File.Exists(triggersPath))
        {
            _triggersJson = CoreHelper.ReadFile(triggersPath);
        }
    }
    
    /// <summary>
    /// 模块入口点
    /// </summary>
    public static string Run(object projectObj, object instanceObj)
    {
        _project = projectObj;
        
        try
        {
            CoreHelper.Init(projectObj, instanceObj);
            
            
            // ========== 设备连接检测 ==========
            if (!CoreHelper.HasInstance())
            {
                CoreHelper.LogErr(TAG, "设备未连接: instance 对象为空");
                CoreHelper.SetVar("session_result", "ERROR");
                return "ERROR: 设备未连接 - ZennoDroid instance 未初始化";
            }
            
            dynamic droid = CoreHelper.GetDroid();
            if (droid == null)
            {
                CoreHelper.LogErr(TAG, "设备未连接: DroidInstance 为空");
                CoreHelper.SetVar("session_result", "ERROR");
                return "ERROR: 设备未连接 - DroidInstance 不可用";
            }
            
            // 尝试获取 UI 层级，验证设备真实连接
            try
            {
                string testLayout = CoreHelper.GetLayout();
                if (string.IsNullOrEmpty(testLayout))
                {
                    CoreHelper.LogErr(TAG, "设备未连接: GetLayout 返回空");
                    CoreHelper.SetVar("session_result", "ERROR");
                    return "ERROR: 设备未连接 - 无法获取 UI 层级";
                }
                CoreHelper.Log(TAG, "设备连接检测通过");
            }
            catch (Exception ex)
            {
                CoreHelper.LogErr(TAG, "设备未连接: GetLayout 异常 - " + ex.Message);
                CoreHelper.SetVar("session_result", "ERROR");
                return "ERROR: 设备未连接 - " + ex.Message;
            }

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
            
            // v4.6.0: 初始化智能编排器（ADR-015）
            _orchestrator.ResetAll();
            _orchestrator.LoadConfig(behaviorConfigJson);
            
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
            
            SessionState sessionState = new SessionState();
            
            string fatigueSection = JsonHelper.ExtractObject(_decisionConfigJson, "fatigue_model");
            if (!string.IsNullOrEmpty(fatigueSection))
            {
                string enabledFatigue = JsonHelper.Get(fatigueSection, "enabled");
                sessionState.FatigueEnabled = (enabledFatigue == "true");
                sessionState.Energy = JsonHelper.GetDouble(fatigueSection, "initial_energy", 1.0);
                sessionState.RecoveryPerPauseSec = JsonHelper.GetDouble(fatigueSection, "recovery_per_pause_sec", 0.01);
                sessionState.MinEnergyToComment = JsonHelper.GetDouble(fatigueSection, "min_energy_to_comment", 0.3);
                sessionState.MinEnergyToLike = JsonHelper.GetDouble(fatigueSection, "min_energy_to_like", 0.15);
                
                string decaySection = JsonHelper.ExtractObject(fatigueSection, "decay_per_action");
                if (!string.IsNullOrEmpty(decaySection))
                {
                    sessionState.DecayBrowse = JsonHelper.GetDouble(decaySection, "browse", 0.02);
                    sessionState.DecayRead = JsonHelper.GetDouble(decaySection, "read", 0.05);
                    sessionState.DecayLike = JsonHelper.GetDouble(decaySection, "like", 0.03);
                    sessionState.DecayComment = JsonHelper.GetDouble(decaySection, "comment", 0.10);
                }
                
                CoreHelper.Log(TAG, string.Format("疲劳模型: enabled={0}, energy={1:F2}", sessionState.FatigueEnabled, sessionState.Energy));
            }
            else
            {
                sessionState.FatigueEnabled = false;
                sessionState.Energy = 1.0;
                sessionState.DecayBrowse = 0.02;
                sessionState.DecayRead = 0.05;
                sessionState.DecayLike = 0.03;
                sessionState.DecayComment = 0.10;
                sessionState.RecoveryPerPauseSec = 0.01;
                sessionState.MinEnergyToComment = 0.3;
                sessionState.MinEnergyToLike = 0.15;
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
                // ========== 自动接入新平台 (App Onboarder) ==========
                // 尝试从 device_app_mapping 获取包名，调用 Python app_onboarder 自动生成配置
                string packageName = GetPackageNameForPlatform(projectRoot, platformName);
                if (!string.IsNullOrEmpty(packageName))
                {
                    CoreHelper.Log(TAG, "未找到平台配置: " + platformName + "，尝试自动接入...");
                    bool onboardSuccess = RunAppOnboarder(projectRoot, packageName, platformName);
                    
                    if (onboardSuccess)
                    {
                        // 重新加载配置
                        if (File.Exists(platformsConfigPath))
                        {
                            platformsConfigJson = CoreHelper.ReadFile(platformsConfigPath);
                            CoreHelper.SetVar("platforms_config_json", platformsConfigJson);
                        }
                        platformsSection = JsonHelper.ExtractObject(platformsConfigJson, "platforms");
                        platformConfig = JsonHelper.ExtractObject(platformsSection, platformName);
                        
                        if (!string.IsNullOrEmpty(platformConfig))
                        {
                            CoreHelper.Log(TAG, "自动接入成功，已加载平台配置: " + platformName);
                        }
                        else
                        {
                            CoreHelper.LogErr(TAG, "自动接入完成但配置仍为空: " + platformName);
                            CoreHelper.SetVar("session_result", "ERROR");
                            return "ERROR: 自动接入后平台配置仍不存在";
                        }
                    }
                    else
                    {
                        CoreHelper.LogErr(TAG, "自动接入失败: " + platformName);
                        CoreHelper.SetVar("session_result", "ERROR");
                        return "ERROR: 平台自动接入失败";
                    }
                }
                else
                {
                    CoreHelper.LogErr(TAG, "未找到平台配置且无法获取包名: " + platformName);
                    CoreHelper.SetVar("session_result", "ERROR");
                    return "ERROR: 平台配置不存在";
                }
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
            
            // ========== 初始化 VisionCorrector (v4.5.12 三层恢复) ==========
            string screenshotDir = projectRoot + "Screenshots\\Recovery";
            string aiConfigPath = projectRoot + "Config\\AIConfig.json";
            if (File.Exists(aiConfigPath))
            {
                VisionCorrector.Init(_project, instanceObj, screenshotDir, aiConfigPath);
                CoreHelper.Log(TAG, "VisionCorrector 已初始化（三层恢复可用）");
            }
            else
            {
                CoreHelper.LogWarn(TAG, "AIConfig.json 不存在，VisionCorrector 未初始化（Layer 3 不可用）");
            }
            _visionRecoveryCount = 0;
            
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
                CoreHelper.LogErr(TAG, "操作配置不存在，平台将无法执行操作: " + opsPath);
            }
            
            // ========== 加载统一意图映射与全局用户策略 ==========
            LoadUserStrategy(projectRoot);
            LoadIntentMapping(projectRoot, platformName);
            
            // ========== 初始页面检测与剧本规划 ==========
            // 打开 APP 后第一步：检测当前所处页面（首页/帖子/频道/其他）
            _currentPage = DetectInitialPage();
            CoreHelper.Log(TAG, "APP 启动时页面: " + _currentPage);
            CoreHelper.SetVar("initial_page", _currentPage);
            
            // 根据检测到的页面状态规划预设动作序列（剧本）
            List<string> preSessionActions = PlanPreSessionActions(_currentPage);
            if (preSessionActions.Count > 0)
            {
                CoreHelper.Log(TAG, string.Format("规划初始剧本: {0} 个预设动作", preSessionActions.Count));
            }
            else
            {
                CoreHelper.Log(TAG, "当前在首页，直接开始正常会话");
            }
            
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
            int actionCount = 0;        // 总尝试次数（每轮累加）
            int successfulActions = 0;  // SUCCESS 动作数
            int failedActions = 0;      // ERROR 动作数
            int skippedActions = 0;     // SKIP 动作数
            List<string> memoryEntries = new List<string>();
            
            // 重置连续 SKIP 计数
            _consecutiveSkips = 0;
            
            // ========== 执行预设动作（剧本） ==========
            // 在主循环前，根据初始页面检测结果执行预设动作
            if (preSessionActions.Count > 0)
            {
                ExecutePreSessionActions(preSessionActions, platformName, projectRoot);
                CoreHelper.Log(TAG, "初始剧本执行完成，当前页面: " + _currentPage);
            }
            
            while (DateTime.Now < sessionEnd)
            {
                // Step 1: 疲劳调权 — 能量不足时自动禁用高消耗动作
                double[] adjustedWeights = AdjustWeightsForFatigue(actionTypes, weights, sessionState);
                
                // 加权随机选择动作
                string selectedAction = WeightedChoice(actionTypes, adjustedWeights);
                string selectedIntent = ResolveIntentForAction(selectedAction);
                
                CoreHelper.SetVar("current_action", selectedAction);
                CoreHelper.SetVar("current_intent", selectedIntent);
                
                if (_aiDirectExecution && ShouldNotifyHumanForIntent(selectedIntent))
                {
                    NotifyHumanControl(selectedAction, selectedIntent, platformName);
                }
                
                // Step 2: RuleEngine 帖子评估门控
                // BUG-02 fix v2: 使用上一轮遗留的帖子数据进行评估（browse 会填充数据）
                // 不在循环开头清空，让 browse → like/comment 的跨迭代数据流成立
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
                        selectedIntent = ResolveIntentForAction(selectedAction);
                    }
                }
                
                // Step 2b: RuleEngine 评估
                if (!EvaluatePostForAction(selectedAction, currentPostJson))
                {
                    CoreHelper.Log(TAG, string.Format("RuleEngine 拒绝 {0}，降级为 browse", selectedAction));
                    selectedAction = "browse";
                    selectedIntent = ResolveIntentForAction(selectedAction);
                }
                
                // 降级后同步变量
                CoreHelper.SetVar("current_action", selectedAction);
                CoreHelper.SetVar("current_intent", selectedIntent);
                
                PrepareActionVariables(selectedAction);
                CoreHelper.Log(TAG, string.Format("执行动作: {0} (energy={1:F2})", selectedAction, sessionState.Energy));
                
                // 执行实际动作
                string actionResult = "PENDING";
                try {
                    string currentPlatform = CoreHelper.GetVar("current_platform", "reddit");
                    
                    // v4.5 统一引擎执行（唯一执行路径）
                    if (!string.IsNullOrEmpty(_operationsJson))
                    {
                        actionResult = ExecuteWithUnifiedEngine(selectedAction, selectedIntent, currentPlatform, projectRoot);
                    }
                    else
                    {
                        // operations.json 缺失 — 无法执行任何操作
                        CoreHelper.LogErr(TAG, "操作配置缺失，无法执行动作: " + selectedAction);
                        actionResult = "ERROR";
                    }
                } catch (System.Exception ex) {
                    actionResult = "ERROR";
                    CoreHelper.LogErr(TAG, "动作执行失败: " + ex.Message);
                }
                
                // v4.5.12: Vision 验证层 — ERROR 时主动验证是否为 ZD 误报
                // v4.5.2 原设计: 仅在 SUCCESS 的 like/comment 跳过验证
                // v4.5.12 升级: ERROR 时截图发给 Vision 模型，检查操作是否实际成功
                if (actionResult == "ERROR" && VisionCorrector.IsInitialized()
                    && _visionRecoveryCount < MAX_VISION_RECOVERY_PER_SESSION)
                {
                    CoreHelper.Log(TAG, "[VISION] 验证操作是否为误报: " + selectedAction);
                    _visionRecoveryCount++;
                    string errScreenshot = VisionCorrector.CaptureScreenshot();
                    if (!string.IsNullOrEmpty(errScreenshot))
                    {
                        ZDResult errResult = ZDResult.FailedRetryable("Operation " + selectedAction + " reported ERROR");
                        bool actuallyOk = VisionCorrector.VerifyError(selectedIntent, errResult, errScreenshot);
                        if (actuallyOk)
                        {
                            CoreHelper.Log(TAG, "[VISION] 操作实际成功（ZD 误报）: " + selectedAction);
                            actionResult = "SUCCESS";
                            CoreHelper.SetVar("vision_verified", "corrected");
                        }
                        else
                        {
                            CoreHelper.SetVar("vision_verified", "confirmed_error");
                        }
                    }
                }
                else
                {
                    CoreHelper.SetVar("vision_verified", actionResult == "SUCCESS" ? "skipped" : "not_applicable");
                }
                
                // BUG-04 fix: SKIP 不算失败（页面不匹配等正常情况）
                // v4.5.8: 分类计数 SUCCESS/SKIP/ERROR
                if (actionResult == "SUCCESS")
                {
                    successfulActions++;
                }
                else if (actionResult.StartsWith("SKIP"))
                {
                    skippedActions++;
                }
                else
                {
                    failedActions++;
                }
                
                // 导航恢复: 跟踪连续 SKIP（页面不匹配导致的跳过）
                if (actionResult.StartsWith("SKIP"))
                {
                    _consecutiveSkips++;
                    if (_consecutiveSkips >= MAX_CONSECUTIVE_SKIPS)
                    {
                        CoreHelper.Log(TAG, string.Format("连续 {0} 次 SKIP，触发导航恢复", _consecutiveSkips));
                        ForceNavigateToFeed(platformName, projectRoot);
                        _consecutiveSkips = 0;
                    }
                }
                else
                {
                    _consecutiveSkips = 0;
                }
                
                // v4.5.1: 通过 MemoryManager 记录结构化交互
                if (actionResult == "SUCCESS" && selectedAction != "browse" && !string.IsNullOrEmpty(currentPostId))
                {
                    string currentPlatformForRecord = CoreHelper.GetVar("current_platform", "reddit");
                    MemoryManager.RecordInteraction(deviceId, currentPlatformForRecord, currentPostId, selectedAction);
                    CoreHelper.Log(TAG, string.Format("MemoryManager 已记录: {0} on {1}", selectedAction, currentPostId));
                    
                    // BUG-02 fix v2: 交互完成后清空帖子数据，防止下一轮重复评估同一帖子
                    CoreHelper.SetVar("current_post_json", "");
                    CoreHelper.SetVar("current_post_id", "");
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
                UpdateEnergy(selectedAction, delayMs / 1000, sessionState);
                
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
            
            CoreHelper.Log(TAG, string.Format(
                "会话结束 — 总尝试: {0}, 成功: {1}, 失败: {2}, 跳过: {3}",
                actionCount, successfulActions, failedActions, skippedActions));
            
            // v4.6.0: 输出智能编排器统计（ADR-015）
            CoreHelper.Log(TAG, string.Format("[ORCHESTRATOR] 会话统计: {0}", _orchestrator.GetSessionSummary()));
            if (_orchestrator.GetFalseSuccessCount() > 0)
            {
                CoreHelper.LogWarn(TAG, string.Format(
                    "[ORCHESTRATOR] 本次会话检出 {0} 次假成功（执行成功但业务失败）",
                    _orchestrator.GetFalseSuccessCount()));
            }
            
            // ========== v4.5.8 会话成功门控 ==========
            // 成功率 = 成功数 / (成功数 + 失败数)，SKIP 不计入分母
            int effectiveActions = successfulActions + failedActions;
            double sessionSuccessRate = effectiveActions > 0
                ? (double)successfulActions / effectiveActions
                : 0.0;
            
            // 从配置读取门控阈值（默认: 成功率 >= 95%，最少成功 6 次）
            string sessionGateSection = JsonHelper.ExtractObject(behaviorConfigJson, "session_gate");
            double requiredSuccessRate = JsonHelper.GetDouble(sessionGateSection, "min_success_rate", 0.95);
            int minSuccessfulActions = JsonHelper.GetInt(sessionGateSection, "min_successful_actions", 6);
            
            bool isSuccess = sessionSuccessRate >= requiredSuccessRate
                          && successfulActions >= minSuccessfulActions;
            
            CoreHelper.Log(TAG, string.Format(
                "门控判定: success_rate={0:F3} (需>={1:F2}), successful={2} (需>={3}), 结果={4}",
                sessionSuccessRate, requiredSuccessRate, successfulActions, minSuccessfulActions,
                isSuccess ? "PASS" : "FAIL"));
            
            // ========== v4.5.8 输出变量 ==========
            // action_count 语义: 成功动作数（v4.5.8 定义）
            CoreHelper.SetVar("action_count", successfulActions.ToString());
            CoreHelper.SetVar("action_attempt_count", actionCount.ToString());
            CoreHelper.SetVar("session_successful_actions", successfulActions.ToString());
            CoreHelper.SetVar("session_failed_actions", failedActions.ToString());
            CoreHelper.SetVar("session_skipped_actions", skippedActions.ToString());
            CoreHelper.SetVar("session_success_rate", sessionSuccessRate.ToString("F4"));
            
            if (isSuccess)
            {
                CoreHelper.SetVar("run_result", "SUCCESS");
                CoreHelper.SetVar("session_result", "SUCCESS");
                return "SUCCESS";
            }
            else
            {
                CoreHelper.LogErr(TAG, string.Format(
                    "会话未达标: success_rate={0:F3}, successful={1}",
                    sessionSuccessRate, successfulActions));
                CoreHelper.SetVar("run_result", "ERROR");
                CoreHelper.SetVar("session_result", "ERROR");
                return "ERROR: 会话成功率不达标";
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
    
    // ==================== v4.7.0: ZD 外层流程编排入口方法 ====================
    // 以下 4 个 public static 方法对应 ZD Loop+Switch 流程中的 4 个阶段:
    //   1. InitSession    — 初始化会话（配置加载、平台确认、预设动作执行）
    //   2. DecideNextAction — 每次循环决策下一个操作名
    //   3. EvaluateActionResult — ZD 原生动作执行后评估结果
    //   4. FinalizeSession — 会话结束收尾（记忆保存、门控判定）
    // Run() 方法保持不变，作为整体式执行的向后兼容入口。
    
    /// <summary>
    /// [ZD 外层流程] 阶段 1: 初始化会话
    /// 提取 Run() 中 L83-L700 的初始化逻辑，将所有共享状态写入 ZD 变量
    /// </summary>
    public static string InitSession(object projectObj, object instanceObj)
    {
        _project = projectObj;
        
        try
        {
            CoreHelper.Init(projectObj, instanceObj);
            
            // ========== T17: 兼容开关检测 ==========
            // sr_use_legacy_run = "true" 时，委托旧 Run() 整体式执行整个会话
            // 默认值为空，走新四入口逻辑（当前行为不变）
            string useLegacyRun = CoreHelper.GetVar(VK.UseLegacyRun, "");
            bool isLegacyMode = (useLegacyRun == "true");
            CoreHelper.Log(TAG, string.Format("InitSession: 兼容开关 sr_use_legacy_run={0}, 执行路径={1}",
                string.IsNullOrEmpty(useLegacyRun) ? "(空)" : useLegacyRun,
                isLegacyMode ? "旧 Run() 整体式" : "新四入口"));
            
            if (isLegacyMode)
            {
                CoreHelper.Log(TAG, "InitSession: 兼容模式 — 委托 Run() 执行整个会话");
                string runResult = Run(projectObj, instanceObj);
                CoreHelper.SetVar(VK.LegacyRunCompleted, "true");
                CoreHelper.SetVar(VK.LegacyRunResult, runResult);
                CoreHelper.Log(TAG, "InitSession: 兼容模式 — Run() 已完成, 结果=" + runResult);
                return runResult;
            }
            
            // ========== 设备连接检测 ==========
            if (!CoreHelper.HasInstance())
            {
                CoreHelper.LogErr(TAG, "InitSession: 设备未连接 - instance 对象为空");
                CoreHelper.SetVar("session_result", "ERROR");
                return "ERROR: 设备未连接 - ZennoDroid instance 未初始化";
            }
            
            dynamic droid = CoreHelper.GetDroid();
            if (droid == null)
            {
                CoreHelper.LogErr(TAG, "InitSession: 设备未连接 - DroidInstance 为空");
                CoreHelper.SetVar("session_result", "ERROR");
                return "ERROR: 设备未连接 - DroidInstance 不可用";
            }
            
            try
            {
                string testLayout = CoreHelper.GetLayout();
                if (string.IsNullOrEmpty(testLayout))
                {
                    CoreHelper.LogErr(TAG, "InitSession: 设备未连接 - GetLayout 返回空");
                    CoreHelper.SetVar("session_result", "ERROR");
                    return "ERROR: 设备未连接 - 无法获取 UI 层级";
                }
                CoreHelper.Log(TAG, "InitSession: 设备连接检测通过");
            }
            catch (Exception connEx)
            {
                CoreHelper.LogErr(TAG, "InitSession: 设备未连接 - " + connEx.Message);
                CoreHelper.SetVar("session_result", "ERROR");
                return "ERROR: 设备未连接 - " + connEx.Message;
            }
            
            // ========== 读取 ZD 变量 ==========
            string projectRoot = CoreHelper.GetVar("project_root", "");
            string deviceId = CoreHelper.GetVar("device_id", "");
            string personaJson = CoreHelper.GetVar("persona_json", "{}");
            string sessionPlanJson = CoreHelper.GetVar("session_plan_json", "{}");
            string behaviorConfigJson = CoreHelper.GetVar("behavior_config_json", "");
            
            if (string.IsNullOrEmpty(projectRoot) || string.IsNullOrEmpty(deviceId))
            {
                CoreHelper.LogErr(TAG, "InitSession: project_root 或 device_id 未设置");
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
                    CoreHelper.Log(TAG, "InitSession: 已从文件加载行为配置");
                }
            }
            
            CoreHelper.Log(TAG, "InitSession: 开始初始化会话");
            
            // ========== 智能编排器初始化 ==========
            _orchestrator.ResetAll();
            _orchestrator.LoadConfig(behaviorConfigJson);
            
            // ========== 加载决策配置 ==========
            string decisionConfigPath = projectRoot + "Config\\DecisionConfig.json";
            if (File.Exists(decisionConfigPath))
            {
                _decisionConfigJson = CoreHelper.ReadFile(decisionConfigPath);
                RuleEngine.LoadConfig(_decisionConfigJson);
                CoreHelper.Log(TAG, "InitSession: 已加载 DecisionConfig.json");
            }
            else
            {
                CoreHelper.LogWarn(TAG, "InitSession: DecisionConfig.json 不存在，使用默认规则");
            }
            
            // ========== 初始化 SessionState（疲劳模型） ==========
            SessionState sessionState = new SessionState();
            
            string fatigueSection = JsonHelper.ExtractObject(_decisionConfigJson, "fatigue_model");
            if (!string.IsNullOrEmpty(fatigueSection))
            {
                string enabledFatigue = JsonHelper.Get(fatigueSection, "enabled");
                sessionState.FatigueEnabled = (enabledFatigue == "true");
                sessionState.Energy = JsonHelper.GetDouble(fatigueSection, "initial_energy", 1.0);
                sessionState.RecoveryPerPauseSec = JsonHelper.GetDouble(fatigueSection, "recovery_per_pause_sec", 0.01);
                sessionState.MinEnergyToComment = JsonHelper.GetDouble(fatigueSection, "min_energy_to_comment", 0.3);
                sessionState.MinEnergyToLike = JsonHelper.GetDouble(fatigueSection, "min_energy_to_like", 0.15);
                
                string decaySection = JsonHelper.ExtractObject(fatigueSection, "decay_per_action");
                if (!string.IsNullOrEmpty(decaySection))
                {
                    sessionState.DecayBrowse = JsonHelper.GetDouble(decaySection, "browse", 0.02);
                    sessionState.DecayRead = JsonHelper.GetDouble(decaySection, "read", 0.05);
                    sessionState.DecayLike = JsonHelper.GetDouble(decaySection, "like", 0.03);
                    sessionState.DecayComment = JsonHelper.GetDouble(decaySection, "comment", 0.10);
                }
                
                CoreHelper.Log(TAG, string.Format("InitSession: 疲劳模型 enabled={0}, energy={1:F2}", sessionState.FatigueEnabled, sessionState.Energy));
            }
            else
            {
                sessionState.FatigueEnabled = false;
                sessionState.Energy = 1.0;
                sessionState.DecayBrowse = 0.02;
                sessionState.DecayRead = 0.05;
                sessionState.DecayLike = 0.03;
                sessionState.DecayComment = 0.10;
                sessionState.RecoveryPerPauseSec = 0.01;
                sessionState.MinEnergyToComment = 0.3;
                sessionState.MinEnergyToLike = 0.15;
            }
            
            // ========== 确定平台 ==========
            string platformName = DeterminePlatform(projectRoot, deviceId);
            CoreHelper.Log(TAG, "InitSession: 选择平台: " + platformName);
            CoreHelper.SetVar("current_platform", platformName);
            
            // ========== 加载平台配置 ==========
            string platformsConfigPath = projectRoot + "Config\\PlatformsConfig.json";
            string platformsConfigJson = "";
            if (File.Exists(platformsConfigPath))
            {
                platformsConfigJson = CoreHelper.ReadFile(platformsConfigPath);
                CoreHelper.SetVar("platforms_config_json", platformsConfigJson);
            }
            
            string platformsSection = JsonHelper.ExtractObject(platformsConfigJson, "platforms");
            string platformConfig = JsonHelper.ExtractObject(platformsSection, platformName);
            
            if (string.IsNullOrEmpty(platformConfig))
            {
                // 自动接入新平台
                string packageName = GetPackageNameForPlatform(projectRoot, platformName);
                if (!string.IsNullOrEmpty(packageName))
                {
                    CoreHelper.Log(TAG, "InitSession: 未找到平台配置: " + platformName + "，尝试自动接入...");
                    bool onboardSuccess = RunAppOnboarder(projectRoot, packageName, platformName);
                    
                    if (onboardSuccess)
                    {
                        if (File.Exists(platformsConfigPath))
                        {
                            platformsConfigJson = CoreHelper.ReadFile(platformsConfigPath);
                            CoreHelper.SetVar("platforms_config_json", platformsConfigJson);
                        }
                        platformsSection = JsonHelper.ExtractObject(platformsConfigJson, "platforms");
                        platformConfig = JsonHelper.ExtractObject(platformsSection, platformName);
                        
                        if (!string.IsNullOrEmpty(platformConfig))
                        {
                            CoreHelper.Log(TAG, "InitSession: 自动接入成功: " + platformName);
                        }
                        else
                        {
                            CoreHelper.LogErr(TAG, "InitSession: 自动接入完成但配置仍为空: " + platformName);
                            CoreHelper.SetVar("session_result", "ERROR");
                            return "ERROR: 自动接入后平台配置仍不存在";
                        }
                    }
                    else
                    {
                        CoreHelper.LogErr(TAG, "InitSession: 自动接入失败: " + platformName);
                        CoreHelper.SetVar("session_result", "ERROR");
                        return "ERROR: 平台自动接入失败";
                    }
                }
                else
                {
                    CoreHelper.LogErr(TAG, "InitSession: 未找到平台配置且无法获取包名: " + platformName);
                    CoreHelper.SetVar("session_result", "ERROR");
                    return "ERROR: 平台配置不存在";
                }
            }
            
            // 检查平台是否启用
            string enabledStr = JsonHelper.Get(platformConfig, "enabled");
            if (enabledStr == "false")
            {
                CoreHelper.LogErr(TAG, "InitSession: 平台未启用: " + platformName);
                CoreHelper.SetVar("session_result", "ERROR");
                return "ERROR: 平台未启用";
            }
            
            _platformConfig = platformConfig;
            CoreHelper.Log(TAG, "InitSession: 平台配置已加载: " + platformName);
            
            // ========== 初始化 MemoryManager ==========
            string memoryBasePath = projectRoot + "Memory";
            MemoryManager.Init(memoryBasePath);
            CoreHelper.Log(TAG, "InitSession: MemoryManager 已初始化");
            
            // ========== 初始化 VisionCorrector ==========
            string screenshotDir = projectRoot + "Screenshots\\Recovery";
            string aiConfigPath = projectRoot + "Config\\AIConfig.json";
            if (File.Exists(aiConfigPath))
            {
                VisionCorrector.Init(_project, instanceObj, screenshotDir, aiConfigPath);
                CoreHelper.Log(TAG, "InitSession: VisionCorrector 已初始化");
            }
            else
            {
                CoreHelper.LogWarn(TAG, "InitSession: AIConfig.json 不存在，VisionCorrector 未初始化");
            }
            _visionRecoveryCount = 0;
            
            // ========== 加载操作配置 JSON ==========
            string opsPath = projectRoot + "Config\\Operations\\" + platformName + "_operations.json";
            if (File.Exists(opsPath))
            {
                string rawOpsJson = CoreHelper.ReadFile(opsPath);
                string opsSection = JsonHelper.ExtractObject(rawOpsJson, "operations");
                _operationsJson = string.IsNullOrEmpty(opsSection) ? rawOpsJson : opsSection;
                CoreHelper.Log(TAG, "InitSession: 已加载操作配置: " + opsPath);
            }
            else
            {
                _operationsJson = "";
                CoreHelper.LogErr(TAG, "InitSession: 操作配置不存在: " + opsPath);
            }
            
            // ========== 加载意图映射与用户策略 ==========
            LoadUserStrategy(projectRoot);
            LoadIntentMapping(projectRoot, platformName);
            
            // ========== 初始页面检测与剧本规划 ==========
            _currentPage = DetectInitialPage();
            CoreHelper.Log(TAG, "InitSession: APP 启动时页面: " + _currentPage);
            CoreHelper.SetVar("initial_page", _currentPage);
            
            List<string> preSessionActions = PlanPreSessionActions(_currentPage);
            if (preSessionActions.Count > 0)
            {
                CoreHelper.Log(TAG, string.Format("InitSession: 规划初始剧本: {0} 个预设动作", preSessionActions.Count));
            }
            else
            {
                CoreHelper.Log(TAG, "InitSession: 当前在首页，直接开始正常会话");
            }
            
            // ========== 加载 interests / triggers ==========
            string interestsPath = projectRoot + "Data\\Keywords\\" + platformName + "\\interests.json";
            if (File.Exists(interestsPath))
            {
                _interestsJson = CoreHelper.ReadFile(interestsPath);
                CoreHelper.Log(TAG, "InitSession: 已加载 interests.json (" + platformName + ")");
            }
            else
            {
                _interestsJson = "";
            }
            
            string triggersPath = projectRoot + "Data\\Keywords\\" + platformName + "\\triggers.json";
            if (File.Exists(triggersPath))
            {
                _triggersJson = CoreHelper.ReadFile(triggersPath);
                CoreHelper.Log(TAG, "InitSession: 已加载 triggers.json (" + platformName + ")");
            }
            else
            {
                _triggersJson = "";
            }
            
            // ========== 执行预设动作（剧本） ==========
            if (preSessionActions.Count > 0)
            {
                ExecutePreSessionActions(preSessionActions, platformName, projectRoot);
                CoreHelper.Log(TAG, "InitSession: 初始剧本执行完成，当前页面: " + _currentPage);
            }
            
            // ========== 计算会话时长 ==========
            int sessionDuration = JsonHelper.GetInt(sessionPlanJson, "session_duration_minutes", 12);
            
            string sessionSection = JsonHelper.ExtractObject(behaviorConfigJson, "session");
            int minDuration = JsonHelper.GetInt(sessionSection, "min_duration_minutes", 3);
            int maxDuration = JsonHelper.GetInt(sessionSection, "max_duration_minutes", 30);
            
            if (sessionDuration < minDuration) sessionDuration = minDuration;
            if (sessionDuration > maxDuration) sessionDuration = maxDuration;
            
            CoreHelper.Log(TAG, "InitSession: 计划会话时长: " + sessionDuration + " 分钟");
            
            // ========== 计算动作权重 ==========
            string actionsSection = JsonHelper.ExtractObject(behaviorConfigJson, "actions");
            string[] actionTypes = new string[] { "browse", "read_post", "like", "comment", "post" };
            double[] weights = new double[5];
            
            for (int i = 0; i < actionTypes.Length; i++)
            {
                string actionSection2 = JsonHelper.ExtractObject(actionsSection, actionTypes[i]);
                double weight = JsonHelper.GetDouble(actionSection2, "weight_base", 0.2);
                weights[i] = weight;
            }
            
            CoreHelper.Log(TAG, string.Format("InitSession: 动作权重: browse={0:F2}, read={1:F2}, like={2:F2}, comment={3:F2}, post={4:F2}",
                weights[0], weights[1], weights[2], weights[3], weights[4]));
            
            // ========== 准备记忆文件路径 ==========
            string today = CoreHelper.GetToday();
            string memoryDir = projectRoot + "Memory\\" + deviceId;
            CoreHelper.EnsureDir(memoryDir);
            string memoryFile = memoryDir + "\\" + today + ".json";
            
            // ========== 写入会话时间到 ZD 变量 ==========
            DateTime sessionStart = DateTime.Now;
            DateTime sessionEnd = sessionStart.AddMinutes(sessionDuration);
            CoreHelper.SetVar(VK.SessionEndTime, sessionEnd.ToString("o"));
            CoreHelper.SetVar(VK.SessionStartTime, sessionStart.ToString("o"));
            
            // ========== 初始化计数器 ==========
            _consecutiveSkips = 0;
            
            // ========== 保存所有动态状态 ==========
            SaveDynamicState(sessionState, 0, 0, 0, 0);
            
            // ========== 写入动作权重 ==========
            CoreHelper.SetVar(VK.ActionWeightsJson, SerializeWeights(actionTypes, weights));
            
            // ========== 写入记忆文件路径 ==========
            CoreHelper.SetVar(VK.MemoryFile, memoryFile);
            CoreHelper.SetVar(VK.MemoryEntries, "");
            
            // ========== 存储配置 key 到 ZD 变量 ==========
            CoreHelper.SetVar(VK.ProjectRoot, projectRoot);
            CoreHelper.SetVar(VK.DeviceId, deviceId);
            CoreHelper.SetVar(VK.PlatformName, platformName);
            CoreHelper.SetVar(VK.BehaviorConfigJson, behaviorConfigJson);
            
            // ========== T13: 初始化 ZD 外层流程 Step DSL 前置变量 ==========
            // 在进入 Loop 前，必须确保所有 StepRunner/Switch/原生块依赖的变量已存在且有安全默认值。
            // 原则: 空值可恢复、默认值安全 — 任何变量缺失都不应导致 ZD 流程异常中断。
            
            // -- 屏幕尺寸: 从 UI 布局 XML 根节点 bounds 解析，失败则使用安全默认值 --
            int screenWidth = 1080;   // 安全默认值（常见 Android 设备）
            int screenHeight = 2400;  // 安全默认值（常见 Android 设备）
            try
            {
                string layoutXml = CoreHelper.GetLayout();
                if (!string.IsNullOrEmpty(layoutXml))
                {
                    // UI 布局根节点 bounds 格式: bounds="[0,0][1080,2400]"
                    // 查找最后一个 bounds 中的最大值，即屏幕尺寸
                    int lastBounds = layoutXml.LastIndexOf("bounds=\"[0,0][", StringComparison.Ordinal);
                    if (lastBounds >= 0)
                    {
                        int start = lastBounds + "bounds=\"[0,0][".Length;
                        int end = layoutXml.IndexOf("]", start, StringComparison.Ordinal);
                        if (end > start)
                        {
                            string sizeStr = layoutXml.Substring(start, end - start);
                            string[] parts = sizeStr.Split(',');
                            if (parts.Length == 2)
                            {
                                int parsedW, parsedH;
                                if (int.TryParse(parts[0].Trim(), out parsedW)
                                    && int.TryParse(parts[1].Trim(), out parsedH)
                                    && parsedW > 0 && parsedH > 0)
                                {
                                    screenWidth = parsedW;
                                    screenHeight = parsedH;
                                    CoreHelper.Log(TAG, string.Format("InitSession: 从 UI 布局解析屏幕尺寸: {0}x{1}", screenWidth, screenHeight));
                                }
                            }
                        }
                    }
                    
                    if (screenWidth == 1080 && screenHeight == 2400)
                    {
                        CoreHelper.LogWarn(TAG, "InitSession: 未能从 UI 布局解析屏幕尺寸，使用默认值 1080x2400");
                    }
                }
                else
                {
                    CoreHelper.LogWarn(TAG, "InitSession: GetLayout 返回空，屏幕尺寸使用默认值 1080x2400");
                }
            }
            catch (Exception screenEx)
            {
                CoreHelper.LogWarn(TAG, "InitSession: 解析屏幕尺寸异常，使用默认值 1080x2400 - " + screenEx.Message);
            }
            
            CoreHelper.SetVar(VK.ZdScreenWidth, screenWidth.ToString());
            CoreHelper.SetVar(VK.ZdScreenHeight, screenHeight.ToString());
            
            // -- Step DSL 状态变量: 初始化为安全空/零值 --
            // StepRunner 在接收到 DecideNextAction 的 DSL 输出后会重写这些值，
            // 此处仅保证变量存在，防止 ZD 原生块在 Loop 首次迭代时引用未定义变量。
            // 注意: 如果外部已预设 zd_step_plan (如测试 DSL)，保留其值不覆盖
            string presetPlan = CoreHelper.GetVar(VK.ZdStepPlan, "");
            if (string.IsNullOrEmpty(presetPlan.Trim()))
            {
                CoreHelper.SetVar(VK.ZdStepPlan, "");
            }
            else
            {
                CoreHelper.Log(TAG, "InitSession: 检测到外部预设 zd_step_plan, 保留: " + presetPlan);
            }
            CoreHelper.SetVar(VK.ZdStepIndex, "0");
            CoreHelper.SetVar(VK.ZdStepCount, "0");
            CoreHelper.SetVar(VK.ZdStepType, "");
            CoreHelper.SetVar(VK.ZdStepParam, "");
            CoreHelper.SetVar(VK.ZdSelectorKey, "");
            
            // -- Touch 矩形坐标: 初始化为 0，Locate 成功后会写入真实坐标 --
            CoreHelper.SetVar(VK.ZdTapX1, "0");
            CoreHelper.SetVar(VK.ZdTapX2, "0");
            CoreHelper.SetVar(VK.ZdTapY1, "0");
            CoreHelper.SetVar(VK.ZdTapY2, "0");
            
            // -- Swipe 坐标: 初始化为 0，StepRunner 解析 S:xxx 时会展开写入真实坐标 --
            CoreHelper.SetVar(VK.ZdSwipeX1, "0");
            CoreHelper.SetVar(VK.ZdSwipeY1, "0");
            CoreHelper.SetVar(VK.ZdSwipeX2, "0");
            CoreHelper.SetVar(VK.ZdSwipeY2, "0");
            CoreHelper.SetVar(VK.ZdSwipeDuration, "800");  // 默认 800ms（契约默认值）
            
            // -- Wait / Locate / Verify 参数 --
            CoreHelper.SetVar(VK.ZdWaitSec, "0");
            CoreHelper.SetVar(VK.ZdFound, "false");         // 默认未找到，Locate 成功后设为 true
            CoreHelper.SetVar(VK.ZdVerifyRule, "");
            
            // -- 安全计数器: 防无限循环，ZD Loop 每轮递增，到上限则强制退出 --
            CoreHelper.SetVar(VK.ZdSafety, "0");
            
            // -- T15: 原生块执行结果回收变量 --
            // ZD 原生动作块执行后写入这三个变量，EvaluateActionResult 读取并消费。
            // 此处初始化安全默认值，防止 ZD Loop 首次迭代时引用未定义变量。
            CoreHelper.SetVar(VK.ZdActionResult, "");          // 原生块执行结果（SUCCESS/ERROR 等）
            CoreHelper.SetVar(VK.ZdActionDuration, "0");       // 原生块执行耗时（秒）
            CoreHelper.SetVar(VK.ZdActionErrorDetail, "");     // 原生块错误详情（仅失败时有值）
            
            CoreHelper.Log(TAG, string.Format(
                "InitSession: Step DSL 前置变量已初始化 (screen={0}x{1}, swipe_duration=800ms, safety=0)",
                screenWidth, screenHeight));
            
            CoreHelper.Log(TAG, "InitSession: 初始化完成，会话将在 " + sessionEnd.ToString("HH:mm:ss") + " 结束");
            return "SUCCESS";
        }
        catch (Exception ex)
        {
            CoreHelper.LogErr(TAG, "InitSession 异常: " + ex.Message);
            CoreHelper.SetVar("session_result", "ERROR");
            return "ERROR: " + ex.Message;
        }
    }
    
    /// <summary>
    /// [ZD 外层流程] 阶段 2: 决策下一个操作
    /// 每次 ZD Loop 迭代调用，返回操作名("browse"等)、"END" 或 "SKIP"
    /// </summary>
    public static string DecideNextAction(object projectObj, object instanceObj)
    {
        _project = projectObj;
        
        try
        {
            CoreHelper.Init(projectObj, instanceObj);
            
            // T17: 兼容模式检测 — Run() 已整体执行完毕，跳过决策
            if (CoreHelper.GetVar(VK.LegacyRunCompleted, "") == "true")
            {
                CoreHelper.Log(TAG, "DecideNextAction: 兼容模式 — Run() 已完成，返回 END");
                return "END";
            }
            
            // 恢复状态
            int actionCount, successCount, failCount, skipCount;
            SessionState sessionState = RestoreState(out actionCount, out successCount, out failCount, out skipCount);
            
            // ========== 检查外部预设的 Step DSL ==========
            // 如果 zd_step_plan 已包含 DSL 步骤类型（L/T/S/W/B/V 开头），
            // 说明是外部预设的测试 DSL，直接解析并路由，不走智能选择
            string existingPlan = CoreHelper.GetVar(VK.ZdStepPlan, "");
            if (!string.IsNullOrEmpty(existingPlan))
            {
                string firstToken = existingPlan.Split(new char[] { '|' })[0].Trim();
                string typeChar = firstToken.Contains(":") ? firstToken.Split(new char[] { ':' })[0] : firstToken;
                string[] dslTypes = new string[] { "L", "T", "T:long", "S", "W", "B", "V" };
                bool isDsl = false;
                for (int di = 0; di < dslTypes.Length; di++)
                {
                    if (typeChar == dslTypes[di]) { isDsl = true; break; }
                }
                if (isDsl)
                {
                    // 外部预设 DSL — 解析步骤并设置 zd_step_type/zd_step_param
                    string[] steps = existingPlan.Split(new char[] { '|' });
                    int stepIndex = CoreHelper.GetVarInt(VK.ZdStepIndex, 0);
                    int stepCount = steps.Length;
                    CoreHelper.SetVar(VK.ZdStepCount, stepCount.ToString());
                    
                    if (stepIndex >= stepCount)
                    {
                        CoreHelper.SetVar(VK.ZdStepType, "DONE");
                        CoreHelper.SetVar(VK.ZdStepParam, "");
                        CoreHelper.Log(TAG, string.Format("DecideNextAction: DSL 全部完成 ({0}/{1})", stepIndex, stepCount));
                        return "DONE";
                    }
                    
                    string currentStep = steps[stepIndex].Trim();
                    string stepType;
                    string stepParam;
                    // 特殊处理 T:long — 前缀是 "T:long" 不是 "T"
                    if (currentStep.StartsWith("T:long"))
                    {
                        stepType = "T:long";
                        stepParam = currentStep.Length > 6 && currentStep[6] == ':'
                            ? currentStep.Substring(7) : "";
                    }
                    else if (currentStep.Contains(":"))
                    {
                        int colonIdx = currentStep.IndexOf(':');
                        stepType = currentStep.Substring(0, colonIdx);
                        stepParam = currentStep.Substring(colonIdx + 1);
                    }
                    else
                    {
                        stepType = currentStep;
                        stepParam = "";
                    }
                    
                    CoreHelper.SetVar(VK.ZdStepType, stepType);
                    CoreHelper.SetVar(VK.ZdStepParam, stepParam);
                    
                    // ========== 展开 DSL 参数到原生块变量 ==========
                    if (stepType == "W" && !string.IsNullOrEmpty(stepParam))
                    {
                        // W:3 → Pause 3 秒
                        CoreHelper.SetVar(VK.ZdWaitSec, stepParam);
                    }
                    else if (stepType == "L" && !string.IsNullOrEmpty(stepParam))
                    {
                        // L:post_unit → Locate selector_key
                        CoreHelper.SetVar(VK.ZdSelectorKey, stepParam);
                    }
                    else if (stepType == "V" && !string.IsNullOrEmpty(stepParam))
                    {
                        // V:rule → Verify rule
                        CoreHelper.SetVar(VK.ZdVerifyRule, stepParam);
                    }
                    else if (stepType == "S" && !string.IsNullOrEmpty(stepParam))
                    {
                        // S:down_900 → 解析方向+距离, 展开为 swipe 坐标
                        int sw = CoreHelper.GetVarInt(VK.ZdScreenWidth, 1080);
                        int sh = CoreHelper.GetVarInt(VK.ZdScreenHeight, 2400);
                        int centerX = sw / 2;
                        int centerY = sh / 2;
                        
                        // 解析方向和距离: down_900, up_500, left_300, right_300
                        string direction = "down";
                        int distance = 900;
                        if (stepParam.Contains("_"))
                        {
                            int uIdx = stepParam.LastIndexOf('_');
                            direction = stepParam.Substring(0, uIdx);
                            int.TryParse(stepParam.Substring(uIdx + 1), out distance);
                        }
                        
                        int x1 = centerX, y1 = centerY, x2 = centerX, y2 = centerY;
                        if (direction == "down") { y1 = centerY - distance / 2; y2 = centerY + distance / 2; }
                        else if (direction == "up") { y1 = centerY + distance / 2; y2 = centerY - distance / 2; }
                        else if (direction == "left") { x1 = centerX + distance / 2; x2 = centerX - distance / 2; }
                        else if (direction == "right") { x1 = centerX - distance / 2; x2 = centerX + distance / 2; }
                        
                        CoreHelper.SetVar(VK.ZdSwipeX1, x1.ToString());
                        CoreHelper.SetVar(VK.ZdSwipeY1, y1.ToString());
                        CoreHelper.SetVar(VK.ZdSwipeX2, x2.ToString());
                        CoreHelper.SetVar(VK.ZdSwipeY2, y2.ToString());
                    }
                    else if (stepType == "B" && !string.IsNullOrEmpty(stepParam))
                    {
                        // B:BACK → 键盘键名(由 Keyboard Emulation 块消费)
                        // 注意: ZD Keyboard Emulation 块本身配置 {AndroidKeys.BACK}，
                        // stepParam 这里仅做日志记录，不需要额外设变量
                    }
                    
                    CoreHelper.Log(TAG, string.Format(
                        "DecideNextAction: DSL Step {0}/{1}: {2} (type={3}, param={4})",
                        stepIndex + 1, stepCount, currentStep, stepType, stepParam));
                    
                    SaveDynamicState(sessionState, actionCount, successCount, failCount, skipCount);
                    return stepType;
                }
            }
            
            // ========== 检查会话结束条件 ==========
            string endTimeStr = CoreHelper.GetVar(VK.SessionEndTime, "");
            if (!string.IsNullOrEmpty(endTimeStr))
            {
                try
                {
                    DateTime sessionEnd = DateTime.Parse(endTimeStr);
                    if (DateTime.Now >= sessionEnd)
                    {
                        CoreHelper.Log(TAG, "DecideNextAction: 会话时间已到，结束");
                        // Dual-Write: END 路径清空 Step DSL，防止残留旧值
                        CoreHelper.SetVar(VK.ZdStepPlan, "");
                        CoreHelper.Log(TAG, "[StepDSL] END 路径: 写入空 DSL（会话超时）");
                        return "END";
                    }
                }
                catch (Exception dtEx)
                {
                    CoreHelper.LogWarn(TAG, "DecideNextAction: 解析 SessionEndTime 失败: " + dtEx.Message);
                }
            }
            
            // 检查最大动作数
            if (actionCount >= 50)
            {
                CoreHelper.Log(TAG, "DecideNextAction: 达到最大动作数限制 (50)，结束");
                // Dual-Write: END 路径清空 Step DSL，防止残留旧值
                CoreHelper.SetVar(VK.ZdStepPlan, "");
                CoreHelper.Log(TAG, "[StepDSL] END 路径: 写入空 DSL（动作数上限）");
                return "END";
            }
            
            // ========== 操作队列处理 ==========
            // 如果队列中还有未执行的操作，直接返回下一个
            if (_operationQueue.Count > 0 && _operationQueueIndex < _operationQueue.Count)
            {
                string queuedOp = _operationQueue[_operationQueueIndex];
                CoreHelper.SetVar(VK.CurrentOpName, queuedOp);
                
                // Dual-Write: 将剩余队列操作写入 Step DSL（从当前 index 到末尾）
                List<string> remaining = new List<string>();
                for (int qi = _operationQueueIndex; qi < _operationQueue.Count; qi++)
                {
                    remaining.Add(_operationQueue[qi]);
                }
                string queueDsl = string.Join("|", remaining.ToArray());
                CoreHelper.SetVar(VK.ZdStepPlan, queueDsl);
                CoreHelper.Log(TAG, string.Format(
                    "DecideNextAction: 队列操作 {0}/{1}: {2}, DSL={3}",
                    _operationQueueIndex + 1, _operationQueue.Count, queuedOp, queueDsl));
                
                SaveDynamicState(sessionState, actionCount, successCount, failCount, skipCount);
                return queuedOp;
            }
            
            // ========== 队列为空，决策新的会话动作 ==========
            string behaviorConfigJson = CoreHelper.GetVar(VK.BehaviorConfigJson, "");
            string actionsSection = JsonHelper.ExtractObject(behaviorConfigJson, "actions");
            string[] actionTypes = new string[] { "browse", "read_post", "like", "comment", "post" };
            
            // 读取动作权重
            string weightsJson = CoreHelper.GetVar(VK.ActionWeightsJson, "");
            double[] weights;
            if (!string.IsNullOrEmpty(weightsJson))
            {
                weights = DeserializeWeights(weightsJson, actionTypes);
            }
            else
            {
                weights = new double[] { 0.3, 0.25, 0.2, 0.15, 0.1 };
            }
            
            // 疲劳调权
            double[] adjustedWeights = AdjustWeightsForFatigue(actionTypes, weights, sessionState);
            
            // 加权随机选择动作
            string selectedAction = WeightedChoice(actionTypes, adjustedWeights);
            string selectedIntent = ResolveIntentForAction(selectedAction);
            
            CoreHelper.SetVar("current_action", selectedAction);
            CoreHelper.SetVar("current_intent", selectedIntent);
            
            // ========== MemoryManager 去重检测 ==========
            string currentPostId = CoreHelper.GetVar("current_post_id", "");
            string deviceId = CoreHelper.GetVar(VK.DeviceId, "");
            string platformName = CoreHelper.GetVar(VK.PlatformName, "reddit");
            
            if (selectedAction != "browse" && !string.IsNullOrEmpty(currentPostId))
            {
                if (MemoryManager.IsDuplicate(deviceId, platformName, currentPostId, 0))
                {
                    CoreHelper.Log(TAG, string.Format("DecideNextAction: MemoryManager 去重: 帖子 {0} 已交互过，降级为 browse", currentPostId));
                    selectedAction = "browse";
                    selectedIntent = ResolveIntentForAction(selectedAction);
                }
            }
            
            // ========== RuleEngine 评估 ==========
            string currentPostJson = CoreHelper.GetVar("current_post_json", "");
            if (!EvaluatePostForAction(selectedAction, currentPostJson))
            {
                CoreHelper.Log(TAG, string.Format("DecideNextAction: RuleEngine 拒绝 {0}，降级为 browse", selectedAction));
                selectedAction = "browse";
                selectedIntent = ResolveIntentForAction(selectedAction);
            }
            
            // 降级后同步变量
            CoreHelper.SetVar("current_action", selectedAction);
            CoreHelper.SetVar("current_intent", selectedIntent);
            
            PrepareActionVariables(selectedAction);
            CoreHelper.Log(TAG, string.Format("DecideNextAction: 选定动作: {0} (energy={1:F2})", selectedAction, sessionState.Energy));
            
            // ========== 映射动作到操作序列 ==========
            string effectiveIntent = ResolveIntentWithFallback(selectedIntent);
            CoreHelper.SetVar("effective_intent", effectiveIntent);
            
            string[] opSequence = GetOperationsByIntent(effectiveIntent, _currentPage);
            if (opSequence.Length == 0)
            {
                opSequence = MapActionToOperations(selectedAction, _currentPage);
            }
            
            if (opSequence.Length == 0)
            {
                CoreHelper.Log(TAG, string.Format("DecideNextAction: 动作 {0} 在页面 {1} 无对应操作，跳过", selectedAction, _currentPage));
                // Dual-Write: SKIP 路径清空 Step DSL，防止残留旧值
                CoreHelper.SetVar(VK.ZdStepPlan, "");
                CoreHelper.Log(TAG, "[StepDSL] SKIP 路径: 写入空 DSL（无对应操作）");
                SaveDynamicState(sessionState, actionCount, successCount, failCount, skipCount);
                return "SKIP";
            }
            
            // 存储为操作队列
            _operationQueue = new List<string>();
            for (int i = 0; i < opSequence.Length; i++)
            {
                _operationQueue.Add(opSequence[i]);
            }
            _operationQueueIndex = 0;
            
            // 取出第一个操作
            string firstOp = _operationQueue[0];
            CoreHelper.SetVar(VK.CurrentSessionAction, selectedAction);
            CoreHelper.SetVar(VK.CurrentOpName, firstOp);
            
            // Dual-Write: 将完整操作序列写入 Step DSL
            string newDsl = SerializeOpQueue(_operationQueue);
            CoreHelper.SetVar(VK.ZdStepPlan, newDsl);
            
            CoreHelper.Log(TAG, string.Format("DecideNextAction: 操作队列 ({0} 步): {1}, DSL={2}",
                _operationQueue.Count, SerializeOpQueue(_operationQueue), newDsl));
            
            SaveDynamicState(sessionState, actionCount, successCount, failCount, skipCount);
            return firstOp;
        }
        catch (Exception ex)
        {
            CoreHelper.LogErr(TAG, "DecideNextAction 异常: " + ex.Message);
            // Dual-Write: 异常路径清空 Step DSL，防止残留旧值
            try { CoreHelper.SetVar(VK.ZdStepPlan, ""); } catch (Exception) { }
            CoreHelper.Log(TAG, "[StepDSL] ERROR 路径: 写入空 DSL（异常: " + ex.Message + "）");
            // 异常时尝试保存当前状态，防止下次调用时状态不一致
            try { SaveDynamicState(new SessionState(), 0, 0, 0, 0); } catch (Exception) { }
            return "ERROR: " + ex.Message;
        }
    }
    
    /// <summary>
    /// [ZD 外层流程] 阶段 3: 评估 ZD 原生动作块的执行结果
    /// 返回 "CONTINUE"（继续下一个操作）/ "RETRY"（重试当前操作）/ "END"（结束会话）
    /// </summary>
    public static string EvaluateActionResult(object projectObj, object instanceObj)
    {
        _project = projectObj;
        
        try
        {
            CoreHelper.Init(projectObj, instanceObj);
            
            // T17: 兼容模式检测 — Run() 已整体执行完毕，跳过评估
            if (CoreHelper.GetVar(VK.LegacyRunCompleted, "") == "true")
            {
                CoreHelper.Log(TAG, "EvaluateActionResult: 兼容模式 — Run() 已完成，返回 CONTINUE");
                return "CONTINUE";
            }
            
            // 恢复状态
            int actionCount, successCount, failCount, skipCount;
            SessionState sessionState = RestoreState(out actionCount, out successCount, out failCount, out skipCount);
            
            // ========== DSL 模式检测: 提前返回，避免不必要的 GetLayout ==========
            string completedStepIndex = CoreHelper.GetVar(VK.ZdStepIndex, "0");
            string existingDslPlan = CoreHelper.GetVar(VK.ZdStepPlan, "");
            if (!string.IsNullOrEmpty(existingDslPlan))
            {
                string dslFirstToken = existingDslPlan.Split(new char[] { '|' })[0].Trim();
                string dslTypeChar = dslFirstToken.Contains(":") ? dslFirstToken.Split(new char[] { ':' })[0] : dslFirstToken;
                string[] knownDslTypes = new string[] { "L", "T", "S", "W", "B", "V" };
                bool isDslMode = false;
                for (int di = 0; di < knownDslTypes.Length; di++)
                {
                    if (dslTypeChar == knownDslTypes[di]) { isDslMode = true; break; }
                }
                if (isDslMode)
                {
                    successCount++;
                    actionCount++;
                    CoreHelper.Log(TAG, string.Format(
                        "EvaluateActionResult: DSL 模式自动通过 (step_idx={0}, action={1}/{2})",
                        completedStepIndex, actionCount, 50));
                    SaveDynamicState(sessionState, actionCount, successCount, failCount, skipCount);
                    return "CONTINUE";
                }
            }
            
            // 读取执行结果（通过 VK 常量，key 值与原硬编码 "zd_action_result" 一致）
            string actionResult = CoreHelper.GetVar(VK.ZdActionResult, "");
            string currentOpName = CoreHelper.GetVar(VK.CurrentOpName, "");
            string currentSessionAction = CoreHelper.GetVar(VK.CurrentSessionAction, "");
            string deviceId = CoreHelper.GetVar(VK.DeviceId, "");
            string platformName = CoreHelper.GetVar(VK.PlatformName, "reddit");
            string projectRoot = CoreHelper.GetVar(VK.ProjectRoot, "");
            projectRoot = CoreHelper.NormalizePath(projectRoot);
            
            // v4.7.0: 回收诊断变量（仅用于日志，不影响主逻辑）
            string errorDetail = CoreHelper.GetVar(VK.ZdActionErrorDetail, "");
            // completedStepIndex 已在上方 DSL 检测中声明
            
            // v4.7.0: 归一化 actionResult — 兼容 ZD 原生块带冒号后缀的格式（如 "SUCCESS:done"）
            // 空值原样透传给编排器（编排器已有自己的空值处理）
            actionResult = NormalizeActionResult(actionResult);
            
            CoreHelper.Log(TAG, string.Format(
                "EvaluateActionResult: op={0}, action={1}, result={2}, step_idx={3}, err=[{4}]",
                currentOpName, currentSessionAction, actionResult, completedStepIndex, errorDetail));
            
            // ========== 获取预期页面 ==========
            string opDef = JsonHelper.ExtractObject(_operationsJson, currentOpName);
            string expectedPage = SmartOrchestrator.ExtractExpectedPage(opDef);
            
            // ========== 检测实际页面 ==========
            string actualPage = _currentPage;
            string postOpXml = CoreHelper.GetLayout();
            if (!string.IsNullOrEmpty(postOpXml))
            {
                string sigJson = JsonHelper.ExtractObject(_platformConfig, "page_signatures");
                if (!string.IsNullOrEmpty(sigJson))
                {
                    actualPage = PageDetector.Detect(postOpXml, sigJson);
                }
            }
            
            // ========== 双层成功判定 ==========
            SmartOrchestrator.OperationVerdict verdict = _orchestrator.EvaluateResult(
                actionResult, currentOpName, expectedPage, actualPage, _platformConfig);
            
            string returnValue = "CONTINUE";
            
            if (verdict == SmartOrchestrator.OperationVerdict.Success)
            {
                // ===== 成功处理 =====
                _orchestrator.RecordSuccess();
                _currentPage = actualPage;
                CoreHelper.SetVar("current_page", _currentPage);
                UpdateCurrentPostContext();
                
                // 推进操作队列索引
                _operationQueueIndex++;
                
                // 检查队列是否耗尽
                if (_operationQueueIndex >= _operationQueue.Count)
                {
                    // 本次 sessionAction 全部操作完成
                    successCount++;
                    _consecutiveSkips = 0;
                    
                    // 记录交互记忆（非 browse 动作）
                    string currentPostId = CoreHelper.GetVar("current_post_id", "");
                    if (currentSessionAction != "browse" && !string.IsNullOrEmpty(currentPostId))
                    {
                        MemoryManager.RecordInteraction(deviceId, platformName, currentPostId, currentSessionAction);
                        CoreHelper.Log(TAG, string.Format("EvaluateActionResult: MemoryManager 已记录: {0} on {1}", currentSessionAction, currentPostId));
                        
                        // 清空帖子数据防止重复评估
                        CoreHelper.SetVar("current_post_json", "");
                        CoreHelper.SetVar("current_post_id", "");
                    }
                    
                    // 累加 actionCount（一个 sessionAction 完成算一次）
                    actionCount++;
                    
                    // 追加记忆条目
                    string timestamp = DateTime.Now.ToString("HH:mm:ss");
                    string logEntry = "{\"time\":\"" + JsonHelper.Escape(timestamp) + "\",\"action_type\":\"" + JsonHelper.Escape(currentSessionAction) + "\",\"result\":\"SUCCESS\"}";
                    AppendMemoryEntry(logEntry);
                }
                
                returnValue = "CONTINUE";
            }
            else if (verdict == SmartOrchestrator.OperationVerdict.Skipped)
            {
                // ===== 跳过处理 =====
                skipCount++;
                _operationQueue.Clear();
                _operationQueueIndex = 0;
                
                _consecutiveSkips++;
                if (_consecutiveSkips >= MAX_CONSECUTIVE_SKIPS)
                {
                    CoreHelper.Log(TAG, string.Format("EvaluateActionResult: 连续 {0} 次 SKIP，触发导航恢复", _consecutiveSkips));
                    ForceNavigateToFeed(platformName, projectRoot);
                    _consecutiveSkips = 0;
                }
                
                actionCount++;
                
                // 追加记忆条目
                string skipTimestamp = DateTime.Now.ToString("HH:mm:ss");
                string skipLogEntry = "{\"time\":\"" + JsonHelper.Escape(skipTimestamp) + "\",\"action_type\":\"" + JsonHelper.Escape(currentSessionAction) + "\",\"result\":\"SKIP\"}";
                AppendMemoryEntry(skipLogEntry);
                
                returnValue = "CONTINUE";
            }
            else
            {
                // ===== 失败处理（ExecutionFailed / BusinessFailed） =====
                _orchestrator.RecordFailure();
                SmartOrchestrator.RecoveryLevel level = _orchestrator.DecideRecovery();
                
                CoreHelper.Log(TAG, string.Format(
                    "[ORCHESTRATOR] EvaluateActionResult: {0}: verdict={1}, recovery={2}, 诊断={3}, err_detail=[{4}]",
                    currentOpName, verdict, level, _orchestrator.GetLastDiagnostics(), errorDetail));
                
                if (level == SmartOrchestrator.RecoveryLevel.Retry)
                {
                    // 简单重试: 不推进队列索引
                    CoreHelper.Log(TAG, string.Format("[ORCHESTRATOR] Retry {0}", currentOpName));
                    returnValue = "RETRY";
                }
                else if (level == SmartOrchestrator.RecoveryLevel.LocalRecovery)
                {
                    // 局部恢复: back_to_feed 后重试
                    CoreHelper.Log(TAG, string.Format("[ORCHESTRATOR] LocalRecovery: 先 back_to_feed 再重试 {0}", currentOpName));
                    string backResult = ActionExecutor.Execute(_operationsJson, "back_to_feed", _platformConfig);
                    if (backResult.StartsWith("SUCCESS"))
                    {
                        string recXml = CoreHelper.GetLayout();
                        if (!string.IsNullOrEmpty(recXml))
                        {
                            string recSigJson = JsonHelper.ExtractObject(_platformConfig, "page_signatures");
                            if (!string.IsNullOrEmpty(recSigJson))
                            {
                                _currentPage = PageDetector.Detect(recXml, recSigJson);
                                CoreHelper.SetVar("current_page", _currentPage);
                            }
                        }
                    }
                    else
                    {
                        // back_to_feed 失败，暴力 Back
                        dynamic recInput = CoreHelper.GetInput();
                        if (recInput != null)
                        {
                            recInput.SendKeyCode(4);
                            System.Threading.Thread.Sleep(1500);
                            recInput.SendKeyCode(4);
                            System.Threading.Thread.Sleep(1500);
                        }
                    }
                    returnValue = "RETRY";
                }
                else if (level == SmartOrchestrator.RecoveryLevel.VisionAssist)
                {
                    // AI 视觉验证
                    CoreHelper.Log(TAG, string.Format("[ORCHESTRATOR] VisionAssist: AI 视觉验证 {0}", currentOpName));
                    bool visionOverride = false;
                    if (VisionCorrector.IsInitialized() && _visionRecoveryCount < MAX_VISION_RECOVERY_PER_SESSION)
                    {
                        string ssPath = VisionCorrector.CaptureScreenshot();
                        if (!string.IsNullOrEmpty(ssPath))
                        {
                            _visionRecoveryCount++;
                            string effectiveIntent = CoreHelper.GetVar("effective_intent", "");
                            ZDResult errResult = ZDResult.FailedRetryable(actionResult);
                            visionOverride = VisionCorrector.VerifyError(effectiveIntent, errResult, ssPath);
                            if (visionOverride)
                            {
                                CoreHelper.Log(TAG, string.Format("[VISION] {0} 实际成功（ZD 误报）", currentOpName));
                                _orchestrator.RecordSuccess();
                                _currentPage = actualPage;
                                CoreHelper.SetVar("current_page", _currentPage);
                                UpdateCurrentPostContext();
                                _operationQueueIndex++;
                                
                                // 队列耗尽时更新 sessionAction 计数
                                if (_operationQueueIndex >= _operationQueue.Count)
                                {
                                    successCount++;
                                    _consecutiveSkips = 0;
                                    actionCount++;
                                }
                                
                                returnValue = "CONTINUE";
                            }
                        }
                    }
                    
                    // Vision 没有翻转结果，视为跳过当前操作
                    if (!visionOverride)
                    {
                        returnValue = "CONTINUE";
                    }
                }
                else if (level == SmartOrchestrator.RecoveryLevel.FallbackScript)
                {
                    // 备用剧本: 当前阶段记录并跳过
                    CoreHelper.Log(TAG, string.Format("[ORCHESTRATOR] FallbackScript: {0} 无备用剧本，跳过", currentOpName));
                    _operationQueue.Clear();
                    _operationQueueIndex = 0;
                    actionCount++;
                    returnValue = "CONTINUE";
                }
                else if (level == SmartOrchestrator.RecoveryLevel.Abort)
                {
                    // 恢复预算耗尽
                    CoreHelper.LogErr(TAG, string.Format(
                        "[ORCHESTRATOR] Abort: {0} 恢复预算耗尽, {1}",
                        currentOpName, _orchestrator.GetSessionSummary()));
                    failCount++;
                    _operationQueue.Clear();
                    _operationQueueIndex = 0;
                    actionCount++;
                    
                    // 追加记忆条目
                    string failTimestamp = DateTime.Now.ToString("HH:mm:ss");
                    string failLogEntry = "{\"time\":\"" + JsonHelper.Escape(failTimestamp) + "\",\"action_type\":\"" + JsonHelper.Escape(currentSessionAction) + "\",\"result\":\"ERROR\"}";
                    AppendMemoryEntry(failLogEntry);
                    
                    returnValue = "CONTINUE";
                }
            }
            
            // ========== 更新能量 ==========
            // v4.7.0: 从 ZD 变量读取实际执行耗时（ZD 原生动作块设置），默认 3 秒
            int actionDurationSec = ParseActionDuration(CoreHelper.GetVar(VK.ZdActionDuration, "3"));
            UpdateEnergy(currentSessionAction, actionDurationSec, sessionState);
            
            // ========== 保存状态 ==========
            SaveDynamicState(sessionState, actionCount, successCount, failCount, skipCount);
            
            CoreHelper.Log(TAG, string.Format("EvaluateActionResult: 返回 {0} (action={1}/{2}, success={3}, fail={4}, skip={5})",
                returnValue, actionCount, 50, successCount, failCount, skipCount));
            
            return returnValue;
        }
        catch (Exception ex)
        {
            CoreHelper.LogErr(TAG, "EvaluateActionResult 异常: " + ex.Message);
            // 异常时尝试保存当前状态
            try { SaveDynamicState(new SessionState(), 0, 0, 0, 0); } catch (Exception) { }
            return "CONTINUE";
        }
    }
    
    /// <summary>
    /// 归一化 ZD 原生块执行结果字符串
    /// 处理带冒号后缀的格式（如 "SUCCESS:done" → "SUCCESS"）
    /// 空值原样返回（不做降级，交由编排器自身处理）
    /// </summary>
    private static string NormalizeActionResult(string raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return raw;
        }
        
        string trimmed = raw.Trim();
        if (trimmed.Length == 0)
        {
            return "";
        }
        
        // 取冒号前的前缀作为归一化结果（"SUCCESS:done" → "SUCCESS"）
        int colonIdx = trimmed.IndexOf(':');
        if (colonIdx > 0)
        {
            return trimmed.Substring(0, colonIdx);
        }
        
        return trimmed;
    }
    
    /// <summary>
    /// 安全解析 ZD 原生块执行耗时（秒）
    /// 兼容整数("3")、浮点("2.5")、空值、非法值
    /// 范围钳制: [1, 60]，失败默认 3（与原始 int.Parse 行为一致但更安全）
    /// </summary>
    private static int ParseActionDuration(string raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return 3;
        }
        
        int intVal;
        if (int.TryParse(raw.Trim(), out intVal))
        {
            if (intVal <= 0) return 3;
            if (intVal > 60) return 60;
            return intVal;
        }
        
        // 兼容浮点（如 "2.5"）
        double dblVal;
        if (double.TryParse(raw.Trim(), out dblVal))
        {
            int rounded = (int)Math.Round(dblVal);
            if (rounded <= 0) return 3;
            if (rounded > 60) return 60;
            return rounded;
        }
        
        return 3;
    }
    
    /// <summary>
    /// [ZD 外层流程] 阶段 4: 会话结束收尾
    /// 保存记忆文件、清理 MemoryManager、输出编排器统计、门控判定
    /// </summary>
    public static string FinalizeSession(object projectObj, object instanceObj)
    {
        _project = projectObj;
        
        try
        {
            CoreHelper.Init(projectObj, instanceObj);
            
            // T17: 兼容模式检测 — Run() 已整体执行完毕，跳过收尾
            if (CoreHelper.GetVar(VK.LegacyRunCompleted, "") == "true")
            {
                string legacyResult = CoreHelper.GetVar(VK.LegacyRunResult, "SUCCESS");
                CoreHelper.Log(TAG, "FinalizeSession: 兼容模式 — Run() 已完成，返回 " + legacyResult);
                return legacyResult;
            }
            
            // 恢复状态
            int actionCount, successCount, failCount, skipCount;
            SessionState sessionState = RestoreState(out actionCount, out successCount, out failCount, out skipCount);
            
            string memoryFile = CoreHelper.GetVar(VK.MemoryFile, "");
            string memoryEntriesStr = CoreHelper.GetVar(VK.MemoryEntries, "");
            string sessionStartStr = CoreHelper.GetVar(VK.SessionStartTime, "");
            string deviceId = CoreHelper.GetVar(VK.DeviceId, "");
            string platformName = CoreHelper.GetVar(VK.PlatformName, "reddit");
            string behaviorConfigJson = CoreHelper.GetVar(VK.BehaviorConfigJson, "");
            
            // ========== 构建并写入记忆 JSON ==========
            if (!string.IsNullOrEmpty(memoryFile))
            {
                string today = CoreHelper.GetToday();
                string sessionStartTime = "00:00:00";
                if (!string.IsNullOrEmpty(sessionStartStr))
                {
                    try
                    {
                        DateTime startDt = DateTime.Parse(sessionStartStr);
                        sessionStartTime = startDt.ToString("HH:mm:ss");
                    }
                    catch (Exception)
                    {
                        // 解析失败使用默认值
                    }
                }
                
                // memoryEntriesStr 是逗号分隔的 JSON 对象列表
                string entriesJson = "[" + memoryEntriesStr + "]";
                
                string memoryJson = "{"
                    + "\"device_id\":\"" + JsonHelper.Escape(deviceId) + "\","
                    + "\"date\":\"" + today + "\","
                    + "\"session_start\":\"" + sessionStartTime + "\","
                    + "\"session_end\":\"" + DateTime.Now.ToString("HH:mm:ss") + "\","
                    + "\"entries\":" + entriesJson
                    + "}";
                
                CoreHelper.WriteFileAtomic(memoryFile, memoryJson);
                CoreHelper.Log(TAG, "FinalizeSession: 记忆已保存: " + memoryFile);
            }
            
            // ========== MemoryManager 清理 ==========
            MemoryManager.CleanupOldInteractions(deviceId, platformName, 0);
            MemoryManager.EnforceMemoryLimits(deviceId, platformName, _decisionConfigJson);
            CoreHelper.Log(TAG, "FinalizeSession: MemoryManager 清理完成");
            
            // ========== 编排器统计 ==========
            CoreHelper.Log(TAG, string.Format("[ORCHESTRATOR] 会话统计: {0}", _orchestrator.GetSessionSummary()));
            if (_orchestrator.GetFalseSuccessCount() > 0)
            {
                CoreHelper.LogWarn(TAG, string.Format(
                    "[ORCHESTRATOR] 本次会话检出 {0} 次假成功",
                    _orchestrator.GetFalseSuccessCount()));
            }
            
            // ========== 会话成功门控 ==========
            // DSL 模式: 放宽门控条件（测试 DSL 步骤数可能少于 minSuccessfulActions）
            string finalDslPlan = CoreHelper.GetVar(VK.ZdStepPlan, "");
            bool isDslTestMode = false;
            if (!string.IsNullOrEmpty(finalDslPlan))
            {
                string fToken = finalDslPlan.Split(new char[] { '|' })[0].Trim();
                string fType = fToken.Contains(":") ? fToken.Split(new char[] { ':' })[0] : fToken;
                string[] fDslTypes = new string[] { "L", "T", "S", "W", "B", "V" };
                for (int fi = 0; fi < fDslTypes.Length; fi++)
                {
                    if (fType == fDslTypes[fi]) { isDslTestMode = true; break; }
                }
            }
            
            int effectiveActions = successCount + failCount;
            double sessionSuccessRate = effectiveActions > 0
                ? (double)successCount / effectiveActions
                : 0.0;
            
            string sessionGateSection = JsonHelper.ExtractObject(behaviorConfigJson, "session_gate");
            double requiredSuccessRate = JsonHelper.GetDouble(sessionGateSection, "min_success_rate", 0.95);
            int minSuccessfulActions = JsonHelper.GetInt(sessionGateSection, "min_successful_actions", 6);
            
            // DSL 模式: 只要有成功步骤且无失败就算通过
            if (isDslTestMode)
            {
                minSuccessfulActions = 1;
                requiredSuccessRate = 0.5;
                CoreHelper.Log(TAG, "FinalizeSession: DSL 测试模式 — 使用放宽门控");
            }
            
            bool isSuccess = sessionSuccessRate >= requiredSuccessRate
                          && successCount >= minSuccessfulActions;
            
            CoreHelper.Log(TAG, string.Format(
                "FinalizeSession: 门控判定: success_rate={0:F3} (需>={1:F2}), successful={2} (需>={3}), 结果={4}",
                sessionSuccessRate, requiredSuccessRate, successCount, minSuccessfulActions,
                isSuccess ? "PASS" : "FAIL"));
            
            CoreHelper.Log(TAG, string.Format(
                "FinalizeSession: 会话结束 - 总尝试: {0}, 成功: {1}, 失败: {2}, 跳过: {3}",
                actionCount, successCount, failCount, skipCount));
            
            // ========== 输出变量 ==========
            CoreHelper.SetVar("action_count", successCount.ToString());
            CoreHelper.SetVar("action_attempt_count", actionCount.ToString());
            CoreHelper.SetVar("session_successful_actions", successCount.ToString());
            CoreHelper.SetVar("session_failed_actions", failCount.ToString());
            CoreHelper.SetVar("session_skipped_actions", skipCount.ToString());
            CoreHelper.SetVar("session_success_rate", sessionSuccessRate.ToString("F4"));
            
            if (isSuccess)
            {
                CoreHelper.SetVar("run_result", "SUCCESS");
                CoreHelper.SetVar("session_result", "SUCCESS");
                return "SUCCESS";
            }
            else
            {
                CoreHelper.LogErr(TAG, string.Format(
                    "FinalizeSession: 会话未达标: success_rate={0:F3}, successful={1}",
                    sessionSuccessRate, successCount));
                CoreHelper.SetVar("run_result", "ERROR");
                CoreHelper.SetVar("session_result", "ERROR");
                return "ERROR: 会话成功率不达标";
            }
        }
        catch (Exception ex)
        {
            CoreHelper.LogErr(TAG, "FinalizeSession 异常: " + ex.Message);
            CoreHelper.SetVar("last_error", ex.Message);
            CoreHelper.SetVar("session_result", "ERROR");
            return "ERROR: " + ex.Message;
        }
    }
    
    /// <summary>
    /// 追加一条记忆条目到 VK.MemoryEntries（逗号分隔的 JSON 对象列表）
    /// </summary>
    private static void AppendMemoryEntry(string jsonEntry)
    {
        string existing = CoreHelper.GetVar(VK.MemoryEntries, "");
        if (string.IsNullOrEmpty(existing))
        {
            CoreHelper.SetVar(VK.MemoryEntries, jsonEntry);
        }
        else
        {
            CoreHelper.SetVar(VK.MemoryEntries, existing + "," + jsonEntry);
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
        
        double r = GetRandom().NextDouble() * total;
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
        
        if (minSec < 0) minSec = 0;
        if (maxSec < 0) maxSec = 0;
        if (maxSec < minSec)
        {
            int temp = minSec;
            minSec = maxSec;
            maxSec = temp;
        }
        if (maxSec == minSec) maxSec = minSec + 1;
        
        if (minSec > 3600) minSec = 3600;
        if (maxSec > 3600) maxSec = 3600;
        
        int minMs = minSec * 1000;
        int maxMs = maxSec * 1000;
        
        return GetRandom().Next(minMs, maxMs);
    }
    
    /// <summary>
    /// 根据疲劳模型调整动作权重
    /// 能量不足时，高消耗动作（comment、like）权重降为 0
    /// </summary>
    private static double[] AdjustWeightsForFatigue(string[] actionTypes, double[] baseWeights, SessionState state)
    {
        if (!state.FatigueEnabled)
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
            
            if (action == "comment" && state.Energy < state.MinEnergyToComment)
            {
                blocked = true;
            }
            else if (action == "like" && state.Energy < state.MinEnergyToLike)
            {
                blocked = true;
            }
            
            if (blocked)
            {
                adjusted[i] = 0.0;
                removedWeight += baseWeights[i];
                CoreHelper.Log(TAG, string.Format("疲劳禁用动作: {0} (energy={1:F2})", action, state.Energy));
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
    /// 修复: 重写方法签名匹配调用点 line 430: UpdateEnergy(selectedAction, delayMs / 1000, sessionState)
    /// </summary>
    private static void UpdateEnergy(string action, int pauseSec, SessionState state)
    {
        if (!state.FatigueEnabled)
        {
            return;
        }
        
        // 消耗能量
        double decay = 0.0;
        if (action == "browse") decay = state.DecayBrowse;
        else if (action == "read_post") decay = state.DecayRead;
        else if (action == "like") decay = state.DecayLike;
        else if (action == "comment") decay = state.DecayComment;
        else decay = state.DecayBrowse; // post 等其他动作用 browse 的消耗
        
        state.Energy -= decay;
        
        // 等待期间恢复能量
        double pauseSecD = (double)pauseSec;
        state.Energy += pauseSecD * state.RecoveryPerPauseSec;
        
        // 钳制到 [0, 1]
        if (state.Energy < 0.0) state.Energy = 0.0;
        if (state.Energy > 1.0) state.Energy = 1.0;
        
        CoreHelper.Log(TAG, string.Format("能量更新: action={0}, decay={1:F3}, recovery={2:F3}, energy={3:F2}",
            action, decay, pauseSecD * state.RecoveryPerPauseSec, state.Energy));
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
            string defaultPlatform = JsonHelper.Get(mappingJson, "default_platform");
            if (string.IsNullOrEmpty(defaultPlatform))
            {
                defaultPlatform = "reddit";
            }
            string devicesSection = JsonHelper.ExtractObject(mappingJson, "devices");
            string deviceConfig = JsonHelper.ExtractObject(devicesSection, deviceId);
            
            if (string.IsNullOrEmpty(deviceConfig))
            {
                CoreHelper.Log(TAG, "设备 " + deviceId + " 未配置，默认使用 " + defaultPlatform);
                return defaultPlatform.ToLower();
            }
            
            string platform = JsonHelper.Get(deviceConfig, "platform");
            
            if (string.IsNullOrEmpty(platform))
            {
                CoreHelper.Log(TAG, "设备 " + deviceId + " 未指定平台，默认使用 " + defaultPlatform);
                return defaultPlatform.ToLower();
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
    private static string ExecuteWithUnifiedEngine(string sessionAction, string sessionIntent, string platformName, string projectRoot)
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
        
        string effectiveIntent = ResolveIntentWithFallback(sessionIntent);
        CoreHelper.SetVar("effective_intent", effectiveIntent);
        
        // 优先按统一意图映射执行，找不到时回退到旧动作映射
        string[] opSequence = GetOperationsByIntent(effectiveIntent, _currentPage);
        if (opSequence.Length == 0)
        {
            // 映射 SessionRunner 动作名 → operations JSON 操作名序列
            // 某些动作可能需要多步操作（如 read_post = open_post → read_post）
            opSequence = MapActionToOperations(sessionAction, _currentPage);
        }
        
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
            
            // v4.6.0: 每个新操作重置编排器的操作级计数
            _orchestrator.ResetForNewOperation();
            
            // 提取操作完成后预期页面（用于双层业务判定）
            string opDef = JsonHelper.ExtractObject(_operationsJson, opName);
            string expectedPage = SmartOrchestrator.ExtractExpectedPage(opDef);
            
            string result = "";
            SmartOrchestrator.OperationVerdict verdict = SmartOrchestrator.OperationVerdict.ExecutionFailed;
            
            // v4.6.0: 分级恢复循环 — 失败时按编排器决策逐级升级
            bool operationDone = false;
            while (!operationDone)
            {
                result = ActionExecutor.Execute(_operationsJson, opName, _platformConfig);
                
                // v4.5.12: 操作后前台健康检查（三层恢复）
                string opPackageName = JsonHelper.Get(_platformConfig, "package_name");
                if (!string.IsNullOrEmpty(opPackageName) && !IsAppForeground(opPackageName))
                {
                    bool recovered = PostOperationHealthCheck(opPackageName, opName, _currentPage, projectRoot);
                    if (!recovered)
                    {
                        CoreHelper.LogErr(TAG, string.Format("[RECOVERY] 恢复失败，中止操作序列: {0}", opName));
                        result = "ERROR:app_not_foreground";
                    }
                    else
                    {
                        CoreHelper.Log(TAG, "[RECOVERY] 前台恢复成功，继续执行");
                    }
                }
                
                // 操作后获取实际页面状态
                string postOpXml = CoreHelper.GetLayout();
                string actualPage = _currentPage;
                if (!string.IsNullOrEmpty(postOpXml))
                {
                    string sigJson = JsonHelper.ExtractObject(_platformConfig, "page_signatures");
                    if (!string.IsNullOrEmpty(sigJson))
                    {
                        actualPage = PageDetector.Detect(postOpXml, sigJson);
                    }
                }
                
                // v4.6.0: 双层成功判定（ADR-015）
                verdict = _orchestrator.EvaluateResult(result, opName, expectedPage, actualPage, _platformConfig);
                
                if (verdict == SmartOrchestrator.OperationVerdict.Success)
                {
                    _orchestrator.RecordSuccess();
                    _currentPage = actualPage;
                    CoreHelper.SetVar("current_page", _currentPage);
                    operationDone = true;
                }
                else if (verdict == SmartOrchestrator.OperationVerdict.Skipped)
                {
                    operationDone = true;
                }
                else
                {
                    // 执行失败或业务失败 — 进入分级恢复决策
                    _orchestrator.RecordFailure();
                    SmartOrchestrator.RecoveryLevel level = _orchestrator.DecideRecovery();
                    
                    CoreHelper.Log(TAG, string.Format(
                        "[ORCHESTRATOR] {0}: verdict={1}, recovery={2}, 诊断={3}",
                        opName, verdict, level, _orchestrator.GetLastDiagnostics()));
                    
                    switch (level)
                    {
                        case SmartOrchestrator.RecoveryLevel.Retry:
                            // 简单重试: 直接重新执行
                            CoreHelper.Log(TAG, string.Format("[ORCHESTRATOR] Retry {0}", opName));
                            break;
                            
                        case SmartOrchestrator.RecoveryLevel.LocalRecovery:
                            // 局部恢复: 回退安全页后重试
                            CoreHelper.Log(TAG, string.Format("[ORCHESTRATOR] LocalRecovery: 先 back_to_feed 再重试 {0}", opName));
                            string backResult = ActionExecutor.Execute(_operationsJson, "back_to_feed", _platformConfig);
                            if (backResult.StartsWith("SUCCESS"))
                            {
                                string recXml = CoreHelper.GetLayout();
                                if (!string.IsNullOrEmpty(recXml))
                                {
                                    string recSigJson = JsonHelper.ExtractObject(_platformConfig, "page_signatures");
                                    if (!string.IsNullOrEmpty(recSigJson))
                                    {
                                        _currentPage = PageDetector.Detect(recXml, recSigJson);
                                        CoreHelper.SetVar("current_page", _currentPage);
                                    }
                                }
                            }
                            else
                            {
                                // back_to_feed 失败，暴力 Back
                                dynamic recInput = CoreHelper.GetInput();
                                if (recInput != null)
                                {
                                    recInput.SendKeyCode(4);
                                    System.Threading.Thread.Sleep(1500);
                                    recInput.SendKeyCode(4);
                                    System.Threading.Thread.Sleep(1500);
                                }
                            }
                            break;
                            
                        case SmartOrchestrator.RecoveryLevel.VisionAssist:
                            // AI 视觉识别 + 纠偏
                            CoreHelper.Log(TAG, string.Format("[ORCHESTRATOR] VisionAssist: AI 视觉验证 {0}", opName));
                            bool visionOverride = false;
                            if (VisionCorrector.IsInitialized() && _visionRecoveryCount < MAX_VISION_RECOVERY_PER_SESSION)
                            {
                                string ssPath = VisionCorrector.CaptureScreenshot();
                                if (!string.IsNullOrEmpty(ssPath))
                                {
                                    _visionRecoveryCount++;
                                    ZDResult errResult = ZDResult.FailedRetryable(result);
                                    visionOverride = VisionCorrector.VerifyError(effectiveIntent, errResult, ssPath);
                                    if (visionOverride)
                                    {
                                        CoreHelper.Log(TAG, string.Format("[VISION] {0} 实际成功（ZD 误报）", opName));
                                        verdict = SmartOrchestrator.OperationVerdict.Success;
                                        _orchestrator.RecordSuccess();
                                        operationDone = true;
                                    }
                                }
                            }
                            // 如果 Vision 没有翻转结果，继续下一级恢复循环
                            break;
                            
                        case SmartOrchestrator.RecoveryLevel.FallbackScript:
                            // 备用操作序列（当前阶段: 记录并跳过）
                            CoreHelper.Log(TAG, string.Format("[ORCHESTRATOR] FallbackScript: {0} 无备用剧本，跳过", opName));
                            // TODO: Phase 3 实现备用剧本查找与执行
                            break;
                            
                        case SmartOrchestrator.RecoveryLevel.Abort:
                            // 所有恢复预算耗尽，中止
                            CoreHelper.LogErr(TAG, string.Format(
                                "[ORCHESTRATOR] Abort: {0} 恢复预算耗尽, {1}",
                                opName, _orchestrator.GetSessionSummary()));
                            operationDone = true;
                            break;
                    }
                }
            }
            
            // 根据最终裁决设置操作结果
            if (verdict == SmartOrchestrator.OperationVerdict.Success)
            {
                lastResult = "SUCCESS";
                // v4.5.2: 提取帖子数据
                UpdateCurrentPostContext();
            }
            else if (verdict == SmartOrchestrator.OperationVerdict.Skipped)
            {
                CoreHelper.Log(TAG, string.Format("操作 {0} 跳过: {1}", opName, result));
                lastResult = "SKIP";
                break;
            }
            else
            {
                // ExecutionFailed / BusinessFailed / Aborted
                CoreHelper.LogErr(TAG, string.Format("操作 {0} 最终失败: {1}", opName, _orchestrator.GetLastDiagnostics()));
                lastResult = "ERROR";
                break;
            }
        }
        
        // v4.6.0: ERROR 后页面清理（保留原有 BUG-09 fix 逻辑，由编排器记录）
        if (lastResult == "ERROR")
        {
            // 检测当前页面是否偏离了 feed
            string cleanupXml = CoreHelper.GetLayout();
            if (!string.IsNullOrEmpty(cleanupXml))
            {
                string cleanupSigJson = JsonHelper.ExtractObject(_platformConfig, "page_signatures");
                if (!string.IsNullOrEmpty(cleanupSigJson))
                {
                    string currentPageNow = PageDetector.Detect(cleanupXml, cleanupSigJson);
                    if (currentPageNow != "feed")
                    {
                        CoreHelper.Log(TAG, string.Format("[RECOVERY] 操作失败后页面偏离: {0}, 尝试 back_to_feed", currentPageNow));
                        // 优先用 operations 中定义的 back_to_feed
                        string backResult = ActionExecutor.Execute(_operationsJson, "back_to_feed", _platformConfig);
                        if (backResult.StartsWith("SUCCESS"))
                        {
                            CoreHelper.Log(TAG, "[RECOVERY] back_to_feed 清理成功");
                            // 更新页面状态
                            string postCleanupXml = CoreHelper.GetLayout();
                            if (!string.IsNullOrEmpty(postCleanupXml))
                            {
                                _currentPage = PageDetector.Detect(postCleanupXml, cleanupSigJson);
                                CoreHelper.SetVar("current_page", _currentPage);
                            }
                        }
                        else
                        {
                            // back_to_feed 失败时，暴力 Back 两次
                            CoreHelper.Log(TAG, "[RECOVERY] back_to_feed 失败，暴力 Back");
                            dynamic cleanupInput = CoreHelper.GetInput();
                            if (cleanupInput != null)
                            {
                                cleanupInput.SendKeyCode(4);
                                System.Threading.Thread.Sleep(1500);
                                cleanupInput.SendKeyCode(4);
                                System.Threading.Thread.Sleep(1500);
                            }
                        }
                    }
                }
            }
        }
        
        // 设置结果变量
        CoreHelper.SetVar("action_result", lastResult);
        return lastResult;
    }
    
    /// <summary>
    /// 加载全局用户策略配置（跨平台统一）
    /// </summary>
    private static void LoadUserStrategy(string projectRoot)
    {
        string strategyPath = projectRoot + "Config\\UserStrategy.json";
        _userStrategyJson = "";
        _aiDirectExecution = true;
        _notifyHumanOnAiControl = false;
        
        if (!File.Exists(strategyPath))
        {
            CoreHelper.Log(TAG, "UserStrategy.json 不存在，使用默认策略");
            CoreHelper.SetVar("strategy_success_weight", "1.0");
            CoreHelper.SetVar("strategy_humanization_weight", "1.0");
            return;
        }
        
        try
        {
            _userStrategyJson = CoreHelper.ReadFile(strategyPath);
            
            string balanceJson = JsonHelper.ExtractObject(_userStrategyJson, "decision_balance");
            if (string.IsNullOrEmpty(balanceJson))
            {
                balanceJson = JsonHelper.ExtractObject(_userStrategyJson, "balance");
            }
            double successWeight = JsonHelper.GetDouble(balanceJson, "success_weight", 1.0);
            double humanizationWeight = JsonHelper.GetDouble(balanceJson, "humanization_weight", 1.0);
            CoreHelper.SetVar("strategy_success_weight", successWeight.ToString("F2"));
            CoreHelper.SetVar("strategy_humanization_weight", humanizationWeight.ToString("F2"));
            
            string aiControl = JsonHelper.ExtractObject(_userStrategyJson, "ai_control");
            string aiControlEnabled = JsonHelper.Get(aiControl, "enabled");
            if (aiControlEnabled == "false")
            {
                _aiDirectExecution = false;
                _notifyHumanOnAiControl = false;
            }
            else
            {
                string directExecution = JsonHelper.Get(aiControl, "direct_execution");
                _aiDirectExecution = (directExecution != "false");
                string notifyHuman = JsonHelper.Get(aiControl, "notify_human");
                _notifyHumanOnAiControl = (notifyHuman == "true");
            }
            CoreHelper.SetVar("ai_direct_execution", _aiDirectExecution ? "true" : "false");
            CoreHelper.SetVar("ai_notify_human", _notifyHumanOnAiControl ? "true" : "false");
            
            CoreHelper.Log(TAG, string.Format("用户策略: success={0:F2}, humanization={1:F2}, aiDirect={2}, notifyHuman={3}",
                successWeight, humanizationWeight, _aiDirectExecution, _notifyHumanOnAiControl));
        }
        catch (Exception ex)
        {
            CoreHelper.LogErr(TAG, "加载用户策略失败: " + ex.Message);
            CoreHelper.SetVar("strategy_success_weight", "1.0");
            CoreHelper.SetVar("strategy_humanization_weight", "1.0");
        }
    }
    
    /// <summary>
    /// 加载平台意图映射配置
    /// </summary>
    private static void LoadIntentMapping(string projectRoot, string platformName)
    {
        _intentMappingJson = "";
        string mappingPath = projectRoot + "Config\\IntentMappings\\" + platformName + "_intents.json";
        
        if (!File.Exists(mappingPath))
        {
            CoreHelper.LogWarn(TAG, "意图映射不存在，使用内置动作映射: " + mappingPath);
            return;
        }
        
        try
        {
            _intentMappingJson = CoreHelper.ReadFile(mappingPath);
            CoreHelper.SetVar("intent_mapping_loaded", "true");
            CoreHelper.Log(TAG, "已加载意图映射: " + platformName);
        }
        catch (Exception ex)
        {
            CoreHelper.LogErr(TAG, "加载意图映射失败: " + ex.Message);
            _intentMappingJson = "";
        }
    }
    
    /// <summary>
    /// 将当前会话动作映射为统一意图
    /// </summary>
    private static string ResolveIntentForAction(string action)
    {
        if (!string.IsNullOrEmpty(_intentMappingJson))
        {
            string actionMap = JsonHelper.ExtractObject(_intentMappingJson, "action_to_intent");
            if (!string.IsNullOrEmpty(actionMap))
            {
                string mappedIntent = JsonHelper.Get(actionMap, action);
                if (!string.IsNullOrEmpty(mappedIntent))
                {
                    return mappedIntent;
                }
            }
        }
        
        // 默认映射，兼容旧配置
        // v4.5.9 修复: like 映射到 like_content（之前错误映射到 open_post）
        if (action == "browse") return "browse_feed";
        if (action == "read_post") return "read_post";
        if (action == "like") return "like_content";
        if (action == "comment") return "reply_post";
        if (action == "post") return "reply_post";
        return "browse_feed";
    }
    
    /// <summary>
    /// 解析意图回退链，返回可执行的意图
    /// </summary>
    private static string ResolveIntentWithFallback(string intent)
    {
        if (string.IsNullOrEmpty(intent))
        {
            return "browse_feed";
        }
        
        if (IsIntentSupported(intent))
        {
            return intent;
        }
        
        string[] fallbacks = GetIntentFallbacks(intent);
        for (int i = 0; i < fallbacks.Length; i++)
        {
            if (IsIntentSupported(fallbacks[i]))
            {
                CoreHelper.Log(TAG, string.Format("意图回退: {0} -> {1}", intent, fallbacks[i]));
                CoreHelper.SetVar("intent_fallback_from", intent);
                CoreHelper.SetVar("intent_fallback_to", fallbacks[i]);
                return fallbacks[i];
            }
        }
        
        if (IsIntentSupported("browse_feed"))
        {
            CoreHelper.Log(TAG, string.Format("意图回退: {0} -> browse_feed", intent));
            CoreHelper.SetVar("intent_fallback_from", intent);
            CoreHelper.SetVar("intent_fallback_to", "browse_feed");
            return "browse_feed";
        }
        
        return intent;
    }
    
    /// <summary>
    /// 判断某个意图是否在平台映射中可执行（存在且有 operations）
    /// </summary>
    private static bool IsIntentSupported(string intent)
    {
        if (string.IsNullOrEmpty(_intentMappingJson) || string.IsNullOrEmpty(intent))
        {
            return false;
        }
        
        string intentsSection = JsonHelper.ExtractObject(_intentMappingJson, "intents");
        if (string.IsNullOrEmpty(intentsSection))
        {
            return false;
        }
        
        string intentJson = JsonHelper.ExtractObject(intentsSection, intent);
        if (string.IsNullOrEmpty(intentJson))
        {
            return false;
        }
        
        string[] ops = JsonHelper.GetArray(intentJson, "operations");
        return ops.Length > 0;
    }
    
    /// <summary>
    /// 获取意图回退链
    /// </summary>
    private static string[] GetIntentFallbacks(string intent)
    {
        if (string.IsNullOrEmpty(_intentMappingJson) || string.IsNullOrEmpty(intent))
        {
            return new string[0];
        }
        
        string intentsSection = JsonHelper.ExtractObject(_intentMappingJson, "intents");
        string intentJson = JsonHelper.ExtractObject(intentsSection, intent);
        if (string.IsNullOrEmpty(intentJson))
        {
            return new string[0];
        }
        
        return JsonHelper.GetArray(intentJson, "fallback_intents");
    }
    
    /// <summary>
    /// 根据统一意图获取操作序列
    /// </summary>
    private static string[] GetOperationsByIntent(string intent, string currentPage)
    {
        if (string.IsNullOrEmpty(_intentMappingJson) || string.IsNullOrEmpty(intent))
        {
            return new string[0];
        }
        
        string intentsSection = JsonHelper.ExtractObject(_intentMappingJson, "intents");
        string intentJson = JsonHelper.ExtractObject(intentsSection, intent);
        if (string.IsNullOrEmpty(intentJson))
        {
            return new string[0];
        }
        
        string[] rawOps = JsonHelper.GetArray(intentJson, "operations");
        if (rawOps.Length == 0)
        {
            return new string[0];
        }
        
        var ops = new List<string>();
        for (int i = 0; i < rawOps.Length; i++)
        {
            string opName = rawOps[i];
            if (string.IsNullOrEmpty(opName)) continue;
            
            // 如果已经在详情页，跳过重复 open_post
            if (currentPage == "post_detail" && opName == "open_post")
            {
                continue;
            }
            
            // 过滤配置中不存在的操作，避免执行器报错
            string opDef = JsonHelper.ExtractObject(_operationsJson, opName);
            if (string.IsNullOrEmpty(opDef))
            {
                continue;
            }
            
            ops.Add(opName);
        }
        
        return ops.ToArray();
    }
    
    /// <summary>
    /// 判断当前意图是否需要发送人工提示
    /// </summary>
    private static bool ShouldNotifyHumanForIntent(string intent)
    {
        if (!_notifyHumanOnAiControl || string.IsNullOrEmpty(intent))
        {
            return false;
        }
        
        string aiControl = JsonHelper.ExtractObject(_userStrategyJson, "ai_control");
        if (string.IsNullOrEmpty(aiControl))
        {
            return true;
        }
        
        string[] notifyIntents = JsonHelper.GetArray(aiControl, "notify_intents");
        if (notifyIntents.Length == 0)
        {
            return true;
        }
        
        for (int i = 0; i < notifyIntents.Length; i++)
        {
            if (string.Equals(notifyIntents[i], intent, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        
        return false;
    }
    
    /// <summary>
    /// AI 直控执行时向人工发送提示（日志 + 变量）
    /// </summary>
    private static void NotifyHumanControl(string action, string intent, string platform)
    {
        string message = string.Format("[HUMAN_PROMPT] platform={0}, action={1}, intent={2}, mode=AI_DIRECT_EXECUTION",
            platform, action, intent);
        CoreHelper.Log(TAG, message);
        CoreHelper.SetVar("human_prompt_text", message);
        CoreHelper.SetVar("human_prompt_platform", platform);
        CoreHelper.SetVar("human_prompt_action", action);
        CoreHelper.SetVar("human_prompt_intent", intent);
    }
    
    /// <summary>
    /// 将 sessionAction + 当前页面映射为 Step DSL 字符串（ASCII | 分隔）。
    /// DSL 内容等同于 MapActionToOperations / GetOperationsByIntent 返回的操作序列，
    /// 以管道符拼接（例: "open_post|read_post|back_to_feed"）。
    /// 映射失败时返回空字符串 ""（绝不返回 DONE/END 等状态 token）。
    /// 
    /// 覆盖 actionTypes: browse, read_post, like, comment, post
    /// </summary>
    private static string MapActionToStepDSL(string sessionAction, string currentPage)
    {
        // 1. 优先走意图映射路径（与 DecideNextAction 主路径一致）
        string intent = ResolveIntentForAction(sessionAction);
        string effectiveIntent = ResolveIntentWithFallback(intent);
        string[] ops = GetOperationsByIntent(effectiveIntent, currentPage);
        
        // 2. 意图映射未命中时回退到硬编码操作映射
        if (ops.Length == 0)
        {
            ops = MapActionToOperations(sessionAction, currentPage);
        }
        
        // 3. 仍为空 — 无法生成 DSL，返回空字符串
        if (ops.Length == 0)
        {
            CoreHelper.Log(TAG, string.Format(
                "[StepDSL] 无法为 action={0}, page={1} 生成 DSL，写入空值",
                sessionAction, currentPage));
            return "";
        }
        
        // 4. 拼接为 | 分隔的 DSL 字符串
        string dsl = string.Join("|", ops);
        CoreHelper.Log(TAG, string.Format(
            "[StepDSL] action={0}, page={1} -> DSL={2}",
            sessionAction, currentPage, dsl));
        return dsl;
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
    
    private static void PrepareActionVariables(string action)
    {
        if (action == "comment" || action == "post")
        {
            EnsureCommentTextAvailable();
        }
    }

    private static void EnsureCommentTextAvailable()
    {
        string aiCommentText = CoreHelper.GetVar("ai_comment_text", "");
        if (!string.IsNullOrEmpty(aiCommentText))
        {
            if (string.IsNullOrEmpty(CoreHelper.GetVar("comment_text", "")))
            {
                CoreHelper.SetVar("comment_text", aiCommentText);
            }
            return;
        }

        string legacyCommentText = CoreHelper.GetVar("comment_text", "");
        if (!string.IsNullOrEmpty(legacyCommentText))
        {
            CoreHelper.SetVar("ai_comment_text", legacyCommentText);
            return;
        }

        string fallbackComment = BuildFallbackCommentText();
        CoreHelper.LogWarn(TAG, "评论文本缺失，使用回退评论文案");
        CoreHelper.SetVar("ai_comment_text", fallbackComment);
        CoreHelper.SetVar("comment_text", fallbackComment);
        // BUG-08 fix: ZD 变量可能因未预建而静默写入失败
        // 同时写入 ActionExecutor 静态上下文作为回退
        ActionExecutor.SetContextVariable("ai_comment_text", fallbackComment);
        ActionExecutor.SetContextVariable("comment_text", fallbackComment);
    }

    private static string BuildFallbackCommentText()
    {
        string title = NormalizeCommentSeed(ActionExecutor.GetContext("post_title"), 48);
        string body = NormalizeCommentSeed(ActionExecutor.GetContext("post_body"), 64);

        if (!string.IsNullOrEmpty(title))
        {
            return "Thanks for sharing about " + title + ".";
        }

        if (!string.IsNullOrEmpty(body))
        {
            return "Thanks for sharing this perspective.";
        }

        string[] fallbacks = new string[]
        {
            "Thanks for sharing this.",
            "Really appreciate this post.",
            "This was helpful to read."
        };
        return fallbacks[GetRandom().Next(0, fallbacks.Length)];
    }

    private static string NormalizeCommentSeed(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        string normalized = Regex.Replace(value, "\\s+", " ").Trim();
        if (normalized.Length > maxLength)
        {
            normalized = normalized.Substring(0, maxLength).Trim();
        }
        return normalized;
    }

    private static void UpdateCurrentPostContext()
    {
        string title = ActionExecutor.GetContext("post_title");
        string body = ActionExecutor.GetContext("post_body");
        string subreddit = ActionExecutor.GetContext("post_subreddit");
        string upvotes = ActionExecutor.GetContext("post_upvotes");
        string comments = ActionExecutor.GetContext("post_comment_count");
        string timestamp = ActionExecutor.GetContext("post_timestamp");
        string targetPost = ActionExecutor.GetContext("target_post");

        string postIdentifier = BuildCurrentPostIdentifier(title, body, subreddit, timestamp, targetPost);
        if (!string.IsNullOrEmpty(postIdentifier))
        {
            CoreHelper.SetVar("current_post_id", postIdentifier);
        }

        string postJson = BuildCurrentPostJson(title, body, subreddit, upvotes, comments, timestamp);
        if (!string.IsNullOrEmpty(postJson))
        {
            CoreHelper.SetVar("current_post_json", postJson);
        }
    }

    private static string BuildCurrentPostIdentifier(string title, string body, string subreddit, string timestamp, string targetPost)
    {
        StringBuilder builder = new StringBuilder();
        AppendIdentifierPart(builder, title, 48);
        AppendIdentifierPart(builder, body, 48);
        AppendIdentifierPart(builder, subreddit, 32);
        AppendIdentifierPart(builder, timestamp, 24);

        string identifierSource = builder.ToString();
        if (string.IsNullOrEmpty(identifierSource))
        {
            identifierSource = targetPost;
        }

        if (string.IsNullOrEmpty(identifierSource))
        {
            return "";
        }

        string normalized = Regex.Replace(identifierSource, "\\s+", "_");
        normalized = Regex.Replace(normalized, "[^a-zA-Z0-9_\\-]", "_");
        normalized = Regex.Replace(normalized, "_{2,}", "_").Trim('_');

        if (normalized.Length > 120)
        {
            normalized = normalized.Substring(0, 120).Trim('_');
        }

        return normalized;
    }

    private static void AppendIdentifierPart(StringBuilder builder, string value, int maxLength)
    {
        string normalized = NormalizeCommentSeed(value, maxLength);
        if (string.IsNullOrEmpty(normalized))
        {
            return;
        }

        if (builder.Length > 0)
        {
            builder.Append("|");
        }
        builder.Append(normalized);
    }

    private static string BuildCurrentPostJson(string title, string body, string subreddit, string upvotes, string comments, string timestamp)
    {
        bool hasSemantic = !string.IsNullOrEmpty(title)
            || !string.IsNullOrEmpty(body)
            || !string.IsNullOrEmpty(subreddit)
            || !string.IsNullOrEmpty(upvotes)
            || !string.IsNullOrEmpty(comments)
            || !string.IsNullOrEmpty(timestamp);

        if (!hasSemantic)
        {
            return "";
        }

        StringBuilder builder = new StringBuilder("{");
        bool hasField = false;

        hasField = AppendJsonStringField(builder, hasField, "title", title);
        hasField = AppendJsonStringField(builder, hasField, "body", body);
        hasField = AppendJsonStringField(builder, hasField, "subreddit", subreddit);
        hasField = AppendJsonNumericField(builder, hasField, "upvotes", upvotes);
        hasField = AppendJsonNumericField(builder, hasField, "comment_count", comments);
        hasField = AppendJsonStringField(builder, hasField, "timestamp", timestamp);

        builder.Append("}");
        return builder.ToString();
    }

    private static bool AppendJsonStringField(StringBuilder builder, bool hasField, string key, string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return hasField;
        }

        if (hasField)
        {
            builder.Append(",");
        }
        builder.Append("\"").Append(key).Append("\":\"").Append(JsonHelper.Escape(value)).Append("\"");
        return true;
    }

    private static bool AppendJsonNumericField(StringBuilder builder, bool hasField, string key, string value)
    {
        if (string.IsNullOrEmpty(value) || !Regex.IsMatch(value, "^\\d+$"))
        {
            return hasField;
        }

        if (hasField)
        {
            builder.Append(",");
        }
        builder.Append("\"").Append(key).Append("\":").Append(value);
        return true;
    }
    
    // ========== v4.5.12 三层恢复系统 ==========
    
    /// <summary>
    /// Layer 1: 检测目标 APP 是否在前台
    /// 通过 ZennoDroid App.Top API 获取顶层包名并比对
    /// </summary>
    private static bool IsAppForeground(string packageName)
    {
        if (string.IsNullOrEmpty(packageName)) return true;
        
        try
        {
            dynamic app = CoreHelper.GetApp();
            if (app == null) return true; // 无法检测时假定正常
            
            string topPackage = app.Top;
            if (string.IsNullOrEmpty(topPackage)) return true;
            
            return topPackage.Contains(packageName);
        }
        catch (Exception ex)
        {
            CoreHelper.LogWarn(TAG, "前台检测异常: " + ex.Message);
            return true; // 异常时假定正常，避免误触发恢复
        }
    }
    
    /// <summary>
    /// Layer 2: 检测系统遮挡物类型
    /// 通过 App.Top 包名 + UI XML 特征判断遮挡物类型
    /// 返回: share_sheet / permission_dialog / system_dialog / launcher / unknown_overlay / ""(无遮挡)
    /// </summary>
    private static string DetectOverlayType(string targetPackage)
    {
        try
        {
            dynamic app = CoreHelper.GetApp();
            if (app == null) return "";
            
            string topPackage = app.Top;
            if (string.IsNullOrEmpty(topPackage)) return "";
            if (topPackage.Contains(targetPackage)) return ""; // 无遮挡
            
            // 根据顶层包名判断遮挡物类型
            if (topPackage.Contains("com.android.internal.app")
                || topPackage.Contains("chooser")
                || topPackage.Contains("intentresolver"))
            {
                return "share_sheet";
            }
            
            if (topPackage.Contains("permissioncontroller")
                || topPackage.Contains("packageinstaller"))
            {
                return "permission_dialog";
            }
            
            if (topPackage.Contains("launcher"))
            {
                return "launcher";
            }
            
            if (topPackage == "android")
            {
                return "system_dialog";
            }
            
            // 包名无法判断时，尝试 XML 特征匹配
            string xml = CoreHelper.GetLayout();
            if (!string.IsNullOrEmpty(xml))
            {
                if (xml.Contains("resolver_list") || xml.Contains("ResolverActivity"))
                {
                    return "share_sheet";
                }
                if (xml.Contains("com.android.permissioncontroller") || xml.Contains("grant_permissions"))
                {
                    return "permission_dialog";
                }
                if (xml.Contains("alertTitle") || xml.Contains("android:id/message"))
                {
                    return "system_dialog";
                }
            }
            
            return "unknown_overlay";
        }
        catch (Exception ex)
        {
            CoreHelper.LogWarn(TAG, "遮挡物检测异常: " + ex.Message);
            return "";
        }
    }
    
    /// <summary>
    /// Layer 2: 关闭检测到的遮挡物
    /// share_sheet/permission_dialog/system_dialog → Back 键
    /// launcher → 重新启动 APP
    /// unknown_overlay → Back 键 + 重启
    /// </summary>
    private static bool DismissOverlay(string overlayType, string packageName)
    {
        dynamic input = CoreHelper.GetInput();
        if (input == null) return false;
        
        try
        {
            if (overlayType == "share_sheet" || overlayType == "permission_dialog"
                || overlayType == "system_dialog")
            {
                CoreHelper.Log(TAG, string.Format("[RECOVERY L2] Back 关闭遮挡物: {0}", overlayType));
                input.SendKeyCode(4); // KEYCODE_BACK
                System.Threading.Thread.Sleep(1500);
                return IsAppForeground(packageName);
            }
            
            if (overlayType == "launcher")
            {
                CoreHelper.Log(TAG, "[RECOVERY L2] APP 掉到桌面，重新启动");
                input.Shell("monkey -p " + packageName + " -c android.intent.category.LAUNCHER 1");
                System.Threading.Thread.Sleep(5000);
                return IsAppForeground(packageName);
            }
            
            if (overlayType == "unknown_overlay")
            {
                CoreHelper.Log(TAG, "[RECOVERY L2] 未知遮挡物，尝试 Back");
                input.SendKeyCode(4);
                System.Threading.Thread.Sleep(1500);
                if (IsAppForeground(packageName)) return true;
                
                CoreHelper.Log(TAG, "[RECOVERY L2] Back 无效，Home + 重启");
                input.SendKeyCode(3); // KEYCODE_HOME
                System.Threading.Thread.Sleep(1000);
                input.Shell("monkey -p " + packageName + " -c android.intent.category.LAUNCHER 1");
                System.Threading.Thread.Sleep(5000);
                return IsAppForeground(packageName);
            }
            
            return false;
        }
        catch (Exception ex)
        {
            CoreHelper.LogErr(TAG, "[RECOVERY L2] 关闭遮挡物异常: " + ex.Message);
            return false;
        }
    }
    
    /// <summary>
    /// v4.5.12: 操作后健康检查 — 三层恢复编排
    ///   Layer 1: 前台检测 (App.Top)
    ///   Layer 2: 遮挡物检测 + 自动 Back/重启 (DetectOverlayType + DismissOverlay)
    ///   Layer 2.5: 暴力恢复 (多次 Back + force-stop + 重启)
    ///   Layer 3: Vision 模型兜底 (VisionCorrector.CorrectWithRetry)
    /// </summary>
    /// <returns>true = APP 恢复到前台; false = 所有恢复层均失败</returns>
    private static bool PostOperationHealthCheck(string packageName, string operationName,
        string expectedPage, string projectRoot)
    {
        // Layer 1: 前台检测
        if (IsAppForeground(packageName))
        {
            return true; // 一切正常
        }
        
        CoreHelper.Log(TAG, string.Format("[RECOVERY] '{0}' -> APP 离开前台", operationName));
        
        // Layer 2: 检测并关闭遮挡物
        for (int attempt = 0; attempt < 3; attempt++)
        {
            string overlayType = DetectOverlayType(packageName);
            if (string.IsNullOrEmpty(overlayType))
            {
                // 无法确定遮挡物但 App 不在前台 — 可能延迟检测
                System.Threading.Thread.Sleep(1000);
                if (IsAppForeground(packageName)) return true;
                overlayType = "unknown_overlay";
            }
            
            CoreHelper.Log(TAG, string.Format(
                "[RECOVERY L2] {0}/{1} | 遮挡物: {2}", attempt + 1, 3, overlayType));
            
            if (DismissOverlay(overlayType, packageName))
            {
                CoreHelper.Log(TAG, "[RECOVERY L2] 遮挡物已清除，恢复成功");
                return true;
            }
        }
        
        // Layer 2.5: 暴力恢复 — 多次 Back + force-stop 重启
        CoreHelper.Log(TAG, "[RECOVERY L2.5] 遮挡物清除失败，暴力恢复");
        dynamic recoveryInput = CoreHelper.GetInput();
        if (recoveryInput != null)
        {
            // 连续 Back
            for (int i = 0; i < 5; i++)
            {
                recoveryInput.SendKeyCode(4);
                System.Threading.Thread.Sleep(800);
                if (IsAppForeground(packageName))
                {
                    CoreHelper.Log(TAG, string.Format("[RECOVERY L2.5] Back 第 {0} 次后恢复", i + 1));
                    return true;
                }
            }
            
            // Home + 重启
            recoveryInput.SendKeyCode(3);
            System.Threading.Thread.Sleep(1000);
            recoveryInput.Shell("monkey -p " + packageName + " -c android.intent.category.LAUNCHER 1");
            System.Threading.Thread.Sleep(5000);
            if (IsAppForeground(packageName))
            {
                CoreHelper.Log(TAG, "[RECOVERY L2.5] Home + 重启后恢复");
                return true;
            }
            
            // Force-stop + 重启
            recoveryInput.Shell("am force-stop " + packageName);
            System.Threading.Thread.Sleep(2000);
            recoveryInput.Shell("monkey -p " + packageName + " -c android.intent.category.LAUNCHER 1");
            System.Threading.Thread.Sleep(5000);
            if (IsAppForeground(packageName))
            {
                CoreHelper.Log(TAG, "[RECOVERY L2.5] Force-stop + 重启后恢复");
                return true;
            }
        }
        
        // Layer 3: Vision 模型兜底
        if (VisionCorrector.IsInitialized() && _visionRecoveryCount < MAX_VISION_RECOVERY_PER_SESSION)
        {
            CoreHelper.Log(TAG, "[RECOVERY L3] 启动 Vision 模型分析");
            _visionRecoveryCount++;
            
            string context = string.Format(
                "操作 '{0}' 执行后 APP 离开前台。多次自动恢复均失败。需要分析当前屏幕并恢复到 APP 的 {1} 页面。",
                operationName, expectedPage);
            
            bool visionRecovered = VisionCorrector.CorrectWithRetry(context, expectedPage, 3);
            if (visionRecovered)
            {
                CoreHelper.Log(TAG, "[RECOVERY L3] Vision 模型恢复成功");
                return true;
            }
        }
        else if (_visionRecoveryCount >= MAX_VISION_RECOVERY_PER_SESSION)
        {
            CoreHelper.LogWarn(TAG, string.Format(
                "[RECOVERY L3] Vision 调用次数已达上限 ({0})", MAX_VISION_RECOVERY_PER_SESSION));
        }
        
        CoreHelper.LogErr(TAG, string.Format(
            "[RECOVERY] 所有恢复层均失败: {0} (Vision 调用: {1}/{2})",
            operationName, _visionRecoveryCount, MAX_VISION_RECOVERY_PER_SESSION));
        return false;
    }
    
    // ========== 初始页面检测与剧本规划方法 ==========
    
    /// <summary>
    /// 强制导航回 feed 页面（导航恢复机制）
    /// 当连续多次 SKIP 时触发，依次尝试：
    ///   1. 点击 Reddit 返回按钮（fbp_back_button）
    ///   2. Android 返回键（多次按压）
    ///   3. 重启 APP（最后手段）
    /// </summary>
    private static void ForceNavigateToFeed(string platformName, string projectRoot)
    {
        CoreHelper.Log(TAG, "导航恢复: 开始强制返回 feed");
        
        string sigJson = JsonHelper.ExtractObject(_platformConfig, "page_signatures");
        string selectorsJson = JsonHelper.ExtractObject(_platformConfig, "ui_selectors");
        
        // 策略 1: 点击 Reddit 的 fbp_back_button（最可靠）
        string xml = CoreHelper.GetLayout();
        if (!string.IsNullOrEmpty(xml) && !string.IsNullOrEmpty(selectorsJson))
        {
            string backSelector = JsonHelper.ExtractObject(selectorsJson, "back_button");
            if (!string.IsNullOrEmpty(backSelector))
            {
                int[] center = SelectorEngine.FindCenter(xml, backSelector);
                if (center != null)
                {
                    CoreHelper.Log(TAG, "导航恢复: 找到返回按钮，点击");
                    dynamic input = CoreHelper.GetInput();
                    if (input != null)
                    {
                        input.Tap(center[0], center[1]);
                        System.Threading.Thread.Sleep(3000);
                        
                        // 检查是否成功
                        xml = CoreHelper.GetLayout();
                        if (!string.IsNullOrEmpty(xml) && !string.IsNullOrEmpty(sigJson))
                        {
                            _currentPage = PageDetector.Detect(xml, sigJson);
                            if (_currentPage == "feed")
                            {
                                CoreHelper.Log(TAG, "导航恢复成功: 已回到 feed");
                                CoreHelper.SetVar("current_page", _currentPage);
                                return;
                            }
                        }
                    }
                }
            }
        }
        
        // 策略 2: 连续按 Android 返回键（最多 5 次）
        dynamic backInput = CoreHelper.GetInput();
        if (backInput != null)
        {
            for (int i = 0; i < 5; i++)
            {
                CoreHelper.Log(TAG, string.Format("导航恢复: 按返回键 ({0}/5)", i + 1));
                backInput.Shell("input keyevent 4");
                System.Threading.Thread.Sleep(2000);
                
                xml = CoreHelper.GetLayout();
                if (!string.IsNullOrEmpty(xml) && !string.IsNullOrEmpty(sigJson))
                {
                    _currentPage = PageDetector.Detect(xml, sigJson);
                    if (_currentPage == "feed")
                    {
                        CoreHelper.Log(TAG, "导航恢复成功: 按返回键回到 feed");
                        CoreHelper.SetVar("current_page", _currentPage);
                        return;
                    }
                }
            }
        }
        
        // 策略 3: 如果还没回到 feed，重启 APP
        CoreHelper.Log(TAG, "导航恢复: 返回键无效，尝试重启 APP");
        string packageName = JsonHelper.Get(_platformConfig, "package_name");
        if (!string.IsNullOrEmpty(packageName) && backInput != null)
        {
            backInput.Shell("am force-stop " + packageName);
            System.Threading.Thread.Sleep(2000);
            backInput.Shell("monkey -p " + packageName + " -c android.intent.category.LAUNCHER 1");
            System.Threading.Thread.Sleep(5000);
            
            xml = CoreHelper.GetLayout();
            if (!string.IsNullOrEmpty(xml) && !string.IsNullOrEmpty(sigJson))
            {
                _currentPage = PageDetector.Detect(xml, sigJson);
                CoreHelper.SetVar("current_page", _currentPage);
                CoreHelper.Log(TAG, "导航恢复: APP 重启后页面: " + _currentPage);
            }
        }
        else
        {
            CoreHelper.LogWarn(TAG, "导航恢复: 无法重启 APP（缺少 package_name 或 input）");
        }
    }
    
    /// <summary>
    /// 检测 APP 启动时当前所处页面
    /// 调用 PageDetector.Detect() 识别页面类型（feed/post_detail/comment/profile/unknown）
    /// </summary>
    private static string DetectInitialPage()
    {
        try
        {
            string xml = CoreHelper.GetLayout();
            if (string.IsNullOrEmpty(xml))
            {
                CoreHelper.LogWarn(TAG, "初始页面检测: 无法获取 UI 布局，默认 unknown");
                return "unknown";
            }
            
            string signaturesJson = JsonHelper.ExtractObject(_platformConfig, "page_signatures");
            if (string.IsNullOrEmpty(signaturesJson))
            {
                CoreHelper.LogWarn(TAG, "初始页面检测: 平台配置缺少 page_signatures，默认 unknown");
                return "unknown";
            }
            
            string detected = PageDetector.Detect(xml, signaturesJson);
            CoreHelper.SetVar("current_page", detected);
            return detected;
        }
        catch (Exception ex)
        {
            CoreHelper.LogErr(TAG, "初始页面检测失败: " + ex.Message);
            return "unknown";
        }
    }
    
    /// <summary>
    /// 根据检测到的当前页面状态，规划预设动作序列（剧本）
    /// - feed: 正常会话，无需预设动作
    /// - post_detail: 先阅读当前帖子，再返回首页
    /// - 其他页面: 先返回首页
    /// - unknown: 尝试返回首页
    /// </summary>
    private static List<string> PlanPreSessionActions(string currentPage)
    {
        List<string> actions = new List<string>();
        
        if (currentPage == "post_detail")
        {
            // 当前在帖子详情页 — 先完成帖子交互再回到首页
            CoreHelper.Log(TAG, "剧本规划: 当前在帖子详情页，先阅读帖子再返回首页");
            string readOp = GetReadOperationName();
            // 仅在操作存在时添加
            string readDef = JsonHelper.ExtractObject(_operationsJson, readOp);
            if (!string.IsNullOrEmpty(readDef))
            {
                actions.Add(readOp);
            }
            string backDef = JsonHelper.ExtractObject(_operationsJson, "back_to_feed");
            if (!string.IsNullOrEmpty(backDef))
            {
                actions.Add("back_to_feed");
            }
        }
        else if (currentPage == "feed")
        {
            // 当前在首页 — 正常流程，无需预设动作
            // 不添加任何动作
        }
        else if (currentPage == "unknown")
        {
            // 未知页面 — 尝试返回首页
            CoreHelper.Log(TAG, "剧本规划: 当前在未知页面，尝试返回首页");
            string backDef = JsonHelper.ExtractObject(_operationsJson, "back_to_feed");
            if (!string.IsNullOrEmpty(backDef))
            {
                actions.Add("back_to_feed");
            }
        }
        else
        {
            // 其他已知页面（profile, comment 等）— 返回首页
            CoreHelper.Log(TAG, string.Format("剧本规划: 当前在 {0} 页面，返回首页继续", currentPage));
            string backDef = JsonHelper.ExtractObject(_operationsJson, "back_to_feed");
            if (!string.IsNullOrEmpty(backDef))
            {
                actions.Add("back_to_feed");
            }
        }
        
        return actions;
    }
    
    /// <summary>
    /// 执行预设动作序列（初始剧本）
    /// 在主循环前将 APP 导航到正确的页面状态
    /// </summary>
    private static void ExecutePreSessionActions(List<string> actions, string platformName, string projectRoot)
    {
        for (int i = 0; i < actions.Count; i++)
        {
            string opName = actions[i];
            CoreHelper.Log(TAG, string.Format("执行初始剧本: {0} ({1}/{2})", opName, i + 1, actions.Count));
            
            // 确保操作在 operations JSON 中存在
            string opDef = JsonHelper.ExtractObject(_operationsJson, opName);
            if (string.IsNullOrEmpty(opDef))
            {
                CoreHelper.LogWarn(TAG, "初始剧本操作不存在，跳过: " + opName);
                continue;
            }
            
            try
            {
                string result = ActionExecutor.Execute(_operationsJson, opName, _platformConfig);
                CoreHelper.Log(TAG, string.Format("初始剧本结果: {0} = {1}", opName, result));
            }
            catch (Exception ex)
            {
                CoreHelper.LogErr(TAG, string.Format("初始剧本执行异常: {0} - {1}", opName, ex.Message));
            }
            
            // 执行后更新页面状态
            try
            {
                string xml = CoreHelper.GetLayout();
                if (!string.IsNullOrEmpty(xml))
                {
                    string sigJson = JsonHelper.ExtractObject(_platformConfig, "page_signatures");
                    if (!string.IsNullOrEmpty(sigJson))
                    {
                        _currentPage = PageDetector.Detect(xml, sigJson);
                        CoreHelper.SetVar("current_page", _currentPage);
                    }
                }
            }
            catch (Exception ex)
            {
                CoreHelper.LogWarn(TAG, "初始剧本页面检测失败: " + ex.Message);
            }
            
            // 短暂延迟，模拟人类行为
            System.Threading.Thread.Sleep(GetRandom().Next(800, 2000));
        }
        
        // 更新帖子上下文（如果在 post_detail 页执行了阅读操作）
        UpdateCurrentPostContext();
    }
    
    // ========== 自动接入新平台 (v4.5.11) ==========
    
    /// <summary>
    /// 从 device_app_mapping.json 获取平台对应的包名
    /// 如果映射中没有 package_name，尝试从 Apps.json 查找
    /// </summary>
    private static string GetPackageNameForPlatform(string projectRoot, string platformName)
    {
        // 1. 从 device_app_mapping.json 查找（如果有 package_name 字段）
        string mappingPath = projectRoot + "Config\\device_app_mapping.json";
        if (File.Exists(mappingPath))
        {
            try
            {
                string mappingJson = CoreHelper.ReadFile(mappingPath);
                string devicesSection = JsonHelper.ExtractObject(mappingJson, "devices");
                // 遍历所有设备找到匹配平台的包名
                // 简单方式: 检查 platforms 别名映射
                string platformsMapping = JsonHelper.ExtractObject(mappingJson, "platform_packages");
                if (!string.IsNullOrEmpty(platformsMapping))
                {
                    string pkg = JsonHelper.Get(platformsMapping, platformName);
                    if (!string.IsNullOrEmpty(pkg))
                    {
                        return pkg;
                    }
                }
            }
            catch (Exception)
            {
                // 忽略解析错误
            }
        }
        
        // 2. 从 Apps.json 查找
        string appsPath = projectRoot + "Config\\Apps.json";
        if (File.Exists(appsPath))
        {
            try
            {
                string appsJson = CoreHelper.ReadFile(appsPath);
                // Apps.json 结构: { "apps": { "reddit": { "package_name": "..." } } }
                string appsSection = JsonHelper.ExtractObject(appsJson, "apps");
                if (!string.IsNullOrEmpty(appsSection))
                {
                    string appSection = JsonHelper.ExtractObject(appsSection, platformName);
                    if (!string.IsNullOrEmpty(appSection))
                    {
                        string pkg = JsonHelper.Get(appSection, "package_name");
                        if (!string.IsNullOrEmpty(pkg))
                        {
                            return pkg;
                        }
                    }
                }
            }
            catch (Exception)
            {
                // 忽略解析错误
            }
        }
        
        // 3. 从 PlatformsConfig.json 中已有的同名平台查找（可能部分配置）
        // 4. 尝试使用 ZennoDroid 变量
        string varPkg = CoreHelper.GetVar("onboard_package_name", "");
        if (!string.IsNullOrEmpty(varPkg))
        {
            return varPkg;
        }
        
        // 5. 常见平台包名映射（硬编码兜底）
        if (platformName == "reddit") return "com.reddit.frontpage";
        if (platformName == "instagram") return "com.instagram.android";
        if (platformName == "babycenter") return "com.babycenter.pregnancytracker";
        if (platformName == "tiktok") return "com.ss.android.ugc.trill";
        
        return "";
    }
    
    /// <summary>
    /// 调用 Python App Onboarder 自动探索并生成平台配置
    /// 使用 System.Diagnostics.Process 运行 Python 脚本
    /// 注意: 需要先释放 ZennoDroid 对 uiautomator 的占用
    /// </summary>
    private static bool RunAppOnboarder(string projectRoot, string packageName, string platformName)
    {
        try
        {
            string toolPath = projectRoot + "Tools\\app_onboarder\\main.py";
            if (!File.Exists(toolPath))
            {
                CoreHelper.LogErr(TAG, "App Onboarder 工具不存在: " + toolPath);
                return false;
            }
            
            // 释放 ZennoDroid 对 uiautomator 的占用
            // ZennoDroid 的 Hierarchy.GetLayout() 会启动 uiautomator 守护进程
            // 需要在调用 app_onboarder 前释放，否则 uiautomator dump 会被 kill
            CoreHelper.Log(TAG, "释放 ZennoDroid Hierarchy 控制（允许 uiautomator dump）...");
            try
            {
                dynamic input = CoreHelper.GetInput();
                if (input != null)
                {
                    // 通过 Shell 命令 kill 掉 ZennoDroid 的 uiautomator 服务进程
                    input.Shell("am force-stop com.github.nicoco007.cmdserver 2>/dev/null");
                    input.Shell("killall -9 uiautomator 2>/dev/null");
                    System.Threading.Thread.Sleep(2000);
                }
            }
            catch (Exception ex)
            {
                CoreHelper.LogWarn(TAG, "释放 Hierarchy 控制失败（可能影响 dump）: " + ex.Message);
            }
            
            // 获取设备 ADB serial（从 ZennoDroid 实例）
            string adbDevice = "";
            try
            {
                dynamic droid = CoreHelper.GetDroid();
                if (droid != null)
                {
                    adbDevice = droid.Serial;
                }
            }
            catch (Exception)
            {
                // Serial 不可用，app_onboarder 将使用默认设备
            }
            
            // 构建命令行参数
            // --yes: 非交互模式自动确认
            // --skip-test: 跳过 E2E 测试（在 SessionRunner 环境中无需测试）
            // --enable-vision: 启用 Vision AI 发现 APP 特有功能
            string arguments = string.Format(
                "\"{0}\" --package {1} --key {2} --skip-test -y --enable-vision",
                toolPath, packageName, platformName
            );
            
            // 如果有设备 serial，传给 app_onboarder
            if (!string.IsNullOrEmpty(adbDevice))
            {
                arguments += " --device " + adbDevice;
            }
            
            CoreHelper.Log(TAG, string.Format("启动 App Onboarder: python {0}", arguments));
            
            System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo();
            psi.FileName = "python";
            psi.Arguments = arguments;
            psi.WorkingDirectory = projectRoot + "Tools\\app_onboarder";
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;
            
            // 设置 UTF-8 编码
            psi.StandardOutputEncoding = System.Text.Encoding.UTF8;
            psi.StandardErrorEncoding = System.Text.Encoding.UTF8;
            
            System.Diagnostics.Process proc = System.Diagnostics.Process.Start(psi);
            
            // 异步读取输出避免死锁
            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            
            // 等待进程完成（最多 5 分钟）
            bool exited = proc.WaitForExit(300000);
            
            if (!exited)
            {
                CoreHelper.LogErr(TAG, "App Onboarder 超时（5分钟），强制终止");
                try { proc.Kill(); } catch (Exception) { }
                return false;
            }
            
            int exitCode = proc.ExitCode;
            
            // 记录输出到日志（截取关键部分）
            if (!string.IsNullOrEmpty(stdout))
            {
                // 只记录最后 2000 字符避免日志过长
                string logOutput = stdout.Length > 2000
                    ? "...(truncated)..." + stdout.Substring(stdout.Length - 2000)
                    : stdout;
                CoreHelper.Log(TAG, "App Onboarder 输出:\n" + logOutput);
            }
            if (!string.IsNullOrEmpty(stderr))
            {
                CoreHelper.LogWarn(TAG, "App Onboarder stderr:\n" + stderr);
            }
            
            if (exitCode != 0)
            {
                CoreHelper.LogErr(TAG, string.Format("App Onboarder 退出码: {0}", exitCode));
                return false;
            }
            
            // 验证配置文件是否已生成
            string opsPath = projectRoot + "Config\\Operations\\" + platformName + "_operations.json";
            if (!File.Exists(opsPath))
            {
                CoreHelper.LogWarn(TAG, "App Onboarder 完成但操作文件未生成: " + opsPath);
                // 配置文件可能已合并到 PlatformsConfig.json，不算失败
            }
            
            CoreHelper.Log(TAG, "App Onboarder 自动接入完成: " + platformName);
            
            // 延迟 3 秒让 ZennoDroid 重新获取 Hierarchy 控制权
            System.Threading.Thread.Sleep(3000);
            
            return true;
        }
        catch (Exception ex)
        {
            CoreHelper.LogErr(TAG, "App Onboarder 调用异常: " + ex.Message);
            return false;
        }
    }
}
