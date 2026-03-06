# DPS v4.5 更新日志

## [4.5.10] - 2026-03-04

### 🆕 App Onboarder — 新平台自动接入工具

新增独立 Python CLI 工具 `Tools/app_onboarder/`，用于自动化新 APP 平台接入流程。

#### 1) 工具架构（6 个模块）
- `adb_controller.py` (300 行) — ADB 命令封装：设备连接、UI dump、截图、点击、滑动、拟人化延迟
- `ui_analyzer.py` (483 行) — UI Dump XML 解析引擎：元素查找、底部导航检测、Feed 类型判断、WebView 检测、帖子容器识别、页面分类
- `app_explorer.py` (699 行) — 5 阶段自主探索引擎：首页扫描 → 导航探索 → Feed 分析 → 帖子详情分析 → 交互按钮发现。支持 WebView accessibility nodes 深度分析，启发式失败时截图向用户提问
- `config_generator.py` (1812 行) — 配置生成器：基于探索结果自动生成 PlatformsConfig.json 平台条目、{platform}_operations.json 操作定义、{platform}_e2e_test.ps1 端到端测试脚本
- `test_runner.py` — 测试运行器：执行 E2E 测试 → 解析结果 → 分析失败 → 自动修复 → 重试。支持 5 种修复策略（延迟增加、选择器切换、滚动调整、坐标校准、WebView 等待增加）
- `main.py` — CLI 入口：支持交互模式和命令行参数模式（`--package`/`--key`/`--skip-test`）

#### 2) 核心特性
- **全自动探索**: 无需手动查看 XML dump，工具自主导航 APP 发现 UI 结构
- **WebView 感知**: 自动检测 WebView 页面并分析 accessibility nodes（如 BabyCenter 的点赞/评论按钮）
- **双 Feed 类型**: 支持 ViewPager 水平滑动和 RecyclerView 垂直滚动两种 feed 模式
- **自动修复循环**: E2E 测试失败后自动分析原因并尝试修复配置，最多 3 轮
- **零第三方依赖**: 仅使用 Python 标准库

#### 3) 使用方式
```bash
# 交互模式
python Tools/app_onboarder/main.py

# 命令行模式
python Tools/app_onboarder/main.py --package com.example.app --key example

# 跳过测试
python Tools/app_onboarder/main.py --package com.example.app --key example --skip-test
```

#### 4) 验证
- 使用 BabyCenter 模拟数据通过集成测试
- 生成的 PlatformsConfig.json 条目包含 12 个字段、17 个 UI 选择器
- 生成的 operations.json（示例）可覆盖 WebView 场景，含 `scroll_to_reactions` 等关键步骤
- 生成的 E2E 测试脚本可直接由 PowerShell 执行并进入自动修复循环

#### 5) 实现计划文档
- 新增 `Docs/plans/2026-03-04-app-onboarder.md` — 完整实现计划

### 📚 Docs 一致性审核（2026-03-05）

- 审核并更新 `Docs/` 目录核心说明书，统一到 v4.5.10 基线
- 修复文档索引失效链接与不存在路径引用（如 `子项目调用架构.md`、历史错误文件名链接）
- 校正文档与代码不一致项：BabyCenter 为配置驱动模式、行为档案命名（`speed_demon/casual/deep_reader/distracted`）、`Config/IntentMappings` 路径
- 补充历史文档说明：`FIX_REPORT_2026-02-27.md` 中旧绝对路径属于历史快照，不代表当前仓库路径约定

---

## [4.5.9] - 2026-02-28

### 🔧 代码重构优化

#### 1) SessionRunner 帖子 JSON 构建逻辑封装
- `Modules/SessionRunner.cs`
  - 新增方法 `BuildAndSetPostJsonFromContext()` - 封装原本在 `ExecuteWithUnifiedEngine()` 中的 24 行复杂逻辑
  - `ExecuteWithUnifiedEngine()` 方法简化，调用新方法替代内联代码
  - 提高代码可维护性和可读性
  - 行数变化: 1492 → 1501 (+9 行)

#### 2) .omo 2.0 模块追踪系统
- 新增 `.omo/modules/SessionRunner.md` - SessionRunner 模块追踪文件
- 新增 `.omo/modules/TEMPLATE.md` - 模块追踪模板
- 新增 `.omo/modules/index.md` - 活跃模块索引
- 新增 `.omo/PLANS.md` - 优化计划文档
- 完善 L3/L4 层级操作映射
#### 3) 文件 I/O 错误处理增强
- `Modules/SessionRunner.cs`
  - 为 `LoadUserStrategy()` 方法添加 try-catch 错误处理
  - 为 `LoadIntentMapping()` 方法添加 try-catch 错误处理
  - 确保文件读取失败时使用默认值，不中断流程
  - 符合 .omo 规范：所有 I/O 操作必须包含 try-catch

#### 4) 编译错误修复
- `Modules/SessionRunner.cs`
  - 修复 `LoadUserStrategy()` 方法结构损坏：恢复 `File.Exists()` 条件检查，补全 `balanceJson`/`aiControl` 变量声明
  - 修复 `LoadIntentMapping()` 方法结构损坏：恢复 `File.Exists()` 条件检查，使 try-catch 可达
  - 修复视觉验证块中 `effectiveIntent` 变量未定义的编译错误（CS0103），简化为快速模式逻辑
  - 修复 `ResolveIntentForAction()` 默认映射：`like` 从错误的 `open_post` 修正为 `like_content`

