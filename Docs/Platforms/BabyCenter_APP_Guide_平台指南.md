# BabyCenter APP 平台指南

> **平台**: BabyCenter  
> **包名**: `com.babycenter.pregnancytracker`  
> **APP 版本**: 6.02.0  
> **验证日期**: 2026-03-04  
> **文档版本**: v2.0 (WebView Accessibility 更新)

---

## 1. 平台概述

BabyCenter 是一款面向孕期和育儿家庭的社交内容平台，核心功能包括社区讨论、孕周日历、育儿工具等。DPS v4.5 主要针对其**社区（Birth Club）** 模块进行会话模拟，模拟用户浏览帖子、阅读、点赞、评论等行为。

该平台的 UI 架构与 Reddit 有本质区别：社区 feed 采用 **ViewPager 水平滑动**，帖子详情页则是 **WebView 渲染**。这两个特征直接影响了操作配置的设计策略。

---

## 2. UI 架构特征

### 2.1 核心发现

| 特征 | 说明 |
|------|------|
| **社区 feed 布局** | ViewPager 水平滑动（非传统垂直 RecyclerView） |
| **帖子详情页** | WebView 渲染（WebViewActivity），非原生 UI |
| **底部导航** | 标准 BottomNavigationView，5 个 tab |
| **首页 feed** | 传统 RecyclerView 垂直滚动 |

### 2.2 两种 feed 的区别

```
首页 (Home)                    社区 (Birth Club)
┌──────────────┐               ┌──────────────┐
│  RecyclerView │               │   ViewPager   │
│  ↕ 垂直滚动   │               │  ↔ 水平滑动   │
│              │               │              │
│  recycler    │               │  posts       │
│  (resource-id)│               │  (resource-id)│
└──────────────┘               └──────────────┘
```

社区页面的 `posts` 是一个 ViewPager 容器，用户通过左右滑动切换帖子卡片，这与大多数 APP 的垂直 feed 完全不同。browse 操作需要配置为**水平滑动**（`direction: left`），而非垂直滚动。

### 2.3 WebView 帖子详情

点击帖子后，APP 跳转到 `WebViewActivity`，整个帖子内容（正文、评论、点赞按钮）都在 `webViewLayout` 元素内渲染。

#### ✅ 关键发现：WebView 内部元素对 uiautomator 可见

经过深度测试（`bc_webview_deep.ps1`），发现 BabyCenter 的 WebView **暴露了内部 DOM 元素作为 Android accessibility nodes**。这意味着：

- **点赞按钮**可通过 `text="select for list of reactions"` 定位（`android.widget.Button`, `clickable=true`）
- **评论按钮**可通过 `text="ADD A COMMENT"`（浮动）或 `text="Comment"`（内嵌）定位
- **评论输入框**可通过 `text="Add a comment"` 定位（`android.view.View`）
- **书签按钮**可通过 `text="bookmark post"` 定位
- **回复按钮**可通过 `text="reply to comment"` 定位
- **Emoji 回复**可通过 `text="reply with emoji"` 定位

#### ⚠️ 注意：bounds 可能为 [0,0][0,0]

WebView 内部元素在 uiautomator dump 中**始终存在**，但只有当元素在 WebView 可视区域内时，bounds 才会显示非零值。对于长帖子，需要多次滚动才能使 reaction 按钮进入可视区域。

```
WebView 视口示意：
┌──────────────────────┐
│ [帖子标题]           │  ← 可见区域
│ [作者信息]           │
│ [帖子正文...]        │
│ [正文继续...]        │  ← bounds 非零
│                      │
├ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ┤
│ [reaction 按钮]      │  ← 初始 bounds=[0,0][0,0]
│ [Comment 按钮]       │    滚动到此处后 bounds 变为非零
│ [评论列表...]        │
└──────────────────────┘
```

---

## 3. 实机验证的 Resource ID 映射表

以下所有 resource-id 均通过 ADB `uiautomator dump` 在实机上验证（2026-03-04）。

### 3.1 首页（Home）

