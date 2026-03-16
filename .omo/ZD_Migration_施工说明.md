# DPS v4.6 ZennoDroid 原生动作迁移 - 完整施工说明

> 经 Oracle + Librarian + Explore x3 + 手动 Momus/Metis 多智能体审核
> 审核发现 25 项问题 + 5 项补充，全部已纳入修订
> 最后更新: 2026-03-15

---

## 总览

| 项 | 值 |
|---|---|
| 目标 | SessionRunner 执行层从 C# ActionExecutor 迁移到 ZD 原生动作块编排 |
| 动机 | 利用 ZD 原生贝塞尔曲线滑动、随机偏移、分心模式等拟人化能力 |
| 试点 | Reddit 平台 |
| 架构 | ADR-015 四层架构 |
| 关键约束 | C# OwnCode 不能调用 ZD 子工作流（仅变量 + Switch 模式） |
| 总阶段 | Phase 0-I（11 个阶段） |
| 预估工期 | 14-20 天 |

---

## Phase 0: 技术验证（阻塞项，1-2 天）

必须在 ZennoDroid ProjectMaker 中实际验证，任一不通过则需调整架构。

| # | 验证项 | 方法 | 通过标准 | 不通过应对 |
|---|---|---|---|---|
| 0.1 | ZD Tap/Swipe 块的失败检测 | 创建最小测试项目：Tap 不存在的元素 | 有红线/绿线异常处理端口 | 每个原子动作前加 OwnCode 微块做元素查找 |
| 0.2 | OwnCode 块异常后 ZD 循环行为 | 故意 throw exception，观察循环 | ZD 有可预测的异常处理行为 | 每个 OwnCode 最外层 try-catch |
| 0.3 | Switch 块多分支性能 | 15+ case 的 Switch，观察路由延迟 | 路由延迟 <50ms | 嵌套 If-Else 替代 |
| 0.4 | ZD Pause 块变量驱动时长 | `{-Variable.zd_wait_ms-}` 作为 Pause 参数 | 变量值被正确读取 | OwnCode 调用 Thread.Sleep() |
| 0.5 | ZD Set Value 块变量输入 | `{-Variable.zd_text-}` 作为输入文本 | 文本正确输入 | OwnCode 调用 input.SendText() |
| 0.6 | Action Block 数量上限 | 创建 30+ 块的项目 | ProjectMaker 正常运行 | 拆分为多个子项目 |
| 0.7 | AI 视觉定位延迟 | VisionLocator 调用测试 | 响应 <3 秒 | 仅作 L5 兜底，不影响主流程 |

---

## Phase A: SmartOrchestrator 重构（1 天）

**目标**: 从"循环执行者"改为"纯决策器"。

**改造内容**:
- 新增 `GetNextAction()` 方法 → 返回下一步操作名（retry_same/back_to_feed/vision_check/abort），不执行任何动作
- 保留 `EvaluateResult()`（可能需微调参数）
- 保留 `SaveState()`/`LoadState()`（已实现）
- 旧 `ExecuteWithUnifiedEngine()` 内的 retry 循环标记为 legacy 路径

**受影响文件**:
- `Modules/Core/SmartOrchestrator.cs` — 新增方法
- `Modules/SessionRunner.cs` — 不修改（legacy 路径保留）

---

## Phase B0: ActionExecutor 通用步骤扩展（2-3 天）

**目标**: 在 ActionExecutor 中新增 5 个步骤类型 + 2 个扩展 + 1 个 VisionLocator 方法，为通用 ZD 动作块提供 C# 侧基础设施。

### B0.1 新增步骤类型（P0 必须）

#### `find_adaptive` — 多策略自适应定位

五级降级策略链：
```
L1: resource-id/text/content-desc/class (SelectorEngine 现有)
L2: text-contains/desc-contains (SelectorEngine 现有)
L3: relative (锚点元素 + bounds 内偏移比例)
L4: ratio (屏幕百分比坐标)
L5: vision (AI 截图识别 — 仅兜底)
```

