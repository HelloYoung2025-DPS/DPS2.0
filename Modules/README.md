# DPS v4.5 Modules 目录

## 概述

此目录包含所有业务逻辑模块，使用 **C# 5.0 语法**编写。

> ⚠️ **重要**: 外部 .cs 文件使用 `CSharpCodeProvider` 编译，仅支持 **C# 5.0**！
> 禁止使用：`$""`、`?.`、`nameof()`、模式匹配等 C# 6.0+ 语法。

## v4.5 更新

- **多平台支持**: 通过 `Config/Operations/*.json` + `Config/IntentMappings/*.json` 配置驱动
- **已接入平台**: Reddit、Instagram、BabyCenter（通过 PlatformsConfig.json 配置）
- **Core/HumanizationEngine.cs**: 4种行为配置文件（speed_demon, casual, deep_reader, distracted）
- **Core/UILocator.cs**: 多策略 UI 定位（resource-id → XPath → 图像识别）
- **Core/ErrorRecovery.cs**: 自动重试 + 指数退避（2s, 4s, 8s）

## v4.1 更新

- **JsonHelper.cs**: 完全重写，使用栈式状态机实现健壮的 JSON 解析
- **CoreHelper.cs**: JGet/JSet 现在委托给 JsonHelper
- **AIService.cs**: 使用 JsonHelper 解析响应，添加 API 错误检测

---

## 目录结构

```
Modules/
├── Core/                    # 核心辅助类
│   ├── CoreHelper.cs       # 日志、变量、文件、JSON
│   ├── JsonHelper.cs       # JSON 解析
│   ├── AIService.cs        # Gemini/OpenAI API
│   ├── FileHelper.cs       # 文件操作
│   ├── HumanizationEngine.cs # 人性化行为 (v4.5)
│   ├── UILocator.cs        # UI 定位 (v4.5)
│   ├── ErrorRecovery.cs    # 错误恢复 (v4.5)
│   └── RateLimiter.cs      # 速率限制 (已就绪，未接入主链)
├── Initializer.cs           # [1] 初始化
├── Main.cs                  # [2] 主入口（检查画像、生成计划）
├── PersonaCreate.cs         # [2a] 画像生成（按需）
├── DailyUpdate.cs           # [2b] 每日更新（按需）
├── Extension.cs             # [3] 扩展功能（IP/天气）
├── SessionRunner.cs         # [4] 会话执行 + 保存记忆
├── StateSaver.cs            # [5] 保存画像 + 统计
├── Maintenance.cs           # [6] 日志清理
├── ReportGen.cs             # [7] 报告生成（条件触发）
└── WeeklyEvolve.cs          # [独立] 每周进化
```

---

## 模块开发规范

### 标准模块结构

```csharp
// ⚠️ C# 5.0 语法！禁止 $""、?.、nameof()
using System;
using System.IO;

public class ModuleName
{
    private static dynamic _project;
    private const string TAG = "ModuleName";
    
    public static string Run(object projectObj)
    {
        _project = projectObj;
        
        try
        {
            CoreHelper.Init(projectObj);
            
            // 业务逻辑...
            
            return "SUCCESS";
        }
        catch (Exception ex)
        {
            CoreHelper.LogErr(TAG, "异常: " + ex.Message);
            return "ERROR: " + ex.Message;
        }
    }
}
```

---

## C# 语法版本对照表

| 场景 | C# 版本 | `$""` | `?.` |
|------|---------|-------|------|
| **Own Code** | ~7.0+ | ✅ | ⚠️ |
| **外部 .cs 模块** | ~5.0 | ❌ | ❌ |

### 禁止使用的语法

| 语法 | 替代方案 |
|------|----------|
| `$"用户: {name}"` | `"用户: " + name` 或 `string.Format("用户: {0}", name)` |
| `obj?.Method()` | `if (obj != null) obj.Method();` |
| `nameof(variable)` | `"variable"` |
| `x is T t` | `if (x is T) { var t = (T)x; }` |
| `??=` | `if (x == null) x = value;` |

---

## 模块调用方式

1. 在 ZD 中创建 Own Code 动作块
2. 复制 `ZDProjects/XxxModule_OwnCode.cs` 内容
3. 模块加载器会自动：
   - 读取外部 .cs 文件
   - 合并 Core/*.cs 依赖
   - 动态编译并执行 `Run()` 方法

---

## 添加新模块

1. 在 `Modules/` 创建 `NewModule.cs`
2. 遵循标准模块结构
3. 在 `ZDProjects/` 创建对应的 `NewModule_OwnCode.cs`
4. 修改模块路径和标签名称
