using System;
using System.Collections.Generic;

/// <summary>
/// 智能编排器 - SessionRunner 内部子模块
/// 
/// 职责:
/// 1. 双层成功判定: ZD 判执行成功 + 编排器判业务成功
/// 2. 分级恢复决策: retry -> 局部恢复 -> AI 视觉 -> 备用剧本 -> 终止
/// 3. 操作间状态追踪: 连续失败计数、恢复预算、升级阈值
/// 
/// 设计原则:
/// - 先内嵌于 SessionRunner，成熟后可独立抽取 (ADR-015)
/// - 不直接执行物理动作，只做判定和决策
/// - 不写回全局长期规则，修正仅限当前 session
/// 
/// C# 5.0 兼容: 无 $"", ?., nameof()
/// </summary>
public class SmartOrchestrator
{
    private const string TAG = "SmartOrchestrator";
    
    // ==================== 恢复级别枚举 ====================
    
    /// <summary>
    /// 分级恢复级别，按升级顺序排列
    /// </summary>
    public enum RecoveryLevel
    {
        /// <summary>第 1 级: 简单重试同一操作</summary>
        Retry = 0,
        /// <summary>第 2 级: 回退到安全页面后重试</summary>
        LocalRecovery = 1,
        /// <summary>第 3 级: AI 视觉识别当前状态并提供纠偏建议</summary>
        VisionAssist = 2,
        /// <summary>第 4 级: 切换到备用操作序列</summary>
        FallbackScript = 3,
        /// <summary>第 5 级: 放弃当前操作，上报 SessionRunner</summary>
        Abort = 4
    }
    
    /// <summary>
    /// 操作最终裁决（双层判定结果）
    /// </summary>
    public enum OperationVerdict
    {
        /// <summary>执行成功 + 业务成功</summary>
        Success,
        /// <summary>ZD 报告执行失败</summary>
        ExecutionFailed,
        /// <summary>ZD 执行成功但页面状态未达预期（假成功）</summary>
        BusinessFailed,
        /// <summary>操作被跳过</summary>
        Skipped,
        /// <summary>不可恢复，已中止</summary>
        Aborted
    }
    
    // ==================== 恢复预算配置 ====================
    
    private int _maxRetries = 2;
    private int _maxLocalRecoveries = 2;
    private int _maxVisionAssists = 3;
    private int _maxFallbackAttempts = 1;
    
    // ==================== 运行时状态 ====================
    
    // 当前操作的连续失败计数（每次成功或换操作时重置）
    private int _consecutiveFailures = 0;
    
    // 当前操作各级别已使用次数
    private int _retryCount = 0;
    private int _localRecoveryCount = 0;
    private int _visionAssistCount = 0;
    private int _fallbackAttemptCount = 0;
    
    // 会话级统计
    private int _totalRecoveries = 0;
    private int _totalVisionCalls = 0;
    private int _totalFalseSuccesses = 0;
    
    // 当前恢复级别
    private RecoveryLevel _currentLevel = RecoveryLevel.Retry;
    
    // 最近一次判定的诊断信息
    private string _lastDiagnostics = "";
    
    // ==================== 初始化 ====================
    
    /// <summary>
    /// 使用默认恢复预算创建编排器
    /// </summary>
    public SmartOrchestrator()
    {
    }
    
    /// <summary>
    /// 从 JSON 配置加载恢复预算
    /// 配置路径: BehaviorConfig.json -> smart_orchestrator
    /// </summary>
    public void LoadConfig(string behaviorConfigJson)
    {
        if (string.IsNullOrEmpty(behaviorConfigJson))
        {
            CoreHelper.Log(TAG, "使用默认恢复预算配置");
            return;
        }
        
        string orchConfig = JsonHelper.ExtractObject(behaviorConfigJson, "smart_orchestrator");
        if (string.IsNullOrEmpty(orchConfig))
        {
            CoreHelper.Log(TAG, "BehaviorConfig 中无 smart_orchestrator 节，使用默认值");
            return;
        }
        
        _maxRetries = JsonHelper.GetInt(orchConfig, "max_retries", 2);
        _maxLocalRecoveries = JsonHelper.GetInt(orchConfig, "max_local_recoveries", 2);
        _maxVisionAssists = JsonHelper.GetInt(orchConfig, "max_vision_assists", 3);
        _maxFallbackAttempts = JsonHelper.GetInt(orchConfig, "max_fallback_attempts", 1);
        
        CoreHelper.Log(TAG, string.Format(
            "恢复预算: retry={0}, localRecovery={1}, vision={2}, fallback={3}",
            _maxRetries, _maxLocalRecoveries, _maxVisionAssists, _maxFallbackAttempts));
    }
    
