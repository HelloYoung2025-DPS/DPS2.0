# DPS v4.5 项目上下文

## 1. 项目概述

DPS v4.5 (Dynamic Persona Simulation) 是基于 ZennoDroid 的人格驱动行为仿真框架。让 Android 设备上的社交媒体账号像真人一样自主浏览、点赞、评论、关注，并随时间自然演化行为模式。

核心理念：每个账号拥有独立的"人格画像"（Persona），包含年龄、性格、兴趣、生活阶段等属性。行为决策由规则引擎 + AI 共同驱动，模拟真实用户的浏览习惯、疲劳感、情绪波动。

---

## 2. 技术栈

| 层级 | 技术 |
|------|------|
| 语言 | C# 5.0 (Modules/) + C# 7.0+ (ZDProjects/) |
| 运行时 | .NET Framework，ZennoDroid 7.x+ 嵌入式 |
| 平台 | ZennoDroid on Windows |
| 编译 | CSharpCodeProvider 动态编译 + LRU 缓存（32 条目） |
| 可用引用 | System.dll, System.Core.dll, System.Data.dll, System.Xml.dll, Microsoft.CSharp.dll |
| JSON | 手写 JsonHelper.cs（993 行，23 方法），无第三方依赖 |
| AI | 3 级 fallback：Gemini → Claude → GPT |
| 存储 | 纯文件系统（JSON 文件），无数据库 |

---

## 3. 完整目录结构

```
DPS_v4.5/
├── .omo.conf                    # 项目约束配置（轻量级）
├── .omo/                        # .omo 2.0 完整状态管理
├── Config/                      # 14 个 JSON 配置文件
│   ├── AIConfig.json            # AI 模型配置 — 3 级 fallback，含真实 API Key
│   ├── Apps.json                # 应用包名注册表
│   ├── BehaviorConfig.json      # 行为参数 — 动作权重、打字速度
│   ├── DecisionConfig.json      # 决策引擎规则 — 帖子评估阈值
│   ├── EvolutionRules.json      # 画像演化规则
│   ├── ExtensionsConfig.json    # 扩展全局配置
│   ├── ExtensionsRegistry.json  # 扩展注册表
│   ├── MaintenanceConfig.json   # 维护任务配置
│   ├── Operations/              # ActionExecutor 操作步骤定义
│   │   ├── reddit_operations.json
│   │   └── instagram_operations.json
│   ├── PersonaPrompt.txt        # AI 画像生成 Prompt 模板
│   ├── PlatformsConfig.json     # 核心！平台配置 — ui_selectors, rate_limits
│   ├── StageConfig.json         # 7 阶段生命周期配置
│   ├── ValidationRules.json     # 数据验证规则
│   └── device_app_mapping.json  # 设备→平台映射
├── Core/                        # ZD 脚本层引擎（5 个文件）
│   ├── ScriptHelpers.cs         # ZD 公共函数库（GetVar/SetVar/Humanization）
│   ├── HumanizationEngine.cs    # 人性化行为引擎 — 4 种配置
│   ├── UILocator.cs             # 多策略 UI 元素定位
│   ├── ErrorRecovery.cs         # 错误恢复 — 指数退避重试
│   └── PlatformBase.cs          # 平台基类接口定义
├── Modules/                     # 业务逻辑模块（14 个 .cs）
│   ├── Main.cs                  # 主入口 — 检查画像状态，分发流程
│   ├── Initializer.cs           # 初始化 — 创建目录、验证配置
│   ├── SessionRunner.cs         # 核心！会话执行引擎（853 行）
│   ├── PersonaCreate.cs         # AI 画像生成
│   ├── DailyUpdate.cs           # 每日更新 — 年龄/孕周/季节
│   ├── WeeklyEvolve.cs          # 每周 AI 进化 — 分析行为调整画像
│   ├── MemoryManager.cs         # 交互记忆管理（738 行，24 方法）
│   ├── RuleEngine.cs            # 规则引擎 — 帖子评估决策
│   ├── StateSaver.cs            # 状态持久化
│   ├── ReportGen.cs             # 报告生成
│   ├── Maintenance.cs           # 维护任务 — 清理过期数据
│   ├── Extension.cs             # 扩展入口
│   ├── UIHelper.cs              # UI 辅助 — Android XML 解析
│   └── Core/                    # 核心工具（9 个文件）
│       ├── JsonHelper.cs        # 核心！JSON 解析器（993 行）
│       ├── AIService.cs         # AI API 调用 — 3 级 fallback
│       ├── ActionExecutor.cs    # 统一操作执行器
│       ├── CoreHelper.cs        # 日志/变量/文件工具
│       ├── FileHelper.cs        # 文件操作 — 原子写入
│       ├── ExtensionManager.cs  # 扩展管理器
│       ├── IExtension.cs        # 扩展接口定义
│       ├── PageDetector.cs      # 页面状态检测
│       └── SelectorEngine.cs    # UI 选择器引擎
├── Platforms/                   # 平台实现
│   ├── Reddit/
│   │   └── RedditModule.cs      # Reddit 6 种操作
│   └── Instagram/
│       └── InstagramModule.cs   # Instagram 6 种操作 + 速率限制
├── Extensions/                  # 可插拔扩展
│   ├── DataSources/
│   │   ├── IPLocationExtension.cs
│   │   └── WeatherExtension.cs
│   └── Hooks/                   # 待实现（目录为空）
├── ZDProjects/                  # ZD Own Code 入口脚本
│   ├── ModuleLoader.cs          # 通用模块加载器（273 行）
│   ├── *_OwnCode.cs             # 10 个模块入口脚本
│   ├── Reddit_*.cs              # 4 个 Reddit 操作脚本
│   └── Tests/                   # 测试/诊断脚本
├── Persons/                     # 画像存储（运行时生成）
├── Memory/                      # 行为记忆（运行时生成）
├── Logs/                        # 日志（运行时生成）
├── Reports/                     # 报告（运行时生成）
├── Docs/                        # 项目文档
├── SYSTEM_BIBLE.md              # 928 行完整技术手册
├── CHANGELOG.md                 # 版本历史
└── run.droid                    # ZennoDroid 项目文件
```

