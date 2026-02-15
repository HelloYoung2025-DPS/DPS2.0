# DPS v4.5 代码库清理与修复 - 实施计划

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** 修复 DPS v4.5 的 6 个核心问题：硬编码路径、SessionRunner 断裂、代码重复、配置未使用、XML 解析冲突、扩展接口缺失

**Architecture:** 分 4 阶段递进修复。每阶段独立可验证，不破坏现有功能。所有代码必须兼容 C# 5.0（ZennoDroid 环境）。

**Tech Stack:** C# 5.0, ZennoDroid API, CSharpCodeProvider 动态编译, 纯字符串 JSON/XML 解析

**约束:** 禁止 $"", ?., nameof(), System.Xml.Linq（运行时不可靠）

---

## 代码库现状（55 个 .cs 文件）

| 目录 | 文件数 | 角色 |
|------|--------|------|
| Modules/Core/ | 4 | 核心库（CoreHelper, JsonHelper, AIService, FileHelper） |
| Core/ | 4 | 共享引擎（HumanizationEngine, UILocator, ErrorRecovery, PlatformBase） |
| Modules/ | 9 | 业务模块（Main, SessionRunner, Extension, RuleEngine 等） |
| Platforms/ | 2 | 平台实现（RedditModule, InstagramModule） |
| ZDProjects/ | 12 | ZD 脚本（Reddit_Browse/Like/Comment/ReadPost, *_OwnCode） |
| ZDProjects/Tests/ | 11 | 测试脚本 |
| 其他 | 3 | 根目录测试文件 |

---

## 阶段 1：紧急修复（阻塞性问题）

### 任务 1.1：修复所有硬编码路径（DPS_v4.1 → 动态读取）

**Files:**
- Modify: `Platforms/Reddit/RedditModule.cs:42,47,51,55`
- Modify: `Platforms/Instagram/InstagramModule.cs:47,52,56,60`

**问题:** 默认路径硬编码为 `DPS_v4.1`，当 `project_root` 变量未设置时会读取错误目录。

**Step 1: 修复 RedditModule.cs**

将第 42 行：
```csharp
string humanizationPath = GetVar("project_root", "C:\\Users\\Hu\\.gemini\\zennoDroid\\DPS_v4.1") + "\\Core\\HumanizationEngine.cs";
```
改为：
```csharp
string humanizationPath = GetVar("project_root", "") + "\\Core\\HumanizationEngine.cs";
```

同样修改第 47、51、55 行的默认值从 `"C:\\Users\\Hu\\.gemini\\zennoDroid\\DPS_v4.1"` 改为 `""`。

在第 39 行（`// ========== Load Core Modules ==========` 之后）添加路径验证：
```csharp
string _projectRoot = GetVar("project_root", "");
if (string.IsNullOrEmpty(_projectRoot)) {
    LogErr("FATAL: project_root 未设置");
    return;
}
if (!_projectRoot.EndsWith("\\")) _projectRoot += "\\";
```

然后将第 42-56 行改为使用 `_projectRoot`：
```csharp
string humanizationPath = _projectRoot + "Core\\HumanizationEngine.cs";
string uiLocatorPath = _projectRoot + "Core\\UILocator.cs";
string errorRecoveryPath = _projectRoot + "Core\\ErrorRecovery.cs";
string configPath = _projectRoot + "Config\\PlatformsConfig.json";
```

**Step 2: 修复 InstagramModule.cs**

完全相同的修改，第 47-60 行。

**Step 3: 验证**

搜索整个项目确认无残留 `DPS_v4.1` 字符串：
```
grep -r "DPS_v4.1" --include="*.cs" .
```
预期结果：0 匹配

---

### 任务 1.2：修复版本号显示错误

**Files:**
- Modify: `Modules/Main.cs:49`
- Modify: `Modules/Initializer.cs:43-44`

**Step 1: 修复 Main.cs 第 49 行**

```csharp
// 旧:
CoreHelper.Log(TAG, "  DPS v4.0 - 会话开始");
// 新:
CoreHelper.Log(TAG, "  DPS v4.5 - 会话开始");
```

**Step 2: 修复 Initializer.cs 第 43-44 行**

```csharp
// 旧:
CoreHelper.Log(TAG, "  DPS v4.0 Persona - 初始化程序");
CoreHelper.Log(TAG, "  版本: 4.0.0-Modular");
// 新:
CoreHelper.Log(TAG, "  DPS v4.5 Persona - 初始化程序");
CoreHelper.Log(TAG, "  版本: 4.5.0-Modular");
```

