# 平台接入文档模板

> **用途**: 此模板用于为新平台接入编写统一格式的文档
> **使用方式**: 优先在 `Docs/Platforms/{AppName}_APP_Guide_平台指南.md` 中套用本模板；不要在 `Docs/` 根目录新建新文档

---

## 1. 模块概述

### 基本信息
| 字段 | 值 |
|------|-----|
| **平台名称** | `[例: BabyCenter]` |
| **包名** | `[例: com.babycenter.pregnancytracker]` |
| **模块文件（可选）** | `[无需（所有平台通过 ActionExecutor + operations.json 配置驱动）]` |
| **操作配置** | `[例: Config/Operations/babycenter_operations.json]` |
| **意图映射** | `[例: Config/IntentMappings/babycenter_intents.json]` |
| **状态** | `[开发中 / 已完成 / 已废弃]` |
| **版本** | `[例: v4.5.8]` |

### 模块描述
[简要描述此平台的用途、支持的操作、特殊功能]

---

## 2. 支持的操作 / Intent

| 操作 | JSON 名称 | Intent 映射 | 描述 |
|------|-----------|-------------|------|
| `[例: 浏览信息流]` | `browse` | `browse_feed` | [详细描述] |
| `[例: 打开帖子]` | `open_post` | `open_post` | [详细描述] |
| `[例: 阅读帖子]` | `read_post` | `read_post` | [详细描述] |
| `[例: 点赞]` | `like` | `like_content` | [详细描述] |
| `[例: 评论]` | `comment` | `reply_post` | [详细描述] |
| `[例: 返回]` | `back_to_feed` | `navigate_back` | [详细描述] |

---

## 3. 配置文件位置

### 3.1 平台配置
**文件**: `Config/PlatformsConfig.json`
```json
"[platform_name_lowercase]": {
  "enabled": true,
  "package_name": "com.example.app",
  "ui_selectors": { ... },
  "rate_limits": { ... }
}
```

### 3.2 操作配置
**文件**: `Config/Operations/[platform_name]_operations.json`
```json
{
  "browse": { "steps": [...] },
  "like": { "steps": [...] }
}
```

### 3.3 意图映射
**文件**: `Config/IntentMappings/[platform_name]_intents.json`
```json
{
  "intents": {
    "browse_feed": { "operations": ["browse"], "fallback_intents": [] },
    "like_content": { "operations": ["like"], "fallback_intents": ["browse_feed"] }
  },
  "action_to_intent": {
    "browse": "browse_feed",
    "like": "like_content"
  }
}
```

---

## 4. UI 选择器

### 关键元素选择器

| 元素 | 主策略 | 主值 | 回退策略 | 回退值 |
|------|--------|------|----------|--------|
| post_unit | `[例: resource-id]` | `[例: com.app:id/post]` | `[例: text]` | `[例: Post]` |
| like_button | ... | ... | ... | ... |
| comment_button | ... | ... | ... | ... |

### 页面签名
| 页面 | visual_marker | ui_signature |
|------|---------------|--------------|
| feed | `[描述特征]` | `[XML 签名片段]` |
| post_detail | `[描述特征]` | `[XML 签名片段]` |

---

## 5. 速率限制

| 操作 | 每小时限制 | 冷却时间(秒) | 每日限制 |
|------|:----------:|:------------:|:--------:|
| browse | `[例: 100]` | `[例: 5]` | `-` |
| like | `[例: 24]` | `[例: 120]` | `[例: 80]` |
| comment | `[例: 12]` | `[例: 300]` | `[例: 30]` |

---

## 6. 特殊功能

### [功能名称]
- **描述**: [功能的作用]
- **实现方式**: [如何实现]
- **配置**: [相关配置参数]

### 回退链示例
```json
{
  "like": {
    "steps": [
      {
        "action": "if_exists",
        "condition": { "selector": "like_button" },
        "then": [
          { "action": "tap", "selector": "like_button" }
        ],
        "else": [
          { "action": "call_operation", "operation": "double_tap_like" }
        ]
      }
    ]
  }
}
```

---

## 7. 测试

### 测试脚本
**文件**: `ZDProjects/Tests/[PlatformName]_E2E_Test.cs`

### 测试流程
1. `[测试步骤 1]`
2. `[测试步骤 2]`
3. `[测试步骤 3]`

### 验证清单
- [ ] APP 启动成功
- [ ] 信息流加载正常
- [ ] 所有操作可执行
- [ ] 速率限制生效
- [ ] 回退链工作正常

---

## 8. 常见问题

### Q: [问题 1]
**A**: [解答]

### Q: [问题 2]
**A**: [解答]

---

## 9. 变更历史

| 日期 | 版本 | 变更内容 |
|------|------|----------|
| [YYYY-MM-DD] | [vX.X.X] | [变更描述] |
| [YYYY-MM-DD] | [vX.X.X] | [变更描述] |

---

## 10. 相关链接

- **主项目文档**: `Docs/README.md`
- **新人施工**: `Docs/ConfigGuide_配置指南.md`
- **架构参考**: `Docs/TechManual_技术手册.md`
- **测试工具**: `Tools/app_onboarder/README.md`

---

**文档维护**: 请在修改模块配置或实现时同步更新此文档
