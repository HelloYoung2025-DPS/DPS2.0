# DPS v4.5 L1-L4 强制执行协议

## 1. 适用范围

本协议适用于本仓库的所有非简单开发任务。  
无论只改一个 `L4 step`，还是改跨平台架构，AI 都必须先按本协议落地。

## 2. 核心原则

1. **先判层级，再做实现**
2. **先改治理记录，再改运行文件**
3. **混合变更按 `L1 -> L2 -> L3 -> L4` 修改**
4. **验证按 `L4 -> L3 -> L2 -> L1` 回归**
5. **必须先跑 Gate 脚本 Preflight**
6. **必须按文件顺序执行 Advance**
7. **必须在结束前跑 Gate 脚本 Postflight**
8. **`CHANGELOG.md` 最后更新**
9. **若主层级无法判定，先用简短的苏格拉底式问题澄清**

## 3. 层级判定标准

| 层级 | 主要对象 | 典型目录/文件 | 什么时候归到这一层 |
|------|----------|---------------|--------------------|
| `L1` | 项目级治理、架构、跨模块规则 | `.omo/`、`Docs/`、全局约束 | 涉及主执行链、目录职责、跨模块契约 |
| `L2` | 单模块边界与接口 | `Modules/`、`Modules/Core/`、`Platforms/`、`Extensions/` | 主要影响一个模块的输入输出或职责 |
| `L3` | 操作、意图、流程契约 | `Config/ActionCatalog.json`、`Config/IntentMappings/*`、`Config/Operations/*` | 主要影响 action / intent / operation 编排 |
| `L4` | step、primitive、局部代码路径 | `Modules/Core/ActionExecutor.cs`、叶子模块方法、具体 steps | 主要影响具体执行步骤或最小代码块 |

## 4. 任务开始前必须读取

1. `.omo.conf`
2. `.omo/context.md`
3. `.omo/decisions_架构决策.md`
4. `.omo/conventions_代码规范.md`
5. `.omo/layers/l1-project.yaml`
6. `.omo/layers/l2-module.yaml`
7. `.omo/layers/l3-operation.yaml`
8. `.omo/layers/l4-step.yaml`
9. `.omo/modules/WORKFLOW.md`
10. `Docs/DOCS_RULES.md`
11. `~/.omo/global-hooks/pre-task.md`（若存在）

## 4.1 脚本级 Gate

强制使用：

- `Tools/omo_guard/Invoke-OmoGate.ps1 -Phase Preflight`
- `Tools/omo_guard/Invoke-OmoGate.ps1 -Phase Advance -FilePath "<当前文件>"`
- `Tools/omo_guard/Invoke-OmoGate.ps1 -Phase Postflight -ExecuteCommands`

规则：

1. `Preflight` 在计划文件与模块追踪文件准备完成后、任何实现文件修改前执行
2. `Preflight` 通过后，先对已完成的计划文件 / 模块追踪文件执行 `Advance`
3. 每修改完计划中的一个文件，必须用 `Advance` 按顺序打卡
4. 全部文件打卡完成前，`Postflight` 不允许通过
5. 若任务计划发生变化，必须先更新 `plan.md`，再重新执行 `Preflight`
6. 仅在只读演练或沙箱限制下，才允许使用 `-NoStateWrite`

## 5. 全任务公共顺序

### 修改顺序

1. 更新 `.omo/current-task/plan.md`
2. 若涉及 `L2/L3/L4`，创建或更新 `.omo/modules/{ModuleName}.md`
3. 执行 `Preflight`
4. 对已完成的计划文件 / 模块追踪文件执行 `Advance`
5. 更新受影响的 `.omo/layers/*.yaml`
6. 更新契约/配置文件
7. 更新实现文件
8. 更新入口包装、测试资产或工具脚本
9. 更新 `CHANGELOG.md`
10. 执行 `Postflight` 与验证命令

### 验证顺序

1. `L4`：step / primitive / 局部逻辑
2. `L3`：operation / intent / config 契约
3. `L2`：模块输入输出与装载
4. `L1`：跨模块、跨平台、架构一致性

## 6. 按主层级的强制文件顺序

### `L1` 主变更

1. `.omo/current-task/plan.md`
2. `.omo/decisions_架构决策.md`
3. `.omo/layers/l1-project.yaml`
4. 受影响的 `.omo/layers/l2-module.yaml`
5. 受影响的 `.omo/layers/l3-operation.yaml`
6. 受影响的 `.omo/layers/l4-step.yaml`
7. `Config/`、`Modules/`、`Tools/`、`Tests/`
8. `ZDProjects/`（仅当入口接口确实变化）
9. `CHANGELOG.md`
10. 每完成一项都必须调用 `Advance`

### `L2` 主变更