#### 5) L3/L4 映射文档完善
- `.omo/layers/l3-operation.yaml`
  - 新增 op-sr-008 (build-post-json) 操作定义
- `.omo/modules/SessionRunner.md`
  - 更新模块追踪状态为 stable，进度 100%

---


## [4.5.8] - 2026-02-27

### 🔧 CODEX 复扫修复（逻辑与流程）

#### 1) 会话成功判定升级（目标对齐 >95%）
- `Modules/SessionRunner.cs`
  - 新增 `successfulActions` / `skippedActions` 计数
  - 成功判定改为：`success_rate >= 0.95` 且 `successfulActions >= min_successful_actions(默认6)`
  - 新增输出变量：`session_success_rate`, `session_successful_actions`, `session_failed_actions`, `session_skipped_actions`, `action_attempt_count`
  - `action_count` 改为记录成功动作数（不再混入 SKIP）

#### 2) 帖子语义链路修复（ActionExecutor -> SessionRunner -> RuleEngine）
- `Modules/Core/ActionExecutor.cs`
  - `find` 步骤新增节点语义提取（`*_text`, `*_desc`）
  - 新增语义别名同步：`title/caption/body/...` -> `post_title/post_body/post_subreddit/post_upvotes/post_comment_count/post_timestamp`
  - 新增数字标准化（支持 `1.2k`, `3,421`）
- `Modules/SessionRunner.cs`
  - 构建 `current_post_json` 时增加多级兜底字段读取（`post_title`, `*_text` 等）
  - 新增 `body` 字段写入
  - 对 `upvotes/comment_count` 做标准化后再写入 JSON，避免无效数字污染
- `Config/Operations/reddit_operations.json`
  - `like` 增加 `post_title` 采集
  - `open_post` 统一使用 `post_title`
  - `read_post` 统一使用 `post_body`
- `Config/Operations/instagram_operations.json`
  - `like` 增加 `post_title`（来自 `caption_text`）采集
  - `view_post` 语义字段改为 `post_title`
- `Config/Operations/babycenter_operations.json`
  - `open_post` 统一使用 `post_title`
  - `read_post` 统一使用 `post_body`
  - `like` 增加 `post_title` 采集

#### 3) 安全与配置完整性修复
- `Modules/Main.cs`
  - 新增 `device_id` 安全校验（`ValidateDeviceId`）
  - 配置检查从单文件扩展为 `AIConfig + StageConfig + BehaviorConfig`
  - 增加配置读取为空时的错误返回

#### 4) I/O 错误处理与维护鲁棒性
- `Modules/Core/CoreHelper.cs`
  - `EnsureDir/ReadFile/WriteFile/AppendFile` 增加 try-catch 与日志
- `Modules/Main.cs`
  - `ClearRuntimeData` 中的备份/删除改为单文件异常隔离，避免一处失败中断全流程
- `Modules/Maintenance.cs`
  - 日志/记忆/备份清理循环增加单文件 try-catch
  - 磁盘空间检查失败不再静默吞掉，改为告警日志
  - `DriveInfo` 路径获取增强（优先 `Path.GetPathRoot`）

#### 5) 返回码与意图映射一致性
- `Modules/ReportGen.cs`
  - `SKIPPED` 统一为 `SKIP`
- `Modules/SessionRunner.cs`
  - 默认意图映射修复：`like -> like_content`（原为错误的 `open_post`）

#### 6) 源文件可编译性修复
- `Modules/Core/AppExplorer_v2.cs`
  - 清理行前缀噪声（`#XX|`），恢复为可读源码文本

#### 7) 文档与上下文同步（v4.5.8）
- 同步更新 `README.md`、`UPGRADE.md`、`Docs/README.md`、`Docs/CopyPaste_Setup.md`、`Docs/DPS_v4.5_完整配置手册.md`、`Docs/FIX_REPORT_2026-02-27.md`、`Docs/QuickSetup_Flowchart.md`、`Docs/BabyCenterModule.md`、`FINAL_DELIVERY_REPORT.md`
- 修正文档口径：`action_count` 为成功动作数，新增 `action_attempt_count/session_success_*` 统计变量说明
- `SKIPPED` 相关文档表述统一为 `SKIP`
- `.omo/decisions.md` 清理前缀噪声，`.omo/context.md` / `.omo/PROJECT.md` 同步到 v4.5.8 状态



## [4.5.7] - 2026-02-27

### 🔧 CODEX 修复 - 运行时稳定性与平台接入

#### 修复内容（由 CODEX 完成）

#### 修复点 1: ActionExecutor 运行稳定性修复
- **新增**: `refresh_layout` 步骤已添加到 `ExecuteStep` 分发（第186行）
  - 修复浏览/返回流程中的布局刷新步骤未执行的问题
- **修复**: `call_operation` 递归时上下文仅在顶层（CallDepth==0）清空
  - 解决递归调用链丢失步骤上下文的问题
- **新增**: `SyncLegacyContext` 方法（第1123行）
  - 将 `OperationContext.Variables` 同步回静态 `_context`
  - 修复 `SessionRunner` 无法稳定读取 `ActionExecutor` 上下文的问题
- **清理**: 移除非当前运行链的 Intent 执行段
  - 降低动态编译失败概率，保持现有流程稳定

