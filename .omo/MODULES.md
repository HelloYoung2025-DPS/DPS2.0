# 模块清单

**最后更新**：2026-02-22

---

## Core 工具层 (Modules/Core/) — 9 个基础模块

| 模块 | 文件 | 行数 | 状态 | 说明 |
|------|------|------|------|------|
| JsonHelper | Modules/Core/JsonHelper.cs | 993 | ✅ 稳定 | 纯字符串 JSON 解析器，栈式解析，23 个方法 |
| AIService | Modules/Core/AIService.cs | — | ✅ 稳定 | AI API 调用，3 级 fallback，HTTP 请求 |
| ActionExecutor | Modules/Core/ActionExecutor.cs | — | ✅ 稳定 | v4.5 统一操作执行器，JSON 步骤驱动 |
| CoreHelper | Modules/Core/CoreHelper.cs | — | ✅ 稳定 | 日志/变量/文件/时间工具函数 |
| FileHelper | Modules/Core/FileHelper.cs | — | ✅ 稳定 | 文件操作，原子写入，目录管理 |
| ExtensionManager | Modules/Core/ExtensionManager.cs | — | ✅ 稳定 | 扩展加载/注册/执行 |
| IExtension | Modules/Core/IExtension.cs | — | ✅ 稳定 | 扩展接口定义 |
| PageDetector | Modules/Core/PageDetector.cs | — | ✅ 稳定 | 页面状态检测，基于 page_signatures |
| SelectorEngine | Modules/Core/SelectorEngine.cs | — | ✅ 稳定 | UI 选择器引擎，解析嵌套 ui_selectors |

---

## 业务逻辑层 (Modules/) — 14 个业务模块

| 模块 | 文件 | 行数 | 状态 | 依赖 | 说明 |
|------|------|------|------|------|------|
| Main | Modules/Main.cs | ~300 | ✅ 稳定 | CoreHelper, JsonHelper, AIService | 主入口，检查画像状态，决定执行分支 |
| Initializer | Modules/Initializer.cs | ~200 | ✅ 稳定 | CoreHelper, FileHelper | 初始化，创建目录，验证配置完整性 |
| SessionRunner | Modules/SessionRunner.cs | 853 | ✅ 稳定 | CoreHelper, JsonHelper, ActionExecutor, RuleEngine, MemoryManager, PageDetector | 核心会话执行引擎，加权随机动作，疲劳模型 |
| PersonaCreate | Modules/PersonaCreate.cs | ~160 | ✅ 稳定 | CoreHelper, AIService, JsonHelper | AI 画像生成，使用 PersonaPrompt.txt |
| DailyUpdate | Modules/DailyUpdate.cs | ~200 | ✅ 稳定 | CoreHelper, JsonHelper, FileHelper | 每日更新年龄/孕周/季节/阶段码 |
| WeeklyEvolve | Modules/WeeklyEvolve.cs | ~300 | ✅ 稳定 | CoreHelper, AIService, JsonHelper, FileHelper | 每周 AI 进化，分析 7 天行为调整画像 |
| MemoryManager | Modules/MemoryManager.cs | 738 | ✅ 稳定 | CoreHelper, JsonHelper, FileHelper | 交互记忆管理，去重/查询/清理，24 个方法 |
| RuleEngine | Modules/RuleEngine.cs | ~600 | ✅ 稳定 | CoreHelper, JsonHelper | 规则引擎，帖子评估门控，动作决策 |
| StateSaver | Modules/StateSaver.cs | ~160 | ✅ 稳定 | CoreHelper, JsonHelper, FileHelper | 状态持久化，保存画像和运行统计 |
| ReportGen | Modules/ReportGen.cs | ~180 | ✅ 稳定 | CoreHelper, JsonHelper, FileHelper | 报告生成，每日/周运行报告 |
| Maintenance | Modules/Maintenance.cs | ~220 | ✅ 稳定 | CoreHelper, FileHelper | 维护任务，清理日志/过期数据 |
| Extension | Modules/Extension.cs | ~80 | ✅ 稳定 | ExtensionManager, IExtension | 扩展入口，注册内置扩展并执行 |
| UIHelper | Modules/UIHelper.cs | ~230 | ✅ 稳定 | CoreHelper | UI 辅助，Android UI XML 解析 |
| Decision (占位) | Modules/Decision/ | — | ⏳ 计划中 | — | 决策模块，仅 README 占位 |

---

## ZD 脚本引擎层 (Core/) — 5 个引擎文件

| 模块 | 文件 | 状态 | 说明 |
|------|------|------|------|
| ScriptHelpers | Core/ScriptHelpers.cs | ✅ 稳定 | ZD 公共函数库，由 ModuleLoader 自动注入 |
| HumanizationEngine | Core/HumanizationEngine.cs | ✅ 稳定 | 人性化行为引擎，4 种配置 |
| UILocator | Core/UILocator.cs | ✅ 稳定 | 多策略 UI 元素定位 |
| ErrorRecovery | Core/ErrorRecovery.cs | ✅ 稳定 | 错误恢复，指数退避重试 |
| PlatformBase | Core/PlatformBase.cs | ✅ 稳定 | 平台基类接口定义 |

