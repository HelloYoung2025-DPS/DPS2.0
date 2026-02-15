# DPS v4.5 "Persona" — 系统全书 (System Bible)

> **目的**: 本文档是项目的完整技术手册。当所有对话记录丢失时，任何 AI 助手仅凭此文件即可 100% 理解项目并无缝接续开发。
>
> **最后更新**: 2026-02-14 v4.5.1
>
> **项目路径**: `C:\Users\Hu\.gemini\zennoDroid\DPS_v4.5\`

---

## 1. 项目概述

**DPS (Dynamic Persona Simulation)** 是一个基于 ZennoDroid 的 **人格驱动行为仿真框架**。它让 Android 设备上的社交媒体账号像真人一样自主浏览、点赞、评论、关注，并随时间自然演化行为模式。

### 1.1 核心特性

| 特性 | 说明 |
|------|------|
| **Persona 驱动** | 每个设备/账号拥有独立的 AI 生成人格画像 (100+ 字段)，影响行为偏好和内容风格 |
| **多平台支持** | Reddit ✅ (完整实现)、Instagram ✅ (完整实现)、TikTok/Facebook (配置已准备，`enabled: false`) |
| **7 阶段生命周期** | TTC → T1 → T2 → T3 → PP0 → PP1 → NP，模拟从备孕到育儿的全周期 |
| **每次运行动态更新** | ★ 每次脚本运行时 `Main.cs` 检查日期，触发 `DailyUpdate.cs` 自动更新年龄、孕周、阶段码、季节 |
| **每周 AI 自我进化** | ★ `WeeklyEvolve.cs` 收集 7 天行为记忆 → 构建 Prompt → 调用 AI 分析 → 自动修改画像字段 (±5 步长) |
| **交互记忆系统** | ★ `MemoryManager.cs` (738 行, 24 方法) 记录每次交互、去重检测、频率统计、过期清理 |
| **人性化引擎** | 4 种行为配置 (`speed_demon`/`casual`/`deep_reader`/`distracted`)：随机延迟、曲线滑动、概率性失误、疲劳模型 |
| **AI 集成** | 3 级 fallback: Gemini → Claude (deeprouter) → GPT (shubiaobiao)，用于生成画像、评论、演化决策 |
| **动态编译** | 运行时通过 `CSharpCodeProvider` 编译 .cs 模块，带编译缓存和时间戳跟踪 |
| **ActionExecutor** | v4.5 新增的 JSON 步骤驱动统一操作引擎，可配置化平台操作 |
| **Extension 系统** | `IExtension` 接口 + `ExtensionManager` 加载器，支持可插拔数据源和钩子 |

#### 1.1.1 画像动态更新机制 (DailyUpdate.cs)

**触发条件**: 每次运行脚本时，`Main.cs` 检查 `_meta.last_updated` 是否等于今天日期。若不等 → 返回 `NEED_DAILY_UPDATE` → ZD 调用 `DailyUpdate_OwnCode.cs`。

```
Main.Run()
  ├── 读取 persona_json._meta.last_updated
  ├── if (lastUpdated != today || force_regenerate)
  │   └── return "NEED_DAILY_UPDATE"  ← ZD 条件跳转到 DailyUpdate
  └── else → 生成 session_plan → return "READY"

DailyUpdate.Run()   ← 每天第一次运行时自动触发
  ├── 1. 备份当前画像 → Persons/Backups/{device_id}/{date}.json
  ├── 2. 更新 _meta.last_updated → 今天日期
  ├── 3. 重新计算年龄 → birth_date 与今天的差值
  ├── 4. 更新孕周 (如果 is_pregnant=true):
  │   ├── conception_date → 计算 current_week, current_days
  │   ├── 自动推算 trimester (1-13周=T1, 14-27周=T2, 28+周=T3)
  │   └── 自动更新 stage_code (阶段自动转换!)
  ├── 5. 更新季节 → current_season (spring/summer/fall/winter)
  └── 6. 保存 → Persons/{device_id}.json + 更新 ZD 变量 persona_json
```

**关键**: 阶段码 (`stage_code`) 会自动随孕周推进而改变 (T1→T2→T3)，这意味着每天运行时用户的行为参数 (来自 StageConfig.json) 会跟着变化。

#### 1.1.2 每周 AI 自我进化 (WeeklyEvolve.cs)

**触发条件**: 由 ZD 流程中的条件判断触发 (通常每周一次)。

```
WeeklyEvolve.Run()
  │
  ├── 1. 收集过去 7 天的行为记忆
  │   └── 遍历 Memory/{device_id}/{date}.json (最近 7 天)
  │       └── 统计每天的 action 数量 → memorySummary
  │
  ├── 2. 构建进化分析 Prompt
  │   ├── 当前画像摘要 (stage_code, current_age)
  │   ├── 过去一周行为记录 (每日动作数汇总)
  │   └── 要求 AI 输出 JSON:
  │       {
  │         "should_evolve": true/false,
  │         "changes": [
  │           {"field": "字段名", "direction": "increase/decrease", "reason": "原因"}
  │         ],
  │         "confidence": 0.0-1.0
  │       }
  │
  ├── 3. 调用 AI (AIService.CallWithRetry, 3 级 fallback)
  │
  ├── 4. 如果 should_evolve == true:
  │   ├── 备份画像 → Persons/Backups/{device_id}/{date}_evolve.json
  │   ├── 解析 changes 数组
  │   └── ApplyEvolutionChanges():
  │       ├── 遍历每个 change 对象
  │       ├── ApplyFieldChange(field, direction):
  │       │   ├── 查找字段当前数值 (整数型)
  │       │   ├── increase → +5, decrease → -5
  │       │   └── 限制在 1-100 范围内
  │       └── 日志: "字段 [xxx]: 70 -> 75"
  │
  └── 5. 保存更新后的画像 → 文件 + ZD 变量

进化修改粒度:
  ┌───────────────────────────────────────────┐
  │ 每次进化只修改 ±5 (步长)                    │
  │ 字段值范围: 1 ~ 100                         │
  │ 只影响整数型字段 (如 interest_scores, mood)  │
  │ 非数值型字段自动跳过                         │
  └───────────────────────────────────────────┘