---

### 任务 1.3：修复 SessionRunner 执行链断裂

**Files:**
- Modify: `Modules/SessionRunner.cs:168-184,337-359`

**问题:** `Run()` 方法第 173 行只设置 `pending_action` 变量，从不调用 `LoadPlatformModule()`。动作永远不会真正执行，但 `actionResult` 默认为 `"SUCCESS"`（第 169 行），造成假成功。

**Step 1: 修改执行逻辑（第 168-184 行）**

将：
```csharp
// 执行实际动作
string actionResult = "SUCCESS";
try {
    string currentPlatform = CoreHelper.GetVar("current_platform", "reddit");
    string actionFunc = currentPlatform + "_" + selectedAction;
    CoreHelper.SetVar("pending_action", actionFunc);
    CoreHelper.SetVar("pending_action_type", selectedAction);
    
    // ⚠️ 注意：在真实 ZD 项目中...
    // ...假设动作执行总是成功的...
} catch (System.Exception ex) {
    actionResult = "ERROR";
    CoreHelper.LogErr(TAG, "动作执行失败: " + ex.Message);
}
```

替换为：
```csharp
// 执行实际动作
string actionResult = "PENDING";
try {
    string currentPlatform = CoreHelper.GetVar("current_platform", "reddit");
    
    // 设置动作参数供平台模块读取
    CoreHelper.SetVar("pending_action", selectedAction);
    CoreHelper.SetVar("pending_action_type", selectedAction);
    CoreHelper.SetVar("pending_platform", currentPlatform);
    
    // 调用平台模块执行动作
    string moduleResult = LoadPlatformModule(projectRoot, currentPlatform, selectedAction);
    
    if (moduleResult.StartsWith("ERROR"))
    {
        actionResult = "ERROR";
        CoreHelper.LogErr(TAG, "平台模块返回错误: " + moduleResult);
    }
    else
    {
        // 检查平台模块设置的结果变量
        actionResult = CoreHelper.GetVar("action_result", "SUCCESS");
    }
} catch (System.Exception ex) {
    actionResult = "ERROR";
    CoreHelper.LogErr(TAG, "动作执行失败: " + ex.Message);
}
```

**Step 2: 增强 LoadPlatformModule 方法（第 337-359 行）**

将：
```csharp
private static string LoadPlatformModule(string projectRoot, string platformName, string operation)
{
    // ...只返回字符串，不执行...
    return platformName + "_" + operation;
}
```

替换为：
```csharp
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
```

**Step 3: 验证**

确认 `actionResult` 不再默认为 `"SUCCESS"`，而是从实际执行结果获取。

---

## 阶段 2：消除代码重复

### 任务 2.1：统一 Helper 函数模板

**Files:**
- Create: `Core/ScriptHelpers.cs`
- Modify: `ZDProjects/Reddit_Browse.cs:7-24`
- Modify: `ZDProjects/Reddit_Like.cs:7-24`
- Modify: `ZDProjects/Reddit_Comment.cs:7-24`
- Modify: `ZDProjects/Reddit_ReadPost.cs:7-24`

**问题:** 4 个 Helper 函数（Log, LogErr, GetVar, SetVar）在 8+ 个文件中重复，共 ~128 行。

**Step 1: 创建 Core/ScriptHelpers.cs**

```csharp
// =====================================================
// ScriptHelpers.cs - ZD 脚本公共 Helper 模板
// ⚠️ C# 5.0 语法
// ⚠️ 此文件由 ModuleLoader 自动加载，ZD 脚本无需手动引入
// =====================================================

// ========== 公共 Helper Functions ==========
// 以下函数在所有 ZD 脚本中可用（通过 ModuleLoader 编译注入）

// 日志
Action<string> Log = (m) => project.SendInfoToLog("[Script] " + m, true);
Action<string> LogErr = (m) => project.SendErrorToLog("[Script] " + m, true);

// 变量读写
Func<string, string, string> GetVar = (name, def) => {
    try {
        string v = project.Variables[name].Value;
        return string.IsNullOrEmpty(v) ? def : v;
    } catch { return def; }
};

Action<string, string> SetVar = (name, val) => {
    try {
        project.Variables[name].Value = val ?? "";
    } catch { }
};
```

**注意:** 由于 ZD 脚本是独立编译执行的（非 class），每个脚本仍需包含这些 Helper。但我们可以：
1. 将 `ScriptHelpers.cs` 作为规范模板
2. 修改 `ModuleLoader.cs` 自动注入这些 Helper（见任务 2.4）
3. 在 ZDProjects 文件中添加注释标注来源