| Resource ID | 类型 | 说明 |
|-------------|------|------|
| `bottom_navigator` | BottomNavigationView | 底部导航栏容器 |
| `menu_home` | MenuItem | 首页 tab |
| `menu_birthclub` | MenuItem | 社区 tab |
| `menu_calendar` | MenuItem | 日历 tab |
| `menu_tools` | MenuItem | 工具 tab |
| `menu_more` | MenuItem | 更多 tab |
| `recycler` | RecyclerView | 首页内容列表 |
| `salutation` | TextView | 问候语（如 "Hi, [name]"） |
| `weekRange` | TextView | 孕周范围显示 |
| `toolbar` | Toolbar | 顶部工具栏 |
| `appBar` | AppBarLayout | 顶部栏容器 |

### 3.2 社区页面（Birth Club / Community）

| Resource ID | 类型 | 说明 |
|-------------|------|------|
| `postContainer` | ViewGroup | 帖子卡片容器 |
| `posts` | ViewPager | 帖子滑动容器（水平滑动） |
| `title` | TextView | 帖子标题 |
| `text` | TextView | 帖子摘要/正文预览 |
| `commentCount` | TextView | 评论数 |
| `reactionsCount` | TextView | 点赞/反应数 |
| `authorAvatar` | ImageView | 作者头像 |
| `subtitle` | TextView | 作者名/发布时间 |
| `groupReference` | TextView | 所属群组引用 |
| `tabs` | TabLayout | 社区页面顶部 tab 栏 |

**社区页面 Tab 项**：

| Tab 标识 | 说明 |
|----------|------|
| `community_home` | 社区首页 |
| `community_my_activity` | 我的动态 |
| `community_my_bookmark` | 我的收藏 |

**其他控件**：

| 控件 | 说明 |
|------|------|
| `button` (text: "See more") | 展开更多帖子内容 |

### 3.3 帖子详情页（WebViewActivity）

#### 原生控件

| Resource ID | 类型 | 说明 |
|-------------|------|------|
| `webViewLayout` | ViewGroup | WebView 外层布局（✅ 正确 ID，非 `web_view`） |
| `toolbar` | Toolbar | 顶部工具栏（原生） |
| `share` | ImageButton | 分享按钮（原生） |
| `refresh` | ImageButton | 刷新按钮（原生） |
| `pull_to_refresh` | SwipeRefreshLayout | 下拉刷新容器 |

#### WebView 内部 Accessibility Nodes（✅ 新发现）

| Text 选择器 | 类型 | Clickable | 说明 |
|-------------|------|-----------|------|
| `select for list of reactions` | android.widget.Button | ✅ | 点赞/反应按钮，滚动后 bounds≈[49,1688][147,1846] |
| `ADD A COMMENT` | android.widget.Button | ✅ | 浮动评论按钮，bounds≈[756,2553][1393,2721] |
| `Comment` | android.widget.Button | ✅ | 内嵌评论按钮，滚动后 bounds≈[49,1916][518,2094] |
| `Add a comment` | android.view.View | ❌ | 评论输入区域 |
| `bookmark post` | android.widget.Button | ✅ | 书签/收藏按钮 |
| `reply to comment` | android.widget.Button | ✅ | 回复评论按钮 |
| `reply with emoji` | android.widget.Button | ✅ | Emoji 回复按钮 |
| `go to original poster's comments page` | android.widget.Button | ✅ | 查看原帖作者评论 |
| `END OF COMMENTS` | android.widget.TextView | ❌ | 评论列表结束标记 |

### 3.4 底部导航栏

| Resource ID | 说明 |
|-------------|------|
| `bottom_navigator` | 导航栏容器 |
| `menu_home` | 首页 |
| `menu_birthclub` | 社区 |
| `menu_calendar` | 日历 |
| `menu_tools` | 工具 |
| `menu_more` | 更多 |

---

## 4. Operations 配置说明

操作配置文件：`Config/Operations/babycenter_operations.json`（v3.0，已验证）

### 4.1 已定义操作

