# Reddit 自动化测试执行指南

## 📋 概述

本指南说明如何在 ZennoDroid 中运行 Reddit 自动化脚本并收集验证证据。

---

## 🎯 测试脚本清单

| 脚本 | 功能 | 文件路径 |
|------|------|----------|
| **Reddit_Browse.cs** | 浏览 feed，跟踪帖子 | `ZDProjects/Reddit_Browse.cs` |
| **Reddit_Like.cs** | 点赞帖子 | `ZDProjects/Reddit_Like.cs` |
| **Reddit_ReadPost.cs** | 阅读帖子内容 | `ZDProjects/Reddit_ReadPost.cs` |
| **Reddit_Comment.cs** | 读取评论，可选回复 | `ZDProjects/Reddit_Comment.cs` |
| **Reddit_IntegrationTest.cs** | 集成测试（全流程） | `ZDProjects/Reddit_IntegrationTest.cs` |

---

## 🔧 前置准备

### 1. ZennoDroid 环境
- ✅ ZennoDroid 已安装并连接到 Android 设备
- ✅ Android 设备已安装 Reddit 应用（`com.reddit.frontpage`）
- ✅ Reddit 应用已登录账号

### 2. 创建项目变量

在 ZennoDroid 项目中创建以下变量（所有脚本共用）：

#### Browse 脚本变量
- `browse_scroll_count` (默认: "3") - 滚动次数
- `browse_scroll_delay` (默认: "2000") - 滚动延迟（毫秒）
- `browse_posts_count` (输出) - 找到的帖子数量
- `browse_result` (输出) - 执行结果

#### Like 脚本变量
- `like_post_index` (默认: "0") - 目标帖子索引
- `like_verify_delay` (默认: "1500") - 验证延迟（毫秒）
- `like_result` (输出) - 执行结果
- `like_ui_changed` (输出) - UI 是否变化

#### ReadPost 脚本变量
- `readpost_post_index` (默认: "0") - 目标帖子索引
- `readpost_scroll_count` (默认: "2") - 滚动次数
- `readpost_scroll_delay` (默认: "1500") - 滚动延迟（毫秒）
- `readpost_result` (输出) - 执行结果
- `readpost_text` (输出) - 提取的文本
- `readpost_text_length` (输出) - 文本长度

#### Comment 脚本变量
- `comment_post_index` (默认: "0") - 目标帖子索引
- `comment_enable_reply` (默认: "false") - 是否启用回复
- `comment_reply_text` (默认: "") - 回复文本
- `comment_scroll_count` (默认: "2") - 滚动次数
- `comment_result` (输出) - 执行结果
- `comment_count` (输出) - 评论数量
- `comment_text` (输出) - 评论文本

#### Integration 脚本变量
- `integration_total_tests` (输出) - 总测试数
- `integration_passed_tests` (输出) - 通过测试数
- `integration_success_rate` (输出) - 成功率
- `integration_result` (输出) - 总体结果

---

## 🚀 执行步骤

### 方法 1：单独测试每个脚本

#### 测试 1: Reddit_Browse.cs

1. **创建 ZennoDroid 项目**
2. **添加 "Own Code" 动作块**
3. **复制 `Reddit_Browse.cs` 的全部内容到代码编辑器**
4. **设置输入变量**：
   - `browse_scroll_count` = "3"
   - `browse_scroll_delay` = "2000"
5. **运行项目**
6. **收集证据**：
   - 查看日志输出（应显示 "[Browse] ..." 消息）
   - 检查变量 `browse_result`（应为 "SUCCESS"）
   - 检查变量 `browse_posts_count`（应为数字，如 "8"）

**预期日志输出示例**：
```
[Browse] Starting Reddit Browse...
[Browse] Parameters: scrollCount=3, scrollDelay=2000
[Browse] Opening Reddit app...
[Browse] Screen size: 1080x2400
[Browse] === Scroll 0/3 ===
[Browse] Found 3 posts on screen
[Browse] New post #1: Y=200-800
[Browse] New post #2: Y=850-1450
[Browse] New post #3: Y=1500-2100
[Browse] New posts this scroll: 3
[Browse] Scrolling down...
...
[Browse] === Browse Complete ===
[Browse] Total unique posts viewed: 8
```

---

#### 测试 2: Reddit_Like.cs

1. **创建新的 ZennoDroid 项目**
2. **添加 "Own Code" 动作块**
3. **复制 `Reddit_Like.cs` 的全部内容**
4. **设置输入变量**：
   - `like_post_index` = "0"
   - `like_verify_delay` = "1500"