JSON 格式:
```json
{
    "action": "find_adaptive",
    "strategies": [
        { "strategy": "resource-id", "value": "com.reddit:id/upvote" },
        { "strategy": "content-desc", "value": "Upvote" },
        { "strategy": "relative", "anchor_selector": "post_footer", "offset_x_ratio": 0.1, "offset_y_ratio": 0.5 },
        { "strategy": "ratio", "x_ratio": 0.15, "y_ratio": 0.85 },
        { "strategy": "vision", "prompt": "find upvote arrow button" }
    ],
    "save_as": "target",
    "on_fail": "skip"
}
```

写入上下文: `{save_as}`, `{save_as}_center_x`, `{save_as}_center_y`, `{save_as}_strategy`

#### `scroll_until` — 滑动寻找

```json
{
    "action": "scroll_until",
    "target_selector": "profile_button",
    "direction": "up",
    "max_attempts": 5,
    "scroll_distance": 600,
    "wait_after_scroll_ms": 800,
    "save_as": "profile_btn",
    "on_fail": "skip"
}
```

逻辑: 循环（最多 max_attempts 次）：GetLayout → Find → 找到则 OK → 没找到则 Scroll → 等待 → 重复

#### `dismiss_overlay` — 遮挡物自动处理

```json
{
    "action": "dismiss_overlay",
    "known_patterns": [
        { "strategy": "text-contains", "value": "Not now", "action": "tap" },
        { "strategy": "text-contains", "value": "Later", "action": "tap" },
        { "strategy": "text-contains", "value": "Allow", "action": "tap" },
        { "strategy": "resource-id", "value": "android:id/button2", "action": "tap" },
        { "strategy": "class", "value": "android.app.Dialog", "action": "back" }
    ],
    "fallback": "back",
    "max_attempts": 2,
    "on_fail": "skip"
}
```

逻辑: 参考 Kaspresso SystemDialogSafety — 扫描已知模式 → 点击/Back 关闭 → 重复检查

#### `find_relative` — 父子/锚点相对定位

```json
{
    "action": "find_relative",
    "anchor_selector": "post_unit",
    "anchor_index": 0,
    "child_selector": "upvote_button",
    "save_as": "upvote_in_post",
    "on_fail": "skip"
}
```

逻辑: 复用 UILocator.FindChildBoundsInParent 思路 — 在锚点元素的 XML 子区域中查找子元素

#### `check_state` — 元素状态检测

```json
{
    "action": "check_state",
    "selector": "upvote_button",
    "check": "content-desc",
    "expect_contains": "not pressed",
    "save_as": "vote_state",
    "on_match": "skip",
    "on_mismatch": "continue"
}
```

逻辑: 提取指定属性值 → 检查是否包含预期文本 → 返回 skip（已是目标状态）或 continue（需要操作）

### B0.2 现有步骤扩展

#### `tap` 支持比例坐标

在 ResolveTapTarget 中新增第 4 优先级:
1. context_ref (已有)
2. selector 实时查找 (已有)
3. 绝对坐标 x/y (已有)
4. **比例坐标 x_ratio/y_ratio (新增)** → 读 screen_width/height 转换

#### `swipe` 支持比例坐标

在 StepSwipe 中: 如果 stepJson 有 `start_x_ratio`，则读取 screen_width/height 转换为绝对坐标。这让 BabyCenter 的 ratio 配置终于能被消费。

#### `find` 输出 checked/selected 属性

在结果写入部分增加:
- `{save_as}_checked` — extractAttr(nodeStr, "checked")
- `{save_as}_selected` — extractAttr(nodeStr, "selected")

### B0.3 VisionLocator 新方法

在 VisionCorrector.cs 中新增:
```csharp
public static int[] LocateElement(string screenshotPath, string elementDescription)
```

输入: 截图路径 + 元素描述文本
输出: int[] {x, y} 坐标（或 null）
仅作为 L5 兜底，调用 AI 视觉模型

### B0.4 受影响文件