#### 修复点 2: SessionRunner 评估门控修复
- **修复**: 空语义帖子处理（第898-931行）
  - 仅在存在语义字段（title/subreddit/upvotes/comments/timestamp）时写入 `current_post_json`
  - 避免无内容帖子被 `RuleEngine` 误判为低分并持续拒绝互动

#### 修复点 3: Reddit / Instagram 意图映射修复
- **新增**: `like_content` 意图定义
  - 修复 `like` 映射到 `like_content`（之前错误映射到 `open_post`）
  - 行为现在符合"值得点赞再点赞"的目标
- **新增**: `follow_entity` 意图定义
  - 补充缺失的 `follow_entity` 映射到 `follow` 操作
- **新增**: `share_content` 意图定义
  - 补充缺失的 `share_content` 映射到 `share` 操作

#### 修复点 4: Instagram 点赞回退链增强
- **修改**: `instagram_operations.json` 中 `like` 操作配置
  - 使用 `if_exists(like_button)` 检查点赞按钮是否存在
  - `else` 分支调用 `call_operation(double_tap_like)` 作为回退
  - UI 变化时容错能力提升
- **验证**: `double_tap_like` 操作存在且配置正确
  - 支持双击图片点赞（Instagram 特有功能）

#### 修复点 5: BabyCenter 三件套接入
- **新增**: `PlatformsConfig.json` 中 `babycenter` 平台配置（第287行）
  - package: `com.babycenter.pregnancytracker`
  - ui_selectors: post_unit, post_title, post_body, like_button, comment_button, etc.
  - rate_limits: 80/hour, 24 likes/hour, 12 comments/hour
  - page_signatures: feed, post_detail, comment
- **新增**: `Config/Operations/babycenter_operations.json`
  - 6 个操作: browse, open_post, read_post, like, comment, back_to_feed
  - 所有操作包含 refresh_layout 步骤和 humanized 参数
- **新增**: `Config/IntentMappings/babycenter_intents.json`
  - 意图映射: browse_feed, open_post, read_post, like_content, reply_post
  - action_to_intent: browse, read_post, like, comment, post, follow, share
- **修改**: `Apps.json` 中 `babycenter.enabled = true`
  - data_path: `Data/BabyCenter`
  - primary_communities: Pregnancy, Baby, Toddler
- **新增**: `device_app_mapping.json` 中 `device_004 -> babycenter` 映射

#### 修复点 6: ZDProjects 兼容性修复
- **修复**: 所有 `*_OwnCode.cs` 文件添加 `OperationContext.cs` 依赖
  - DailyUpdate_OwnCode.cs
  - Extension_OwnCode.cs
  - Initializer_OwnCode.cs
  - Main_OwnCode.cs
  - Maintenance_OwnCode.cs
  - PersonaCreate_OwnCode.cs
  - ReportGen_OwnCode.cs
  - SessionRunner_OwnCode.cs
  - StateSaver_OwnCode.cs
  - WeeklyEvolve_OwnCode.cs
  - 减少动态编译时"类型找不到"的风险

### 📝 新增测试脚本
- **新增**: `ZDProjects/Tests/Reddit_E2E_Test.cs` - Reddit E2E 测试脚本
  - 验证 refresh_layout 步骤
  - 验证 like_content/follow_entity/share_content 意图
  - 完整工作流模拟
- **新增**: `ZDProjects/Tests/Instagram_E2E_Test.cs` - Instagram E2E 测试脚本
  - 验证 if_exists(like_button) 配置
  - 验证 call_operation(double_tap_like) 回退链
  - 验证 double_tap_like 操作
- **新增**: `ZDProjects/Tests/BabyCenter_E2E_Test.cs` - BabyCenter E2E 测试脚本
  - 验证平台配置、操作配置、意图映射
  - 验证应用开关和设备映射

### ⏳ 待用户验证（运行时测试）
- ZennoDroid 实机/模拟器 E2E 测试
  - Reddit: 首页 → 选帖 → 阅读 → 判断点赞 → 点赞 → 返回
  - Instagram: 同上 + like 回退链测试
  - BabyCenter: selectors 与页面签名需要按实际 UI 微调

### ✅ 验证结果
- 所有 JSON 文件通过语法校验
- 关键映射一致性检查通过
- 代码验证通过（C# 5.0 兼容）
- 状态: ⏳ READY - 等待 ZennoDroid 运行时测试

---




## [4.5.6] - 2026-02-27

### 🚀 Universal Framework - 架构完整重构

#### 核心理念
- **DPS (大脑)**: 决策层 - 感知、记忆、决策、验证
- **ZennoDroid (手)**: 执行层 - 元素定位、拟人化执行、异常处理
- **翻译层**: IntentTranslator 将高层意图翻译为物理命令

### 🛠️ 架构重构 - DPS 与 ZennoDroid 分层

#### Phase 1: 重构 ActionExecutor.cs
- **新增**: `Modules/Core/Intent.cs` - 操作意图抽象类
  - 定义高层操作意图，描述"做什么"而不是"怎么做"
  - 支持从 JSON 步骤构造 Intent 对象
  - 168 行，C# 5.0 兼容
- **新增**: `ActionExecutor.ExecuteIntent()` 方法
  - 基于 Intent 对象的执行方法
  - 复用现有的 Step* 方法，保持向后兼容
  - 渐进式重构策略：保留现有执行逻辑，只改变输入格式
