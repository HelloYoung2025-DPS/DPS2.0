# 模块修改记录 - SessionRunner

## 模块信息
- **模块名称**: SessionRunner
- **模块文件**: Modules/SessionRunner.cs
- **修改日期**: 2026-03-14
- **会话ID**: ses_31ef88da3ffe6iQm4H0KDcFixk

## 任务历史

## 任务头
- **任务名称**: SmartOrchestrator 智能编排器实现
- **任务日期**: 2026-03-14
- **主层级**: L2
- **受影响层级**: L2 + L4
- **状态**: in_progress

### 任务 4: SessionRunner 当前职责边界梳理 (2026-03-14)
- **主层级**: L4
- **受影响层级**: L4
- **状态**: completed

#### 目标描述
在不修改 `Modules/SessionRunner.cs` 实现代码前提下，明确 SessionRunner 当前职责边界，输出保留职责、候选下沉职责、不可下沉职责与理由，并给出 keep vs offload 边界表，供后续 Task 8/9 设计使用。

#### 证据基线（文件与行号）
- 主入口与会话总控：`Modules/SessionRunner.cs:78-682`
- 动作循环与门控主链：`Modules/SessionRunner.cs:435-607`
- 会话成功门控与输出变量：`Modules/SessionRunner.cs:630-673`
- 统一引擎编排与恢复钩子：`Modules/SessionRunner.cs:965-1126`
- 意图映射与回退解析：`Modules/SessionRunner.cs:1191-1373`
- 三层恢复系统：`Modules/SessionRunner.cs:1719-1975`
- 初始页面剧本与导航恢复：`Modules/SessionRunner.cs:1977-2219`
- 自动接入新平台：`Modules/SessionRunner.cs:2221-2440`

#### SessionRunner 保留职责清单（keep）
1. **会话生命周期总控**
   - 负责 `Run()` 入口、参数读取、异常兜底、结束态返回。
   - 证据：`78-133`, `675-682`。
2. **会话级策略编排与门控顺序**
   - 负责疲劳调权 -> 去重 -> RuleEngine 门控 -> 执行 -> 统计这一顺序一致性。
   - 证据：`437-477`, `537-567`, `598-606`。
3. **统一意图执行链协调**
   - 负责 action 到 intent、intent 回退、intent 到 operation 序列的编排。
   - 证据：`441-445`, `980-990`, `1215-1373`。
4. **会话级恢复编排与中止判定**
   - 负责操作后前台健康检查、遮挡恢复、多层恢复失败后的中止语义。
   - 证据：`1007-1018`, `1719-1975`。
5. **会话成功门控与对外契约变量**
   - 负责 `session_result/run_result/action_count/session_success_rate` 等输出一致性。
   - 证据：`630-673`。

#### 候选下沉职责清单（offload candidates）
1. **评论回退文案生成与文本清洗**
   - 当前在 `EnsureCommentTextAvailable/BuildFallbackCommentText/NormalizeCommentSeed`。
   - 证据：`1524-1590`。
   - 候选下沉：`CommentTextPolicy` 或 `ContentFallbackService`。
2. **帖子上下文组装与稳定标识构建**
   - 当前在 `UpdateCurrentPostContext/BuildCurrentPostIdentifier/BuildCurrentPostJson`。
   - 证据：`1592-1717`。
   - 候选下沉：`PostContextBuilder`。
3. **初始页面剧本规划细节**
   - 当前在 `PlanPreSessionActions/ExecutePreSessionActions`。
   - 证据：`2108-2219`。
   - 候选下沉：`PreSessionScriptPlanner`。
4. **自动接入新平台流程执行细节**
   - 当前在 `GetPackageNameForPlatform/RunAppOnboarder`。
   - 证据：`2227-2440`。
   - 候选下沉：`PlatformOnboardingBridge`。
5. **低层遮挡物识别与关闭动作细节**
   - 当前在 `DetectOverlayType/DismissOverlay`。
   - 证据：`1751-1865`。
   - 候选下沉：`OverlayRecoveryAgent`（由 SessionRunner 仅保留编排调用）。