- `Modules/Core/ActionExecutor.cs` — ExecuteStep 新增 5 个 case 分支 + 2 个扩展
- `Modules/Core/VisionCorrector.cs` — 新增 LocateElement()
- `Modules/Core/SelectorEngine.cs` — 不修改（新步骤自己做策略循环）

---

## Phase B: ZD 通用原子动作块搭建 — Layer 1（1-2 天）

**设计变更**: 原 Reddit 专用编号 → 通用编号 U-xxx

### 通用原子动作块清单

| 编号 | 动作块 | ZD 实现 | 输入变量 | 输出变量 |
|---|---|---|---|---|
| **定位类** | | | | |
| U-001 | 自适应元素定位 | OwnCode 微块调 find_adaptive | `zd_strategies_json` | `zd_tap_x`, `zd_tap_y`, `zd_find_result` |
| U-002 | 滑动寻找 | OwnCode 微块调 scroll_until | `zd_target_selector`, `zd_scroll_dir`, `zd_max_attempts` | `zd_find_result`, `zd_tap_x`, `zd_tap_y` |
| U-003 | 比例坐标计算 | OwnCode 微块 ratio→absolute | `zd_x_ratio`, `zd_y_ratio` | `zd_tap_x`, `zd_tap_y` |
| **交互类** | | | | |
| U-010 | 自适应点击 | ZD 原生 Tap 块 | `zd_tap_x`, `zd_tap_y` | `zd_action_result` |
| U-011 | 滑动（绝对坐标） | ZD 原生 Swipe 块 | `zd_start_x/y`, `zd_end_x/y`, `zd_duration` | `zd_action_result` |
| U-012 | 滑动（比例坐标） | OwnCode(ratio→abs) + ZD Swipe | `zd_start_x_ratio` 等 | `zd_action_result` |
| U-013 | 文本输入 | ZD 原生 Set Value 或 OwnCode | `zd_text` | `zd_action_result` |
| U-014 | 返回键 | ADB keyevent 4 | - | `zd_action_result` |
| U-015 | 等待 | ZD 原生 Pause 或 OwnCode | `zd_wait_ms` | - |
| **流程类** | | | | |
| U-020 | 遮挡物检测处理 | OwnCode 微块调 dismiss_overlay | `zd_overlay_patterns` | `zd_overlay_dismissed` |
| U-021 | 内容加载等待 | OwnCode(DOM 稳定检测) | `zd_content_selector`, `zd_max_wait_ms` | `zd_content_loaded` |
| U-022 | 页面断言修正 | OwnCode 调 PageDetector + 导航修正 | `zd_require_page` | `zd_page_ok`, `zd_current_page` |
| U-023 | AI 视觉辅助定位 | OwnCode 调 VisionLocator | `zd_vision_prompt` | `zd_tap_x`, `zd_tap_y` |

### ZD 施工步骤

1. 在 ProjectMaker 中创建每个通用块
2. 每个块的 OwnCode 微块通过 ModuleLoader 加载 ActionExecutor 执行对应新步骤
3. ZD 原生块（Tap/Swipe/Pause）直接读变量执行
4. 每次重大修改后导出 .zp 备份
5. 在施工说明中详细记录每个块的参数配置（作为重建手册）

### 与旧 Reddit 编号的映射

| 旧编号 | 新通用块 | 说明 |
|---|---|---|
| 001_01_001 Feed Tap | U-001 + U-010 | 先定位再点击 |
| 001_01_003 Feed SwipeDown | U-012 | 比例坐标滑动 |
| 001_99_001 Any Back | U-014 | 通用返回键 |
| 001_99_003 Any InputText | U-013 | 通用文本输入 |
| 001_99_004 Any Wait | U-015 | 通用等待 |

---

## Phase C: ZD 通用组合微流程搭建 — Layer 2（1-2 天）

### 7 个通用微流程模式

每条组合链 = 通用原子块的组合，头部加 `dismiss_overlay` 预扫描，尾部加日志。