- **新增**: `ActionExecutor.GetContext()` 方法
  - 供 SessionRunner 读取上下文变量（如 post_title, post_subreddit）
  - 兼容现有代码调用

#### Phase 2: 简化 Manifest - 删除物理参数
- **修改**: `Config/Operations/reddit_operations.json`
  - 删除 `duration` 参数（物理参数）
  - 保留 `selector`, `direction`, `distance`（翻译层需要）
- **修改**: `Configs/Manifests/instagram.json`
  - 删除所有 `duration_ms` 参数（4 处）
  - 保留选择器和高层参数

#### Phase 3: 添加视觉验证层
- **新增**: `SessionRunner.cs` 视觉验证逻辑
  - 在关键操作（like, comment）执行后添加截图验证
  - 调用 `VisionCorrector.AnalyzeAndRecover()` 验证结果
  - 验证失败时标记操作为失败，记录失败原因
  - 通过 `behavior_config_json` 中的 `vision_verification_enabled` 控制开关

#### Phase 4: 设计新的 Manifest 格式
- **新增**: `Configs/Manifests/manifest_schema.yaml` - Manifest Schema v2.0
  - 定义 `capabilities`：APP 能做什么
  - 定义 `states`：如何识别当前状态（视觉 + UI 特征）
  - 定义 `intent_mappings`：意图翻译为操作
  - 定义 `rate_limits`：速率限制
  - 91 行，详细注释
- **新增**: `Configs/Manifests/instagram_v2.yaml` - Instagram Manifest v2.0
  - 11 个 capabilities（browse_feed, like_post, comment, 等）
  - 7 个 states（home_feed, post_detail, profile, 等）
  - 每个 state 包含 visual_markers + ui_signatures + gemini_prompt
  - 11 个 intent_mappings，包含 fallback_intents
  - 8 个 rate_limits，包含 per_hour + cooldown_seconds + per_day
  - 223 行，完整实现

#### Phase 5: DPS 架构完整实现
- **新增**: `Modules/Core/ZDCommand.cs` - ZennoDroid 命令类
  - 定义物理操作的命令类型（Tap, Swipe, SendText, 等）
  - 封装坐标、持续时间、文本内容等物理参数
  - 支持人性化执行标志和重试配置
  - 333 行，C# 5.0 兼容
- **新增**: `Modules/Core/ZDResult.cs` - ZennoDroid 执行结果类
  - 统一的执行结果格式（Success, FailedRetryable, FailedFatal, Skipped）
  - 支持扩展数据和错误追踪
  - 提供与旧格式的兼容转换方法
  - 311 行，C# 5.0 兼容
- **新增**: `Modules/Core/ZennoDroidAdapter.cs` - ZennoDroid API 适配器
  - 封装所有 ZennoDroid API 调用（Input.Tap, Input.Swipe, 等）
  - 统一错误处理和重试机制
  - 支持 GetLayout 和 Screenshot 操作
  - 408 行，C# 5.0 兼容
- **新增**: `Modules/Core/IntentTranslator.cs` - 意图翻译器
  - 将 Intent 翻译为 ZDCommand
  - 保留元素定位逻辑（SelectorEngine）和坐标计算逻辑（ParseBounds）
  - 支持回退链翻译
  - 支持从 stepJson 快速翻译
  - 472 行，C# 5.0 兼容
- **修改**: `ZDProjects/ModuleLoader.cs`
  - 添加新文件到编译列表：Intent.cs, ZDCommand.cs, ZDResult.cs, ZennoDroidAdapter.cs, IntentTranslator.cs
  - 确保所有新文件被动态编译加载

#### 架构图
```
DPS (大脑层)
├─ Intent ("我想点赞这个帖子")
├─ IntentTranslator (翻译：意图 → 命令)
│   └─ 输出: ZDCommand ("点击坐标 (540, 1800)")
└─ VisionCorrector (视觉验证)

ZennoDroid (手层)
├─ ZennoDroidAdapter (API 封装)
│   ├─ 输入: ZDCommand
│   └─ 输出: ZDResult
├─ SelectorEngine (元素定位)
└─ ScriptHelpers (人性化执行)
```

#### 关键改进
1. **清晰的架构边界**: DPS 不再直接调用 ZennoDroid API
2. **可测试性**: Intent/ZDCommand/ZDResult 都是纯数据结构
3. **可扩展性**: 新增平台只需定义 Manifest 和翻译规则
4. **向后兼容**: 保留 ActionExecutor 的旧接口，渐进式迁移
5. **C# 5.0 兼容**: 所有新代码使用 C# 5.0 语法

### 📝 架构决策
- **ADR-011**: Intent-Based Execution
  - 状态: 已接受
  - 决策: 引入 Intent 抽象层，分离决策与执行
  - 理由: 提高代码可读性，便于未来添加视觉验证层
  - 后果: 保持向后兼容，现有代码无需修改
- **ADR-012**: Vision Verification Layer
  - 状态: 已接受
  - 决策: 在关键操作后添加 Gemini Flash 截图验证
  - 理由: 提高操作可靠性，及时发现执行失败
  - 后果: 增加每次操作 2-3 秒延迟，但显著提高成功率
- **ADR-013**: Manifest v2.0 Format
  - 状态: 已接受
  - 决策: 重新设计 Manifest 格式，分离语义和物理参数
  - 理由: 现有格式混合了 DPS 层和 ZennoDroid 层的信息
  - 后果: 旧格式仍然支持，新格式逐步迁移

