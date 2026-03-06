// =====================================================
// ZDResult.cs - ZennoDroid 执行结果类
// ⚠️ C# 5.0 语法 - 禁止使用 $""、?.、nameof() 等
// =====================================================
// v4.5.2 架构重构 - DPS "视觉反馈"层
//
// ZDResult 是 ZennoDroidAdapter 执行命令后的反馈，
// 包含成功/失败状态、错误信息、截图等。
//
// 设计原则：
//   1. 所有执行结果统一通过 ZDResult 返回
//   2. 支持视觉验证数据（截图、新布局）
//   3. 便于 VisionCorrector 进行视觉验证
// =====================================================

using System;

/// <summary>
/// 执行结果状态
/// </summary>
public enum ZDResultStatus
{
    /// <summary>执行成功</summary>
    Success,
    /// <summary>执行失败（可重试）</summary>
    FailedRetryable,
    /// <summary>执行失败（不可重试）</summary>
    FailedFatal,
    /// <summary>跳过（条件不满足）</summary>
    Skipped
}

/// <summary>
/// ZennoDroid 执行结果类
/// </summary>
public class ZDResult
{
    private const string TAG = "ZDResult";

    // ========== 属性 ==========

    /// <summary>
    /// 执行状态
    /// </summary>
    public ZDResultStatus Status { get; set; }

    /// <summary>
    /// 错误消息（失败时）
    /// </summary>
    public string ErrorMessage { get; set; }

    /// <summary>
    /// 异常对象（如果有）
    /// </summary>
    public Exception Exception { get; set; }

    /// <summary>
    /// 执行时间戳
    /// </summary>
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// 执行耗时（毫秒）
    /// </summary>
    public long ElapsedMilliseconds { get; set; }

    /// <summary>
    /// 执行后的 UI 布局 XML（用于验证）
    /// </summary>
    public string AfterLayoutXml { get; set; }

    /// <summary>
    /// 执行后的截图（Base64 或文件路径）
    /// </summary>
    public string ScreenshotData { get; set; }

    /// <summary>
    /// 尝试次数
    /// </summary>
    public int AttemptCount { get; set; }

    /// <summary>
    /// 扩展数据（键值对）
    /// </summary>
    public System.Collections.Generic.Dictionary<string, string> ExtraData { get; set; }

    // ========== 构造函数 ==========

    /// <summary>
    /// 默认构造函数
    /// </summary>
    public ZDResult()
    {
        Status = ZDResultStatus.Success;
        ErrorMessage = "";
        Timestamp = DateTime.Now;
        ElapsedMilliseconds = 0;
        AfterLayoutXml = "";
        ScreenshotData = "";
        AttemptCount = 1;
        ExtraData = new System.Collections.Generic.Dictionary<string, string>();
    }

    // ========== 工厂方法 ==========

    /// <summary>
    /// 创建成功结果
    /// </summary>
    public static ZDResult Success(long elapsedMs)
    {
        ZDResult result = new ZDResult();
        result.Status = ZDResultStatus.Success;
        result.ElapsedMilliseconds = elapsedMs;
        return result;
    }

    /// <summary>
    /// 创建失败结果（可重试）
    /// </summary>
    public static ZDResult FailedRetryable(string errorMessage)
    {
        ZDResult result = new ZDResult();
        result.Status = ZDResultStatus.FailedRetryable;
        result.ErrorMessage = errorMessage;
        return result;
    }

    /// <summary>
    /// 创建失败结果（不可重试）
    /// </summary>
    public static ZDResult FailedFatal(string errorMessage)
    {
        ZDResult result = new ZDResult();
        result.Status = ZDResultStatus.FailedFatal;
        result.ErrorMessage = errorMessage;
        return result;
    }

    /// <summary>
    /// 创建跳过结果
    /// </summary>
    public static ZDResult Skipped(string reason)
    {
        ZDResult result = new ZDResult();
        result.Status = ZDResultStatus.Skipped;
        result.ErrorMessage = reason;
        return result;
    }