| # | 模式 | ZD 链组合 | 适用场景 |
|---|---|---|---|
| M1 | 自适应点击 | U-020(弹窗) → U-001(定位) → U-010(点击) → OwnCode(验证) | 目标元素不确定能否定位 |
| M2 | 滑动寻找后操作 | U-020 → U-002(滑动找) → U-010(点击) → OwnCode(验证) | 元素在视口外 |
| M3 | 状态感知操作 | OwnCode(check_state) → If(需要操作) → M1 | 避免取消赞/重复关注 |
| M4 | 多步骤操作 | U-020 → U-001(框定位) → U-010(点击框) → U-013(输入) → U-001(按钮定位) → U-010(点击) | 评论等复杂操作 |
| M5 | 页面导航 | U-022(断言) → If(不在目标页) → U-014(Back)/Tab 点击 → U-022(验证) | 操作前页面修正 |
| M6 | 内容浏览 | U-020 → U-012(比例滑动) → U-015(等待) → U-021(加载等待) | 浏览 feed |
| M7 | 条件策略选择 | if_exists(按钮) → M1 ; else → 双击图片 | Instagram 双击 vs 按钮点赞 |

### Reddit 组合链对照

| 操作 | ZD 链 | 步骤间检查 |
|---|---|---|
| `browse` | M6(弹窗→滑动→等待→加载等待) | 无需中间检查 |
| `open_post` | M5(页面断言feed) → M1(定位帖子→点击) → M5(断言post_detail) | Tap 后检查 |
| `like` | M3(check_state: 未点赞) → M1(定位upvote→点击) | 状态检查 |
| `read_post` | M6(比例滑动×随机次数→等待) | 无需中间检查 |
| `comment` | M4(定位评论框→点击→输入文本→定位发送→点击) | **每步都检查** |
| `back_to_feed` | U-014(Back) → M5(断言feed) | 断言检查 |

### 组合链日志要求（补充项 S1）

每条链的入口和出口 OwnCode 微块必须调用 `CoreHelper.Log()`:
- 入口: `[ZD_CHAIN] 开始执行 {操作名}`
- 出口: `[ZD_CHAIN] {操作名} 结果: {zd_composite_result}`

---

## Phase D: C# SessionRunner 拆分 — Layer 3（2-3 天）

### 4 个入口方法

| 方法 | ZD 块名 | 职责 |
|---|---|---|
| `InitSession(project, instance)` | DPS_Init | 设备检测、加载配置、缓存 screen_width/height、初始化组件、初始页面检测、剧本规划执行 |
| `DecideNextAction(project, instance)` | DPS_Decide | 恢复状态 → 疲劳调权 → 加权选择 → RuleEngine → 去重 → 页面断言 → 设置 zd_composite_op + strategies |
| `EvaluateResult(project, instance)` | DPS_Evaluate | 读 zd_composite_result → 页面检测 → SmartOrchestrator 判定 → 计数器 → checkpoint → 循环控制 |
| `FinalizeSession(project, instance)` | DPS_Finalize | 保存记忆 → 清理 → 成功门控 → 统计 → 输出变量 |

### 静态字段迁移（仅 7 个运行时状态）

| 字段 | CoreHelper Key | 序列化方式 |
|---|---|---|
| `_currentPage` | `current_page` | 已有，不新增 |
| `_consecutiveSkips` | `dps_consecutive_skips` | int→string |
| `_visionRecoveryCount` | `dps_vision_recovery_count` | int→string |
| `_orchestrator` 状态 | `dps_orchestrator_state` | SaveState()/LoadState() |
| `SessionState` | `dps_session_state` | 需新增序列化方法 |
| 计数器(action/success/fail/skip) | `dps_action_count` 等 | int→string |
| `_memoryEntries` | `dps_memory_entries` | JSON 数组字符串 |

不需要迁移: _operationsJson, _platformConfig 等配置类字段（每次 Init 从文件加载）

### VK 常量类