**Step 2: 在每个 ZDProjects 文件的 Helper 区域添加来源注释**

```csharp
// ========== Helper Functions (from Core/ScriptHelpers.cs) ==========
// ⚠️ 这些函数与 Core/ScriptHelpers.cs 保持同步
// ⚠️ 修改时请同步更新所有副本，或等待 ModuleLoader 自动注入机制完成
```

---

### 任务 2.2：消除 Humanization 函数重复（~350 行）

**Files:**
- Keep: `Core/HumanizationEngine.cs` (规范版本，170 行)
- Modify: `ZDProjects/Reddit_Browse.cs:28-126` (删除 ~98 行)
- Modify: `ZDProjects/Reddit_Like.cs:28-126` (删除 ~98 行)
- Modify: `ZDProjects/Reddit_Comment.cs:28-126` (删除 ~98 行)
- Modify: `ZDProjects/Reddit_ReadPost.cs:28-126` (删除 ~98 行)
- Modify: `ZDProjects/ModuleLoader.cs:38,112` (添加 HumanizationEngine.cs 到加载列表)

**Step 1: 修改 ModuleLoader.cs 加载 HumanizationEngine**

第 38 行和第 112 行的 `coreFiles` 数组：
```csharp
// 旧:
string[] coreFiles = new string[] { "CoreHelper.cs", "JsonHelper.cs", "AIService.cs", "FileHelper.cs" };
// 新:
string[] coreFiles = new string[] { "CoreHelper.cs", "JsonHelper.cs", "AIService.cs", "FileHelper.cs", "HumanizationEngine.cs", "UILocator.cs", "ErrorRecovery.cs" };
```

同时修改 `coreDir` 的查找逻辑，因为 HumanizationEngine 等在 `Core/` 而非 `Modules/Core/`：

在第 37 行后添加：
```csharp
// Core 引擎文件（在项目根目录的 Core/ 下）
string engineDir = System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(filePath)) + "\\Core\\";
string[] engineFiles = new string[] { "HumanizationEngine.cs", "UILocator.cs", "ErrorRecovery.cs" };
foreach (string ef in engineFiles) {
    string efPath = engineDir + ef;
    if (System.IO.File.Exists(efPath)) {
        timestamps[efPath] = new System.IO.FileInfo(efPath).LastWriteTimeUtc.Ticks;
    }
}
```

在第 111 行后添加相同的加载逻辑：
```csharp
string engineDir = System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(filePath)) + "\\Core\\";
string[] engineFiles = new string[] { "HumanizationEngine.cs", "UILocator.cs", "ErrorRecovery.cs" };
foreach (string ef in engineFiles) {
    string efPath = engineDir + ef;
    if (System.IO.File.Exists(efPath)) {
        allCodes.Add(System.IO.File.ReadAllText(efPath, System.Text.Encoding.UTF8));
    }
}
```

**Step 2: 从 4 个 ZDProjects 文件中删除重复代码**

在每个文件中，删除第 26-126 行（Humanization Helpers 区域），替换为：
```csharp
// ========== Humanization (from Core/HumanizationEngine.cs) ==========
// ⚠️ GetProfileConfig, HumanizedDelay, HumanizedTap, HumanizedSwipe,
//    ShouldTriggerProbabilistic 由 ModuleLoader 从 Core/HumanizationEngine.cs 自动注入
// ⚠️ 如果独立运行此脚本（不通过 ModuleLoader），需要手动复制这些函数
```

**Step 3: 验证**

确认 `Reddit_Browse.cs` 从 294 行减少到 ~200 行，且通过 ModuleLoader 编译无错误。

---

### 任务 2.3：消除 XML 解析函数重复（~280 行）

**Files:**
- Keep: `Core/UILocator.cs` (规范版本，285 行，含 IsElementClickable)
- Modify: `ZDProjects/Reddit_Browse.cs:128-172` (删除 ~44 行)
- Modify: `ZDProjects/Reddit_Like.cs` (删除相同区域)
- Modify: `ZDProjects/Reddit_Comment.cs` (删除相同区域)
- Modify: `ZDProjects/Reddit_ReadPost.cs` (删除相同区域)
- Modify: `Platforms/Reddit/RedditModule.cs:170-195` (删除内联解析)
- Modify: `Platforms/Instagram/InstagramModule.cs:187-213` (删除内联解析)

**Step 1: 从 ZDProjects 文件删除 ParseBounds 和 FindBoundsByResourceId**