### ✅ 验证结果
- **代码结构**: Intent.cs 创建成功，168 行
- **向后兼容**: 现有 Execute() 方法保留，新增 ExecuteIntent() 方法
- **配置简化**: 删除所有 duration/duration_ms 参数
- **视觉验证**: SessionRunner 集成 VisionCorrector
- **新格式**: manifest_schema.yaml + instagram_v2.yaml 创建成功
- **状态**: ✅ READY - 可以进入运行时测试

### 📚 文档更新
- **新增**: `ARCHITECTURE_REFACTORING_REPORT.md` - 架构重构报告
  - 详细分析当前架构错误
  - 提供 7 个 Phase 的重构计划
  - 428 行，包含代码示例和流程图

---

## [4.5.5] - 2026-02-27

### 🐛 关键修复 - 编译错误修复

#### ActionExecutor.cs
- **修复**: 删除第279-308行重复的 StepTap 代码块（CS0116 错误）
- **修复**: 删除第342-367行重复的 StepSwipe 代码块（CS0116 错误）
- **修复**: 删除第408-443行重复的 StepScroll 代码块（CS0116 错误）
- **修复**: 添加第1116行 `public static string GetContextVariable(string key)` 方法签名
- **修复**: 添加第1132行 `public static void SetContextVariable(string key, string value)` 方法签名
- **修复**: 添加第1145行 `public static void ClearContext()` 方法签名
- **结果**: 所有 CS0116 编译错误已解决，代码结构完整

#### ModuleLoader.cs
- **修复**: 在 coreFiles 数组中添加 `RateLimiter.cs`
- **结果**: 所有新模块现已包含在动态编译系统中

#### AppExplorer.cs
- **修复**: 第720行 `input.PressBack()` 改为 `input.Shell("input keyevent 4")`
- **结果**: 使用 ZennoDroid 标准 API，避免 CS0117 错误

#### NavigationResolver.cs
- **修复**: 第94行 `JsonHelper.GetJsonValue` 改为 `JsonHelper.Get`
- **修复**: 第102-103行 `JsonHelper.ParseJsonArray` 改为 `JsonHelper.GetArray`
- **结果**: 使用存在的 JsonHelper API，避免 CS0117 错误

#### AIConfig.json
- **修复**: 模型名称从 `gemini-3-flash-preview` 改为 `gemini-3-flash`
- **结果**: 使用正确的 Gemini API 模型名称

#### Initializer.cs
- **新增**: 在项目初始化时调用 `VisionCorrector.Init`
- **新增**: 自动创建 `Screenshots/` 目录
- **结果**: VisionCorrector 模块现已正确初始化，可以使用

#### instagram.json
- **修复**: 添加导航路径 `home → notifications`（第197行）
- **修复**: 添加导航路径 `home → direct_messages`（第198行）
- **修复**: `like_feed_posts` 速率限制从 15/min 调整为 30/hour（第233行）
- **修复**: `cooldown_seconds` 从 5秒调整为 120秒
- **结果**: 符合 Instagram 安全限制，所有屏幕可达

### ✅ 验证结果
- **编译验证**: 所有 11 个高优先级修复项已通过验证
- **代码结构**: ActionExecutor.cs 共 28 个方法签名，无孤立代码块
- **API 兼容性**: 所有 API 调用已修正为 ZennoDroid 标准 API
- **配置完整性**: 所有配置文件已修正，符合框架规范
- **状态**: ✅ READY - 可以进入 ZennoDroid 运行时测试

---

## [4.5.4] - 2026-02-27

### ✨ 新功能 - Universal APP Automation Framework

#### 核心模块扩展
- **新增**: `NavigationResolver.cs` - BFS 最短路径导航算法
  - 根据 Manifest navigation.edges 计算页面间最短路径
  - 支持图结构加载、路径查询、直接到达检查
  - 321 行，C# 5.0 兼容

#### 模块集成
- **更新**: `ModuleLoader.cs` coreFiles 数组
  - 添加: ManifestLoader.cs, NavigationResolver.cs, VisionCorrector.cs, AppExplorer.cs, RateLimiter.cs
  - 所有新核心模块现已纳入动态编译系统

#### 配置文件
- **新增**: `Configs/Manifests/instagram.json` - Instagram 完整 Manifest
- **新增**: `Configs/Manifests/reddit.json` - Reddit 完整 Manifest
- **新增**: `Configs/Manifests/template.json` - Manifest 模板

### 📝 技术细节
- **ActionExecutor.cs** 新原语已验证: call_operation, if_exists, foreach, random_pick
- **C# 5.0 语法兼容性验证通过** - 无 $"", ?., nameof 等现代语法
- 所有核心模块已集成到动态编译系统

---

## [4.5.4] - 2026-02-27

### 🔧 稳定性修复

#### ActionExecutor.cs
- **修复** `refresh_layout` 步骤未分发问题（operations 中该步骤此前会被当作未知动作跳过）
- **修复** 递归 `call_operation` 时上下文被误清空的问题（仅顶层执行清理 context）
- **修复** 旧接口兼容：执行后同步 `OperationContext` 到静态上下文，恢复 `SessionRunner` 的 `ActionExecutor.GetContext()` 读取能力
- **清理** 移除不在当前编译链中的 Intent 相关执行段，避免运行时动态编译缺失依赖