#### 不能下沉职责（至少 3 项）与理由
1. **会话主循环控制权不能下沉**
   - 证据：`435-607`。
   - 理由：该循环直接绑定会话时间窗、动作数上限、统计计数与终止条件；若下沉会打散跨步骤状态一致性，导致 `session_result` 语义漂移。
2. **会话成功门控不能下沉到 ActionExecutor 或 RuleEngine**
   - 证据：`630-673`。
   - 理由：成功门控依赖会话级累计指标 `successful/failed/skipped`，而非单动作结果；执行器或规则引擎无法提供完整会话视角。
3. **意图回退最终决策权不能下沉到平台 JSON 层**
   - 证据：`1243-1279`, `1330-1373`。
   - 理由：JSON 只描述候选链，最终可执行性判断需结合运行态 `currentPage` 与 operation 实存性过滤，必须由会话协调层收敛。
4. **恢复分层编排控制不能整体下沉**
   - 证据：`1867-1975`。
   - 理由：Layer1/2/2.5/3 的调用预算和会话内 Vision 计数上限属于全局资源治理，必须由 SessionRunner 统一裁决。

#### keep vs offload 边界表

| 主题 | keep in SessionRunner | offload candidate | 边界规则 |
|------|------------------------|-------------------|----------|
| 会话生命周期 | `Run()` 入口、结束、异常、返回码 | 无 | 入口与最终结果契约必须单点维护 |
| 动作选择与门控顺序 | 疲劳调权、去重门控、RuleEngine 门控顺序控制 | 具体评分实现已在 `RuleEngine` | SessionRunner 只编排顺序，不承载评分算法细节 |
| 意图编排 | action->intent、fallback 决策、可执行序列过滤 | 平台映射数据在 `IntentMappings/*.json` | 数据可配置，裁决必须在运行时协调层 |
| 执行引擎调用 | 调用 `ActionExecutor.Execute`、处理 SUCCESS/SKIP/ERROR 会话语义 | 具体 UI 步骤在 `Operations/*.json` | 执行细节可下沉，结果语义与统计归 SessionRunner |
| 恢复体系 | 多层恢复编排、预算与中止判定 | 遮挡识别与关闭可抽离服务 | SessionRunner 保留编排和预算，底层动作可模块化 |
| 评论回退文案 | 触发时机判定 | 文案生成和清洗 | 触发时机在主循环，文案策略可独立 |
| 帖子上下文 | 何时更新上下文 | JSON 构建与标识算法 | 调用时机保留，构建逻辑可下沉 |
| 新平台接入 | 何时触发自动接入 | Python 调用与参数组装 | 触发条件在会话入口，执行桥接可下沉 |

#### 结论摘要
- SessionRunner 应保持为 **会话协调层**，不应继续吸收文本策略、上下文构建、平台接入工具细节。
- 可优先下沉的高收益区域：`评论回退文案`、`帖子上下文构建`、`遮挡物处理细节`、`自动接入桥接`。
- 后续 Task 8/9 设计可基于本边界表拆分服务，同时保持 SessionRunner 对会话结果契约的唯一所有权。

## 任务头
- **任务名称**: v4.5.8 会话成功门控升级
- **任务日期**: 2026-03-11
- **主层级**: L4
- **受影响层级**: L4
- **状态**: completed

### 任务 1: SessionRunner 初始页面检测与剧本规划 (2026-03-08)
- **主层级**: L4
- **受影响层级**: L4
- **状态**: completed

#### 完成内容
- DetectInitialPage() — APP 启动时检测当前页面
- PlanPreSessionActions() — 根据页面状态规划预设动作
- ExecutePreSessionActions() — 执行预设动作序列
- _consecutiveSkips + MAX_CONSECUTIVE_SKIPS — 连续 SKIP 导航恢复
- ForceNavigateToFeed() — 三级策略恢复到 feed 页

