# DPS v4.5 ZDProjects 目录

## 概述

此目录包含 ZennoDroid 专用的 **Own Code 入口文件**。

每个文件只包含 **模块加载器**（~200行，含缓存逻辑），业务逻辑在 `Modules/` 目录中。

---

## v4.5 新特性：多平台支持

- **JSON 配置驱动**: 通过 `Config/Operations/*.json` + `Config/IntentMappings/*.json` 定义平台操作
- **已接入平台**: Reddit、Instagram、BabyCenter（通过 PlatformsConfig.json 配置）
- **人性化引擎**: 4种行为配置文件
- **错误恢复**: 自动重试 + 指数退避

## v4.1 新特性：编译缓存

ModuleLoader 现在包含**静态编译缓存**：
- 首次运行：编译并缓存 (~500ms)
- 后续运行：直接使用缓存 (<10ms)
- 文件变更：自动检测并重新编译

```
┌─────────────────┐
│  Own Code 入口   │  检查缓存
└────────┬────────┘
         │
    ┌────┴────┐
    │ 缓存命中? │
    └────┬────┘
    是 ↙     ↘ 否
┌──────────┐  ┌──────────┐
│ 直接执行  │  │ 编译+缓存 │
│  <10ms   │  │  ~500ms  │
└──────────┘  └──────────┘
```

---

## 使用方法

1. **设置 ZD 变量**
   - `project_root` = `C:\DPS_v4.5` (项目根目录)
   - `device_id` = `device_001` (设备标识)
   - `current_platform` = `reddit` 或 `instagram` (v4.5 新增)

2. **复制代码到 ZD**
   - 打开对应的 `*_OwnCode.cs` 文件
   - 全选复制到 ZD 的 Own Code 动作块

3. **执行顺序**

   **整体式（旧架构，向后兼容）:**
   ```
   Initializer_OwnCode → Main_OwnCode → SessionRunner_OwnCode
   ```

   **ZD 外层流程编排（v4.7 新架构，ADR-015）:**
   ```
   Initializer_OwnCode → Main_OwnCode → DPS_Init_OwnCode
     → Loop { DPS_DecideAction_OwnCode → [ZD Switch 原生动作块] → DPS_CheckResult_OwnCode }
     → DPS_Finalize_OwnCode
   ```

---

## 文件列表

| 文件 | 功能 | 调用的模块 |
|------|------|-----------|
| `Initializer_OwnCode.cs` | 初始化 | `Modules/Initializer.cs` |
| `Main_OwnCode.cs` | 主入口 | `Modules/Main.cs` |
| `PersonaCreate_OwnCode.cs` | 创建画像 | `Modules/PersonaCreate.cs` |
| `DailyUpdate_OwnCode.cs` | 每日更新 | `Modules/DailyUpdate.cs` |
| `Extension_OwnCode.cs` | 扩展功能 | `Modules/Extension.cs` |
| `SessionRunner_OwnCode.cs` | 执行会话（整体式） | `Modules/SessionRunner.cs → Run()` |
| `DPS_Init_OwnCode.cs` | v4.7 ZD流程: 初始化 | `Modules/SessionRunner.cs → InitSession()` |
| `DPS_DecideAction_OwnCode.cs` | v4.7 ZD流程: 动作决策 | `Modules/SessionRunner.cs → DecideNextAction()` |
| `DPS_CheckResult_OwnCode.cs` | v4.7 ZD流程: 结果评估 | `Modules/SessionRunner.cs → EvaluateActionResult()` |
| `DPS_Finalize_OwnCode.cs` | v4.7 ZD流程: 会话终结 | `Modules/SessionRunner.cs → FinalizeSession()` |
| `StateSaver_OwnCode.cs` | 保存状态 | `Modules/StateSaver.cs` |
| `ReportGen_OwnCode.cs` | 生成报告 | `Modules/ReportGen.cs` |
| `WeeklyEvolve_OwnCode.cs` | 每周进化 | `Modules/WeeklyEvolve.cs` |
| `Maintenance_OwnCode.cs` | 系统维护 | `Modules/Maintenance.cs` |
| `ModuleLoader.cs` | 加载器模板 | - |

---

## 工作流程图

```
┌─────────────────┐
│  Own Code 入口   │  (~70行，ZD中执行)
│  *_OwnCode.cs   │
└────────┬────────┘
         │ 读取并编译
         ▼
┌─────────────────┐
│  业务模块       │  (外部.cs文件)
│  Modules/*.cs   │
└────────┬────────┘
         │ 依赖
         ▼
┌─────────────────┐
│  核心辅助       │
│  Core/*.cs      │
└─────────────────┘
```

---

## 修改业务逻辑

**无需重新复制到 ZD！**

直接编辑 `Modules/` 中的 .cs 文件即可，下次执行时会自动加载最新代码。

---

## v4.7 ZD 微流程搭建指南