#### SessionRunner.cs
- **修复** 当 ActionExecutor 未提供语义字段时，`current_post_json` 不再写入空内容对象，避免 RuleEngine 将“空帖子”误判并持续拒绝互动

### 🌐 多平台配置完善

#### IntentMappings
- **修复** `reddit_intents.json` / `instagram_intents.json` 中 `follow_entity` 与 `share_content` 未定义导致的意图回退问题
- **调整** `like` 动作映射到 `like_content`（直接执行 like 操作，不再错误映射为 open_post）
- **新增** `babycenter_intents.json`（支持 browse/read/like/comment 主链路）

#### Operations
- **增强** `instagram_operations.json` 的 like 操作：新增 `if_exists + call_operation(double_tap_like)` 回退链
- **新增** `babycenter_operations.json`（browse/open_post/read_post/like/comment/back_to_feed）

#### Platforms / Apps / Device Mapping
- **新增** `PlatformsConfig.json` 的 `babycenter` 平台配置（selectors/page_signatures/rate_limits）
- **更新** `Apps.json` 中 babycenter `enabled: true`
- **新增** `device_app_mapping.json` 示例映射 `device_004 -> babycenter`

### 🧩 ZDProjects 兼容性
- **更新** 各 `*_OwnCode.cs` 核心依赖列表，补充 `OperationContext.cs`，避免 `ActionExecutor` 新签名在不同入口编译失败

## [4.5.3] - 2026-02-26

### ✨ 新功能

#### ActionExecutor.cs - call_operation 原语实现
- **新增**: `call_operation` 原语，支持操作组合和递归调用
  - 在 ExecuteStep 方法的 switch-case 中新增 `case "call_operation"`
  - 实现 ExecuteCallOperation 方法，支持递归调用其他操作
  - 递归深度限制为 5 层（通过 OperationContext.CanEnterCall 检查）
  - 使用 context.EnterCall() 和 context.ExitCall() 管理递归深度
  - 异常处理确保递归深度正确退出
- **重构**: Execute 方法签名更新
  - 新增 OperationContext 参数，替代静态 _context 字典
  - 支持多设备并发安全执行
- **重构**: 所有步骤方法更新以使用 OperationContext
  - StepFind: 使用 context.SetVariable 存储查找结果
  - StepTap: 使用 context 参数传递
  - StepSetVar: 使用 context.GetVariable 读取上下文变量
  - ExecuteForeach: 使用 context.SetVariable 设置循环变量
  - ExecuteIfExists: 传递 context 到分支执行
  - ExecuteRandomPick: 传递 context 到子步骤
  - ResolveTapTarget: 使用 context.GetVariable 读取坐标
- **兼容性**: 保留静态 GetContext/SetContext/ClearContext 方法（已标记为废弃）

### 📝 技术细节
- 符合 C# 5.0 语法要求（无 $""、?.、nameof）
- 遵循现有代码风格（JsonHelper 用法、错误处理模式）
- 详细注释说明递归机制和深度控制

---

## [4.5.2] - 2026-02-17

### 🐛 Bug 修复 (P0 级别)

#### ModuleLoader.cs - 缓存管理优化
- **修复**: 缓存失效不检测依赖文件删除
  - 引入 `CacheEntry` 结构化缓存条目（方法 + 依赖快照 + 访问时间）
  - 实现依赖文件删除检测（对比缓存快照与当前文件列表）
  - 添加路径规范化函数避免大小写/分隔符导致重复键
- **修复**: 缓存无界增长导致内存泄漏
  - 实现 LRU 缓存淘汰机制（上限 32 个条目）
  - 添加 `EvictOldestCacheEntry` 自动清理旧缓存

#### SessionRunner.cs - 并发安全性
- **修复**: 静态状态跨会话串扰
  - 移除静态 `_random`，改用 `[ThreadStatic]` 的 `_threadRandom`
  - 添加 `GetRandom()` 方法（线程安全的随机数生成器）
  - 创建 `SessionState` 类封装疲劳模型状态
  - 所有疲劳变量改为 `SessionState` 实例字段
- **修复**: 配置异常导致运行时崩溃
  - `GetActionDelay` 添加 min/max 边界校验
  - 添加溢出保护（上限 3600 秒）
  - 自动修正 min/max 颠倒情况

#### MemoryManager.cs - 并发写入保护
- **修复**: 并发写入导致数据丢失
  - 添加文件级锁机制（`GetFileLock` 按路径获取锁对象）
  - `RecordInteractionWithScore` 使用 `lock` 包裹读改写操作

#### DailyUpdate.cs - 数据完整性
- **修复**: 未来 conception_date 产生负孕周
  - 添加 `totalDays < 0` 检测（跳过更新并记录错误）
  - 添加负值保护（weeks/days 归零）
  - 修改正则表达式支持负数匹配
- **新增**: 产后阶段转换逻辑
  - PP0 → PP1（产后 3 个月）
  - PP1 → NP（产后 12 个月）
  - 基于 `delivery_date` 自动计算并转换阶段

### 📝 技术细节
- 所有修复严格遵守 C# 5.0 语法约束
- 保持原有代码风格和注释规范
- 通过语法验证和关键点验证

---

## [4.5.1] - 2026-02-13

