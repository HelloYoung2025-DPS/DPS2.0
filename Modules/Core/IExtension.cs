// =====================================================
// IExtension.cs - 统一扩展接口定义
// ⚠️ C# 5.0 语法 - 禁止使用 $""、?.、nameof() 等
// v1.0.0 - 扩展系统核心接口
// =====================================================

/// <summary>
/// 统一扩展接口
/// 所有扩展（数据源、钩子、行为等）必须实现此接口
/// </summary>
public interface IExtension
{
    /// <summary>扩展唯一标识（如 "IPLocation", "Weather"）</summary>
    string Name { get; }
    
    /// <summary>扩展类别: DataSource, Hook, Behavior, AI, Report</summary>
    string Category { get; }
    
    /// <summary>扩展版本</summary>
    string Version { get; }
    
    /// <summary>是否启用</summary>
    bool Enabled { get; }
    
    /// <summary>
    /// 初始化扩展
    /// 在 Run 之前调用，用于加载配置、设置状态
    /// </summary>
    /// <param name="projectObj">ZD project 对象</param>
    /// <param name="configJson">扩展专属配置 JSON（从 ExtensionsRegistry.json 读取）</param>
    void Initialize(object projectObj, string configJson);
    
    /// <summary>
    /// 执行扩展逻辑
    /// </summary>
    /// <param name="projectObj">ZD project 对象</param>
    /// <returns>SUCCESS 或 ERROR: 错误信息</returns>
    string Run(object projectObj);
}