替换为注释：
```csharp
// ========== XML Parsing (from Core/UILocator.cs) ==========
// ⚠️ ParseBounds, FindBoundsByResourceId, FindNodesByResourceId, GetCenter,
//    IsElementClickable, FindElement 由 ModuleLoader 从 Core/UILocator.cs 自动注入
```

**Step 2: 修改 RedditModule.cs 和 InstagramModule.cs**

将内联 XML 解析代码替换为对 UILocator 函数的调用。

例如 RedditModule.cs Browse 操作中的：
```csharp
// 内联解析（删除）
string searchPattern = "resource-id=\"post_unit\"";
int pos = 0;
while (pos < layout.Length) { ... }
```

替换为：
```csharp
// 使用 UILocator（由 ModuleLoader 注入）
var postBounds = FindBoundsByResourceId(layout, "post_unit");
```

**关键改进:** UILocator 版本的 `FindBoundsByResourceId` 包含 `IsElementClickable` 检查，会过滤掉 clickable="false"、enabled="false" 和无效 bounds 的元素。这修复了 ZDProjects 版本可能点击不可交互元素的 bug。

---

### 任务 2.4：标记废弃 UIHelper.cs（System.Xml.Linq 版本）

**Files:**
- Modify: `Modules/UIHelper.cs:1-5`

**Step 1: 添加废弃标记**

在文件顶部添加：
```csharp
// =====================================================
// ⚠️ DEPRECATED - 请使用 Core/UILocator.cs 替代
// ⚠️ 此文件使用 System.Xml.Linq，在 ZennoDroid C# 5.0 中不可靠
// ⚠️ 保留仅供参考，将在 v5.0 中删除
// =====================================================
```

---

## 阶段 3：接入配置驱动

### 任务 3.1：让平台模块使用 PlatformsConfig.json 的 ui_selectors

**Files:**
- Modify: `Platforms/Reddit/RedditModule.cs`
- Modify: `Platforms/Instagram/InstagramModule.cs`
- Modify: `ZDProjects/Reddit_Browse.cs`
- Modify: `ZDProjects/Reddit_Like.cs`
- Modify: `ZDProjects/Reddit_Comment.cs`
- Modify: `ZDProjects/Reddit_ReadPost.cs`

**问题:** PlatformsConfig.json 定义了 ui_selectors 映射，但代码全部硬编码 resource-id。

**Step 1: 在 RedditModule.cs 中提取选择器**

在 `configJson` 加载后（第 56 行之后）添加：
```csharp
// 从配置加载 UI 选择器
string platformsJson = JsonHelper.ExtractObject(configJson, "platforms");
string redditJson = JsonHelper.ExtractObject(platformsJson, "reddit");
string selectorsJson = JsonHelper.ExtractObject(redditJson, "ui_selectors");

// 提取各选择器（带默认值回退）
string selectorPostUnit = JsonHelper.Get(selectorsJson, "post_unit");
if (string.IsNullOrEmpty(selectorPostUnit)) selectorPostUnit = "post_unit";

string selectorUpvote = JsonHelper.Get(selectorsJson, "upvote_button");
if (string.IsNullOrEmpty(selectorUpvote)) selectorUpvote = "post_footer_first_child";

string selectorComment = JsonHelper.Get(selectorsJson, "comment_button");
if (string.IsNullOrEmpty(selectorComment)) selectorComment = "comment_button";

string selectorShare = JsonHelper.Get(selectorsJson, "share_button");
if (string.IsNullOrEmpty(selectorShare)) selectorShare = "share_button";

string selectorFollow = JsonHelper.Get(selectorsJson, "follow_button");
if (string.IsNullOrEmpty(selectorFollow)) selectorFollow = "follow_button";
```

**Step 2: 替换所有硬编码引用**

将：
```csharp
string searchPattern = "resource-id=\"post_unit\"";
```
改为：
```csharp
var postBounds = FindBoundsByResourceId(layout, selectorPostUnit);
```

对所有操作中的 resource-id 引用做同样替换。

**Step 3: 对 InstagramModule.cs 做相同修改**

使用 `instagram` 平台的 ui_selectors。

**Step 4: 对 ZDProjects 文件做相同修改**

从变量读取选择器：
```csharp
string selectorPostUnit = GetVar("selector_post_unit", "post_unit");
```

SessionRunner 在调用前设置这些变量。

---

## 阶段 4：扩展接口实现

### 任务 4.1：创建 IExtension 接口

**Files:**
- Create: `Modules/Core/IExtension.cs`

按设计文档 `2026-02-05-extension-interface-design.md` 第 33-55 行创建。