```csharp
private static class VK
{
    public const string CompositeOp = "zd_composite_op";
    public const string CompositeResult = "zd_composite_result";
    public const string StrategiesJson = "zd_strategies_json";
    public const string SessionLoop = "dps_session_loop";
    public const string OrchestratorState = "dps_orchestrator_state";
    public const string SessionState = "dps_session_state";
    public const string ActionCount = "dps_action_count";
    public const string SuccessCount = "dps_success_count";
    public const string FailCount = "dps_fail_count";
    public const string SkipCount = "dps_skip_count";
    public const string ScreenWidth = "screen_width";
    public const string ScreenHeight = "screen_height";
}
```

### 变量命名空间约定

- `dps_*` — 会话编排状态
- `zd_*` — ZD 路由和动作参数/结果
- `sr_*` — 旧变量（向后兼容保留）

### DPS_Init 新增职责

获取并缓存设备屏幕尺寸:
```csharp
// 在 InitSession 中
string layoutXml = CoreHelper.GetLayout();
// 从 XML 根节点提取 bounds="[0,0][width,height]"
int[] screenBounds = SelectorEngine.ParseBounds(rootBounds);
CoreHelper.SetVar("screen_width", screenBounds[2].ToString());
CoreHelper.SetVar("screen_height", screenBounds[3].ToString());
```

### 现有组件命运

| 组件 | 迁移后状态 | 说明 |
|---|---|---|
| ActionExecutor.cs | **保留不废弃** | legacy 路径 + fallback 均需要 |
| IntentTranslator.cs | 标记 legacy-only | ZD native 路径不经过 |
| ZennoDroidAdapter.cs | **保留** | OwnCode 定位仍需要 |
| SelectorEngine.cs | **保留** | 所有定位的核心 |
| HumanizationEngine.cs | **保留** | ZD 原生块替代部分功能，其余保留 |

---

## Phase E: ZD 外层工作流搭建（1-2 天）

### 主流程

```
[START]
   |
[OwnCode: DPS_Init]
   |
   ├─ "READY" → [dps_session_loop = "true"]
   |                |
   |          ┌─────┴──────────────────┐
   |          │ LOOP: dps_session_loop │
   |          └─────┬──────────────────┘
   |                |
   |          [OwnCode: DPS_Decide]
   |                |
   |          [If: zd_composite_op == "END"]
   |           ├─ Yes → [dps_session_loop = "false"]
   |           └─ No  ↓
   |                |
   |          [If: execution_mode == "zd_native"]
   |           ├─ Yes → [Switch: zd_composite_op]
   |           |         ├─ "browse"      → [Browse 组合链]
   |           |         ├─ "open_post"   → [OpenPost 组合链]
   |           |         ├─ "like"        → [Like 组合链]
   |           |         ├─ "comment"     → [Comment 组合链]
   |           |         ├─ "read_post"   → [ReadPost 组合链]
   |           |         ├─ "back_to_feed"→ [BackToFeed 组合链]
   |           |         └─ default       → [OwnCode: legacy_fallback]
   |           └─ No  → [OwnCode: legacy_executor]
   |                |
   |          (汇聚)
   |                |
   |          [OwnCode: DPS_Evaluate]
   |                |
   |          [回到 LOOP]
   |
   └─ "ERROR" → [日志: 初始化失败] → [END]
   
[OwnCode: DPS_Finalize]
   |
[END]
```

### Feature Flag（会话级）

- `execution_mode = "zd_native"` → 走 ZD 原生组合链
- `execution_mode = "legacy"` → 走 ActionExecutor.Execute()
- 在 DPS_Init 中读取，单一 If 分支控制，不做 per-operation 灰度

### OwnCode 安全保障

每个 OwnCode 块最外层:
```csharp
try {
    // 业务逻辑
} catch (Exception ex) {
    project.Variables["dps_session_loop"].Value = "false";
    project.Variables["dps_exit_reason"].Value = "exception: " + ex.Message;
    project.SendErrorToLog("[DPS] " + ex.Message);
}
```

### 新建 OwnCode 入口文件（4 个）