1. `.omo/current-task/plan.md`
2. `.omo/modules/{ModuleName}.md`
3. `.omo/layers/l2-module.yaml`
4. 若 operation 变化，先改 `.omo/layers/l3-operation.yaml`
5. 若 step 变化，先改 `.omo/layers/l4-step.yaml`
6. `Modules/{Module}.cs` 或 `Modules/Core/{Module}.cs`
7. 相关 `Config/` / `Platforms/` / `Extensions/`
8. `ZDProjects/*_OwnCode.cs`（仅接口变化时）
9. 测试资产
10. `CHANGELOG.md`
11. 每完成一项都必须调用 `Advance`

### `L3` 主变更

1. `.omo/current-task/plan.md`
2. `.omo/modules/{ModuleName}.md`
3. `.omo/layers/l3-operation.yaml`
4. `Config/ActionCatalog.json`（若动作语义变化）
5. `Config/IntentMappings/*.json`
6. `Config/Operations/*.json`
7. `Modules/SessionRunner.cs` / `Modules/Core/IntentTranslator.cs` / 相关编排模块
8. 测试资产
9. `CHANGELOG.md`
10. 每完成一项都必须调用 `Advance`

### `L4` 主变更

1. `.omo/current-task/plan.md`
2. `.omo/modules/{ModuleName}.md`
3. `.omo/layers/l4-step.yaml`
4. step 所属实现文件（通常为 `Modules/Core/ActionExecutor.cs` 或叶子模块）
5. 若影响 operation 契约，再回写 `Config/Operations/*.json`
6. 若影响 intent / action 契约，再回写 `Config/IntentMappings/*` 与 `.omo/layers/l3-operation.yaml`
7. 测试资产
8. `CHANGELOG.md`
9. 每完成一项都必须调用 `Advance`

## 7. 强制验证矩阵

| 变更类型 | 必做验证 | 备注 |
|----------|----------|------|
| `Config/*.json` | JSON 结构校验 | 变更后立即执行 |
| `Config/IntentMappings/*` / `Config/Operations/*` | JSON 校验 + 契约一致性检查 + operation 级验证 | 不能只改映射不测 operation |
| `Modules/` / `Modules/Core/` | 针对性编译/装载校验 | 无法编译时要明确说明缺口 |
| `ZDProjects/` | 动态装载兼容性校验 | 只在入口接口变化后触发 |
| 平台配置 / selector / operation | `ZDProjects/RuntimeTestRunner.cs` 或 `ZDProjects/Tests/*` | 若当前环境无 ZD，则必须声明未做运行时验证 |
| `Tools/app_onboarder/*.py` | Python 语法校验 + 相关脚本 smoke test | 不可只做静态阅读 |

### 推荐运行项

- Gate 预检：`pwsh -File Tools\omo_guard\Invoke-OmoGate.ps1 -Phase Preflight`
- Gate 收尾：`pwsh -File Tools\omo_guard\Invoke-OmoGate.ps1 -Phase Postflight -ExecuteCommands`
- JSON 校验：使用 PowerShell 的 `ConvertFrom-Json`
- Python 校验：使用 `python -m py_compile`
- 配置/平台回归：使用 `Tests/playwright_dps_test.js`
- ZennoDroid 运行校验：使用 `ZDProjects/RuntimeTestRunner.cs` 或相关 `ZDProjects/Tests/*_E2E_Test.cs`

## 8. 编码 / 优化硬规则

### 全层通用

- 修根因，不做表面补丁
- 保持向后兼容
- 不新增重复逻辑
- 所有 I/O 必须带错误处理
- 小步修改，禁止把多个无关目标混进一次提交

### `L1`

- 先写架构决策，再改实现
- 不允许隐式改变目录职责
- 不允许新增绕过主执行链的“旁路”

### `L2`

- 模块边界清晰，单一职责
- 不把物理执行细节直接扩散到业务编排层
- 能复用 `Modules/Core/*` 时，不在模块里复制一份工具逻辑

### `L3`

- 优先维护 `action -> intent -> operation` 的一致性
- operation 命名、入参、失败路径必须稳定
- 能通过配置表达，不先改业务代码

### `L4`

- 只改必要步骤，避免偷偷重定义 primitive 语义
- 必须明确 `success / retry / skip / abort` 的行为
- 新 step 若影响上层契约，必须回写 `L3`

## 9. 停止条件

出现以下任一情况时，AI 必须先提问，不得直接编码：

1. 无法准确判定主层级
2. 无法确认主模块归属
3. 用户请求同时改变多个层级，但目标优先级不清
4. 无法判断该改配置还是改模块
5. 无法判断应该跑哪类验证

## 10. 完成定义

只有同时满足以下条件，任务才算完成：

1. Gate `Preflight` 已执行
2. `.omo/current-task/plan.md` 已反映实际执行路径
3. 受影响层级的 `.omo/layers/*.yaml` 已更新
4. `L2/L3/L4` 任务的 `.omo/modules/{ModuleName}.md` 已更新
5. 实现文件已按计划顺序完成 `Advance`
6. 适用验证已执行，或明确声明无法执行的缺口
7. Gate `Postflight` 已执行
8. `CHANGELOG.md` 已最后更新