| 操作名 | 说明 | 关键行为 | 可靠性 |
|--------|------|---------|--------|
| `navigate_to_community` | 导航到社区页面 | 点击 `menu_birthclub` | 高 |
| `enter_group` | 进入群组 | 点击 `groupReference` 链接 | 高 |
| `browse` | 浏览社区 feed | **水平滑动**（direction: left） | 高 |
| `open_post` | 打开帖子 | 点击 `postContainer` | 高 |
| `read_post` | 阅读帖子内容 | WebView 内滚动 + 停留延时 | 高 |
| `scroll_to_reactions` | 滚动到反应按钮 | WebView 内滚动直到 bounds 非零 | 中 |
| `like` | 点赞 | ✅ 通过 text 选择器定位 reaction 按钮 | 中 |
| `comment` | 发表评论 | ✅ 通过 text 选择器定位评论按钮和输入框 | 中 |
| `back_to_feed` | 返回 feed | Navigate up + KEYCODE_BACK 兜底 | 高 |
| `scroll_feed` | 滚动页面 | 垂直滚动（See more 下方内容） | 高 |

### 4.2 browse 操作的特殊性

BabyCenter 的社区 feed 使用 ViewPager，所以 browse 操作配置为水平滑动：

```json
{
    "browse": {
        "description": "浏览社区帖子（水平滑动）",
        "require_page": "feed",
        "steps": [
            { "action": "log", "message": "水平滑动浏览下一个帖子" },
            { "action": "scroll", "direction": "left", "distance": 800 },
            { "action": "delay", "min_ms": 2000, "max_ms": 5000 },
            { "action": "refresh_layout" }
        ]
    }
}
```

---

## 5. WebView Accessibility 特性与操作策略

### 5.1 核心发现（2026-03-04 更新）

BabyCenter 的 WebView **意外地暴露了内部 DOM 元素作为 accessibility nodes**。这彻底改变了 like/comment 操作的可行性：

| 特性 | 旧认知（v1.0） | 新发现（v2.0） |
|------|----------------|----------------|
| WebView 内部元素 | 不可见 | ✅ 通过 text 属性可定位 |
| 点赞按钮 | 需 JS 注入 | ✅ `text="select for list of reactions"` |
| 评论按钮 | 需 JS 注入 | ✅ `text="ADD A COMMENT"` / `"Comment"` |
| 评论输入 | 不可操作 | ✅ `text="Add a comment"` |
| 操作可靠性 | 低 (2-5%) | 中 (60-80%) |

### 5.2 操作策略

**Like 操作流程**：
1. 进入帖子详情（WebViewActivity）
2. 向下滚动 WebView（每次 600px，最多 8 次），直到 `text="select for list of reactions"` 的 bounds 变为非零
3. 点击 reaction 按钮
4. 等待表情选择器弹出
5. 尝试选择 "Love" 等表情选项

**Comment 操作流程**：
1. 查找 `text="ADD A COMMENT"` 浮动按钮
2. 如不可见，查找 `text="Comment"` 内嵌按钮
3. 点击后等待输入界面出现
4. 查找 `text="Add a comment"` 输入区域
5. 点击输入区域 → 输入文本 → 查找提交按钮

### 5.3 权重调整（v3.0）

| 动作 | v2.0 权重 | v3.0 权重 | 调整原因 |
|------|-----------|-----------|----------|
| `browse` | 70% | 40% | 降低，为 like/comment 腾出空间 |
| `like` | 5% | 20% | ✅ 上调，现已可操作 |
| `open_post` | 15% | 20% | 保持主要交互 |
| `comment` | 2% | 10% | ✅ 上调，现已可操作 |
| `read_post` | 5% | 7% | 微调 |
| `share` | 3% | 3% | 不变 |

### 5.4 已知限制

| 限制 | 说明 | 应对策略 |
|------|------|----------|
| bounds 延迟 | WebView 内元素需滚动到可视区域后 bounds 才非零 | 使用滚动+检测循环（最多 8 次） |
| 长帖子 | 正文超长时需更多滚动才能到达 reaction 区域 | 增加最大滚动次数 |
| 表情选择器 | 点击 reaction 后的 picker UI 不一定可被 uiautomator 识别 | 兜底：直接点击 reaction 按钮视为成功 |
| 评论提交 | 提交按钮的文本可能因版本而异 | 尝试多个候选文本：Post, Submit, Send |

---

## 6. page_signatures 说明

`PlatformsConfig.json` 中为 BabyCenter 定义了以下页面签名，供 PageDetector 识别当前页面状态：

