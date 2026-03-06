# 模块修改记录模板

## 模块信息
- **模块名称**: {{MODULE_NAME}}
- **模块文件**: {{MODULE_FILE}}
- **修改日期**: {{DATE}}
- **会话ID**: {{SESSION_ID}}

## 修改目标
- **目标描述**: {{OBJECTIVE}}
- **影响范围**: {{SCOPE}} - L1/L2/L3/L4
- **预计时间**: {{ESTIMATED_HOURS}} 小时

## L2 模块状态
```yaml
module:
  id: {{MODULE_ID}}
  name: {{MODULE_NAME}}
  file: {{MODULE_FILE}}
  status: modifying  # stable | modifying | testing | completed
  last_modified: {{DATE}}
  modified_by: ai_session

# 修改前的状态
before:
  version: {{VERSION_BEFORE}}
  lines: {{LINES_BEFORE}}
  methods: {{METHODS_BEFORE}}
  hash: {{HASH_BEFORE}}

# 修改后的状态
after:
  version: {{VERSION_AFTER}}
  lines: {{LINES_AFTER}}
  methods: {{METHODS_AFTER}}
  hash: {{HASH_AFTER}}

# 变更摘要
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

## L3 操作变更
- **新增操作**: {{NEW_OPERATIONS}}
- **修改操作**: {{MODIFIED_OPERATIONS}}
- **删除操作**: {{REMOVED_OPERATIONS}}

## L4 步骤变更
- **新增步骤**: {{NEW_STEPS}}
- **修改步骤**: {{MODIFIED_STEPS}}
- **删除步骤**: {{REMOVED_STEPS}}

## 依赖影响
- **影响的模块**: {{AFFECTED_MODULES}}
- **影响的合约**: {{AFFECTED_CONTRACTS}}
- **需要更新的测试**: {{AFFECTED_TESTS}}

## 进度跟踪
- **当前阶段**: {{CURRENT_PHASE}}  # planning | implementing | testing | completed
- **完成度**: {{PERCENTAGE}}%
- **剩余工作**: {{REMAINING_WORK}}

## 下次会话继续点
- **当前位置**: {{CURRENT_LOCATION}}
- **下一步操作**: {{NEXT_ACTION}}
- **上下文文件**:
  - `.omo/modules/{{MODULE_NAME}}.md` (本文件)
  - `{{MODULE_FILE}}` (源代码)
  - `.omo/contracts/{{MODULE_NAME}}.contract.json` (合约)

## 变更日志
| 日期 | 会话 | 变更内容 |
|------|------|----------|
| {{DATE_1}} | {{SESSION_1}} | {{CHANGE_1}} |
| {{DATE_2}} | {{SESSION_2}} | {{CHANGE_2}} |

---

**创建于**: {{CREATED_DATE}}
**最后更新**: {{LAST_UPDATED}}