---

## 4. 执行流程

```
ZD 启动
  │
  ▼
Initializer
  创建运行时目录（Persons/Memory/Logs/Reports）
  验证 Config/ 下所有 JSON 配置文件
  │
  ▼
Main
  读取画像状态，分发到对应子流程
  ├─ NEED_CREATE_PERSONA → PersonaCreate
  │     调用 AIService（Gemini → Claude → GPT fallback）
  │     生成画像 JSON，写入 Persons/{id}.json
  │
  ├─ NEED_DAILY_UPDATE → DailyUpdate
  │     更新年龄、孕周、季节字段
  │     触发阶段切换检测
  │
  └─ READY → 继续主流程
  │
  ▼
Extension
  IPLocationExtension（获取设备 IP 地理位置）
  WeatherExtension（获取当地天气，影响行为权重）
  │
  ▼
SessionRunner（核心，853 行）
  加权随机选择动作序列
  疲劳模型（连续操作后降低活跃度）
  调用 RuleEngine 评估每个帖子
  调用 MemoryManager 记录交互
  通过 ActionExecutor 执行具体 UI 操作
  │
  ▼
StateSaver
  持久化画像状态（Persons/{id}.json）
  更新统计数据（今日操作数、互动率等）
  │
  ▼
Maintenance
  清理 Memory/ 中超过保留期的记忆条目
  压缩过期日志
  │
  ▼
ReportGen
  仅在 17:00 后执行
  生成当日行为报告，写入 Reports/
```

---

## 5. 7 阶段生命周期