---

## 平台实现层 (Platforms/) — 2 个平台模块

| 模块 | 文件 | 状态 | 操作 | 说明 |
|------|------|------|------|------|
| RedditModule | Platforms/Reddit/RedditModule.cs | ✅ 完整 | Initialize/Browse/Like/Comment/Follow/Share | Reddit 平台实现 |
| InstagramModule | Platforms/Instagram/InstagramModule.cs | ✅ 完整 | Initialize/Browse/Like/Comment/Follow/Share + 速率限制 | Instagram 平台实现 |

---

## 扩展层 (Extensions/) — 2 个数据源扩展

| 模块 | 文件 | 状态 | 说明 |
|------|------|------|------|
| IPLocationExtension | Extensions/DataSources/IPLocationExtension.cs | ✅ 稳定 | IP 地理位置数据 |
| WeatherExtension | Extensions/DataSources/WeatherExtension.cs | ✅ 稳定 | 天气数据 |
| Hooks | Extensions/Hooks/ | ⏳ 待实现 | 生命周期钩子（目录为空） |

---

## ZD 入口层 (ZDProjects/) — 16 个脚本文件

| 模块 | 文件 | 说明 |
|------|------|------|
| ModuleLoader | ZDProjects/ModuleLoader.cs | 通用模块加载器，LRU 编译缓存 (273 行) |
| Initializer_OwnCode | ZDProjects/Initializer_OwnCode.cs | ZD 入口：加载 Initializer.cs |
| Main_OwnCode | ZDProjects/Main_OwnCode.cs | ZD 入口：加载 Main.cs |
| SessionRunner_OwnCode | ZDProjects/SessionRunner_OwnCode.cs | ZD 入口：加载 SessionRunner.cs |
| PersonaCreate_OwnCode | ZDProjects/PersonaCreate_OwnCode.cs | ZD 入口：加载 PersonaCreate.cs |
| DailyUpdate_OwnCode | ZDProjects/DailyUpdate_OwnCode.cs | ZD 入口：加载 DailyUpdate.cs |
| WeeklyEvolve_OwnCode | ZDProjects/WeeklyEvolve_OwnCode.cs | ZD 入口：加载 WeeklyEvolve.cs |
| StateSaver_OwnCode | ZDProjects/StateSaver_OwnCode.cs | ZD 入口：加载 StateSaver.cs |
| Extension_OwnCode | ZDProjects/Extension_OwnCode.cs | ZD 入口：加载 Extension.cs |
| ReportGen_OwnCode | ZDProjects/ReportGen_OwnCode.cs | ZD 入口：加载 ReportGen.cs |
| Maintenance_OwnCode | ZDProjects/Maintenance_OwnCode.cs | ZD 入口：加载 Maintenance.cs |
| Reddit_Browse | ZDProjects/Reddit_Browse.cs | Reddit 浏览操作脚本 |
| Reddit_Like | ZDProjects/Reddit_Like.cs | Reddit 点赞操作脚本 |
| Reddit_Comment | ZDProjects/Reddit_Comment.cs | Reddit 评论操作脚本 (含 AI 生成) |
| Reddit_ReadPost | ZDProjects/Reddit_ReadPost.cs | Reddit 阅读帖子脚本 |

---

## 模块依赖关系图

```
SessionRunner (核心)
├── CoreHelper, JsonHelper, FileHelper
├── ActionExecutor → SelectorEngine → PageDetector
├── RuleEngine
├── MemoryManager
└── PlatformModule (Reddit/Instagram)
    └── ScriptHelpers → HumanizationEngine, UILocator, ErrorRecovery

Main
├── CoreHelper, JsonHelper, AIService
└── 触发: PersonaCreate / DailyUpdate / SessionRunner

WeeklyEvolve
├── CoreHelper, AIService, JsonHelper, FileHelper
└── 读取: Memory/{device_id}/{date}.json
```

---

## 会话跟踪与合约系统

### 合约目录
 路径: `.omo/contracts/{ModuleName}.contract.json`
 核心合约 (v1.0.0): CoreHelper, JsonHelper, SessionRunner, RuleEngine, ActionExecutor
 Stub 合约 (v0.0.0): 其余 26 个模块 — 首次修改时需生成完整合约

### 会话管理
 会话文件: `.omo/sessions/{session_id}.json`
 锁文件: `.omo/locks/{module_name}.lock.json`
 变更集: `.omo/changes/{session_id}.changeset.json`
 协议详情: 见 `~/.omo/global-hooks/pre-task.md` Step 3.5.1-3.5.7

### 配置
 `.omo/config.json`: lock_ttl=120min, heartbeat_stale=30min, confirm_delay=5s
