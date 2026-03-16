# .omo 模块追踪与分层落地工作流

## 概述

本文件不是“可选建议”，而是 `L2/L3/L4` 任务的强制执行流程。  
任何涉及模块、操作、step 的任务，都必须同时遵守：

1. `.omo/layers/EXECUTION_PROTOCOL.md`
2. 本文件
3. `.omo/modules/TEMPLATE.md`
4. `Tools/omo_guard/Invoke-OmoGate.ps1`

## 文件结构

```
.omo/
├── modules/
│   ├── TEMPLATE.md
│   ├── {{ModuleName}}.md
│   └── index.md
├── layers/
│   ├── l1-project.yaml
│   ├── l2-module.yaml
│   ├── l3-operation.yaml
│   ├── l4-step.yaml
│   └── EXECUTION_PROTOCOL.md
└── current-task/
    └── plan.md
```

## 强制流程

### 第 1 步：先判层级

先判断这次修改的：

- **主层级**：`L1 / L2 / L3 / L4`
- **受影响层级**：可能不止一个
- **主模块**：如果主层级是 `L2/L3/L4`，必须明确主模块

若这三项有任何一项不清楚，先提问，不准直接编码。

### 第 2 步：先写计划

在 `.omo/current-task/plan.md` 中写清：

- 主层级
- 受影响层级
- 主模块
- 文件修改顺序
- 验证顺序
- 强制运行命令（这里填验证/构建/测试命令，不填 `Postflight` 自身）
- 预计要跑的检查

未写完计划，不允许开始实现。

### 第 3 步：创建或更新模块追踪文件

只要任务涉及 `L2/L3/L4`，都必须创建或更新：

- `.omo/modules/{ModuleName}.md`

这个文件必须在任何实现文件之前更新，至少包含：

- 主层级 / 受影响层级
- 本次任务目标
- 强制文件顺序
- 强制验证顺序
- 强制运行命令
- 当前进度与下次继续点

### 第 4 步：执行 Gate 预检

在计划和模块追踪文件准备完成后，必须执行：

- `pwsh -File Tools\omo_guard\Invoke-OmoGate.ps1 -Phase Preflight`

随后立刻对已经完成的文件按顺序执行：

- `pwsh -File Tools\omo_guard\Invoke-OmoGate.ps1 -Phase Advance -FilePath ".omo/current-task/plan.md"`
- 若存在模块追踪文件，再对 `.omo/modules/{ModuleName}.md` 执行 `Advance`

### 第 5 步：先更新分层登记，再改实现

顺序固定：

1. `.omo/layers/l2-module.yaml`
2. `.omo/layers/l3-operation.yaml`
3. `.omo/layers/l4-step.yaml`
4. `Config/` / `Modules/` / `Platforms/` / `Extensions/`
5. `ZDProjects/`（仅接口变化时）
6. 测试资产
7. `CHANGELOG.md`

若某层不受影响，可跳过；但不得跳过实际受影响的层。

每完成一个计划中的文件，必须执行：

- `pwsh -File Tools\omo_guard\Invoke-OmoGate.ps1 -Phase Advance -FilePath "<刚完成的文件>"`

## 模块追踪文件必须记录什么

| 部分 | 必填内容 |
|------|----------|
| 任务头 | 任务名称、主层级、受影响层级、模块名 |
| 目标 | 目标描述、风险、兼容性要求 |
| 文件顺序 | 先改哪些文件、后改哪些文件 |
| 验证顺序 | 先跑哪些检查、后跑哪些检查 |
| 运行命令 | `Postflight` 将执行的验证命令（不含 Gate 本身） |
| L2 状态 | 模块状态、版本、变更摘要 |
| L3 变化 | operation / intent / contract 变化 |
| L4 变化 | step / primitive / 局部代码变化 |
| 依赖影响 | 影响的模块、配置、测试 |
| 继续点 | 当前停留位置与下一步 |

## 分层开发规则

### `L2`

- 重点是模块边界、接口、职责
- 先更新 `l2-module.yaml`，再改模块代码
- 若涉及 operation / step，必须继续下钻到 `L3/L4`

### `L3`

- 重点是 `action -> intent -> operation`
- 先更新 `l3-operation.yaml`
- 再改 `Config/ActionCatalog.json`、`Config/IntentMappings/*`、`Config/Operations/*`
- 最后才改编排模块

### `L4`

- 重点是具体 step / primitive / 局部逻辑
- 先更新 `l4-step.yaml`
- 再改 step 所属代码
- 若影响 operation 契约，必须回写 `L3`

## 会话结束前必须完成

1. 更新 `.omo/modules/{ModuleName}.md`
2. 更新 `.omo/modules/index.md`（若有活跃模块变化）
3. 更新相关 `l2/l3/l4` yaml
4. 记录已跑验证和未跑验证
5. 最后更新 `CHANGELOG.md`
6. 执行 `pwsh -File Tools\omo_guard\Invoke-OmoGate.ps1 -Phase Postflight -ExecuteCommands`

## 失败与中断规则

若会话中断或任务未完成，模块追踪文件必须明确写出：

- 停在什么文件
- 已完成到哪一层
- 下一步先改什么
- 还缺哪些验证

## 版本

- **最后更新**: 2026-03-06
- **版本**: 3.1.0
