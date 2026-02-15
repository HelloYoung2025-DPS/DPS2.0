# Reddit 自动化验证证据清单

## 📋 文档说明

本文档列出每个 Reddit 自动化脚本需要收集的验证证据，用于确认功能正常工作。

---

## ✅ 验证标准总览

### 通用验证标准（所有脚本）
- ✅ **语法正确**：符合 ZennoDroid Own Code 约束
- ✅ **日志完整**：所有关键步骤有日志输出
- ✅ **错误处理**：异常被捕获并记录
- ✅ **变量输出**：结果保存到项目变量
- ✅ **返回值**：返回 "SUCCESS: ..." 或 "ERROR: ..."

---

## 1️⃣ Reddit_Browse.cs 验证清单

### 功能描述
滚动 Reddit feed，检测并跟踪已浏览的帖子。

### 必需证据

#### A. 日志输出证据
```
必须包含以下日志消息：
✅ [Browse] Starting Reddit Browse...
✅ [Browse] Parameters: scrollCount=X, scrollDelay=Y
✅ [Browse] Opening Reddit app...
✅ [Browse] Screen size: WxH
✅ [Browse] === Scroll N/M ===
✅ [Browse] Found X posts on screen
✅ [Browse] New post #N: Y=start-end
✅ [Browse] New posts this scroll: X
✅ [Browse] Scrolling down...
✅ [Browse] === Browse Complete ===
✅ [Browse] Total unique posts viewed: X
```

#### B. 变量输出证据
| 变量名 | 预期值 | 验证方法 |
|--------|--------|----------|
| `browse_result` | "SUCCESS" | 截图或文本复制 |
| `browse_posts_count` | 数字 > 0 (如 "8") | 截图或文本复制 |

#### C. 行为验证
- [ ] Reddit 应用自动打开
- [ ] 屏幕自动向下滚动 N 次
- [ ] 滚动过程中无卡顿或崩溃
- [ ] 脚本执行完成后停留在 feed 页面

#### D. 错误场景测试（可选）
- [ ] Reddit 未安装 → 日志显示错误
- [ ] 网络断开 → 脚本能否优雅处理

---

## 2️⃣ Reddit_Like.cs 验证清单

### 功能描述
动态定位点赞按钮，点击，验证 UI 状态变化。

### 必需证据

#### A. 日志输出证据
```
必须包含以下日志消息：
✅ [Like] Starting Reddit Like...
✅ [Like] Parameters: postIndex=X
✅ [Like] Getting UI hierarchy...
✅ [Like] Found X posts on screen
✅ [Like] Target post: index X
✅ [Like] Post footer bounds: [x1,y1][x2,y2]
✅ [Like] Upvote button at: (X, Y)
✅ [Like] Clicking upvote button...
✅ [Like] Verifying UI change...
✅ [Like] UI changed: true/false
✅ [Like] ✓ Like action successful
```

#### B. 变量输出证据
| 变量名 | 预期值 | 验证方法 |
|--------|--------|----------|
| `like_result` | "SUCCESS" | 截图或文本复制 |
| `like_ui_changed` | "true" 或 "false" | 截图或文本复制 |

#### C. 行为验证
- [ ] 点赞按钮被点击（观察屏幕）
- [ ] 点赞按钮颜色变化（橙色 ↔ 灰色）
- [ ] 点赞数字增加或减少
- [ ] 无误点其他按钮（评论、分享）

#### D. 视觉证据（强烈推荐）
- [ ] **点赞前截图**：显示灰色点赞按钮
- [ ] **点赞后截图**：显示橙色点赞按钮
- [ ] 两张截图对比验证 UI 变化

---

## 3️⃣ Reddit_ReadPost.cs 验证清单

### 功能描述
点击帖子，滚动阅读内容，提取文本，返回 feed。

### 必需证据

#### A. 日志输出证据
```
必须包含以下日志消息：
✅ [ReadPost] Starting Reddit ReadPost...
✅ [ReadPost] Parameters: postIndex=X, scrollCount=Y
✅ [ReadPost] Getting UI hierarchy...
✅ [ReadPost] Found X posts on screen
✅ [ReadPost] Target post: index X
✅ [ReadPost] Clicking post at: (X, Y)
✅ [ReadPost] ✓ Entered post detail page
✅ [ReadPost] Initial text length: X chars
✅ [ReadPost] Scrolling N/M...
✅ [ReadPost] Total text extracted: X chars
✅ [ReadPost] Returning to feed...
✅ [ReadPost] ✓ Returned to feed successfully
```