5. **确保 Reddit 应用已打开并显示 feed**
6. **运行项目**
7. **收集证据**：
   - 查看日志输出
   - 检查变量 `like_result`（应为 "SUCCESS"）
   - 检查变量 `like_ui_changed`（应为 "true" 或 "false"）
   - **手动验证**：观察设备屏幕，第一个帖子的点赞按钮应变色

**预期日志输出示例**：
```
[Like] Starting Reddit Like...
[Like] Parameters: postIndex=0
[Like] Getting UI hierarchy...
[Like] Found 3 posts on screen
[Like] Target post: index 0
[Like] Upvote button at: (174, 1720)
[Like] Clicking upvote button...
[Like] Verifying UI change...
[Like] UI changed: true
[Like] ✓ Like action successful
```

---

#### 测试 3: Reddit_ReadPost.cs

1. **创建新的 ZennoDroid 项目**
2. **添加 "Own Code" 动作块**
3. **复制 `Reddit_ReadPost.cs` 的全部内容**
4. **设置输入变量**：
   - `readpost_post_index` = "0"
   - `readpost_scroll_count` = "2"
   - `readpost_scroll_delay` = "1500"
5. **确保 Reddit 应用已打开并显示 feed**
6. **运行项目**
7. **收集证据**：
   - 查看日志输出
   - 检查变量 `readpost_result`（应为 "SUCCESS"）
   - 检查变量 `readpost_text_length`（应为数字，如 "1234"）
   - 检查变量 `readpost_text`（应包含提取的文本内容）

**预期日志输出示例**：
```
[ReadPost] Starting Reddit ReadPost...
[ReadPost] Parameters: postIndex=0, scrollCount=2
[ReadPost] Getting UI hierarchy...
[ReadPost] Found 3 posts on screen
[ReadPost] Clicking post at: (540, 600)
[ReadPost] ✓ Entered post detail page
[ReadPost] Initial text length: 456 chars
[ReadPost] Scrolling 1/2...
[ReadPost] Scrolling 2/2...
[ReadPost] Total text extracted: 1234 chars
[ReadPost] Returning to feed...
[ReadPost] ✓ Returned to feed successfully
```

---

#### 测试 4: Reddit_Comment.cs

1. **创建新的 ZennoDroid 项目**
2. **添加 "Own Code" 动作块**
3. **复制 `Reddit_Comment.cs` 的全部内容**
4. **设置输入变量**：
   - `comment_post_index` = "0"
   - `comment_enable_reply` = "false"
   - `comment_scroll_count` = "2"
5. **确保 Reddit 应用已打开并显示 feed**
6. **运行项目**
7. **收集证据**：
   - 查看日志输出
   - 检查变量 `comment_result`（应为 "SUCCESS"）
   - 检查变量 `comment_count`（应为数字，如 "15"）
   - 检查变量 `comment_text`（应包含评论内容）

**预期日志输出示例**：
```
[Comment] Starting Reddit Comment...
[Comment] Parameters: postIndex=0, enableReply=false
[Comment] Getting UI hierarchy...
[Comment] Found 3 posts on screen
[Comment] Target post: index 0
[Comment] Clicking comment button at: (395, 1720)
[Comment] ✓ Entered comment section
[Comment] Initial comments found: 8
[Comment] Scrolling 1/2...
[Comment] New comments this scroll: 4
[Comment] Scrolling 2/2...
[Comment] New comments this scroll: 3
[Comment] Total unique comments collected: 15
[Comment] Returning to feed...
[Comment] ✓ Returned to feed successfully
```

---

### 方法 2：运行集成测试

#### 测试 5: Reddit_IntegrationTest.cs

**注意**：当前集成测试使用模拟结果。要使用真实自动化，需要将各个脚本设置为 ZennoDroid 子项目。

1. **创建新的 ZennoDroid 项目**
2. **添加 "Own Code" 动作块**
3. **复制 `Reddit_IntegrationTest.cs` 的全部内容**
4. **运行项目**
5. **收集证据**：
   - 查看日志输出（应显示所有4个测试的执行情况）
   - 检查变量 `integration_result`（应为 "SUCCESS" 或 "PARTIAL"）
   - 检查变量 `integration_passed_tests`（应为通过的测试数）

**预期日志输出示例**：
```
[Integration] ========================================
[Integration]   Reddit Automation Integration Test
[Integration]   Time: 2026-02-06 15:30:00
[Integration] ========================================
[Integration] Phase 0: Preparation
[Integration] Opening Reddit app...
[Integration] ✓ Reddit opened
[Integration] ========================================
[Integration] Starting: Test 1: Browse Feed
[Integration] ========================================
[Integration]   Scrolling feed and tracking posts...
[Integration] ✓ Test 1: Browse Feed PASSED
...
[Integration] ========================================
[Integration]   Integration Test Summary
[Integration] ========================================
[Integration] Total Tests: 4
[Integration] Passed: 4
[Integration] Failed: 0
[Integration] Success Rate: 100%
```