| 文件 | 调用方法 |
|---|---|
| `ZDProjects/DPS_Init_OwnCode.cs` | `SessionRunner.InitSession(project, instance)` |
| `ZDProjects/DPS_Decide_OwnCode.cs` | `SessionRunner.DecideNextAction(project, instance)` |
| `ZDProjects/DPS_Evaluate_OwnCode.cs` | `SessionRunner.EvaluateResult(project, instance)` |
| `ZDProjects/DPS_Finalize_OwnCode.cs` | `SessionRunner.FinalizeSession(project, instance)` |

---

## Phase F: 打字速度逻辑清理（0.5 天）

从 SessionRunner 中删除打字速度相关代码（用户明确说由其他程序处理）。
删除: typingSection 配置读取、typingSpeed/typingLevel/wpmMin/wpmMax 变量。
保留: EnsureCommentTextAvailable()（评论文本生成仍需要）。

---

## Phase G: .omo Gate 合规与文档更新（1 天）

按 EXECUTION_PROTOCOL.md 严格执行:

1. `.omo/current-task/plan.md` — 创建迁移任务计划
2. `.omo/modules/SessionRunner.md` — 更新模块追踪
3. 执行 `Preflight`
4. `.omo/decisions_架构决策.md` — ADR-015 更新
5. `.omo/layers/l1-project.yaml` — 更新编码规则
6. `.omo/layers/l2-module.yaml` — 新增模块登记
7. `.omo/layers/l3-operation.yaml` — 新增操作登记
8. `.omo/layers/l4-step.yaml` — 新增步骤登记
9. 实现文件修改（已在各 Phase 中完成）
10. `CHANGELOG.md` — v4.6.2 更新
11. 执行 `Postflight`

---

## Phase H: 验证与 ConfigGuide（2-3 天）

### 阶段 1: Reddit 试点最小闭环（7 个场景）

| # | 场景 | 验证路径 | 成功标志 |
|---|---|---|---|
| 1 | 正常 browse | DPS_Decide → browse 链 → DPS_Evaluate → SUCCESS | result_page=feed |
| 2 | like 操作 | check_state → like 链 → 双层判定 | 不会取消已有赞 |
| 3 | open_post | 定位帖子 → 点击 → 页面=post_detail | 页面检测正确 |
| 4 | back_to_feed | Back → 断言 feed | 页面恢复 |
| 5 | 故障恢复 | 模拟元素找不到 → SmartOrchestrator → Retry | 自动升级 |
| 6 | 假成功检测 | 操作后页面未变 → BusinessFailed | 检出假成功 |
| 7 | 弹窗处理 | dismiss_overlay 预扫描 → 关闭 → 继续操作 | 弹窗不阻塞 |

### 阶段 2: ConfigGuide 重写（至少 3 个核心操作验证通过后）

### 阶段 3: 经典案例集（16 个案例，含恢复和拟人化）

---

## Phase I: 旧文件处置与 operations.json v2（0.5 天）

### 旧文件处置

- `ZDProjects/Reddit_Browse.cs` 等 → 标记 @deprecated
- 验证通过后决定是否删除

### operations.json 通用元数据升级

所有平台的 operations 统一添加:
```json
{
    "operation_name": {
        "overlay_risk": false,
        "recovery_hint": "none|nav_tab|relaunch",
        "fallback_op": null,
        "preconditions": ["feed"],
        "pre_check": {
            "dismiss_overlay": true,
            "check_state": { "selector": "...", "expect": "..." }
        }
    }
}
```

---

## 执行顺序与依赖图

```
Phase 0 (技术验证) ─── 通过 ──┐
                              │
                   ┌──────────┤
                   │          │
            Phase A          Phase B0
         (SmartOrch)      (AE 扩展)
                   │          │
                   │    ┌─────┤
                   │    │     │
                   │  Phase B  │
                   │ (ZD原子块) │
                   │    │     │
                   │  Phase C  │
                   │ (ZD组合链) │
                   │    │     │
                   └────┴─────┘
                        │
                   Phase D (C# 拆分)
                        │
                   Phase E (ZD 外层工作流)
                        │
              ┌─────────┤
              │         │
         Phase F    Phase G
        (清理)    (.omo Gate)
              │         │
              └────┬────┘
                   │
              Phase H (验证)
                   │
              Phase I (收尾)
```