### 任务 2: SessionRunner 无限循环 Bug 修复与导航恢复 (2026-03-08)
- **主层级**: L3
- **受影响层级**: L3/L4
- **状态**: completed

#### 完成内容
- 修复了 Reddit 操作无限循环问题
- 添加了导航恢复机制

### 任务 3: v4.5.8 会话成功门控升级 (2026-03-11)
- **主层级**: L4
- **受影响层级**: L4
- **状态**: completed

#### 目标描述
修复 SessionRunner.cs 中遗留的 4 个问题，使其与 v4.5.8 CHANGELOG 声明一致:
1. 成功门控阈值: 50% -> 95% + min_successful_actions
2. 缺少输出变量: session_success_rate 等 5 个变量
3. actionCount 语义: action_count 输出改为仅统计成功动作
4. .omo 追踪文件同步

#### 完成内容
- 添加 successfulActions / skippedActions 分类计数器
- 计数逻辑改为 SUCCESS/SKIP/ERROR 三路分类
- 成功门控: sessionSuccessRate >= 0.95 && successfulActions >= min_successful_actions(默认6）
- 从 BehaviorConfig.json 的 session_gate 节读取门控阈值
- 输出变量: action_count(成功数), action_attempt_count(总尝试), session_successful_actions, session_failed_actions, session_skipped_actions, session_success_rate
- 更新 .omo 追踪文件

## 当前结构
- **修改后行数**: ~1790
- **核心方法**: 约 24 个

## L2 模块状态
module:
  id: m-biz-001
  name: SessionRunner
  file: Modules/SessionRunner.cs
  status: in_progress
  last_modified: 2026-03-14
  modified_by: ai_session

## 依赖影响
- **影响的模块**: StateSaver (读取 action_count 变量的语义变化)
- **依赖的模块**:
  - PageDetector (Detect 方法)
  - ActionExecutor (Execute 方法)
  - CoreHelper (GetLayout/Log/SetVar)
  - JsonHelper (ExtractObject/GetDouble/GetInt)
  - MemoryManager (v4.5.1)

## 变更日志
| 日期 | 会话 | 变更内容 |
|------|------|----------|
| 2026-03-08 | ses_333b9e5aaffeXX0HlRTFDkYjSf | 初始页面检测、剧本规划、导航恢复 |
| 2026-03-08 | ses_333b9e5aaffeXX0HlRTFDkYjSf | 无限循环修复、导航恢复策略 |
| 2026-03-11 | ses_current | v4.5.8 会话成功门控升级 (4 个遗留问题修复) |
| 2026-03-14 | ses_31ef88da3ffe6iQm4H0KDcFixk | SessionRunner 向 ZennoDroid 原生执行层迁移 (架构设计阶段) |
| 2026-03-14 | ses_31ef88da3ffe6iQm4H0KDcFixk | SmartOrchestrator 智能编排器实现 (Phase 1) |

---

## 强制文件顺序
1. `.omo/current-task/plan.md`
2. `.omo/modules/SessionRunner.md`
3. `Modules/Core/SmartOrchestrator.cs`
4. `Modules/SessionRunner.cs`
5. `CHANGELOG.md`

## 强制验证顺序
1. `SmartOrchestrator.cs` C# 5.0 语法合规（无 $""、?.、nameof()）
2. `SmartOrchestrator.cs` 花括号平衡
3. `SessionRunner.cs` C# 5.0 语法合规
4. `SessionRunner.cs` 花括号平衡
5. `.omo/modules/SessionRunner.md` 任务状态检查

## 强制运行命令
1. Select-String -Path 'Modules/Core/SmartOrchestrator.cs' -Pattern 'EvaluateResult|DecideRecovery|RecordSuccess|RecordFailure'
2. Select-String -Path 'Modules/SessionRunner.cs' -Pattern '_orchestrator|SmartOrchestrator'

**创建于**: 2026-03-08
**最后更新**: 2026-03-14
