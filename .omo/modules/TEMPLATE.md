# 模块修改记录模板

## 任务头
- **任务名称**: {{TASK_NAME}}
- **主层级**: {{PRIMARY_LAYER}}  # L1 | L2 | L3 | L4
- **受影响层级**: {{AFFECTED_LAYERS}}
- **模块名称**: {{MODULE_NAME}}
- **模块文件**: {{MODULE_FILE}}
- **修改日期**: {{DATE}}
- **会话ID**: {{SESSION_ID}}

## 修改目标
- **目标描述**: {{OBJECTIVE}}
- **兼容性要求**: {{COMPATIBILITY_REQUIREMENT}}
- **风险等级**: {{RISK_LEVEL}}
- **预计时间**: {{ESTIMATED_HOURS}} 小时

## 强制文件顺序
1. {{FILE_ORDER_1}}
2. {{FILE_ORDER_2}}
3. {{FILE_ORDER_3}}
4. {{FILE_ORDER_4}}
5. {{FILE_ORDER_5}}

## 强制验证顺序
1. {{VALIDATION_1}}
2. {{VALIDATION_2}}
3. {{VALIDATION_3}}
4. {{VALIDATION_4}}

## 强制运行命令
1. {{COMMAND_1}}
2. {{COMMAND_2}}
3. {{COMMAND_3}}
4. {{COMMAND_4}}

> 这里填写 `Postflight` 要执行的验证/构建/测试命令，不要填写 `Invoke-OmoGate.ps1 -Phase Postflight`

## L2 模块状态
```yaml
module:
  id: {{MODULE_ID}}
  name: {{MODULE_NAME}}
  file: {{MODULE_FILE}}
  status: modifying  # stable | modifying | testing | completed
  last_modified: {{DATE}}
  modified_by: ai_session
  primary_layer: {{PRIMARY_LAYER}}
  affected_layers: {{AFFECTED_LAYERS}}

before:
  version: {{VERSION_BEFORE}}
  lines: {{LINES_BEFORE}}
  methods: {{METHODS_BEFORE}}
  hash: {{HASH_BEFORE}}

after:
  version: {{VERSION_AFTER}}
  lines: {{LINES_AFTER}}
  methods: {{METHODS_AFTER}}
  hash: {{HASH_AFTER}}

changes:
  - type: {{CHANGE_TYPE}}  # feature | bugfix | refactor | optimization
    description: {{DESCRIPTION}}
    files_affected:
      - {{FILE_1}}
      - {{FILE_2}}
    methods_changed:
      - {{METHOD_1}}
      - {{METHOD_2}}
```

## L3 操作 / 契约变更
- **新增操作**: {{NEW_OPERATIONS}}
- **修改操作**: {{MODIFIED_OPERATIONS}}
- **删除操作**: {{REMOVED_OPERATIONS}}
- **影响的 intent / action / operation 契约**: {{AFFECTED_CONTRACTS}}

## L4 步骤 / Primitive 变更
- **新增步骤**: {{NEW_STEPS}}
- **修改步骤**: {{MODIFIED_STEPS}}
- **删除步骤**: {{REMOVED_STEPS}}
- **是否改变 primitive 语义**: {{PRIMITIVE_SEMANTIC_CHANGE}}

## 依赖影响
- **影响的模块**: {{AFFECTED_MODULES}}
- **影响的配置**: {{AFFECTED_CONFIGS}}
- **需要更新的测试**: {{AFFECTED_TESTS}}
- **需要更新的 .omo 文件**: {{AFFECTED_OMO_FILES}}

## 进度跟踪
- **当前阶段**: {{CURRENT_PHASE}}  # planning | implementing | testing | completed
- **完成度**: {{PERCENTAGE}}%
- **剩余工作**: {{REMAINING_WORK}}
- **已完成到哪一层**: {{COMPLETED_LAYER}}

## 下次会话继续点
- **当前位置**: {{CURRENT_LOCATION}}
- **下一步操作**: {{NEXT_ACTION}}
- **下一步先改哪个文件**: {{NEXT_FILE}}
- **还缺哪些验证**: {{MISSING_VALIDATIONS}}
- **Gate 当前状态**: {{GATE_STATUS}}
- **上下文文件**:
  - `.omo/current-task/plan.md`
  - `.omo/modules/{{MODULE_NAME}}.md`
  - `{{MODULE_FILE}}`
  - `.omo/layers/l2-module.yaml`
  - `.omo/layers/l3-operation.yaml`
  - `.omo/layers/l4-step.yaml`

## 变更日志
| 日期 | 会话 | 主层级 | 变更内容 |
|------|------|--------|----------|
| {{DATE_1}} | {{SESSION_1}} | {{LAYER_1}} | {{CHANGE_1}} |
| {{DATE_2}} | {{SESSION_2}} | {{LAYER_2}} | {{CHANGE_2}} |

---

**创建于**: {{CREATED_DATE}}
**最后更新**: {{LAST_UPDATED}}