可并行: Phase A + Phase B0; Phase B + Phase D（部分）; Phase F 可随时做

---

## 风险登记

| # | 风险 | 级别 | 缓解 |
|---|---|---|---|
| R1 | ZD Tap/Swipe 无原生失败信号 | **高** | Phase 0 验证；不通过用 OwnCode 微块 |
| R2 | 变量命名空间污染 | **中** | VK 常量类 + dps_/zd_ 前缀约定 |
| R3 | ZD 循环异常退出 | **中** | 每个 OwnCode 最外层 try-catch |
| R4 | 进程被杀状态丢失 | **中** | DPS_Evaluate 每次循环末尾 checkpoint |
| R5 | ZD 项目不可 diff | **低** | .zp 备份 + 参数配置文档 |
| R6 | AI 视觉定位延迟 | **低** | 仅 L5 兜底，主流程不依赖 |
| R7 | 多策略定位增加执行时间 | **低** | L1 命中则不走后续级别 |
| R8 | ZD native 块行为与 ActionExecutor 不一致 | **中** | 先用 feature flag 对比测试 |

---

## 工作量估计

| Phase | 工作量 | 前置条件 |
|---|---|---|
| Phase 0: 技术验证 | 1-2 天 | 需要 ZD ProjectMaker |
| Phase A: SmartOrch 重构 | 1 天 | Phase 0 通过 |
| Phase B0: AE 扩展 | 2-3 天 | Phase 0 通过 |
| Phase B: ZD 原子块 | 1-2 天 | Phase B0 |
| Phase C: ZD 组合链 | 1-2 天 | Phase B |
| Phase D: C# 拆分 | 2-3 天 | Phase A |
| Phase E: ZD 外层工作流 | 1-2 天 | Phase C + Phase D |
| Phase F: 打字速度清理 | 0.5 天 | 无 |
| Phase G: .omo Gate | 1 天 | Phase E |
| Phase H: 验证 + ConfigGuide | 2-3 天 | Phase G |
| Phase I: 收尾 | 0.5 天 | Phase H |
| **总计** | **14-20 天** | |

---

## 跨平台影响声明

| 平台 | 影响 | 说明 |
|---|---|---|
| Instagram | **无影响** | legacy Run() 保留，通用块未来可复用 |
| BabyCenter | **无影响** | 已有 ratio 坐标和 visual_verify 配置，未来直接受益 |
| TikTok/Facebook | **无影响** | 待接入，直接使用通用框架 |

---

## 审核记录

| 日期 | 审核源 | 发现数 | 状态 |
|---|---|---|---|
| 2026-03-15 | Oracle (架构审查) | 3 风险 + 8 建议 | ✅ 已纳入 |
| 2026-03-15 | Librarian (ZD 约束) | 1 架构决定性约束 | ✅ 已纳入 |
| 2026-03-15 | Explore (CoreHelper) | 变量机制确认 | ✅ 已纳入 |
| 2026-03-15 | Explore (ActionExecutor) | 17 步骤类型全景 | ✅ 已纳入 |
| 2026-03-15 | Explore (ZD 原生能力) | VisionCorrector + 人性化引擎 | ✅ 已纳入 |
| 2026-03-15 | Librarian (Android 自动化) | 7 边界场景解决模式 | ✅ 已纳入 |
| 2026-03-15 | 手动 Momus (计划批评) | 5 补充项 | ✅ 已纳入 |
| 2026-03-15 | 手动 Metis (隐含需求) | 跨平台/多设备/生命周期 | ✅ 已纳入 |
| 2026-03-15 | Oracle (验证) | VERIFIED | ✅ |