    // ==================== 核心: 双层成功判定 ====================
    
    /// <summary>
    /// 对一次操作执行双层成功判定
    /// 
    /// 第 1 层（执行成功）: ActionExecutor 返回的 result 是否为 SUCCESS
    /// 第 2 层（业务成功）: 操作后页面状态是否符合预期
    /// 
    /// 参数:
    ///   executionResult - ActionExecutor.Execute() 的返回值
    ///   operationName   - 当前操作名称
    ///   expectedPage    - 操作完成后预期的页面类型（可为空，表示不检查）
    ///   actualPage      - 操作完成后实际检测到的页面类型
    ///   platformConfig  - 平台配置 JSON（用于读取 page_signatures）
    /// 
    /// 返回: OperationVerdict 枚举
    /// </summary>
    public OperationVerdict EvaluateResult(
        string executionResult,
        string operationName,
        string expectedPage,
        string actualPage,
        string platformConfig)
    {
        // 第 1 层: 执行成功判定
        if (string.IsNullOrEmpty(executionResult))
        {
            _lastDiagnostics = string.Format("[{0}] 执行结果为空", operationName);
            return OperationVerdict.ExecutionFailed;
        }
        
        if (executionResult.StartsWith("ERROR") || executionResult.StartsWith("ABORT"))
        {
            _lastDiagnostics = string.Format("[{0}] 执行失败: {1}", operationName, executionResult);
            return OperationVerdict.ExecutionFailed;
        }
        
        if (executionResult.StartsWith("SKIP"))
        {
            _lastDiagnostics = string.Format("[{0}] 操作跳过: {1}", operationName, executionResult);
            return OperationVerdict.Skipped;
        }
        
        // 第 2 层: 业务成功判定
        // 只有当 expectedPage 非空时才检查页面状态
        if (!string.IsNullOrEmpty(expectedPage) && !string.IsNullOrEmpty(actualPage))
        {
            if (actualPage != expectedPage && actualPage != "unknown")
            {
                _lastDiagnostics = string.Format(
                    "[{0}] 假成功: 执行返回 SUCCESS 但页面状态不符, 预期={1}, 实际={2}",
                    operationName, expectedPage, actualPage);
                _totalFalseSuccesses++;
                return OperationVerdict.BusinessFailed;
            }
        }
        
        _lastDiagnostics = string.Format("[{0}] 双层判定通过", operationName);
        return OperationVerdict.Success;
    }
    
    // ==================== 核心: 分级恢复决策 ====================
    