---

## 📊 证据收集清单

### 每个脚本需要收集的证据：

#### ✅ Reddit_Browse.cs
- [ ] 日志显示 "Starting Reddit Browse..."
- [ ] 日志显示找到的帖子数量（"Found X posts on screen"）
- [ ] 日志显示滚动操作（"Scrolling down..."）
- [ ] 日志显示 "Browse Complete"
- [ ] 变量 `browse_result` = "SUCCESS"
- [ ] 变量 `browse_posts_count` > 0

#### ✅ Reddit_Like.cs
- [ ] 日志显示 "Starting Reddit Like..."
- [ ] 日志显示动态坐标（"Upvote button at: (X, Y)"）
- [ ] 日志显示点击操作（"Clicking upvote button..."）
- [ ] 日志显示 UI 验证（"UI changed: true/false"）
- [ ] 变量 `like_result` = "SUCCESS"
- [ ] 手动验证：设备屏幕上点赞按钮变色

#### ✅ Reddit_ReadPost.cs
- [ ] 日志显示 "Starting Reddit ReadPost..."
- [ ] 日志显示进入详情页（"✓ Entered post detail page"）
- [ ] 日志显示文本提取（"Total text extracted: X chars"）
- [ ] 日志显示返回 feed（"✓ Returned to feed successfully"）
- [ ] 变量 `readpost_result` = "SUCCESS"
- [ ] 变量 `readpost_text_length` > 0
- [ ] 变量 `readpost_text` 包含实际内容

#### ✅ Reddit_Comment.cs
- [ ] 日志显示 "Starting Reddit Comment..."
- [ ] 日志显示进入评论区（"✓ Entered comment section"）
- [ ] 日志显示评论收集（"Total unique comments collected: X"）
- [ ] 日志显示返回 feed（"✓ Returned to feed successfully"）
- [ ] 变量 `comment_result` = "SUCCESS"
- [ ] 变量 `comment_count` > 0
- [ ] 变量 `comment_text` 包含实际评论

#### ✅ Reddit_IntegrationTest.cs
- [ ] 日志显示所有4个测试的执行
- [ ] 日志显示测试总结（"Integration Test Summary"）
- [ ] 变量 `integration_result` = "SUCCESS"
- [ ] 变量 `integration_passed_tests` = "4"
- [ ] 变量 `integration_success_rate` = "100"

---

## 🐛 常见问题排查

### 问题 1：找不到帖子（"No posts found"）
**原因**：Reddit feed 未加载或 UI 结构变化
**解决**：
- 确保 Reddit 应用已完全加载
- 手动滚动一次确认 feed 可见
- 检查 `post_unit` resource-id 是否仍然有效

### 问题 2：点赞按钮点击无效（"UI unchanged"）
**原因**：动态坐标提取失败或按钮已点赞
**解决**：
- 检查日志中的坐标是否合理
- 手动验证按钮位置
- 尝试点击未点赞的帖子

### 问题 3：无法进入帖子详情页
**原因**：点击位置不正确或加载延迟
**解决**：
- 增加延迟时间
- 检查帖子中心点坐标
- 确保帖子完全可见

### 问题 4：评论按钮找不到
**原因**：resource-id 可能不是 "post_comment_button"
**解决**：
- 使用 `hierarchy.GetLayout()` 导出 XML
- 查找实际的评论按钮 resource-id
- 修改脚本中的 resource-id

---

## 📝 提交证据格式

完成测试后，请提交以下内容：

### 1. 日志文件
从 ZennoDroid 日志窗口复制完整日志，保存为：
- `Reddit_Browse_log.txt`
- `Reddit_Like_log.txt`
- `Reddit_ReadPost_log.txt`
- `Reddit_Comment_log.txt`
- `Reddit_IntegrationTest_log.txt`

### 2. 变量截图
截图显示所有输出变量的值

### 3. 设备截图（可选）
- 点赞前后的对比截图
- 帖子详情页截图
- 评论页截图

---

## ✅ 验证标准

### 单个脚本验证标准
- ✅ 日志无错误信息
- ✅ 返回值为 "SUCCESS: ..."
- ✅ 所有输出变量有合理值
- ✅ 手动观察设备行为符合预期

### 集成测试验证标准
- ✅ 所有4个测试通过
- ✅ 成功率 = 100%
- ✅ 无异常或错误

---

## 🎯 下一步

完成所有测试并收集证据后：
1. 将日志文件整理到 `test_results/` 目录
2. 创建测试报告总结
3. 标记任何发现的问题或改进建议
4. 如果所有测试通过，项目即可投入生产使用

---

**测试愉快！** 🚀
