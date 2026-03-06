# Reddit 测试指南 (Reddit Test Guide)

> 合并自: Reddit_TestGuide.md + Reddit_EvidenceChecklist.md (2026-03-06)
> 适用于: DPS v4.5 Reddit 自动化模块验证

---

## Part 1: 测试执行指南

### 测试脚本清单

| 脚本 | 功能 | 文件路径 |
|------|------|----------|
| **Reddit_Browse.cs** | 浏览 feed，跟踪帖子 | `ZDProjects/Reddit_Browse.cs` |
| **Reddit_Like.cs** | 点赞帖子 | `ZDProjects/Reddit_Like.cs` |
| **Reddit_ReadPost.cs** | 阅读帖子内容 | `ZDProjects/Reddit_ReadPost.cs` |
| **Reddit_Comment.cs** | 读取评论，可选回复 | `ZDProjects/Reddit_Comment.cs` |
| **Reddit_IntegrationTest.cs** | 集成测试（全流程） | `ZDProjects/Reddit_IntegrationTest.cs` |

### 前置准备

#### 1. ZennoDroid 环境
- ZennoDroid 已安装并连接到 Android 设备
- Android 设备已安装 Reddit 应用（`com.reddit.frontpage`）
- Reddit 应用已登录账号

#### 2. 创建项目变量

在 ZennoDroid 项目中创建以下变量（所有脚本共用）：

**Browse 脚本变量**:
- `browse_scroll_count` (默认: "3") - 滚动次数
- `browse_scroll_delay` (默认: "2000") - 滚动延迟（毫秒）
- `browse_posts_count` (输出) - 找到的帖子数量
- `browse_result` (输出) - 执行结果

**Like 脚本变量**:
- `like_post_index` (默认: "0") - 目标帖子索引
- `like_verify_delay` (默认: "1500") - 验证延迟（毫秒）
- `like_result` (输出) - 执行结果
- `like_ui_changed` (输出) - UI 是否变化

**ReadPost 脚本变量**:
- `readpost_post_index` (默认: "0") - 目标帖子索引
- `readpost_scroll_count` (默认: "2") - 滚动次数
- `readpost_scroll_delay` (默认: "1500") - 滚动延迟（毫秒）
- `readpost_result` (输出) - 执行结果
- `readpost_text` (输出) - 提取的文本
- `readpost_text_length` (输出) - 文本长度

**Comment 脚本变量**:
- `comment_post_index` (默认: "0") - 目标帖子索引
- `comment_enable_reply` (默认: "false") - 是否启用回复
- `comment_reply_text` (默认: "") - 回复文本
- `comment_scroll_count` (默认: "2") - 滚动次数
- `comment_result` (输出) - 执行结果
- `comment_count` (输出) - 评论数量
- `comment_text` (输出) - 评论文本

**Integration 脚本变量**:
- `integration_total_tests` (输出) - 总测试数
- `integration_passed_tests` (输出) - 通过测试数
- `integration_success_rate` (输出) - 成功率
- `integration_result` (输出) - 总体结果

---

### 执行步骤

#### 方法 1：单独测试每个脚本

##### 测试 1: Reddit_Browse.cs

1. 创建 ZennoDroid 项目 → 添加 "Own Code" 动作块
2. 复制 `Reddit_Browse.cs` 全部内容到代码编辑器
3. 设置变量: `browse_scroll_count` = "3", `browse_scroll_delay` = "2000"
4. 运行项目

**预期日志输出**:
```
[Browse] Starting Reddit Browse...
[Browse] Parameters: scrollCount=3, scrollDelay=2000
[Browse] Opening Reddit app...
[Browse] Screen size: 1080x2400
[Browse] === Scroll 0/3 ===
[Browse] Found 3 posts on screen
[Browse] New post #1: Y=200-800
...
[Browse] === Browse Complete ===
[Browse] Total unique posts viewed: 8
```

##### 测试 2: Reddit_Like.cs

1. 创建新的 ZennoDroid 项目 → 添加 "Own Code" 动作块
2. 复制 `Reddit_Like.cs` 全部内容
3. 设置变量: `like_post_index` = "0", `like_verify_delay` = "1500"
4. 确保 Reddit 已打开显示 feed → 运行

**预期日志输出**:
```
[Like] Starting Reddit Like...
[Like] Parameters: postIndex=0
[Like] Getting UI hierarchy...
[Like] Found 3 posts on screen
[Like] Upvote button at: (174, 1720)
[Like] Clicking upvote button...
[Like] Verifying UI change...
[Like] UI changed: true
[Like] ✓ Like action successful
```

##### 测试 3: Reddit_ReadPost.cs

1. 复制 `Reddit_ReadPost.cs` 到 Own Code
2. 设置变量: `readpost_post_index` = "0", `readpost_scroll_count` = "2"
3. 确保 Reddit feed 可见 → 运行

