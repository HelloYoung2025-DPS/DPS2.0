# .omo 2.0 模块修改工作流程

## 🎯 概述

当需要修改一个模块时，.omo 2.0 会创建一个**模块追踪文件**，记录从 L1 到 L4 的所有修改内容，确保下次会话无缝衔接。

---

## 📁 文件结构

```
.omo/
├── modules/                    # 模块修改追踪目录
│   ├── TEMPLATE.md             # 模块记录模板
│   ├── {{ModuleName}}.md       # 模块修改记录（自动创建）
│   └── index.md                # 模块修改索引
│
├── layers/                     # L1-L4 层级定义（动态更新）
│   ├── l1-project.yaml         # 反映项目级变更
│   ├── l2-module.yaml          # 反映模块状态变更
│   ├── l3-operation.yaml       # 反映操作变更
│   └── l4-step.yaml            # 反映步骤变更
│
└── current-task/               # 当前任务（指向模块）
    └── plan.md                 # 包含模块修改计划
```

---

## 🔄 模块修改工作流

### 第一步：开始模块修改

用户指令：
```
"修改 SessionRunner 模块，优化疲劳模型"
```

**AI 自动执行**：
1. 在 `.omo/modules/` 创建 `SessionRunner.md`
2. 复制 `TEMPLATE.md` 内容
3. 填写模块基本信息
4. 更新 `l2-module.yaml` 中 SessionRunner 的状态为 `modifying`

### 第二步：记录修改内容

在修改过程中，AI 实时更新 `SessionRunner.md`：

```yaml
# L2 模块状态
module:
  status: modifying
  last_modified: 2026-02-28
  modified_by: ai_session_abc123

changes:
  - type: optimization
    description: 优化疲劳模型算法
    files_affected:
      - Modules/SessionRunner.cs
    methods_changed:
      - ApplyFatigue()
      - CalculateDecay()
```

### 第三步：会话结束保存

AI 自动：
1. 更新 `.omo/modules/SessionRunner.md` 的"下次会话继续点"
2. 更新 `.omo/modules/index.md` 记录本次会话
3. 保存当前状态到 `l2-module.yaml`

### 第四步：下次会话恢复

用户指令：
```
"继续修改 SessionRunner 模块"
```

**AI 自动执行**：
1. 读取 `.omo/modules/SessionRunner.md`
2. 定位"下次会话继续点"
3. 读取相关源码文件
4. 继续工作

---

## 📝 模块记录文件格式

每个模块的记录文件包含：

| 部分 | 内容 |
|------|------|
| **模块信息** | 名称、文件、日期、会话ID |
| **修改目标** | 目标描述、影响范围、预计时间 |
| **L2 模块状态** | 修改前后的状态对比（YAML） |
| **L3 操作变更** | 新增/修改/删除的操作 |
| **L4 步骤变更** | 新增/修改/删除的步骤 |
| **依赖影响** | 影响的模块、合约、测试 |
| **进度跟踪** | 当前阶段、完成度、剩余工作 |
| **继续点** | 下次会话从哪里开始 |
| **变更日志** | 历史修改记录 |

---

## 🔍 自动更新机制

### l2-module.yaml 自动更新

当模块状态变化时，自动更新：

```yaml
- id: m-biz-001
  name: SessionRunner
  status: modifying  # ← 自动变更
  last_modified: 2026-02-28  # ← 自动更新
  current_task: "优化疲劳模型"  # ← 新增字段
  session_id: "ses_abc123"  # ← 新增字段
```

### l3-operation.yaml 自动更新

当操作变更时，自动添加/更新操作记录

### l4-step.yaml 自动更新

当步骤变更时，自动添加/更新步骤记录

---

## 🎯 使用示例

### 示例 1：单次会话完成

```
用户: "修复 MemoryManager 的去重 bug"
AI:
  1. 创建 .omo/modules/MemoryManager.md
  2. 修改源码
  3. 更新记录文件
  4. 更新 l2-module.yaml 状态为 "stable"
  5. 归档记录文件到 .omo/history/
```

### 示例 2：多次会话完成

```
# 会话 1
用户: "开始重构 SessionRunner"
AI:
  1. 创建 .omo/modules/SessionRunner.md
  2. 状态: "implementing" (20%)
  3. 继续: "完成主循环重构"

# 会话 2
用户: "继续 SessionRunner 重构"
AI:
  1. 读取 .omo/modules/SessionRunner.md
  2. 定位继续点
  3. 继续重构
  4. 状态: "implementing" (60%)
  5. 继续: "完成疲劳模型重构"

# 会话 3
用户: "完成 SessionRunner 重构"
AI:
  1. 读取 .omo/modules/SessionRunner.md
  2. 完成剩余工作
  3. 状态: "testing"
  4. 更新 l2-module.yaml 为 "stable"
  5. 归档到 .omo/history/
```

---

## 📊 模块索引文件

`.omo/modules/index.md` 记录所有活跃的模块修改：

```markdown
# 活跃模块修改

| 模块 | 状态 | 进度 | 最后修改 | 会话 |
|------|------|------|----------|------|
| SessionRunner | modifying | 60% | 2026-02-28 | ses_abc123 |
| MemoryManager | stable | 100% | 2026-02-27 | ses_xyz789 |
```

---

## ✅ 无缝衔接保证

1. **状态持久化**：所有修改状态保存在 `.omo/modules/{{ModuleName}}.md`
2. **继续点明确**：每次会话结束前记录"下次会话继续点"
3. **上下文完整**：记录所有相关文件路径和修改内容
4. **版本追踪**：记录修改前后的版本和哈希
5. **依赖追踪**：记录影响的模块、合约、测试

---

**最后更新**: 2026-02-28
**版本**: 2.1.0