#### B. 变量输出证据
| 变量名 | 预期值 | 验证方法 |
|--------|--------|----------|
| `readpost_result` | "SUCCESS" | 截图或文本复制 |
| `readpost_text_length` | 数字 > 0 (如 "1234") | 截图或文本复制 |
| `readpost_text` | 实际文本内容 | 复制前 100 字符验证 |

#### C. 行为验证
- [ ] 帖子被点击，进入详情页
- [ ] 详情页自动向下滚动 N 次
- [ ] 滚动过程中提取文本
- [ ] 按返回键返回 feed
- [ ] 返回后仍在 feed 页面（可见帖子列表）

#### D. 文本提取质量验证
- [ ] `readpost_text` 包含帖子标题
- [ ] `readpost_text` 包含帖子正文
- [ ] 文本无乱码或异常字符
- [ ] 文本长度合理（不为空，不过短）

---

## 4️⃣ Reddit_Comment.cs 验证清单

### 功能描述
打开评论区，读取评论内容，可选回复功能。

### 必需证据

#### A. 日志输出证据
```
必须包含以下日志消息：
✅ [Comment] Starting Reddit Comment...
✅ [Comment] Parameters: postIndex=X, enableReply=true/false
✅ [Comment] Getting UI hierarchy...
✅ [Comment] Found X posts on screen
✅ [Comment] Target post: index X
✅ [Comment] Clicking comment button at: (X, Y)
✅ [Comment] ✓ Entered comment section
✅ [Comment] Initial comments found: X
✅ [Comment] Scrolling N/M...
✅ [Comment] New comments this scroll: X
✅ [Comment] Total unique comments collected: X
✅ [Comment] Returning to feed...
✅ [Comment] ✓ Returned to feed successfully
```

#### B. 变量输出证据
| 变量名 | 预期值 | 验证方法 |
|--------|--------|----------|
| `comment_result` | "SUCCESS" | 截图或文本复制 |
| `comment_count` | 数字 > 0 (如 "15") | 截图或文本复制 |
| `comment_text` | 实际评论内容 | 复制前 200 字符验证 |
| `comment_reply_entered` | "true"/"false" (如启用回复) | 截图或文本复制 |

#### C. 行为验证
- [ ] 评论按钮被点击
- [ ] 进入评论页面
- [ ] 评论页面自动向下滚动 N 次
- [ ] 滚动过程中收集评论
- [ ] 按返回键返回 feed

#### D. 评论提取质量验证
- [ ] `comment_text` 包含多条评论
- [ ] 评论之间用 "---" 分隔
- [ ] 无重复评论
- [ ] 评论内容可读（无乱码）

#### E. 回复功能验证（如启用）
- [ ] 日志显示 "Reply feature enabled"
- [ ] 日志显示 "Clicking reply input at: (X, Y)"
- [ ] 日志显示 "Typing reply text: ..."
- [ ] 日志显示 "✓ Reply text entered"
- [ ] 变量 `comment_reply_entered` = "true"
- [ ] **手动验证**：屏幕上输入框显示回复文本

---

## 5️⃣ Reddit_IntegrationTest.cs 验证清单

### 功能描述
集成测试，按顺序执行 Browse → Like → Read → Comment。

### 必需证据

#### A. 日志输出证据
```
必须包含以下日志消息：
✅ [Integration] ========================================
✅ [Integration]   Reddit Automation Integration Test
✅ [Integration]   Time: YYYY-MM-DD HH:MM:SS
✅ [Integration] ========================================
✅ [Integration] Phase 0: Preparation
✅ [Integration] Opening Reddit app...
✅ [Integration] ✓ Reddit opened
✅ [Integration] ========================================
✅ [Integration] Starting: Test 1: Browse Feed
✅ [Integration] ========================================
✅ [Integration] ✓ Test 1: Browse Feed PASSED
✅ [Integration] ========================================
✅ [Integration] Starting: Test 2: Like Post
✅ [Integration] ========================================
✅ [Integration] ✓ Test 2: Like Post PASSED
✅ [Integration] ========================================
✅ [Integration] Starting: Test 3: Read Post
✅ [Integration] ========================================
✅ [Integration] ✓ Test 3: Read Post PASSED
✅ [Integration] ========================================
✅ [Integration] Starting: Test 4: Read Comments
✅ [Integration] ========================================
✅ [Integration] ✓ Test 4: Read Comments PASSED
✅ [Integration] ========================================
✅ [Integration]   Integration Test Summary
✅ [Integration] ========================================
✅ [Integration] Total Tests: 4
✅ [Integration] Passed: 4
✅ [Integration] Failed: 0
✅ [Integration] Success Rate: 100%
```