    /// <summary>
    /// 根据当前失败状态决定应该采取哪个恢复级别
    /// 
    /// 升级链:
    ///   第 1 次失败 -> Retry（简单重试）
    ///   第 2-3 次失败 -> LocalRecovery（回退安全页 + 重试）
    ///   第 4-5 次失败 -> VisionAssist（AI 视觉识别 + 纠偏）
    ///   第 6 次失败 -> FallbackScript（备用操作序列）
    ///   超出预算 -> Abort
    /// 
    /// 返回: 建议的 RecoveryLevel
    /// </summary>
    public RecoveryLevel DecideRecovery()
    {
        // 阶段 1: 简单重试
        if (_retryCount < _maxRetries)
        {
            _currentLevel = RecoveryLevel.Retry;
            CoreHelper.Log(TAG, string.Format(
                "恢复决策: Retry ({0}/{1})", _retryCount + 1, _maxRetries));
            return RecoveryLevel.Retry;
        }
        
        // 阶段 2: 局部恢复
        if (_localRecoveryCount < _maxLocalRecoveries)
        {
            _currentLevel = RecoveryLevel.LocalRecovery;
            CoreHelper.Log(TAG, string.Format(
                "恢复决策: LocalRecovery ({0}/{1})", _localRecoveryCount + 1, _maxLocalRecoveries));
            return RecoveryLevel.LocalRecovery;
        }
        
        // 阶段 3: AI 视觉
        if (_visionAssistCount < _maxVisionAssists)
        {
            _currentLevel = RecoveryLevel.VisionAssist;
            CoreHelper.Log(TAG, string.Format(
                "恢复决策: VisionAssist ({0}/{1})", _visionAssistCount + 1, _maxVisionAssists));
            return RecoveryLevel.VisionAssist;
        }
        
        // 阶段 4: 备用剧本
        if (_fallbackAttemptCount < _maxFallbackAttempts)
        {
            _currentLevel = RecoveryLevel.FallbackScript;
            CoreHelper.Log(TAG, string.Format(
                "恢复决策: FallbackScript ({0}/{1})", _fallbackAttemptCount + 1, _maxFallbackAttempts));
            return RecoveryLevel.FallbackScript;
        }
        
        // 阶段 5: 中止
        _currentLevel = RecoveryLevel.Abort;
        CoreHelper.Log(TAG, "恢复决策: Abort - 所有恢复预算已耗尽");
        return RecoveryLevel.Abort;
    }
    
    // ==================== 状态更新 ====================
    
    /// <summary>
    /// 记录一次操作成功，重置当前操作的失败计数
    /// </summary>
    public void RecordSuccess()
    {
        _consecutiveFailures = 0;
        _retryCount = 0;
        _localRecoveryCount = 0;
        _visionAssistCount = 0;
        _fallbackAttemptCount = 0;
        _currentLevel = RecoveryLevel.Retry;
    }
    
    /// <summary>
    /// 记录一次操作失败，并按当前恢复级别累计对应计数器
    /// </summary>
    public void RecordFailure()
    {
        _consecutiveFailures++;
        _totalRecoveries++;
        
        switch (_currentLevel)
        {
            case RecoveryLevel.Retry:
                _retryCount++;
                break;
            case RecoveryLevel.LocalRecovery:
                _localRecoveryCount++;
                break;
            case RecoveryLevel.VisionAssist:
                _visionAssistCount++;
                _totalVisionCalls++;
                break;
            case RecoveryLevel.FallbackScript:
                _fallbackAttemptCount++;
                break;
        }
    }
    
    /// <summary>
    /// 切换到新操作时重置当前操作的恢复计数（但保留会话级统计）
    /// </summary>
    public void ResetForNewOperation()
    {
        _consecutiveFailures = 0;
        _retryCount = 0;
        _localRecoveryCount = 0;
        _visionAssistCount = 0;
        _fallbackAttemptCount = 0;
        _currentLevel = RecoveryLevel.Retry;
    }
    
    /// <summary>
    /// 会话开始时完全重置所有状态
    /// </summary>
    public void ResetAll()
    {
        ResetForNewOperation();
        _totalRecoveries = 0;
        _totalVisionCalls = 0;
        _totalFalseSuccesses = 0;
        _lastDiagnostics = "";
    }
    
    // ==================== 查询接口 ====================
    
    /// <summary>
    /// 当前操作是否应该中止（所有恢复预算耗尽）
    /// </summary>
    public bool ShouldAbort()
    {
        return _currentLevel == RecoveryLevel.Abort;
    }
    
    /// <summary>
    /// 获取当前恢复级别
    /// </summary>
    public RecoveryLevel GetCurrentLevel()
    {
        return _currentLevel;
    }
    
    /// <summary>
    /// 获取当前操作的连续失败次数
    /// </summary>
    public int GetConsecutiveFailures()
    {
        return _consecutiveFailures;
    }
    
    /// <summary>
    /// 获取最近一次判定的诊断信息
    /// </summary>
    public string GetLastDiagnostics()
    {
        return _lastDiagnostics;
    }
    
    /// <summary>
    /// 获取会话级恢复统计摘要
    /// </summary>
    public string GetSessionSummary()
    {
        return string.Format(
            "恢复总次数={0}, 视觉调用={1}, 假成功检出={2}",
            _totalRecoveries, _totalVisionCalls, _totalFalseSuccesses);
    }
    
