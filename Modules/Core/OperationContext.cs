// =====================================================
// OperationContext.cs - Operation Execution Context
// ⚠️ C# 5.0 语法（兼容 ZennoDroid）
// =====================================================
// Purpose: 实例化的操作上下文，替代静态 _context
//          支持多设备并发安全、跨操作数据传递、递归调用控制
//
// Usage:
//   OperationContext context = new OperationContext(deviceId);
//   context.SetVariable("key", "value");
//   string result = ActionExecutor.Execute(operationsJson, opName, platformConfig, context);
//
// Thread Safety:
//   每个设备/会话使用独立的 OperationContext 实例
//   无静态共享状态，支持多设备并发执行
// =====================================================

using System;
using System.Collections.Generic;

/// <summary>
/// 操作执行上下文（实例化，非静态）
/// 替代 ActionExecutor 中的静态 _context，支持多设备并发
/// </summary>
public class OperationContext
{
    // ========== 实例变量（替代静态 _context） ==========
    
    /// <summary>
    /// 上下文变量字典（find 步骤存储结果，tap 步骤读取）
    /// </summary>
    public Dictionary<string, string> Variables;
    
    // ========== 页面状态 ==========
    
    /// <summary>
    /// 当前页面 ID（由 PageDetector 设置）
    /// </summary>
    public string CurrentPage;
    
    /// <summary>
    /// 最后一次获取的 UI 布局 XML（缓存，避免重复 GetLayout）
    /// </summary>
    public string LastLayoutXml;
    
    // ========== 递归控制 ==========
    
    /// <summary>
    /// 当前递归调用深度（call_operation 使用）
    /// </summary>
    public int CallDepth;
    
    /// <summary>
    /// 最大递归深度限制（防止无限递归）
    /// </summary>
    private const int MAX_CALL_DEPTH = 5;
    
    // ========== 多设备并发安全 ==========
    
    /// <summary>
    /// 设备 ID（用于日志和错误追踪）
    /// </summary>
    public string DeviceId;
    
    // ========== 随机数生成器 ==========
    
    /// <summary>
    /// 每个 context 独立的随机数生成器（用于 random_pick）
    /// </summary>
    public Random Random;
    
    // ========== 构造函数 ==========
    
    /// <summary>
    /// 创建新的操作上下文
    /// </summary>
    /// <param name="deviceId">设备 ID</param>
    public OperationContext(string deviceId)
    {
        Variables = new Dictionary<string, string>();
        CurrentPage = "";
        LastLayoutXml = "";
        CallDepth = 0;
        DeviceId = deviceId;
        Random = new Random();
    }
    
    // ========== 递归深度控制 ==========
    
    /// <summary>
    /// 检查是否可以进入新的递归调用
    /// </summary>
    /// <returns>true 如果未超过深度限制</returns>
    public bool CanEnterCall()
    {
        return CallDepth < MAX_CALL_DEPTH;
    }
    
    /// <summary>
    /// 进入递归调用（深度 +1）
    /// </summary>
    public void EnterCall()
    {
        CallDepth++;
    }
    
    /// <summary>
    /// 退出递归调用（深度 -1）
    /// </summary>
    public void ExitCall()
    {
        if (CallDepth > 0)
        {
            CallDepth--;
        }
    }
    
    // ========== 变量操作 ==========
    
    /// <summary>
    /// 设置上下文变量
    /// </summary>
    /// <param name="key">变量名</param>
    /// <param name="value">变量值</param>
    public void SetVariable(string key, string value)
    {
        if (string.IsNullOrEmpty(key))
        {
            return;
        }
        Variables[key] = value ?? "";
    }
    
    /// <summary>
    /// 获取上下文变量
    /// </summary>
    /// <param name="key">变量名</param>
    /// <param name="defaultValue">默认值（如果变量不存在）</param>
    /// <returns>变量值或默认值</returns>
    public string GetVariable(string key, string defaultValue)
    {
        if (string.IsNullOrEmpty(key))
        {
            return defaultValue;
        }
        
        if (Variables.ContainsKey(key))
        {
            return Variables[key];
        }
        
        return defaultValue;
    }
    
    /// <summary>
    /// 检查变量是否存在
    /// </summary>
    /// <param name="key">变量名</param>
    /// <returns>true 如果变量存在</returns>
    public bool HasVariable(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }
        return Variables.ContainsKey(key);
    }
    
    /// <summary>
    /// 清空所有变量（用于操作开始时重置）
    /// </summary>
    public void ClearVariables()
    {
        Variables.Clear();
    }
    
    // ========== 辅助方法 ==========
    
    /// <summary>
    /// 获取上下文摘要（用于日志）
    /// </summary>
    /// <returns>上下文摘要字符串</returns>
    public string GetSummary()
    {
        return string.Format("[Device={0}, Page={1}, Depth={2}, Vars={3}]",
            DeviceId,
            CurrentPage,
            CallDepth,
            Variables.Count);
    }
}