```

**示例**: AI 分析 7 天内用户很少评论但大量浏览 → 建议 `comment_willingness` decrease → 字段从 65 变为 60。

#### 1.1.3 交互记忆系统 (MemoryManager.cs, 738 行)

**存储结构**: `Memory/{device_id}/{app_name}/interactions.json`  
**v4.5.1 已接入**: SessionRunner 在会话循环中通过 MemoryManager 进行去重检查和交互记录。

> ⚠️ 系统使用**双重记忆机制**:
> 1. **日记忆文件** (`Memory/{device_id}/{date}.json`): SessionRunner 手动写入，供 WeeklyEvolve 读取统计
> 2. **结构化交互** (`Memory/{device_id}/{app_name}/interactions.json`): MemoryManager 管理，支持去重/查询/清理

```
MemoryManager 核心 API:
  ├── Init(basePath)                     # 初始化记忆目录
  ├── HasInteracted(device, app, post)   # 检查帖子是否已交互过
  ├── RecordInteraction(device, app, post, actionType)
  ├── RecordInteractionWithScore(device, app, post, action, score)  # 带决策分数
  ├── GetInteractionHistory(device, app, limit)   # 获取最近 N 条 (按时间倒序)
  ├── GetInteractionCount(device, app, actionType) # 统计动作类型总数
  ├── GetTodayInteractionCount(device, app, action) # 今日统计
  ├── IsDuplicate(device, app, post, windowHours)  # 窗口内去重 (默认 24h)
  ├── CleanupOldInteractions(device, app, maxAgeDays) # 过期清理 (默认 30 天)
  └── EnforceMemoryLimits(device, app, configJson)  # 强制上限裁剪

SessionRunner 中的 5 个集成点 (v4.5.1):
  ① 平台确定后 → MemoryManager.Init(projectRoot + "Memory")
  ② 动作选择后 → IsDuplicate(postId) → 重复则降级为 browse
  ③ 动作成功后 → RecordInteraction(postId, actionType)
  ④ 会话结束时 → CleanupOldInteractions() 过期清理
  ⑤ 会话结束时 → EnforceMemoryLimits() 上限裁剪