> 从初始化到循环 + Switch + Finalize 的完整搭建路径。
> 搭建前请先阅读 [ConfigGuide_配置指南.md 6.1 节](../Docs/ConfigGuide_配置指南.md#61-v47-zd-微流程变量-新增) 创建所需变量。

### 快速搭建步骤

**Step 1: 创建 ZD 变量**

1. 菜单 **Window > Variables** 打开变量窗口
2. 点击 **Custom** 标签
3. 点左下方 **Add** 按钮
4. 输入变量名（如 `zd_step_plan`）
5. 在 **Default value** 列填初始值（全部为 String 类型，默认留空或填 `0`）
6. 重复上述步骤，直到全部约 20 个 `zd_*` 前缀变量创建完毕（完整清单参见 ConfigGuide 6.1 节）

**Step 2: 摆放 OwnCode 块**

> OwnCode 块的通用添加方法（后续所有 OwnCode 块均按此操作）：
> 1. 右键空白 > **Add action > Custom code > C# code**
> 2. 双击块打开代码编辑器
> 3. 用文本编辑器打开对应 .cs 文件 > Ctrl+A 全选 > Ctrl+C 复制
> 4. 回到 ZD 代码编辑器 > Ctrl+A > Ctrl+V 粘贴覆盖 > 点 **Save**
> 5. 右键块 > **Comment** > 输入块名（如 `DPS_Init`）

按以下顺序摆放（上方的块连线到下方）：

1. **Initializer_OwnCode**（已有，跳过）
2. **Main_OwnCode**（已有，跳过）
3. **DPS_Init_OwnCode**: 按上述通用方法，粘贴 `DPS_Init_OwnCode.cs` 代码，注释写 `DPS_Init`

**Step 3: 摆放循环体内的块**

循环体内需要按顺序摆放以下 4 个块（先全部建好，暂不建循环）：

1. **DPS_DecideAction_OwnCode**: 右键空白 > **Add action > Custom code > C# code**，粘贴 `DPS_DecideAction_OwnCode.cs`，注释写 `DPS_DecideAction`

2. **Switch 块**:
   - 右键空白 > **Add action > Logic > Switch**
   - 双击 Switch 块打开属性
   - **Variable** 字段填：`{-Variable.zd_step_type-}`
   - 在 **Conditions list** 中逐个添加 case 值：`L`, `T`, `T:long`, `S`, `B`, `W`, `V`, `DONE`
   - **Default** 分支自动存在，必须连线到 BadEnd（见下方）

3. **Advancer（步进器，用两个 Variables processing 块实现）**:

   - 右键空白 > **Add action > Data > Variables processing**
   - 模式选 **Increase\Decrease counter**
   - 方向属性选 **Increase counter**
   - Variable name: `zd_step_index`，Value: `1`
   - 再添加一个同类块：右键空白 > **Add action > Data > Variables processing**
   - 模式选 **Increase\Decrease counter**
   - 方向属性选 **Increase counter**
   - Variable name: `zd_safety`，Value: `1`

   备选方式（C# code 块）：
   - 右键空白 > **Add action > Custom code > C# code**
   - 贴入以下代码：
   ```csharp
   int idx = int.Parse(project.Variables["zd_step_index"].Value);
   project.Variables["zd_step_index"].Value = (idx + 1).ToString();
   int safety = int.Parse(project.Variables["zd_safety"].Value);
   project.Variables["zd_safety"].Value = (safety + 1).ToString();
   ```
   - 右键块 > **Comment** > 输入 `Advancer`

4. **DPS_CheckResult_OwnCode**: 右键空白 > **Add action > Custom code > C# code**，粘贴 `DPS_CheckResult_OwnCode.cs`，注释写 `DPS_CheckResult`

**Step 4: 创建循环（Repeat in loop）**

> ZennoDroid 没有独立的 Loop 块。正确做法是对一组块设置"循环重复"。

1. 按住 Ctrl，依次点选 Step 3 中建好的所有块（DecideAction、Switch 及其分支原生块、Advancer 计数器块、CheckResult）
2. 全部选中后，右键 > **Repeat in loop** > **Repeat while the condition is true**
3. 在弹出的输入框中填写条件：`{-Variable.zd_safety-} < 100 && {-Variable.zd_step_type-} != "DONE"`
4. ZD 会自动在这组块外包一个 IF 判断 + 回跳连线

**Step 5: 摆放 Switch 分支的原生块**

针对 Switch 的每个 case，创建对应的原生块并连线：

1. **case `L`（Locate，定位占位块）**:
   - 右键空白 > **Add action > Custom code > C# code**
   - 代码留空（或写 `project.SendInfoToLog("Locate placeholder");`）
   - 右键块 > **Comment** > 输入 `Locate`
   - 说明：实际定位逻辑由 DPS_CheckResult_OwnCode 中的 EvaluateActionResult 处理，此块仅做占位

2. **case `T`（Touch 普通点击）**:
   - 右键空白 > **Add action > Android > Touch emulation**
   - X from: `{-Variable.zd_tap_x1-}`，X to: `{-Variable.zd_tap_x2-}`
   - Y from: `{-Variable.zd_tap_y1-}`，Y to: `{-Variable.zd_tap_y2-}`
   - **不勾选** Long Tap

3. **case `T:long`（Touch 长按，独立块）**:
   - 右键空白 > **Add action > Android > Touch emulation**
   - X/Y 坐标同上，使用 `zd_tap_x1/x2/y1/y2` 变量
   - **勾选** Long Tap

4. **case `S`（Swipe 滑动）**:
   - 右键空白 > **Add action > Android > Swipe emulation**
   - X from: `{-Variable.zd_swipe_x1-}`，X to: `{-Variable.zd_swipe_x2-}`
   - Y from: `{-Variable.zd_swipe_y1-}`，Y to: `{-Variable.zd_swipe_y2-}`
   - Duration: `{-Variable.zd_swipe_duration-}`（毫秒）

5. **case `B`（Keyboard 返回键）**:
   - 右键空白 > **Add action > Android > Keyboard emulation**
   - Text: `{AndroidKeys.BACK}`
   - **Delay 必须启用**

6. **case `W`（Pause 等待）**:
   - 右键空白 > **Add action > Logic > Pause**
   - 秒数: `{-Variable.zd_wait_sec-}`

7. **case `V`（Verify，验证占位块）**:
   - 右键空白 > **Add action > Custom code > C# code**
   - 代码留空（或写 `project.SendInfoToLog("Verify placeholder");`）
   - 右键块 > **Comment** > 输入 `Verify`
   - 说明：实际验证逻辑由 DPS_CheckResult_OwnCode 处理，此块仅做占位

8. **case `DONE`（GoodEnd，正常结束）**:
   - 右键空白 > **Add action > Custom code > C# code**
   - 代码：`project.SendInfoToLog("[GoodEnd] 微流程正常完成");`
   - 右键块 > **Comment** > 输入 `GoodEnd`
   - 此块不再连线到下一个块（流程到此正常结束）

9. **Default 分支（BadEnd，异常结束）**:
   - 右键空白 > **Add action > Custom code > C# code**
   - 代码：`project.SendErrorToLog("[BadEnd] 微流程异常退出: " + project.Variables["zd_step_type"].Value);`
   - 右键块 > **Comment** > 输入 `BadEnd`
   - Switch 的 Default 分支必须连到这个块，不可悬空

**Step 6: 摆放 Finalize 块**

- 右键空白 > **Add action > Custom code > C# code**
- 粘贴 `DPS_Finalize_OwnCode.cs` 代码
- 右键块 > **Comment** > 输入 `DPS_Finalize`
- 连线：循环结束后（即循环条件不满足时）自动走到此块

**Step 7: 连线规则**

1. 所有 Switch 分支的原生块（Touch、Swipe、Keyboard、Pause、Locate 占位、Verify 占位），绿色箭头和红色箭头都连到 Advancer 的第一个计数器块
2. Advancer 计数器块之间顺序连线（zd_step_index 块 > zd_safety 块 > DPS_CheckResult_OwnCode）
3. DecideAction 和 Advancer 的红色箭头连 BadEnd
4. **GoodEnd 和 BadEnd 都必须连到 DPS_Finalize**（确保收尾逻辑执行：保存记忆、输出统计）
5. Switch 的 Default 分支必须连线到 BadEnd（不可悬空）

**Step 8: 测试验收**

1. 在变量窗口设置 `zd_step_plan` 的值为 `L:post_unit|W:3|S:down_900|W:2`
2. 点击运行
3. 预期结果：4 个步骤依次执行，最终 `zd_step_type` 的值变为 `DONE`
4. 检查日志输出中有 `[GoodEnd] 微流程正常完成`

> **兼容模式**: 若暂时不用新流程，将变量 `sr_use_legacy_run` 设为 `"true"` 即可走旧 `Run()` 路径。

### 相关文档

| 文档 | 内容 |
|------|------|
| [ConfigGuide 6.1 节](../Docs/ConfigGuide_配置指南.md#61-v47-zd-微流程变量-新增) | ZD 变量创建清单 + DSL 配置契约 |
| [TechManual 3.14~3.17](../Docs/TechManual_技术手册.md#314-zd-微流程已知坑点与防错规则-anti-errors) | Anti-Errors / 自检清单 / 防回归检查 / 施工路径 |
| `.sisyphus/plans/sessionrunner-zd-microflow-plan.md` | 完整契约定义 (Native Block / Variable / Switch / Arrow) |

---

## 语法版本说明

| 位置 | C# 版本 | 字符串插值 |
|------|---------|-----------|
| Own Code (本目录) | ~7.0+ | ✅ 可用 |
| Modules/*.cs | ~5.0 | ❌ 禁止 |

详见 `Modules/README.md`