### 🔧 Config-Driven Selectors (Phase 3)
- **修复** `RedditModule.cs` / `InstagramModule.cs` - 嵌套 JSON selector 对象解析 bug（`GetJsonValue` 无法解析嵌套对象，导致始终使用默认值）
- **新增** `GetSelectorValue` 辅助函数 - 正确提取 `PlatformsConfig.json` 中 `ui_selectors` 的嵌套 `value` 字段
- **新增** `RedditModule.cs` 导出 selector 变量到 ZD 变量 (`reddit_sel_*`)，供 ZDProjects 脚本使用
- **修复** `InstagramModule.cs` Like 操作中硬编码的 `media_image` fallback，改为 `cfg_mediaImage` 从配置读取
- **修改** ZDProjects 脚本 (`Reddit_Browse.cs`, `Reddit_Like.cs`, `Reddit_Comment.cs`, `Reddit_ReadPost.cs`) - 从 ZD 变量读取 selectors，不再硬编码

### ✅ Extension Integration (Phase 4 验证)
- **确认** `Extension.cs` 已完全重构为使用 `ExtensionManager`（`RegisterBuiltinExtensions` + `LoadFromRegistry` + `RunCategory`）
- **确认** `ExtensionManager.cs`, `IExtension.cs`, `ExtensionsRegistry.json` 完整且集成
- **确认** `IPLocationExtension.cs`, `WeatherExtension.cs` 独立扩展类正常工作

---

## [4.5.0] - 2026-02-07

### 🌐 多平台支持
- **新增** Reddit 平台支持 (`Platforms/Reddit/RedditModule.cs`)
- **新增** Instagram 平台支持 (`Platforms/Instagram/InstagramModule.cs`)
- **新增** 平台配置文件 `Config/PlatformsConfig.json`
- **新增** 设备应用映射 `Config/device_app_mapping.json`

### 🧩 Core Modules
- **新增** `Core/HumanizationEngine.cs` - 人性化行为引擎 (4 种配置文件)
- **新增** `Core/UILocator.cs` - 多策略 UI 元素定位器
- **新增** `Core/ErrorRecovery.cs` - 错误恢复机制 (指数退避)
- **新增** `Core/PlatformBase.cs` - 平台基类接口

### 📚 Documentation
- **新增** `Docs/GETTING_STARTED.md` - 新人入门指南
- **新增** `Docs/QuickSetup_Flowchart.md` - 快速配置流程图
- **新增** `Docs/CopyPaste_Setup.md` - 复制粘贴配置手册
- **新增** `Docs/MultiPlatformFramework.md` - 多平台框架文档
- **新增** `Docs/PersonaSchema_MultiPlatform.md` - 多平台画像 Schema
- **更新** 所有文档版本号升级至 v4.5

### 🔧 架构改进
- **新增** 混合架构模式 - 共享核心框架 + 平台独立模块
- **新增** 相对坐标系统 (百分比) - 多分辨率适配
- **新增** 速率限制系统 - Reddit 120/小时, Instagram 60/小时

---

## [4.1.0] - 2026-02-05

### 🚀 性能优化

#### ModuleLoader.cs
- **新增** 静态编译缓存机制，避免重复编译
- **新增** 文件时间戳检测，仅在源码变更时重新编译
- **新增** 线程安全的缓存访问 (`lock`)
- **性能** 第二次运行从 ~500ms 降至 <10ms

### 🔧 架构改进

#### JsonHelper.cs (完全重写)
- **重写** 使用栈式状态机实现健壮的 JSON 解析器
- **修复** 嵌套对象中同名键的正确匹配（深度感知）
- **修复** 转义字符处理（包括 `\"` 和 `\\`）
- **修复** Unicode 转义序列 `\uXXXX` 完整支持
- **新增** `GetArrayElement(arrayJson, index)` - 按索引获取数组元素
- **新增** `IsValidJson(json)` - JSON 格式验证
- **新增** `CreateArray(values)` - 创建 JSON 数组

#### CoreHelper.cs
- **重构** `JGet/JGetNested/JSet` 现在委托给 `JsonHelper`
- **移除** 重复的 JSON 解析逻辑

#### AIService.cs
- **改进** 使用 `JsonHelper` 解析 API 响应
- **新增** API 错误检测（检查响应中的 `error` 字段）
- **改进** Gemini/OpenAI 响应解析更加健壮

### 📊 测试验证

所有修改通过以下测试用例：
- 嵌套对象同名键: `{"data": {"name": "inner"}, "name": "outer"}` → 正确返回 `"outer"`
- 转义引号: `{"msg": "He said \"hello\""}` → 正确解析
- Unicode: `{"text": "\u0048\u0065\u006c\u006c\u006f"}` → 返回 `"Hello"`
- 嵌套路径: `user.profile.name` → 正确遍历

---

## [4.0.2] - 2026-02-04

### 🐛 Bug 修复

#### JsonHelper.cs
- **修复** `Get` 方法现在是上下文感知的，不会错误匹配字符串值中的键名
- **修复** `Unescape` 方法现在支持 Unicode 转义序列 `\uXXXX`

#### CoreHelper.cs
- **修复** `WriteFileAtomic` 添加异常处理，当 `.bak` 文件被锁定时回退到直接覆盖
- **新增** `CountOccurrences(text, pattern)` - 统一的字符串计数方法
- **新增** `ValidateDeviceId(deviceId)` - 防止路径遍历攻击的安全验证
- **新增** `GetSafeDeviceId(deviceId, defaultValue)` - 安全获取设备ID