```

**去重机制**: `IsDuplicate` 检查指定帖子在 `windowHours` (默认 24h, 可从 DecisionConfig 读取) 内是否已交互过。防止对同一帖子反复点赞/评论。

**内存上限**: `EnforceMemoryLimits` 按动作类型分别裁剪，超过配置上限时删除最旧记录。

### 1.2 技术约束 ⚠️

| 约束 | 说明 | 影响 |
|------|------|------|
| **C# 5.0 严格** | ZennoDroid 的 `CSharpCodeProvider` 仅支持 C# 5.0 | 禁止: `$""` 字符串插值、`?.` 空条件运算符、`nameof()`、auto-property initializers、`??=`、`out var` |
| **无 NuGet** | 不能使用任何第三方包 | JSON 解析使用手写 `JsonHelper.cs` (993 行, 栈式解析器) |
| **可用引用** | `System.dll`, `System.Core.dll`, `System.Data.dll`, `System.Xml.dll`, `Microsoft.CSharp.dll` | 这是 ModuleLoader 编译时加入的全部引用 |
| **ZennoDroid API** | 所有 UI 操作通过 `instance.DroidInstance` (Input/App/Hierarchy) | `project` 和 `instance` 是隐式全局变量 |
| **动态编译** | `CSharpCodeProvider` 在内存中编译，using 从所有源文件自动提取合并 | 类名不能冲突，所有 `.cs` 在同一编译单元 |

### 1.3 运行环境

| 项目 | 值 |
|------|-----|
| 宿主软件 | ZennoDroid (Android 模拟器自动化) |
| 运行时 | .NET Framework (ZD 内嵌) |
| 项目文件 | `run.droid` (ZennoDroid 项目文件) |
| 系统 | Windows |
| 项目根 | `C:\Users\Hu\.gemini\zennoDroid\DPS_v4.5\` |

---

## 2. 完整目录结构

```
DPS_v4.5/
│
├── Config/                              # 所有 JSON 配置文件 (14 个文件)
│   ├── AIConfig.json              (1.4K) # ★ AI 模型配置 — 3 级 fallback
│   ├── Apps.json                  (1.2K) # 应用包名注册表
│   ├── BehaviorConfig.json        (1.7K) # ★ 行为参数 — 动作权重、打字速度、滚动参数
│   ├── DecisionConfig.json        (2.8K) # 决策引擎规则 — 帖子评估阈值
│   ├── EvolutionRules.json        (2.1K) # 画像演化规则 — 周演化条件
│   ├── ExtensionsConfig.json      (1.8K) # 扩展全局配置 — 各扩展的 API 密钥/URL
│   ├── ExtensionsRegistry.json    (1.1K) # ★ 扩展注册表 — IExtension 发现列表
│   ├── MaintenanceConfig.json     (0.4K) # 维护任务配置 — 日志清理周期/阈值
│   ├── Operations/                       # ★ ActionExecutor 操作步骤 JSON
│   │   ├── reddit_operations.json (6.4K) #   Reddit 6 个操作的步骤定义
│   │   └── instagram_operations.json (7.8K) # Instagram 操作步骤定义
│   ├── PersonaPrompt.txt         (20.4K) # AI 画像生成 Prompt 模板 (非常详细)
│   ├── PlatformsConfig.json      (15.3K) # ★★★ 核心! 平台配置 — ui_selectors(嵌套对象), rate_limits, page_signatures
│   ├── StageConfig.json           (4.3K) # ★ 7 阶段生命周期配置 — 每阶段的会话频率/时长/关注点
│   ├── ValidationRules.json       (3.4K) # 数据验证规则 — 画像字段合规检查
│   └── device_app_mapping.json    (0.7K) # 设备→平台映射 — device_001→reddit 等
│
├── Core/                                 # 跨平台通用引擎 (5 个文件)
│   ├── HumanizationEngine.cs             # ★ 人性化行为引擎 — 4 种配置文件参数
│   ├── UILocator.cs                      # 多策略 UI 元素定位 (resource-id/text/content-desc/class)
│   ├── ErrorRecovery.cs                  # 错误恢复 — 指数退避重试 (2s → 4s → 8s, 最多 3 次)
│   ├── PlatformBase.cs                   # 平台基类接口定义
│   └── ScriptHelpers.cs                  # ★ ZDProjects 脚本公共函数库 — 由 ModuleLoader 自动注入
│       # 包含: GetVar/SetVar, GetProfileConfig, HumanizedDelay,
│       #       HumanizedTap, HumanizedSwipe, FindBoundsByResourceId,
│       #       ExtractAllText, FindNodesByResourceId, GetCenter,
│       #       ExtractBoundsFromNode, FindEditTextBounds
│
├── Modules/                              # 业务逻辑模块 (14 个 .cs + 4 个子目录)
│   ├── Main.cs                   (11.4K) # ★ 主入口 — 检查画像状态、决定执行分支
│   ├── Initializer.cs             (7.3K) # 初始化 — 创建目录、加载配置、检查/创建画像
│   ├── SessionRunner.cs          (35.8K) # ★★★ 核心! 会话执行引擎 — 853 行, 14 个方法
│   ├── PersonaCreate.cs           (6.1K) # AI 画像生成 — 调用 AIService + PersonaPrompt.txt
│   ├── DailyUpdate.cs             (7.5K) # 每日更新 — 话题热度、mood 漂移
│   ├── WeeklyEvolve.cs           (11.0K) # 每周演化 — AI 分析 7 天行为并调整画像
│   ├── MemoryManager.cs          (22.6K) # 行为记忆管理 — 短期/长期/社交/行为模式
│   ├── Extension.cs               (3.0K) # 扩展入口 — 委托给 ExtensionManager
│   ├── RuleEngine.cs             (23.7K) # ★ 规则引擎 — 帖子评估、动作决策
│   ├── StateSaver.cs              (5.9K) # 状态持久化 — 保存画像和运行统计
│   ├── ReportGen.cs               (6.5K) # 报告生成 — 每日/周运行报告
│   ├── Maintenance.cs             (8.3K) # 维护任务 — 清理日志/过期数据
│   ├── UIHelper.cs                (8.5K) # UI 辅助 — Android UI XML 解析
│   ├── Core/                             # 模块核心依赖 (8 个文件)
│   │   ├── JsonHelper.cs         (28.3K) # ★★★ 核心! 纯字符串 JSON 解析器 — 993 行, 23 个方法
│   │   ├── AIService.cs                  # ★ AI API 调用 — 3 级 fallback, HTTP 请求
│   │   ├── ActionExecutor.cs             # v4.5 统一操作执行器 — JSON 步骤驱动
│   │   ├── PageDetector.cs               # 页面状态检测 — 基于 page_signatures
│   │   ├── SelectorEngine.cs             # 选择器引擎 — 解析嵌套 ui_selectors
│   │   ├── ExtensionManager.cs           # 扩展管理器 — 加载/注册/执行 IExtension
│   │   ├── IExtension.cs                 # 扩展接口定义 — Name, Initialize, Run
│   │   ├── CoreHelper.cs                 # 核心辅助函数
│   │   └── FileHelper.cs                 # 文件操作辅助 — 原子写入、目录管理
│   ├── Decision/                         # 决策模块 (仅 README.md 占位)
│   ├── Persona/                          # 画像模块 (仅 README.md 占位)
│   ├── Memory/                           # 记忆模块 (仅 README.md 占位)
│   └── Report/                           # 报告模块 (仅 README.md 占位)
│
├── Platforms/                            # 平台专属实现
│   ├── Reddit/
│   │   └── RedditModule.cs               # Reddit 6 操作 (Initialize/Browse/Like/Comment/Follow/Share)
│   └── Instagram/
│       └── InstagramModule.cs            # Instagram 6 操作 + 速率限制 (60 actions/hour)
│
├── Extensions/                           # 可插拔扩展
│   ├── DataSources/
│   │   ├── IPLocationExtension.cs        # IP 地理位置数据
│   │   └── WeatherExtension.cs           # 天气数据
│   └── Hooks/                            # 钩子扩展 (目录存在但待实现)
│
├── ZDProjects/                           # ZennoDroid 项目脚本 (16 个 .cs)
│   ├── ModuleLoader.cs           (11.6K) # ★★ 通用模块加载器 — 带编译缓存 (273 行)
│   ├── Initializer_OwnCode.cs    (4.2K) # ZD Own Code: 加载 Initializer.cs
│   ├── Main_OwnCode.cs           (4.2K) # ZD Own Code: 加载 Main.cs
│   ├── SessionRunner_OwnCode.cs   (4.0K) # ZD Own Code: 加载 SessionRunner.cs
│   ├── PersonaCreate_OwnCode.cs   (4.0K) # ZD Own Code: 加载 PersonaCreate.cs
│   ├── DailyUpdate_OwnCode.cs     (4.0K) # ZD Own Code: 加载 DailyUpdate.cs
│   ├── WeeklyEvolve_OwnCode.cs    (4.0K) # ZD Own Code: 加载 WeeklyEvolve.cs
│   ├── StateSaver_OwnCode.cs      (4.3K) # ZD Own Code: 加载 StateSaver.cs
│   ├── Extension_OwnCode.cs      (4.8K) # ZD Own Code: 加载 Extension.cs
│   ├── ReportGen_OwnCode.cs       (4.0K) # ZD Own Code: 加载 ReportGen.cs
│   ├── Maintenance_OwnCode.cs     (4.0K) # ZD Own Code: 加载 Maintenance.cs
│   ├── Reddit_Browse.cs           (6.0K) # Reddit 浏览 — 独立脚本, 读取 ZD 变量中的 selectors
│   ├── Reddit_Like.cs             (6.2K) # Reddit 点赞
│   ├── Reddit_Comment.cs         (10.8K) # Reddit 评论 — 含 AI 生成评论内容
│   ├── Reddit_ReadPost.cs         (6.3K) # Reddit 阅读帖子
│   └── Tests/                            # 测试/诊断脚本 (仍用硬编码 selectors，有意保留)
│
├── Data/                                 # 运行时数据 (画像 JSON 等)
├── Persons/                              # 画像存储 ({device_id}_persona.json)
├── Memory/                               # 行为记忆 ({device_id}/{date}.json, long_term.json, social.json)
├── Stats/                                # 运行统计 ({device_id}_stats.json)
├── Logs/                                 # 程序日志
├── Reports/                              # 生成的报告
├── Docs/                                 # 项目文档 (10 个 .md + 9 个流程图)
│   ├── plans/                            # 实施计划
│   └── 流程图/                           # 架构流程图 (9 个 .md)
│
├── CHANGELOG.md                          # 版本更新日志
├── README.md                             # 项目简介
├── CHECKLIST.txt                         # 初始化检查清单
├── QUICK_START.txt                       # 快速入门
├── run.droid                             # ZennoDroid 项目文件
└── SYSTEM_BIBLE.md                       # ← 本文件
```

---

## 3. 执行架构

### 3.1 两套执行管线

DPS 有两条并行的执行路径。SessionRunner 优先使用管线 A，失败时逐级回退。

```
┌──────────────────────────────────────────────────────────────────────┐
│ ZennoDroid (.droid 项目)                                               │
│                                                                        │
│  管线 A: 统一引擎 (v4.5 首选)                                           │
│  OwnCode → ModuleLoader → SessionRunner.ExecuteWithUnifiedEngine()    │
│                            ↓                                           │
│                   ActionExecutor + Config/Operations/*.json             │
│                            ↓                                           │
│                   PageDetector → SelectorEngine → UILocator             │
│                                                                        │
│  管线 B: 平台模块 (v4.5 回退)                                           │
│  SessionRunner.LoadPlatformModule()                                    │
│       ↓                                                                │
│  Platforms/Reddit/RedditModule.cs  (编译时加载)                         │
│       ↓                                                                │
│  如果也失败 → ZDProjects/Reddit_*.cs (独立脚本，读 ZD 变量)             │
│                                                                        │
│  管线 C: 独立脚本 (旧架构，仍在使用)                                     │
│  OwnCode → ModuleLoader → ZDProjects/Reddit_*.cs                      │
│       ↓                                                                │
│  Core/ScriptHelpers.cs (由 ModuleLoader 自动注入公共函数)               │
└──────────────────────────────────────────────────────────────────────┘
```

### 3.2 ModuleLoader 动态编译流程 (ModuleLoader.cs, 273 行)

```
OwnCode.cs (ZD Own Code 动作块)
    │
    ▼
RunModule(filePath, methodName, args)
    │
    ├── 1. 检查编译缓存
    │   ├── BuildCacheKey(filePath) → 小写绝对路径
    │   ├── GetFileTimestamps(filePath) → 收集所有相关文件时间戳
    │   │   ├── 目标模块 .cs
    │   │   ├── Core/ 引擎文件 (ScriptHelpers, HumanizationEngine, UILocator, ErrorRecovery)
    │   │   ├── Modules/Core/ 核心依赖 (JsonHelper, AIService, FileHelper 等全部 .cs)
    │   │   ├── Extensions/ 所有 .cs (递归)
    │   │   └── 同目录同级 .cs 模块
    │   │
    │   └── IsCacheValid() ? → 是 → 直接返回缓存的 MethodInfo
    │
    ├── 2. 收集全部代码 (List<string> allCodes)
    │   ├── Core/ 引擎文件 (4 个: ScriptHelpers, HumanizationEngine, UILocator, ErrorRecovery)
    │   ├── Modules/Core/ 核心依赖 (全部 .cs)
    │   ├── Extensions/ 全部 .cs (递归搜索)
    │   ├── 同目录同级模块 (排除目标文件本身)
    │   └── 目标模块 .cs (最后加入)
    │
    ├── 3. 提取 using 语句 → 正则 "^using\s+[\w.]+;" → 去重 → 放最前面
    │
    ├── 4. CSharpCodeProvider.CompileAssemblyFromSource()
    │   ├── 引用: System.dll, System.Core.dll, System.Data.dll,
    │   │         System.Xml.dll, Microsoft.CSharp.dll
    │   └── GenerateInMemory = true
    │
    ├── 5. 反射获取 ClassName.MethodName → 缓存到 _methodCache
    │
    └── 6. method.Invoke(null, args)
         └── args = new object[] { project }
```

**缓存机制**:
- `static Dictionary<string, MethodInfo> _methodCache` — 编译后的方法缓存
- `static Dictionary<string, long> _timestampCache` — 所有相关文件的时间戳
- `static object _cacheLock` — 线程安全锁
- 任何相关文件修改 → 自动重新编译

### 3.3 OwnCode 入口脚本模式

每个 `*_OwnCode.cs` 都是相同结构，只是目标模块路径不同：

```csharp
// 1. 内嵌简化版 RunModule (不带缓存，用于首次加载)
Func<string, string, object[], object> RunModule = (filePath, methodName, args) => {
    // ... 收集代码 → 提取 using → 编译 → 反射调用
};

// 2. 读取 project_root，加载目标模块
try {
    string root = project.Variables["project_root"].Value;
    // 规范化路径 (补 \\ 结尾)
    string modulePath = root + "Modules\\<ModuleName>.cs";
    object result = RunModule(modulePath, "Run", new object[] { project });
    return result != null ? result.ToString() : "ERROR: 模块执行失败";
} catch (System.Exception ex) {
    return "ERROR: " + ex.Message;
}
```

### 3.4 SessionRunner 完整执行流程 (SessionRunner.cs, 853 行, 14 个方法)

```
SessionRunner.Run(project)
    │
    ├── 1. DeterminePlatform()
    │   └── 读 device_app_mapping.json → 获取当前设备应该操作的平台
    │
    ├── 2. 加载配置
    │   ├── BehaviorConfig.json → 动作权重 (browse:0.50, read:0.30, like:0.15, comment:0.04, post:0.01)
    │   ├── PlatformsConfig.json → 速率限制、ui_selectors
    │   └── StageConfig.json → 当前阶段参数
    │
    ├── 3. 初始化能量系统
    │   └── energy = 100.0, session_count 由 StageConfig 决定
    │
    ├── 4. 会话主循环 (session_count 次):
    │   │
    │   ├── a. AdjustWeightsForFatigue(energy)
    │   │   └── energy < 30 → comment/like 权重归 0
    │   │   └── energy < 50 → comment 权重 ×0.5
    │   │
    │   ├── b. WeightedChoice(adjustedWeights)
    │   │   └── 加权随机选择: browse/like/comment/follow/share
    │   │
    │   ├── c. EvaluatePostForAction(action, postData)
    │   │   └── 调用 RuleEngine 决策是否执行 (无帖子数据时返回 true)
    │   │
    │   ├── d. 执行动作 (3 级回退):
    │   │   ├── 优先: ExecuteWithUnifiedEngine(action, platform)
    │   │   │   ├── MapActionToOperations(action) → 操作名序列
    │   │   │   │   └── 考虑页面状态 (PageDetector): 如 read_post 需先 open_post
    │   │   │   └── ActionExecutor.Execute(operationJson) → 逐步执行
    │   │   │
    │   │   ├── 回退1: LoadPlatformModule(platform, action)
    │   │   │   └── 编译 Platforms/{platform}/{platform}Module.cs
    │   │   │
    │   │   └── 回退2: ZDProjects/Reddit_{action}.cs
    │   │       └── 通过 ScriptHelpers.cs 注入的公共函数执行
    │   │
    │   ├── e. UpdateEnergy(action)
    │   │   └── browse: -5, like: -8, comment: -15, follow: -10, share: -12
    │   │   └── 等待时自然恢复: +2/动作间隔
    │   │
    │   └── f. GetActionDelay(action)
    │       └── 从 BehaviorConfig 读取延迟范围 → 添加人性化随机
    │
    └── 5. 保存会话状态到 ZD 变量
```

**SessionRunner 方法清单**:

| 方法 | 行数 | 说明 |
|------|------|------|
| `Run` | 47-398 | 模块入口，完整会话循环 |
| `WeightedChoice` | 400-416 | 加权随机选择算法 |
| `GetActionDelay` | 418-431 | 获取动作间延迟 (ms) |
| `AdjustWeightsForFatigue` | 433-512 | 疲劳模型权重调整 |
| `MapActionToRuleAction` | 514-526 | 动作名映射到 RuleEngine 决策类型 |
| `EvaluatePostForAction` | 528-568 | 帖子评估决策 |
| `UpdateEnergy` | 570-600 | 能量消耗与恢复 |
| `DeterminePlatform` | 602-643 | 设备→平台映射 |
| `ExecuteWithUnifiedEngine` | 645-717 | v4.5 统一引擎入口 |
| `MapActionToOperations` | 719-787 | 动作→操作 JSON 序列映射 |
| `GetReadOperationName` | 789-811 | 平台特定的阅读操作名 |
| `LoadPlatformModule` | 813-851 | 旧模式平台模块加载 |

---

## 4. ZennoDroid 变量完整合约

所有变量通过 `project.Variables[name].Value` 读写。**共 52+ 个变量**。

### 4.1 必填系统变量 (4 个)

| 变量名 | 类型 | 默认值 | 说明 | 设置者 |
|--------|------|--------|------|--------|
| `project_root` | String | `C:\DPS_v4.5\` | **必须设置!** 项目根目录绝对路径 | 用户/ZD |
| `device_id` | String | `Device_001` | ZennoDroid 识别的设备唯一标识 | ZD 配置 |
| `current_app` | String | `reddit` | 当前操作的目标 APP 名称 | SessionRunner |
| `current_platform` | String | `reddit` | 当前所属的社交平台名称 | Initializer |

### 4.2 画像相关变量 (8 个)

| 变量名 | 说明 | 设置者 |
|--------|------|--------|
| `persona_json` | 完整画像 JSON (100+ 字段) | PersonaCreate / StateSaver |
| `persona_name` | 画像姓名 | Main |
| `persona_age` | 画像年龄 | Main |
| `current_stage` | 当前生命周期阶段 (`TTC`/`T1`/`T2`/`T3`/`PP0`/`PP1`/`NP`) | Initializer |
| `humanization_profile` | 行为配置名 (`speed_demon`/`casual`/`deep_reader`/`distracted`) | SessionRunner |
| `force_regenerate` | 是否强制重新生成画像 (`true`/`false`) | 用户手动 |
| `persona_path` | 画像文件路径 | Initializer |
| `stats_path` | 统计文件路径 | Initializer |

### 4.3 会话运行变量 (10 个)

| 变量名 | 说明 | 设置者 |
|--------|------|--------|
| `session_action_count` | 本次会话已执行动作数 | SessionRunner |
| `session_energy` | 当前能量值 (0-100) | SessionRunner |
| `session_platform` | 本次会话的平台 | SessionRunner |
| `action_type` | 当前执行的动作类型 | SessionRunner |
| `action_result` | 当前动作结果 (`SUCCESS`/`SKIP`/`ERROR`) | SessionRunner |
| `action_params_json` | 动作参数 JSON | SessionRunner |
| `next_action` | 逻辑预测的下一个动作 | SessionRunner |
| `last_action_log` | 最后一次动作的完整日志 | SessionRunner |
| `browse_scroll_count` | 浏览时滚动次数 | Reddit_Browse.cs |
| `browse_scroll_delay` | 浏览时滚动间隔 (ms) | Reddit_Browse.cs |

### 4.4 操作结果变量 (6 个)

| 变量名 | 说明 |
|--------|------|
| `browse_result` | 浏览结果 (`SUCCESS`/`ERROR`) |
| `like_result` | 点赞结果 |
| `like_post_index` | 点赞目标帖子索引 |
| `comment_text` | 评论内容 (AI 生成) |
| `comment_result` | 评论结果 |
| `follow_result` | 关注结果 |

### 4.5 设备/屏幕变量 (2 个)

| 变量名 | 默认值 | 说明 |
|--------|--------|------|
| `screen_width` | `1080` | 屏幕宽度 (px) |
| `screen_height` | `2400` | 屏幕高度 (px) |

### 4.6 Reddit Selector 变量 (v4.5.1 新增, 7 个)

由 `RedditModule.cs` 初始化时从 `PlatformsConfig.json` 的嵌套 `ui_selectors` 中读取并设置到 ZD 变量，供 `ZDProjects/Reddit_*.cs` 脚本使用：

| 变量名 | PlatformsConfig 路径 | 默认值 |
|--------|---------------------|--------|
| `reddit_sel_post_unit` | `ui_selectors.post_unit.value` | `post_unit` |
| `reddit_sel_post_footer` | `ui_selectors.post_footer.value` | `post_footer` |
| `reddit_sel_upvote_button` | `ui_selectors.upvote_button.value` | `post_footer_first_child` |
| `reddit_sel_comment_button` | `ui_selectors.comment_button.value` | `comment_button` |
| `reddit_sel_submit_button` | `ui_selectors.submit_button.value` | `submit_comment` |
| `reddit_sel_follow_button` | `ui_selectors.follow_button.value` | `follow_button` |
| `reddit_sel_share_button` | `ui_selectors.share_button.value` | `share_button` |

### 4.7 Instagram 速率限制变量 (5 个)

| 变量名 | 说明 |
|--------|------|
| `instagram_actions_this_hour` | 本小时总动作计数 |
| `instagram_likes_this_hour` | 本小时点赞计数 |
| `instagram_comments_this_hour` | 本小时评论计数 |
| `instagram_follows_this_hour` | 本小时关注计数 |
| `instagram_hour_start` | 当前计时周期起始时间 |

### 4.8 ErrorRecovery 变量 (7 个, 可选)

| 变量名 | 说明 |
|--------|------|
| `error_count_browse` | 浏览操作连续失败计数 |
| `error_count_like` | 点赞操作连续失败计数 |
| `error_count_comment` | 评论操作连续失败计数 |
| `error_count_follow` | 关注操作连续失败计数 |
| `error_count_share` | 分享操作连续失败计数 |
| `last_error_type` | 最后一次错误类型 |
| `last_error_time` | 最后一次错误时间 |

---

## 5. 配置文件详解

### 5.1 AIConfig.json — AI 服务 (3 级 fallback)

```json
{
    "version": "4.5",
    "models": {
        "primary": {
            "provider": "gemini",
            "model": "gemini-3-flash-preview",
            "api_key": "AIzaSy...",
            "base_url": "https://generativelanguage.googleapis.com/v1beta",
            "timeout_ms": 60000,
            "max_tokens": 8192,
            "temperature": 0.7
        },
        "fallback": {
            "provider": "openai",
            "model": "claude-sonnet-4-5-thinking",
            "api_key": "sk-6poy...",
            "base_url": "https://deeprouter.top/v1",
            "timeout_ms": 60000,
            "max_tokens": 4096
        },
        "backup": {
            "provider": "openai",
            "model": "gpt-5.2-2025-12-11",
            "api_key": "sk-zk-...",
            "base_url": "https://api.shubiaobiao.cn/v1",
            "timeout_ms": 6000,
            "max_tokens": 4096
        }
    },
    "retry_config": { "max_retries": 3, "backoff_ms": 2000 },
    "usage_limits": { "max_requests_per_hour": 100, "max_tokens_per_day": 500000 }
}
```

### 5.2 PlatformsConfig.json — 平台配置 (★ 最重要)

```json
{
    "version": "2.0",
    "platforms": {
        "reddit": {
            "name": "Reddit",
            "package_name": "com.reddit.frontpage",
            "enabled": true,
            "rate_limits": {
                "max_actions_per_hour": 120,
                "max_likes_per_hour": 40,
                "max_comments_per_hour": 20,
                "max_follows_per_hour": 15,
                "min_action_delay_ms": 2000,
                "max_action_delay_ms": 8000
            },
            "ui_selectors": {
                "post_unit": {
                    "strategy": "resource-id",
                    "value": "post_unit",
                    "fallback_strategy": "class",
                    "fallback_value": "android.widget.FrameLayout"
                },
                "upvote_button": {
                    "strategy": "resource-id",
                    "value": "post_footer_first_child",
                    "fallback_strategy": "content-desc",
                    "fallback_value": "Upvote"
                }
                // ... post_footer, comment_button, share_button, follow_button,
                //     comment_input, submit_button, post_title, post_body,
                //     subreddit_name, author_name, toolbar, back_button
            },
            "page_signatures": {
                "feed": { "threshold": 0.5, "signals": [...] }
                // ... post_detail, comment_section 等
            },
            "action_weights": { "browse": 40, "like": 30, "comment": 15, "follow": 10, "share": 5 },
            "scroll_config": { ... },
            "error_thresholds": { ... }
        },
        "instagram": { "enabled": true, "package_name": "com.instagram.android", ... },
        "tiktok":    { "enabled": false, "package_name": "com.zhiliaoapp.musically", ... },
        "facebook":  { "enabled": false, "package_name": "com.facebook.katana", ... }
    },
    "device_platform_mapping": { "default": "reddit", "device_001": "reddit", "device_002": "instagram" }
}
```

> **⚠️ 关键**: `ui_selectors` 是 **嵌套对象**，不是平面字符串!
> 读取时必须用 `GetJsonSection` 先提取子对象，再用 `GetJsonValue` 读 `value` 字段。
> 直接 `GetJsonValue(selectors, "post_unit")` 会返回 `{` 而不是预期值。
> v4.5.1 提供了 `GetSelectorValue(selectorsJson, selectorName, defaultVal)` 辅助函数。

### 5.3 StageConfig.json — 7 阶段生命周期

| 阶段 | 名称 | sessions/天 | 分钟/次 | 夜间活动 | attention | mental_health | primary_concerns |
|------|------|:-----------:|:-------:|:--------:|:---------:|:-------------:|-----------------|
| TTC | 备孕期 | 6 | 12 | 8% | ×1.0 | ×1.0 | 备孕、排卵追踪 |
| T1 | 孕早期 (1-13w) | 7 | 10 | 12% | ×0.9 | ×0.8 | 晨吐、产检焦虑 |
| T2 | 孕中期 (14-27w) | 7 | 12 | 10% | ×1.0 | ×1.1 | 胎动、彩超 |
| T3 | 孕晚期 (28-42w) | 6 | 11 | 12% | ×0.9 | ×0.9 | 分娩计划、待产包 |
| PP0 | 产后早期 (0-3m) | **10** | **7** | **25%** | ×0.75 | ×0.6 | 新生儿睡眠、母乳 |
| PP1 | 产后中期 (4-12m) | 8 | 9 | 15% | ×0.85 | ×0.85 | 辅食、里程碑 |
| NP | 育儿期 (13+m) | 6 | 12 | 8% | ×1.0 | ×1.0 | 家庭日常 |

### 5.4 BehaviorConfig.json — 行为参数

```json
{
    "actions": {
        "browse":    { "weight_base": 0.50, "duration_sec_min": 30, "duration_sec_max": 90 },
        "read_post": { "weight_base": 0.30, "duration_sec_min": 20, "duration_sec_max": 70 },
        "like":      { "weight_base": 0.15, "probability_base": 0.35 },
        "comment":   { "weight_base": 0.04, "probability_base": 0.12, "typing_speed_wpm_base": 40 },
        "post":      { "weight_base": 0.01, "probability_base": 0.02, "cooldown_hours": 24 }
    },
    "typing": {
        "regular":    { "wpm_min": 30, "wpm_max": 50, "error_rate_min": 0.08, "error_rate_max": 0.15 },
        "proficient": { "wpm_min": 50, "wpm_max": 80, "error_rate_min": 0.03, "error_rate_max": 0.08 }
    },
    "scrolling": { "pause_probability": 0.30, "scroll_pixels_min": 200, "scroll_pixels_max": 600 },
    "session":   { "min_duration_minutes": 3, "max_duration_minutes": 30, "max_read_cycles": 8 }
}
```

---

## 6. 关键代码模式

### 6.1 ZD 变量读写 (所有模块统一模式)
```csharp
// C# 5.0 — 必须使用 Func/Action 委托而非方法定义
Func<string, string, string> GetVar = (name, def) => {
    try {
        string v = project.Variables[name].Value;
        return string.IsNullOrEmpty(v) ? def : v;
    } catch { return def; }
};

Action<string, string> SetVar = (name, val) => {
    try { project.Variables[name].Value = val ?? ""; } catch { }
};
```

### 6.2 JSON 解析 (JsonHelper.cs, 993 行, 23 个方法)

```csharp
// 获取顶层字段值 (深度感知，只匹配 depth=1)
string value = JsonHelper.Get(json, "field_name", "default");

// 获取嵌套字段 (点号路径)
string nested = JsonHelper.GetNested(json, "parent.child.field", "default");

// 提取嵌套对象 (返回完整子 JSON)
string subObject = JsonHelper.ExtractObject(json, "section_name");

// 提取数组
string arrayJson = JsonHelper.ExtractArray(json, "items");
string[] elements = JsonHelper.GetArray(json, "items");

// 类型转换
int count = JsonHelper.GetInt(json, "count", 0);
double rate = JsonHelper.GetDouble(json, "rate", 0.5);
bool enabled = JsonHelper.GetBool(json, "enabled", false);

// 设置/修改值
string updated = JsonHelper.Set(json, "field", "new_value");

// 转义/反转义
string escaped = JsonHelper.Escape(rawString);
string unescaped = JsonHelper.Unescape(jsonString);

// 验证
bool valid = JsonHelper.IsValidJson(text);

// 构造
string obj = JsonHelper.CreateObject(new string[]{"key1","val1","key2","val2"});
string arr = JsonHelper.CreateArray(new string[]{"a","b","c"});
```

### 6.3 v4.5.1 Selector 读取模式

```csharp
// ScriptHelpers.cs 中定义 (由 ModuleLoader 自动注入)
Func<string, string, string, string> GetSelectorValue = (selectorsJson, selectorName, defaultVal) => {
    string selectorObj = GetJsonSection(selectorsJson, selectorName);
    if (string.IsNullOrEmpty(selectorObj)) return defaultVal;
    string val = GetJsonValue(selectorObj, "value", "");
    return string.IsNullOrEmpty(val) ? defaultVal : val;
};

// 使用方式 (Reddit_Browse.cs):
string selPostUnit = GetVar("reddit_sel_post_unit", "post_unit");
// 或从 PlatformsConfig 直接读:
string selectors = GetJsonSection(platformConfig, "ui_selectors");
string postUnitId = GetSelectorValue(selectors, "post_unit", "post_unit");
```

### 6.4 UI 操作模式

```csharp
// 获取屏幕 XML 布局
string layout = instance.DroidInstance.Hierarchy.GetLayout();

// 查找元素 (通过 resource-id)
List<string> bounds = FindBoundsByResourceId(layout, selectorValue);

// 解析 bounds "[x1,y1][x2,y2]" → 中心点
Tuple<int, int> center = GetCenter(ParseBounds(bounds[0]));

// 人性化点击 (带随机偏移)
HumanizedTap(center.Item1, center.Item2, screenBounds, profileConfig, rnd);

// 人性化滑动 (带曲线弯曲)
HumanizedSwipe(startX, startY, endX, endY, durationMs, profileConfig, rnd);
```

### 6.5 ScriptHelpers.cs 公共函数清单

| 函数 | 签名 | 说明 |
|------|------|------|
| `GetVar` | `Func<string, string, string>` | 安全读取 ZD 变量 |
| `SetVar` | `Action<string, string>` | 安全设置 ZD 变量 |
| `GetProfileConfig` | `Func<string, Dictionary<string, double>>` | 获取行为配置参数 |
| `HumanizedDelay` | `Func<int, Dictionary, Random, int>` | 基于配置的随机延迟 |
| `HumanizedTap` | `Action<int, int, int[], Dictionary, Random>` | 带偏移的点击 |
| `HumanizedSwipe` | `Action<int, int, int, int, int, Dictionary, Random>` | 带弯曲的滑动 |
| `GetCenter` | `Func<int[], Tuple<int,int>>` | bounds 中心点 |
| `FindBoundsByResourceId` | `Func<string, string, List<string>>` | 按 resource-id 查找 |
| `ExtractAllText` | `Func<string, List<string>>` | 提取所有 text 属性 |
| `FindNodesByResourceId` | `Func<string, string, List<string>>` | 查找完整节点 |
| `ExtractBoundsFromNode` | `Func<string, string>` | 从节点提取 bounds |
| `FindEditTextBounds` | `Func<string, List<string>>` | 查找输入框 |

---

## 7. 人性化引擎

### 7.1 行为配置 (ScriptHelpers.cs 中的实际代码)

> **注意**: 代码中使用的配置名是 `speed_demon`/`casual`/`deep_reader`/`distracted`。

| 参数 | speed_demon | casual (默认) | deep_reader | distracted |
|------|:-----------:|:------------:|:-----------:|:----------:|
| `base_delay_mult` | 0.6 | 1.0 | 1.5 | 1.8 |
| `delay_variance` | 0.15 | 0.25 | 0.35 | 0.45 |
| `tap_offset_max` | 5.0 | 15.0 | 10.0 | 25.0 |
| `swipe_bending_range` | 10.0 | 30.0 | 40.0 | 60.0 |
| `prob_accidental_back` | 0.01 | 0.03 | 0.05 | 0.08 |
| `prob_scroll_back` | 0.01 | 0.02 | 0.04 | 0.06 |
| `prob_longer_pause` | 0.02 | 0.05 | 0.10 | 0.15 |
| `prob_double_tap` | 0.005 | 0.01 | 0.02 | 0.03 |

### 7.2 HumanizedDelay 算法

```csharp
int HumanizedDelay(int baseDelayMs, Dictionary<string, double> profile, Random rnd) {
    double mult = profile["base_delay_mult"];
    double variance = profile["delay_variance"];
    double adjusted = baseDelayMs * mult;
    double jitter = adjusted * variance * (rnd.NextDouble() * 2 - 1);
    return Math.Max(100, (int)(adjusted + jitter));
}
```

---

## 8. Extension 系统

```
IExtension 接口 (Modules/Core/IExtension.cs)
├── Name: string           # 扩展名
├── Category: string       # 类别 ("DataSource" / "Hook")
├── Version: string        # 版本
├── Enabled: bool          # 是否启用
├── Initialize(project, configJson)  # 初始化
└── Run(project): string   # 执行，返回结果 JSON

ExtensionManager (Modules/Core/ExtensionManager.cs)
├── Register(IExtension ext)           # 注册扩展
├── LoadFromRegistry(registryPath)     # 从 ExtensionsRegistry.json 加载
├── RunCategory(category): string      # 按类别执行全部扩展
└── GetResult(extensionName): string   # 获取特定扩展结果

Extension.cs (入口模块)
├── RegisterBuiltinExtensions()        # 注册内置扩展
│   ├── new IPLocationExtension()
│   └── new WeatherExtension()
├── ExtensionManager.LoadFromRegistry()
├── ExtensionManager.RunCategory("DataSource")
└── ExtensionManager.RunCategory("Hook")
```

---

## 9. 开发注意事项

### 9.1 绝对禁止 ❌

| 禁止 | 替代方案 |
|------|----------|
| `$"string {var}"` | `"string " + var` |
| `obj?.Method()` | `if (obj != null) obj.Method()` |
| `nameof(x)` | `"x"` (手写字符串) |
| `auto-property { get; } = value` | `{ get; set; }` + 构造函数赋值 |
| `using Newtonsoft.Json` | `JsonHelper.cs` 纯字符串解析 |
| `??=` 运算符 | `if (x == null) x = value;` |
| `out var` | `Type x; ... out x` |
| 硬编码 UI selector 字符串 | 从 `PlatformsConfig.json` 或 ZD 变量读取 |

### 9.2 文件修改后的生效方式

| 文件类型 | 修改后 | 说明 |
|----------|--------|------|
| `Modules/*.cs` | 自动生效 | ModuleLoader 检测时间戳变化自动重新编译 |
| `Core/*.cs` | 自动生效 | 同上 (作为编译依赖被跟踪) |
| `Config/*.json` | 立即生效 | 每次运行时读取 |
| `ZDProjects/*_OwnCode.cs` | **需手动复制** | 必须粘贴到 ZD 的 Own Code 动作块 |
| `ZDProjects/Reddit_*.cs` | 自动生效 | 由 ModuleLoader 编译 |

### 9.3 新增平台步骤

1. `PlatformsConfig.json` — 添加平台配置 (ui_selectors, rate_limits, page_signatures)
2. `Platforms/<Name>/<Name>Module.cs` — 创建平台模块 (继承 PlatformBase)
3. `Config/Operations/<name>_operations.json` — 创建操作步骤 JSON
4. `device_app_mapping.json` — 添加设备→平台映射
5. 可选: `ZDProjects/<Name>_*.cs` — 独立操作脚本

### 9.4 新增扩展步骤

1. `Extensions/DataSources/<Name>Extension.cs` — 实现 `IExtension`
2. `ExtensionsRegistry.json` — 注册
3. `Extension.cs` 的 `RegisterBuiltinExtensions()` — 添加 `Register(new ...())`

---

## 10. 版本历史

| 版本 | 日期 | 主要变更 |
|------|------|---------|
| v4.0.0 | 2026-01 | 初始版本：动态编译框架、基础模块 |
| v4.0.1 | 2026-01 | 动态配置加载、AI 重试机制 |
| v4.0.2 | 2026-01 | Bug 修复 (JSON 解析、文件处理、周演化) |
| v4.1.0 | 2026-02 | ModuleLoader 编译缓存优化、JsonHelper 栈式解析器重写 |
| v4.5.0 | 2026-02-07 | **重大更新**: 多平台支持、Core 引擎模块 (HumanizationEngine/UILocator/ErrorRecovery)、Extension 系统、ActionExecutor + PageDetector + SelectorEngine |
| v4.5.1 | 2026-02-13 | 修复 selector 解析 bug、配置驱动 selectors、ZD 变量导出、文档全面更新 |

---

## 11. 已知技术债 & 未来计划

### 11.1 技术债

| 项目 | 说明 |
|------|------|
| 子目录占位符 | `Modules/Decision/`, `Modules/Persona/`, `Modules/Memory/`, `Modules/Report/` 仅有 README.md |
| Hooks 未实现 | `Extensions/Hooks/` 目录为空 |
| 测试硬编码 | `ZDProjects/Tests/` 中的测试脚本仍使用硬编码 selectors (有意保留用于诊断) |
| Instagram 无独立脚本 | 没有 `ZDProjects/Instagram_*.cs`，依赖 ActionExecutor |
| TikTok/Facebook | 配置已在 PlatformsConfig.json 中但 `enabled: false` |
| 行为配置名不一致 | 代码中用 `speed_demon`/`deep_reader`/`distracted`，文档已更新为 `active`/`lurker`/`new_user` |

### 11.2 推荐下一步

1. 统一行为配置名称 — 决定代码和文档使用同一套名称
2. 实现 `Modules/Decision/` — 将 RuleEngine 中的决策逻辑模块化
3. 实现 `Extensions/Hooks/` — session_start/session_end 钩子
4. 创建 `Instagram_*.cs` ZDProjects 脚本
5. ZennoDroid 环境集成测试
6. 启用 TikTok 平台

---

## 12. 快速启动提示 (Startup Prompt)

如需在新对话中继续开发，使用以下 Prompt:

```
我正在开发 DPS v4.5 项目，这是一个基于 ZennoDroid 的人格驱动行为仿真框架。
项目位于: C:\Users\Hu\.gemini\zennoDroid\DPS_v4.5\
请先阅读项目根目录的 SYSTEM_BIBLE.md 了解完整的项目架构和技术约束。
当前版本: v4.5.1，所有 Phase 1-4 实施计划已完成。
核心约束: C# 5.0 语法，无第三方 NuGet 包，纯字符串 JSON 解析。
```

---

*本文档由 AI 助手生成并维护。如有重大架构变更，请同步更新本文件。*
*文档版本: v2.0 | 最后更新: 2026-02-14*