| 阶段码 | 名称 | 触发条件 | sessions/天 | 分钟/次 |
|--------|------|----------|:-----------:|:-------:|
| TTC | 备孕期 | 未怀孕，无孩子 | 6 | 12 |
| T1 | 孕早期 | 孕 1-13 周 | 7 | 10 |
| T2 | 孕中期 | 孕 14-27 周 | 7 | 12 |
| T3 | 孕晚期 | 孕 28+ 周 | 6 | 11 |
| PP0 | 产后早期 | 孩子 0-3 月 | 10 | 7 |
| PP1 | 产后中期 | 孩子 4-12 月 | 8 | 9 |
| NP | 育儿期 | 孩子 13+ 月 | 6 | 12 |

阶段配置存储在 `Config/StageConfig.json`，由 `DailyUpdate.cs` 在每日更新时检测并切换。

---

## 6. 关键模块说明

### SessionRunner.cs（853 行）
会话执行的核心。负责：
- 从 BehaviorConfig 读取动作权重，加权随机选择下一个动作
- 疲劳模型：连续执行同类动作后，该动作权重临时降低
- 时间感知：根据当前时间（早/午/晚）调整行为模式
- 调用 HumanizationEngine 在每次操作前后插入随机延迟

### JsonHelper.cs（993 行，23 方法）
项目唯一的 JSON 处理工具，无第三方依赖。关键方法：
- `GetJsonValue(json, key)` — 读取顶层字段
- `GetNestedValue(json, path)` — 读取嵌套路径（用点分隔）
- `SetJsonValue(json, key, value)` — 修改字段并返回新 JSON 字符串
- `ParseJsonArray(json, key)` — 解析数组为 `List<string>`

### SelectorEngine.cs
专门解析 `PlatformsConfig.json` 中的 `ui_selectors` 嵌套对象。不能用 `JsonHelper.GetJsonValue` 直接读取，必须通过 `SelectorEngine.GetSelector(platform, action, element)` 获取选择器字符串。

### MemoryManager.cs（738 行，24 方法）
管理账号的交互记忆：
- 记录已互动过的帖子/用户，避免重复操作
- 按时间衰减记忆权重
- 提供"相似内容偏好"计算，影响 RuleEngine 评分

### AIService.cs
封装 3 级 AI fallback：
1. Gemini（主力）
2. Claude（备用）
3. GPT（最终兜底）

每级失败后自动切换，全部失败则返回预设默认值，不中断流程。

---

## 7. 已知技术债

- `Modules/Decision/`、`Modules/Persona/`、`Modules/Memory/`、`Modules/Report/` 子目录仅有 README 占位，功能尚未迁移
- `Extensions/Hooks/` 目录为空，钩子机制未实现
- 行为配置名不一致：代码中使用 `speed_demon / casual / deep_reader / distracted`，部分文档写的是 `active / lurker / new_user`
- Instagram 无独立 ZDProjects 脚本，完全依赖 ActionExecutor 通过 operations JSON 驱动
- TikTok 和 Facebook 的平台配置已存在于 PlatformsConfig.json，但对应 Module 未实现，未启用

---

## 8. 重要注意事项

**API Key 安全**
`Config/AIConfig.json` 包含真实的 Gemini/Claude/GPT API Key，已在 `.gitignore` 中排除，不得提交到版本控制。

**ZD Own Code 同步**
`ZDProjects/*_OwnCode.cs` 修改后必须手动复制到 ZennoDroid 项目对应的 Own Code 动作块中，ZD 不会自动读取文件系统上的变更。

**其他模块自动重编译**
`Modules/` 下的 `.cs` 文件由 `ModuleLoader.cs` 在运行时动态编译。ModuleLoader 检测文件时间戳，有变更时自动重新编译，无需手动操作。

**ui_selectors 解析规则**
`PlatformsConfig.json` 中的 `ui_selectors` 是多层嵌套对象，必须通过 `SelectorEngine.GetSelector()` 读取，直接调用 `JsonHelper.GetJsonValue` 只能拿到原始 JSON 字符串，无法正确解析。

**C# 版本限制**
`Modules/` 下的代码必须兼容 C# 5.0（CSharpCodeProvider 默认版本）。禁止使用 C# 6.0+ 语法（字符串插值 `$""`、null 条件运算符 `?.`、nameof 等）。`ZDProjects/` 层可以使用 C# 7.0+。