```csharp
// =====================================================
// IExtension.cs - 统一扩展接口
// ⚠️ C# 5.0 语法
// =====================================================
using System;

public interface IExtension
{
    string Name { get; }
    string Category { get; }
    string Version { get; }
    bool Enabled { get; }
    void Initialize(object projectObj);
    string Run(object projectObj);
}
```

### 任务 4.2：创建 ExtensionManager

**Files:**
- Create: `Modules/Core/ExtensionManager.cs`

按设计文档第 60-93 行创建，添加从 ExtensionsRegistry.json 自动加载的能力。

### 任务 4.3：创建 ExtensionsRegistry.json

**Files:**
- Create: `Config/ExtensionsRegistry.json`

按设计文档第 98-126 行创建。

### 任务 4.4：迁移现有扩展

**Files:**
- Create: `Extensions/DataSources/IPLocationExtension.cs`
- Create: `Extensions/DataSources/WeatherExtension.cs`
- Modify: `Modules/Extension.cs` (标记废弃)

将 `Extension.cs` 中的 IP 定位（第 112-132 行）和天气（第 158-186 行）逻辑分别迁移为独立扩展类。

### 任务 4.5：更新 SessionRunner 使用扩展系统

**Files:**
- Modify: `Modules/SessionRunner.cs`

在 `Run()` 方法开头添加：
```csharp
// 初始化扩展系统
ExtensionManager.LoadFromRegistry(projectRoot + "Config\\ExtensionsRegistry.json", projectObj);

// 执行会话前钩子
ExtensionManager.RunCategory("Hook:BeforeSession", projectObj);

// 执行数据源扩展（IP、天气等）
ExtensionManager.RunCategory("DataSource", projectObj);
```

在 `Run()` 方法结尾添加：
```csharp
// 执行会话后钩子
ExtensionManager.RunCategory("Hook:AfterSession", projectObj);
```

---

## 执行顺序与依赖关系

```
阶段 1（紧急修复）
  ├── 任务 1.1 修复硬编码路径 ← 无依赖，立即执行
  ├── 任务 1.2 修复版本号 ← 无依赖，可并行
  └── 任务 1.3 修复 SessionRunner ← 无依赖，可并行

阶段 2（消除重复）← 依赖阶段 1 完成
  ├── 任务 2.1 统一 Helper 模板 ← 无依赖
  ├── 任务 2.2 消除 Humanization 重复 ← 依赖 2.1（ModuleLoader 修改）
  ├── 任务 2.3 消除 XML 解析重复 ← 依赖 2.2（同一 ModuleLoader 修改）
  └── 任务 2.4 标记废弃 UIHelper ← 无依赖

阶段 3（配置驱动）← 依赖阶段 2 完成
  └── 任务 3.1 使用 ui_selectors ← 依赖 2.3（UILocator 统一后）

阶段 4（扩展接口）← 依赖阶段 1.3 完成
  ├── 任务 4.1 创建 IExtension ← 无依赖
  ├── 任务 4.2 创建 ExtensionManager ← 依赖 4.1
  ├── 任务 4.3 创建 ExtensionsRegistry.json ← 依赖 4.2
  ├── 任务 4.4 迁移现有扩展 ← 依赖 4.1-4.3
  └── 任务 4.5 更新 SessionRunner ← 依赖 4.4 + 1.3
```

## 风险评估

| 任务 | 风险 | 缓解措施 |
|------|------|---------|
| 1.3 SessionRunner | 高 - ZD 运行时行为不确定 | 保留 pending_action 变量作为兼容层 |
| 2.2 Humanization 去重 | 中 - ModuleLoader 编译顺序 | 先测试 ModuleLoader 能否编译合并代码 |
| 2.3 XML 解析去重 | 中 - IsElementClickable 可能过滤过多 | 添加日志记录被过滤的元素 |
| 3.1 配置驱动 | 低 - 有默认值回退 | 每个选择器都有硬编码默认值 |
| 4.x 扩展接口 | 低 - 新增代码不影响现有 | 渐进式迁移，旧代码保留 |

## 预计工时

| 阶段 | 任务数 | 预计时间 |
|------|--------|---------|
| 阶段 1 | 3 | ~45 分钟 |
| 阶段 2 | 4 | ~90 分钟 |
| 阶段 3 | 1 | ~40 分钟 |
| 阶段 4 | 5 | ~90 分钟 |
| **总计** | **13** | **~4.5 小时** |

---

*计划编写: 2026-02-10*
*基于: 55 个 .cs 文件全面审查*
