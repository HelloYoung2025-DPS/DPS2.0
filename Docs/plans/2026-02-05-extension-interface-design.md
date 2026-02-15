# DPS v4.5 统一扩展接口设计方案

> 状态: 设计完成，部分实现
> 日期: 2026-02-05 (更新: 2026-02-07)
> 讨论结果: 采用方案 A (轻量级接口)

---

## 1. 背景

当前 DPS v4.5 的扩展机制存在以下问题：
- 硬编码模式：`Extension.cs` 中直接写死了 IP 定位和天气功能
- 无统一接口：添加新扩展需要修改源码
- 子程序交互：浏览、点赞等动作通过独立子项目 + 变量传递

## v4.5 更新

v4.5 已实现部分扩展机制：
- **Core/PlatformBase.cs**: 平台操作标准接口
- **Core/HumanizationEngine.cs**: 人性化行为扩展
- **Core/ErrorRecovery.cs**: 错误恢复扩展
- **Platforms/Reddit/RedditModule.cs**: Reddit 平台实现
- **Platforms/Instagram/InstagramModule.cs**: Instagram 平台实现

## 2. 设计目标

- 统一的代码接口 (Interface)
- 支持多种扩展类型：数据源、AI 服务、行为、报告、生命周期钩子
- 完全兼容 ZennoDroid 环境 (C# 5.0, CSharpCodeProvider)

## 3. 核心接口定义

```csharp
// Modules/Core/IExtension.cs
public interface IExtension
{
    /// <summary>扩展唯一标识</summary>
    string Name { get; }
    
    /// <summary>扩展类别: DataSource, AI, Behavior, Report, Hook</summary>
    string Category { get; }
    
    /// <summary>扩展版本</summary>
    string Version { get; }
    
    /// <summary>是否启用 (从配置读取)</summary>
    bool Enabled { get; }
    
    /// <summary>初始化扩展</summary>
    void Initialize(object projectObj);
    
    /// <summary>执行扩展逻辑</summary>
    /// <returns>SUCCESS 或 ERROR: 错误信息</returns>
    string Run(object projectObj);
}
```

## 4. 扩展管理器

```csharp
// Modules/Core/ExtensionManager.cs
public static class ExtensionManager
{
    private static List<IExtension> _extensions = new List<IExtension>();
    
    /// <summary>注册扩展</summary>
    public static void Register(IExtension extension)
    {
        _extensions.Add(extension);
        CoreHelper.Log("ExtMgr", "注册扩展: " + extension.Name);
    }
    
    /// <summary>获取指定类别的扩展</summary>
    public static List<IExtension> GetByCategory(string category)
    {
        var result = new List<IExtension>();
        foreach (var ext in _extensions)
        {
            if (ext.Category == category && ext.Enabled)
                result.Add(ext);
        }
        return result;
    }
    
    /// <summary>执行指定类别的所有扩展</summary>
    public static void RunCategory(string category, object projectObj)
    {
        foreach (var ext in GetByCategory(category))
        {
            ext.Run(projectObj);
        }
    }
}
```

## 5. 配置格式

```json
// Config/ExtensionsRegistry.json
{
    "extensions": [
        {
            "name": "IPLocation",
            "category": "DataSource",
            "module": "Extensions/IPLocationExtension.cs",
            "enabled": true,
            "config": {
                "update_interval_hours": 24,
                "providers": ["https://api.ipify.org"]
            }
        },
        {
            "name": "WeatherData",
            "category": "DataSource",
            "module": "Extensions/WeatherExtension.cs",
            "enabled": true
        },
        {
            "name": "BeforeSessionHook",
            "category": "Hook:BeforeSession",
            "module": "Extensions/Hooks/BeforeSession.cs",
            "enabled": true
        }
    ]
}
```

## 6. 扩展示例

### 6.1 数据源扩展

```csharp
// Extensions/IPLocationExtension.cs
public class IPLocationExtension : IExtension
{
    public string Name { get { return "IPLocation"; } }
    public string Category { get { return "DataSource"; } }
    public string Version { get { return "1.0"; } }
    public bool Enabled { get; private set; }
    
    private string _configJson;
    
    public void Initialize(object projectObj)
    {
        CoreHelper.Init(projectObj);
        _configJson = CoreHelper.GetVar("IPLocation_config_json", "{}");
        Enabled = true;
    }
    
    public string Run(object projectObj)
    {
        try
        {
            string ip = FetchIP();
            CoreHelper.SetVar("IPLocation_output_json", 
                "{\"ip\": \"" + ip + "\", \"fetched_at\": \"" + CoreHelper.GetNowISO() + "\"}");
            CoreHelper.SetVar("IPLocation_result", "SUCCESS");
            return "SUCCESS";
        }
        catch (Exception ex)
        {
            CoreHelper.SetVar("IPLocation_result", "ERROR");
            return "ERROR: " + ex.Message;
        }
    }
    
    private string FetchIP() { /* ... */ }
}
```

### 6.2 行为扩展 (子程序)

```csharp
// Extensions/Actions/BrowseAction.cs
public class BrowseAction : IExtension
{
    public string Name { get { return "Browse"; } }
    public string Category { get { return "Behavior"; } }
    public string Version { get { return "1.0"; } }
    public bool Enabled { get; private set; }
    
    public void Initialize(object projectObj) { /* ... */ }
    
    public string Run(object projectObj)
    {
        string inputJson = CoreHelper.GetVar("Browse_input_json", "{}");
        int duration = JsonHelper.GetInt(inputJson, "duration_sec", 10);
        
        // 执行浏览逻辑
        // ...
        
        string memoryEntry = "{\"action\": \"browse\", \"duration\": " + duration + "}";
        CoreHelper.SetVar("Browse_output_json", memoryEntry);
        return "SUCCESS";
    }
}
```

## 7. 变量命名约定

| 变量 | 格式 | 用途 |
|------|------|------|
| 输入参数 | `{扩展名}_input_json` | 传递给扩展的参数 |
| 输出结果 | `{扩展名}_output_json` | 扩展返回的数据 |
| 扩展配置 | `{扩展名}_config_json` | 扩展的配置信息 |
| 执行状态 | `{扩展名}_result` | SUCCESS / ERROR |

## 8. 目录结构

```
DPS_v4.5/
├── Modules/
│   └── Core/
│       ├── IExtension.cs        # 接口定义 (待实现)
│       ├── ExtensionManager.cs  # 扩展管理器 (待实现)
│       ├── HumanizationEngine.cs # 人性化引擎 (v4.5 已实现)
│       ├── UILocator.cs         # UI 定位 (v4.5 已实现)
│       ├── ErrorRecovery.cs     # 错误恢复 (v4.5 已实现)
│       └── PlatformBase.cs      # 平台接口 (v4.5 已实现)
├── Platforms/                    # 平台模块 (v4.5 已实现)
│   ├── Reddit/RedditModule.cs
│   └── Instagram/InstagramModule.cs
├── Extensions/                   # 扩展实现目录 (待实现)
│   ├── DataSources/
│   │   ├── IPLocationExtension.cs
│   │   └── WeatherExtension.cs
│   ├── Actions/
│   │   ├── BrowseAction.cs
│   │   ├── LikeAction.cs
│   │   └── CommentAction.cs
│   └── Hooks/
│       ├── BeforeSession.cs
│       └── AfterSession.cs
└── Config/
    ├── PlatformsConfig.json      # 平台配置 (v4.5 已实现)
    └── ExtensionsRegistry.json   # 扩展注册表 (待实现)
```

## 9. 实现计划

1. 创建 `IExtension.cs` 接口
2. 创建 `ExtensionManager.cs` 管理器
3. 创建 `ExtensionsRegistry.json` 配置
4. 迁移现有 IP/天气功能为扩展
5. 创建行为扩展示例
6. 更新 `SessionRunner.cs` 使用扩展系统

---

*设计者: AI Assistant*
*审核者: 待定*