#### B. 变量输出证据
| 变量名 | 预期值 | 验证方法 |
|--------|--------|----------|
| `integration_result` | "SUCCESS" | 截图或文本复制 |
| `integration_total_tests` | "4" | 截图或文本复制 |
| `integration_passed_tests` | "4" | 截图或文本复制 |
| `integration_success_rate` | "100" | 截图或文本复制 |

#### C. 行为验证
- [ ] 所有4个测试按顺序执行
- [ ] 每个测试之间有适当延迟
- [ ] 无测试失败或异常
- [ ] 最终返回成功状态

#### D. 注意事项
⚠️ **当前版本使用模拟结果**。要使用真实自动化：
1. 将各个脚本设置为 ZennoDroid 子项目
2. 修改集成测试调用子项目
3. 通过变量传递参数和结果

---

## 📊 证据提交格式

### 完整证据包应包含：

#### 1. 日志文件（必需）
```
test_results/
├── Reddit_Browse_log.txt
├── Reddit_Like_log.txt
├── Reddit_ReadPost_log.txt
├── Reddit_Comment_log.txt
└── Reddit_IntegrationTest_log.txt
```

#### 2. 变量截图（必需）
```
test_results/screenshots/
├── browse_variables.png
├── like_variables.png
├── readpost_variables.png
├── comment_variables.png
└── integration_variables.png
```

#### 3. 行为截图（推荐）
```
test_results/screenshots/behavior/
├── like_before.png          # 点赞前
├── like_after.png           # 点赞后
├── post_detail.png          # 帖子详情页
├── comment_section.png      # 评论页
└── feed_final.png           # 最终 feed 状态
```

#### 4. 测试报告（推荐）
```
test_results/
└── test_report.md           # 总结所有测试结果
```

---

## ✅ 验证通过标准

### 单个脚本通过标准
- ✅ 所有必需日志消息出现
- ✅ 所有输出变量有合理值
- ✅ 返回值为 "SUCCESS: ..."
- ✅ 行为验证全部通过
- ✅ 无未捕获异常

### 集成测试通过标准
- ✅ 所有4个测试通过
- ✅ 成功率 = 100%
- ✅ 日志显示完整执行流程
- ✅ 无错误或警告

### 项目整体通过标准
- ✅ 5个脚本全部通过验证
- ✅ 证据文件完整提交
- ✅ 测试报告清晰总结
- ✅ 所有已知问题已记录

---

## 🐛 失败场景处理

### 如果测试失败：

#### 1. 收集失败证据
- [ ] 完整错误日志
- [ ] 失败时的屏幕截图
- [ ] 失败时的变量值
- [ ] 失败前的操作序列

#### 2. 分析失败原因
- [ ] 语法错误？→ 检查 ZennoDroid Own Code 约束
- [ ] 逻辑错误？→ 检查条件判断和循环
- [ ] UI 变化？→ 检查 resource-id 是否仍有效
- [ ] 网络问题？→ 检查网络连接和加载时间

#### 3. 修复并重新测试
- [ ] 应用修复
- [ ] 重新运行测试
- [ ] 收集新的证据
- [ ] 对比修复前后差异

---

## 📝 测试报告模板

```markdown
# Reddit 自动化测试报告

## 测试信息
- **测试日期**：YYYY-MM-DD
- **测试人员**：[姓名]
- **ZennoDroid 版本**：[版本号]
- **Android 设备**：[设备型号]
- **Reddit 应用版本**：[版本号]

## 测试结果总览
| 脚本 | 状态 | 通过/失败 |
|------|------|-----------|
| Reddit_Browse.cs | ✅ PASS | 通过 |
| Reddit_Like.cs | ✅ PASS | 通过 |
| Reddit_ReadPost.cs | ✅ PASS | 通过 |
| Reddit_Comment.cs | ✅ PASS | 通过 |
| Reddit_IntegrationTest.cs | ✅ PASS | 通过 |

## 详细测试结果
[每个脚本的详细测试结果...]

## 发现的问题
[列出所有发现的问题...]

## 改进建议
[列出改进建议...]

## 结论
[总体评价和建议...]
```

---

## 🎯 下一步行动

完成所有验证后：
1. ✅ 整理所有证据文件
2. ✅ 编写测试报告
3. ✅ 提交证据包
4. ✅ 等待审核反馈
5. ✅ 如通过，项目可投入生产使用

---

**祝测试顺利！** 🚀