    /// <summary>
    /// 创建异常结果
    /// </summary>
    public static ZDResult FromException(Exception ex)
    {
        ZDResult result = new ZDResult();
        result.Status = ZDResultStatus.FailedRetryable;
        result.ErrorMessage = ex.Message;
        result.Exception = ex;
        return result;
    }

    // ========== 判断方法 ==========

    /// <summary>
    /// 是否成功
    /// </summary>
    public bool IsSuccess()
    {
        return Status == ZDResultStatus.Success;
    }

    /// <summary>
    /// 是否失败
    /// </summary>
    public bool IsFailed()
    {
        return Status == ZDResultStatus.FailedRetryable || Status == ZDResultStatus.FailedFatal;
    }

    /// <summary>
    /// 是否可以重试
    /// </summary>
    public bool CanRetry()
    {
        return Status == ZDResultStatus.FailedRetryable;
    }

    /// <summary>
    /// 是否被跳过
    /// </summary>
    public bool IsSkipped()
    {
        return Status == ZDResultStatus.Skipped;
    }

    // ========== 工具方法 ==========

    /// <summary>
    /// 获取扩展数据
    /// </summary>
    public string GetExtraData(string key, string defaultValue)
    {
        if (ExtraData == null)
        {
            return defaultValue;
        }
        if (ExtraData.ContainsKey(key))
        {
            return ExtraData[key];
        }
        return defaultValue;
    }

    /// <summary>
    /// 设置扩展数据
    /// </summary>
    public void SetExtraData(string key, string value)
    {
        if (ExtraData == null)
        {
            ExtraData = new System.Collections.Generic.Dictionary<string, string>();
        }
        ExtraData[key] = value;
    }

    /// <summary>
    /// 转换为日志字符串
    /// </summary>
    public string ToLogString()
    {
        string statusStr = Status.ToString();
        string log = string.Format("ZDResult[{0}", statusStr);

        if (IsFailed() && !string.IsNullOrEmpty(ErrorMessage))
        {
            log += string.Format(", error={0}", ErrorMessage);
        }

        if (ElapsedMilliseconds > 0)
        {
            log += string.Format(", elapsed={0}ms", ElapsedMilliseconds);
        }

        if (AttemptCount > 1)
        {
            log += string.Format(", attempts={0}", AttemptCount);
        }

        log += "]";
        return log;
    }

    /// <summary>
    /// 转换为兼容的字符串结果（供旧代码使用）
    /// </summary>
    public string ToLegacyResult()
    {
        if (IsSuccess())
        {
            return "SUCCESS";
        }
        if (IsSkipped())
        {
            return "SKIP:" + ErrorMessage;
        }
        return "ERROR:" + ErrorMessage;
    }

    /// <summary>
    /// 从旧格式字符串创建结果
    /// </summary>
    public static ZDResult FromLegacyResult(string legacyResult, long elapsedMs)
    {
        if (string.IsNullOrEmpty(legacyResult))
        {
            return ZDResult.FailedRetryable("空结果");
        }

        if (legacyResult == "SUCCESS")
        {
            return ZDResult.Success(elapsedMs);
        }

        if (legacyResult.StartsWith("SKIP"))
        {
            string reason = legacyResult.Substring(5);
            return ZDResult.Skipped(reason);
        }

        if (legacyResult.StartsWith("ERROR"))
        {
            string errorMsg = legacyResult.Substring(6);
            return ZDResult.FailedRetryable(errorMsg);
        }

        if (legacyResult.StartsWith("ABORT"))
        {
            string errorMsg = legacyResult.Substring(6);
            return ZDResult.FailedFatal(errorMsg);
        }

        // OK 表示步骤成功
        if (legacyResult == "OK")
        {
            return ZDResult.Success(elapsedMs);
        }

        return ZDResult.FailedRetryable("未知结果: " + legacyResult);
    }
}