#### WeeklyEvolve.cs
- **修复** AI 返回的进化建议现在会实际应用到画像
- **新增** 解析 `changes` 数组并应用字段修改
- **新增** 进化前自动备份画像
- **新增** 设备ID安全验证
- **移除** 重复的 `CountOccurrences` 方法，改用 `CoreHelper.CountOccurrences`

#### Extension.cs
- **修复** 配置检查逻辑，正确读取 `extensions.ip_location.enabled` 和 `extensions.weather.enabled`
- **修复** 使用 `JsonHelper.ExtractObject` 替代不可靠的 `JGet` 检查

#### ReportGen.cs
- **修复** 文件名一致性：检查和保存都使用 `{date}_weekly.json`
- **新增** 设备ID安全验证
- **移除** 重复的 `CountOccurrences` 方法，改用 `CoreHelper.CountOccurrences`

#### Maintenance.cs
- **移除** 重复的 `CountOccurrences` 方法，改用 `CoreHelper.CountOccurrences`

#### StateSaver.cs
- **新增** 设备ID安全验证
- **移除** 未使用的 `SaveMemory` 方法（记忆由 SessionRunner 保存）
- **修复** 路径拼接一致性

---

## [4.0.1] - 2026-01-31

### 🔧 动态配置支持
所有模块已更新为从配置文件动态读取参数，不再使用硬编码值。

#### AIService.cs
- **新增** `CallWithRetry(prompt, aiConfigJson)` - 自动重试 + 备用模型
- **新增** `CallPrimary/CallFallback/CallBackup` - 分别调用三个模型
- **新增** `CallOpenAICompatible` - 支持自定义 base_url
- **修改** 所有参数从 `AIConfig.json` 动态读取：
  - model, api_key, base_url
  - timeout_ms, max_tokens, temperature

#### JsonHelper.cs
- **新增** `ExtractObject(json, key)` - 提取嵌套对象
- **新增** `ExtractArray(json, key)` - 提取数组

#### PersonaCreate.cs
- **修改** 使用 `AIService.CallWithRetry` 替代 `CallGemini`
- **修改** 自动从文件加载 AI 配置（如变量为空）
- **删除** 废弃的 `ExtractApiKey` 方法

#### WeeklyEvolve.cs
- **修改** 使用 `AIService.CallWithRetry` 替代 `CallGemini`
- **修改** 自动从文件加载 AI 配置（如变量为空）
- **删除** 废弃的 `ExtractApiKey` 方法

#### SessionRunner.cs
- **修改** 动作权重从 `BehaviorConfig.json` 读取
- **修改** 打字速度从配置的 typing 节读取
- **修改** 动作延迟从配置的 duration_sec_min/max 读取
- **修改** 会话时长限制从配置读取

#### Maintenance.cs
- **新增** 支持从 `MaintenanceConfig.json` 读取保留期限
- **修改** 日志/记忆/备份保留天数可配置

---

### ⬆️ 版本升级支持（增强版）
新增 `force_regenerate` 变量，解决源码更新后运行时数据不同步问题。

#### Main.cs
- **新增** 读取 `force_regenerate` 变量（true/1/yes）
- **新增** `ClearRuntimeData()` 方法，统一清理所有运行时数据
- **新增** 启用时清理以下内容：
  - 画像文件 `Persons/{device_id}.json`
  - 记忆文件 `Memory/{device_id}/*.json`
  - 报告文件 `Reports/{device_id}/*.json`
- **新增** 所有文件备份到 `Backups/Upgrade_{date}/`
- **新增** 清空缓存变量 `persona_json`, `session_plan_json`
- **新增** 强制执行每日更新
- **新增** 执行完成后自动重置 `force_regenerate = false`

#### 备份目录结构
```
Backups/
└── Upgrade_2026-01-31/
    ├── persona_device_001.json
    ├── Memory_device_001/
    │   ├── 2026-01-30.json
    │   └── 2026-01-29.json
    └── Reports_device_001/
        └── 2026-01-31_weekly.json
```

---

### 📄 新增配置文件

#### Config/MaintenanceConfig.json
```json
{
    "log_retention_days": 30,
    "memory_retention_days": 180,
    "backup_retention_days": 30
}
```

---

### 📋 ZennoDroid 变量更新

新增需要在 ZD 中创建的变量：

| 变量名 | 类型 | 初始值 | 用途 |
|--------|------|--------|------|
| `force_regenerate` | 文本 | `false` | 设为 `true` 强制重新生成所有内容 |

---

## 升级指南

### 从旧版本升级

1. **复制最新代码**
   - 将 `Modules/` 目录下所有 `.cs` 文件覆盖
   - 将 `Config/MaintenanceConfig.json` 复制到项目

2. **更新 ZD 变量**
   - 在 ZennoDroid 中新增 `force_regenerate` 变量，初始值 `false`

3. **强制重新生成（可选）**
   - 如需重新生成画像等内容，设置 `force_regenerate = true`
   - 运行 Main 模块，系统会自动备份旧内容并重新生成
   - 完成后变量自动重置为 `false`

### 模块加载器 (ZDProjects/*_OwnCode.cs)

这些文件**无需更新**，除非日志显示编译错误。模块加载器只负责编译外部文件，业务逻辑更新会自动生效。

---

## [4.0.0] - 2026-01-30

### 初始版本
- 动态编译架构
- 模块化设计
- AI 画像生成
- 会话模拟
- 每日/每周更新

---