**预期日志输出**:
```
[ReadPost] Starting Reddit ReadPost...
[ReadPost] Clicking post at: (540, 600)
[ReadPost] ✓ Entered post detail page
[ReadPost] Total text extracted: 1234 chars
[ReadPost] ✓ Returned to feed successfully
```

##### 测试 4: Reddit_Comment.cs

1. 复制 `Reddit_Comment.cs` 到 Own Code
2. 设置变量: `comment_post_index` = "0", `comment_enable_reply` = "false"
3. 确保 Reddit feed 可见 → 运行

**预期日志输出**:
```
[Comment] Starting Reddit Comment...
[Comment] Clicking comment button at: (395, 1720)
[Comment] ✓ Entered comment section
[Comment] Total unique comments collected: 15
[Comment] ✓ Returned to feed successfully
```

#### 方法 2：运行集成测试

##### 测试 5: Reddit_IntegrationTest.cs

> 注意：当前版本使用模拟结果。真实自动化需将各脚本设置为 ZennoDroid 子项目。

1. 复制 `Reddit_IntegrationTest.cs` 到 Own Code → 运行

**预期日志输出**:
```
[Integration] ========================================
[Integration]   Reddit Automation Integration Test
[Integration] ========================================
[Integration] ✓ Test 1: Browse Feed PASSED
[Integration] ✓ Test 2: Like Post PASSED
[Integration] ✓ Test 3: Read Post PASSED
[Integration] ✓ Test 4: Read Comments PASSED
[Integration] Total Tests: 4 | Passed: 4 | Success Rate: 100%
```

---

## Part 2: 验证证据清单

### 通用验证标准（所有脚本）

- **语法正确**：符合 ZennoDroid Own Code 约束
- **日志完整**：所有关键步骤有日志输出
- **错误处理**：异常被捕获并记录
- **变量输出**：结果保存到项目变量
- **返回值**：返回 "SUCCESS: ..." 或 "ERROR: ..."

---

### Reddit_Browse.cs 验证清单

**功能**: 滚动 Reddit feed，检测并跟踪已浏览的帖子。

#### 必需日志消息
```
✅ [Browse] Starting Reddit Browse...
✅ [Browse] Parameters: scrollCount=X, scrollDelay=Y
✅ [Browse] Opening Reddit app...
✅ [Browse] Screen size: WxH
✅ [Browse] === Scroll N/M ===
✅ [Browse] Found X posts on screen
✅ [Browse] New post #N: Y=start-end
✅ [Browse] === Browse Complete ===
✅ [Browse] Total unique posts viewed: X
```

#### 变量输出
| 变量名 | 预期值 | 验证方法 |
|--------|--------|----------|
| `browse_result` | "SUCCESS" | 截图或文本复制 |
| `browse_posts_count` | 数字 > 0 (如 "8") | 截图或文本复制 |

#### 行为验证
- [ ] Reddit 应用自动打开
- [ ] 屏幕自动向下滚动 N 次
- [ ] 滚动过程中无卡顿或崩溃
- [ ] 脚本执行完成后停留在 feed 页面

---

### Reddit_Like.cs 验证清单

**功能**: 动态定位点赞按钮，点击，验证 UI 状态变化。

#### 必需日志消息
```
✅ [Like] Starting Reddit Like...
✅ [Like] Parameters: postIndex=X
✅ [Like] Getting UI hierarchy...
✅ [Like] Found X posts on screen
✅ [Like] Upvote button at: (X, Y)
✅ [Like] Clicking upvote button...
✅ [Like] UI changed: true/false
✅ [Like] ✓ Like action successful
```

#### 变量输出
| 变量名 | 预期值 | 验证方法 |
|--------|--------|----------|
| `like_result` | "SUCCESS" | 截图或文本复制 |
| `like_ui_changed` | "true" 或 "false" | 截图或文本复制 |

#### 行为验证
- [ ] 点赞按钮被点击
- [ ] 按钮颜色变化（橙色 ↔ 灰色）
- [ ] 无误点其他按钮

#### 视觉证据（推荐）
- [ ] 点赞前截图：灰色按钮
- [ ] 点赞后截图：橙色按钮

---

### Reddit_ReadPost.cs 验证清单

**功能**: 点击帖子，滚动阅读内容，提取文本，返回 feed。

#### 必需日志消息
```
✅ [ReadPost] Starting Reddit ReadPost...
✅ [ReadPost] Getting UI hierarchy...
✅ [ReadPost] Clicking post at: (X, Y)
✅ [ReadPost] ✓ Entered post detail page
✅ [ReadPost] Total text extracted: X chars
✅ [ReadPost] Returning to feed...
✅ [ReadPost] ✓ Returned to feed successfully
```

#### 变量输出
| 变量名 | 预期值 | 验证方法 |
|--------|--------|----------|
| `readpost_result` | "SUCCESS" | 截图或文本复制 |
| `readpost_text_length` | 数字 > 0 | 截图或文本复制 |
| `readpost_text` | 实际文本内容 | 复制前 100 字符验证 |

#### 行为验证
- [ ] 帖子被点击，进入详情页
- [ ] 详情页自动滚动
- [ ] 按返回键返回 feed