    /// <summary>
    /// 获取会话级假成功检出数
    /// </summary>
    public int GetFalseSuccessCount()
    {
        return _totalFalseSuccesses;
    }
    
    /// <summary>
    /// 获取会话级视觉调用总数
    /// </summary>
    public int GetTotalVisionCalls()
    {
        return _totalVisionCalls;
    }
    
    // ==================== 辅助: 从 operation 定义提取预期页面 ====================
    
    /// <summary>
    /// 从 operation JSON 定义中提取操作完成后预期到达的页面
    /// 
    /// 查找优先级:
    /// 1. steps 中最后一个 require action 的 page 字段
    /// 2. operation 级别的 result_page 字段
    /// 3. 返回空字符串（表示不做业务判定）
    /// </summary>
    public static string ExtractExpectedPage(string operationDef)
    {
        if (string.IsNullOrEmpty(operationDef))
        {
            return "";
        }
        
        // 优先查找 result_page 字段
        string resultPage = JsonHelper.Get(operationDef, "result_page");
        if (!string.IsNullOrEmpty(resultPage))
        {
            return resultPage;
        }
        
        // 否则返回空，表示不做业务页面判定
        return "";
    }
    
    // ==================== 状态序列化（跨 C# code cube 传递） ====================
    
    /// <summary>
    /// 将当前所有运行时状态序列化为 JSON 字符串
    /// 用于跨 ZD C# code cube 传递编排器状态（InitSession → DecideNextAction → EvaluateResult → Finalize）
    /// C# 5.0 兼容: 手动拼接 JSON
    /// </summary>
    public string SaveState()
    {
        return "{"
            + "\"cf\":" + _consecutiveFailures.ToString()
            + ",\"rc\":" + _retryCount.ToString()
            + ",\"lrc\":" + _localRecoveryCount.ToString()
            + ",\"vac\":" + _visionAssistCount.ToString()
            + ",\"fac\":" + _fallbackAttemptCount.ToString()
            + ",\"tr\":" + _totalRecoveries.ToString()
            + ",\"tvc\":" + _totalVisionCalls.ToString()
            + ",\"tfs\":" + _totalFalseSuccesses.ToString()
            + ",\"cl\":" + ((int)_currentLevel).ToString()
            + ",\"mr\":" + _maxRetries.ToString()
            + ",\"mlr\":" + _maxLocalRecoveries.ToString()
            + ",\"mva\":" + _maxVisionAssists.ToString()
            + ",\"mfa\":" + _maxFallbackAttempts.ToString()
            + "}";
    }
    
    /// <summary>
    /// 从 JSON 字符串恢复所有运行时状态
    /// 在 DecideNextAction / EvaluateResult / Finalize 的入口处调用
    /// </summary>
    public void LoadState(string json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return;
        }
        
        _consecutiveFailures = JsonHelper.GetInt(json, "cf", 0);
        _retryCount = JsonHelper.GetInt(json, "rc", 0);
        _localRecoveryCount = JsonHelper.GetInt(json, "lrc", 0);
        _visionAssistCount = JsonHelper.GetInt(json, "vac", 0);
        _fallbackAttemptCount = JsonHelper.GetInt(json, "fac", 0);
        _totalRecoveries = JsonHelper.GetInt(json, "tr", 0);
        _totalVisionCalls = JsonHelper.GetInt(json, "tvc", 0);
        _totalFalseSuccesses = JsonHelper.GetInt(json, "tfs", 0);
        int levelInt = JsonHelper.GetInt(json, "cl", 0);
        if (levelInt >= 0 && levelInt <= 4)
        {
            _currentLevel = (RecoveryLevel)levelInt;
        }
        else
        {
            _currentLevel = RecoveryLevel.Retry;
        }
        _maxRetries = JsonHelper.GetInt(json, "mr", 2);
        _maxLocalRecoveries = JsonHelper.GetInt(json, "mlr", 2);
        _maxVisionAssists = JsonHelper.GetInt(json, "mva", 3);
        _maxFallbackAttempts = JsonHelper.GetInt(json, "mfa", 1);
    }
}