| 页面名 | 签名标识（indicators） | 说明 |
|--------|----------------------|------|
| `feed` | `posts`, `postContainer` | 社区 feed 页面（ViewPager） |
| `home` | `recycler`, `salutation` | APP 首页 |
| `post_detail` | `web_view`, `webViewLayout` | 帖子详情页（WebView） |
| `comment` | `web_view`, `toolbar` | 评论区域（同在 WebView 内） |

PageDetector 通过检查当前 UI 层级中是否存在这些 resource-id 来判断页面。由于 `post_detail` 和 `comment` 都在 WebView 中，实际上很难区分两者，当前实现将它们视为同一页面状态。

---

## 7. 注意事项与已知问题

### 7.1 评分弹窗

APP 可能在使用过程中弹出评分对话框。该弹窗包含一个 `closeBtn` 按钮，需要在操作流程中检测并关闭：

```json
{
    "action": "find",
    "selector": "closeBtn",
    "save_as": "rating_dialog",
    "on_fail": "skip"
}
```

建议在每次操作前添加弹窗检测步骤，或在 `navigate_to_community` 操作中包含弹窗清理逻辑。

### 7.2 注册流程

新账号注册需要注意以下步骤：

1. **邮箱 + 密码注册**：标准注册流程
2. **地址收集页面**：注册后 APP 会要求填写地址，可通过点击 `completeLater` 按钮跳过
3. **Widget 推广页面**：跳过地址后会显示桌面小组件推广，点击 `ok_button` 关闭

```
注册流程：邮箱/密码 → 地址收集（completeLater 跳过）→ Widget 推广（ok_button 关闭）→ 首页
```

### 7.3 社区页面导航

进入 APP 后默认显示首页，需要点击 `menu_birthclub` 才能进入社区 feed。`navigate_to_community` 操作负责处理这一导航步骤。

### 7.4 ViewPager 滑动方向

社区 feed 的滑动方向是**从右向左**（direction: left）表示查看下一个帖子。从左向右（direction: right）回到上一个帖子。配置 browse 操作时务必注意方向设置。

---

## 8. ADB 验证测试结果

### 8.1 原始验证测试

测试脚本：`babycenter_adb_test.ps1`  
测试日期：2026-03-04  
测试结果：**23/23 PASSED**

覆盖范围：底部导航栏、首页元素、社区页面元素、帖子详情页原生控件、页面导航、ViewPager 滑动响应。

### 8.2 E2E 全流程拟人测试（✅ 新增）

测试脚本：`babycenter_e2e_test.ps1`（由 App Onboarder 生成到输出目录）  
测试日期：2026-03-04  
测试结果：**7/7 PASSED**

| Phase | 测试项 | 结果 | 说明 |
|-------|--------|------|------|
| 1 | 启动应用 | ✅ PASS | monkey 启动 + 弹窗检测 |
| 2 | 导航到 Community | ✅ PASS | menu_birthclub → postContainer 可见 |
| 3 | 水平浏览帖子 | ✅ PASS | 2次 ViewPager 左滑 |
| 4 | 打开帖子详情 | ✅ PASS | postContainer → WebViewActivity (webViewLayout) |
| 5 | 滚动到 Reactions | ✅ PASS | 6 次滚动后 reaction 按钮 bounds 非零 |
| 6 | 点赞 (Like) | ✅ PASS | reaction 按钮可点击 |
| 7 | 评论 (Comment) | ✅ PASS | 评论输入框定位成功，文本输入成功 |

---

## 9. 相关文件索引

| 文件 | 路径 | 说明 |
|------|------|------|
| 平台配置 | `Config/PlatformsConfig.json` | babycenter 段落，含 WebView text 选择器 |
| 操作配置 | `Config/Operations/babycenter_operations.json` | v3.0，含 like/comment/enter_group 操作 |
| 意图映射 | `Config/IntentMappings/babycenter_intents.json` | 意图到操作的映射 |
| E2E 测试脚本 | `{output_dir}/babycenter_e2e_test.ps1` | 7 阶段全流程拟人测试（7/7 通过） |
| WebView 分析脚本 | `bc_webview_deep.ps1`（外部调试脚本） | WebView 内部元素 dump 工具 |
| ADB 测试脚本 | `babycenter_adb_test.ps1`（项目根目录外） | 23 项元素验证测试 |
| SessionRunner 说明 | `Docs/SessionRunner使用说明.md` | 核心模块文档 |