---

### Reddit_Comment.cs 验证清单

**功能**: 打开评论区，读取评论内容，可选回复功能。

#### 必需日志消息
```
✅ [Comment] Starting Reddit Comment...
✅ [Comment] Clicking comment button at: (X, Y)
✅ [Comment] ✓ Entered comment section
✅ [Comment] Total unique comments collected: X
✅ [Comment] Returning to feed...
✅ [Comment] ✓ Returned to feed successfully
```

#### 变量输出
| 变量名 | 预期值 | 验证方法 |
|--------|--------|----------|
| `comment_result` | "SUCCESS" | 截图或文本复制 |
| `comment_count` | 数字 > 0 | 截图或文本复制 |
| `comment_text` | 评论内容 | 验证无乱码 |
| `comment_reply_entered` | "true"/"false" | 仅启用回复时 |

#### 行为验证
- [ ] 评论按钮被点击
- [ ] 进入评论页面
- [ ] 评论页面自动滚动
- [ ] 返回 feed

#### 回复功能验证（如启用）
- [ ] 日志显示 "Clicking reply input at: (X, Y)"
- [ ] 日志显示 "Typing reply text: ..."
- [ ] 变量 `comment_reply_entered` = "true"
- [ ] 屏幕输入框显示回复文本

---

### Reddit_IntegrationTest.cs 验证清单

**功能**: 集成测试，按顺序执行 Browse → Like → Read → Comment。

#### 必需日志消息
```
✅ [Integration] Reddit Automation Integration Test
✅ [Integration] Phase 0: Preparation
✅ [Integration] ✓ Test 1: Browse Feed PASSED
✅ [Integration] ✓ Test 2: Like Post PASSED
✅ [Integration] ✓ Test 3: Read Post PASSED
✅ [Integration] ✓ Test 4: Read Comments PASSED
✅ [Integration] Total Tests: 4 | Passed: 4 | Success Rate: 100%
```

#### 变量输出
| 变量名 | 预期值 |
|--------|--------|
| `integration_result` | "SUCCESS" |
| `integration_total_tests` | "4" |
| `integration_passed_tests` | "4" |
| `integration_success_rate` | "100" |

> 注意：当前版本使用模拟结果。真实自动化需将各脚本设置为 ZennoDroid 子项目。

---

## Part 3: 证据提交与验证标准

### 证据包结构

```
test_results/
├── Reddit_Browse_log.txt
├── Reddit_Like_log.txt
├── Reddit_ReadPost_log.txt
├── Reddit_Comment_log.txt
├── Reddit_IntegrationTest_log.txt
├── screenshots/
│   ├── browse_variables.png
│   ├── like_variables.png
│   ├── like_before.png         # 点赞前
│   ├── like_after.png          # 点赞后
│   └── ...
└── test_report.md
```

### 验证通过标准

**单个脚本**: 所有必需日志出现 + 输出变量合理 + 返回 "SUCCESS" + 行为验证通过 + 无未捕获异常

**集成测试**: 4 个测试全部通过 + 成功率 100% + 日志显示完整流程

**项目整体**: 5 个脚本全部通过 + 证据文件完整 + 测试报告清晰

---

## Part 4: 常见问题排查

| 问题 | 原因 | 解决方案 |
|------|------|----------|
| 找不到帖子 ("No posts found") | Reddit feed 未加载或 UI 结构变化 | 确保 APP 已完全加载；检查 `post_unit` resource-id |
| 点赞按钮点击无效 ("UI unchanged") | 动态坐标提取失败或已点赞 | 检查日志坐标；尝试未点赞的帖子 |
| 无法进入帖子详情页 | 点击位置不正确或加载延迟 | 增加延迟；检查帖子中心点坐标 |
| 评论按钮找不到 | resource-id 变化 | 用 `GetLayout()` 导出 XML，查找实际 resource-id |

### 失败时处理流程

1. **收集失败证据**: 完整错误日志 + 失败时截图 + 失败时变量值
2. **分析原因**: 语法错误? UI 变化? 网络问题?
3. **修复并重测**: 应用修复 → 重新运行 → 收集新证据 → 对比差异

---

### 测试报告模板

```markdown
# Reddit 自动化测试报告

## 测试信息
- **测试日期**: YYYY-MM-DD
- **ZennoDroid 版本**: [版本号]
- **Android 设备**: [设备型号]
- **Reddit 应用版本**: [版本号]

## 测试结果总览
| 脚本 | 状态 |
|------|------|
| Reddit_Browse.cs | ✅ PASS / ❌ FAIL |
| Reddit_Like.cs | ✅ PASS / ❌ FAIL |
| Reddit_ReadPost.cs | ✅ PASS / ❌ FAIL |
| Reddit_Comment.cs | ✅ PASS / ❌ FAIL |
| Reddit_IntegrationTest.cs | ✅ PASS / ❌ FAIL |

## 发现的问题
[...]

## 改进建议
[...]
```
