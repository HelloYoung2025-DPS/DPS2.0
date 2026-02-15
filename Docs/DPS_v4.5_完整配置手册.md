# DPS v4.5 完整配置手册

## 1. 系统总览流程图

本流程展示了 DPS v4.5 从初始化到报告生成的完整生命周期：

```text
[ 开始 ]
    |
    v
+------------------+
|   Initializer    | (环境初始化：检查路径、加载设备映射、连接 ADB)
+------------------+
    |
    v
+------------------+
|      Main        | (主控逻辑：任务分发与状态调度)
+------------------+
    |
    +------------------------------------------+
    |                                          |
    v                                          v
+------------------+                    +------------------+
|  PersonaCreate   | (AI 画像生成)        |   DailyUpdate    | (每日任务更新/演进)
+------------------+                    +------------------+
    |                                          |
    +--------------------+---------------------+
                         |
                         v
                +------------------+
                |    Extension     | (IP 检查、地理位置/天气扩展)
                +------------------+
                         |
                         v
                +------------------+
                |  SessionRunner   | (核心执行：按计划模拟人类行为)
                +------------------+
                         |
                         v
                +------------------+
                |    StateSaver    | (实时保存运行状态与 Session 缓存)
                +------------------+
                         |
                         v
                +------------------+
                |   Maintenance    | (日志清理、临时文件回收)
                +------------------+
                         |
                         v
                +------------------+
                |    ReportGen     | (生成 HTML/Markdown 运行报告)
+------------------+
```

---

## 2. 完整变量清单 (ZennoDroid 格式)

### 2.1 必填变量 (4个)
| 变量名 | 类型 | 初始值 | 说明 |
|:---:|:---:|:---:|:---|
| project_root | String | C:\DPS_v4.5\ | 项目根目录，所有相对路径的基准 |
| device_id | String | Device_001 | ZennoDroid 识别的设备唯一标识 |
| current_app | String | reddit | 当前正在操作的目标 APP 名称 |
| current_platform | String | reddit | 当前所属的社交平台名称 |

### 2.2 模块结果变量 (8个)
| 变量名 | 类型 | 初始值 | 说明 |
|:---:|:---:|:---:|:---|
| initializer_result | String | - | 初始化模块执行结果 (Success/Failed) |
| main_result | String | - | 主程序调度结果 |
| persona_result | String | - | 画像生成结果标识 |
| daily_result | String | - | 每日更新任务分配结果 |
| session_result | String | - | 会话执行器最终状态 |
| report_result | String | - | 报告生成是否成功 |
| evolve_result | String | - | 周演进/成长模块结果 |
| extension_result | String | - | 扩展插件加载状态 |

### 2.3 配置缓存变量 (7个)
| 变量名 | 类型 | 初始值 | 说明 |
|:---:|:---:|:---:|:---|
| ai_config_json | String | {} | 内存中的 AI 服务配置 (API Key/Endpoint) |
| stage_config_json | String | {} | 当前账号阶段参数缓存 |
| behavior_config_json | String | {} | 行为控制逻辑缓存 |
| persona_json | String | {} | 当前画像详细数据 |
| session_plan_json | String | {} | 待执行的会话计划表 |
| platforms_config_json | String | {} | 各平台通用配置项 |
| device_app_mapping_json | String | {} | 设备与 APP 的关联映射 |

### 2.4 控制变量 (1个)
| 变量名 | 类型 | 初始值 | 说明 |
|:---:|:---:|:---:|:---|
| force_regenerate | String | false | 是否强制重新生成画像或配置 |

### 2.5 状态变量 (9个)
| 变量名 | 类型 | 初始值 | 说明 |
|:---:|:---:|:---:|:---|
| last_error | String | - | 最后一次捕获的异常信息 |
| run_result | String | - | 全局运行结果汇总 |
| action_count | Int | 0 | 当前会话已执行动作总数 |
| effective_date | String | - | 规则生效日期 |
| current_action | String | - | 正在进行的动作名称 |
| current_post_id | String | - | 当前正在交互的帖子 ID |
| _init_status | String | False | 系统是否完成初始化 |
| _config_ok | String | False | 配置文件是否校验通过 |
| _module_ok | String | False | 模块热加载是否就绪 |

### 2.6 画像变量 (3个)
| 变量名 | 类型 | 初始值 | 说明 |
|:---:|:---:|:---:|:---|
| pregnancy_status | String | Normal | 账号“孕育”/权重状态 |
| special_requirements | String | None | 针对该画像的特殊行为要求 |
| _config_missing | String | False | 是否缺失画像必要的元数据 |

### 2.7 多平台变量 (6个)
| 变量名 | 类型 | 初始值 | 说明 |
|:---:|:---:|:---:|:---|
| current_platform | String | - | 当前操作平台 (与必填变量共用) |
| platform_module_path | String | - | 平台专属逻辑插件路径 |
| platform_rate_limit | Int | 60 | 平台频率限制阈值 (秒) |
| platform_actions_count | Int | 0 | 当日该平台累计动作数 |
| platform_last_action_time| String | - | 该平台最后一次动作的时间戳 |
| platform_cooldown_until | String | - | 冷却结束的具体时刻 |

### 2.8 人性化引擎变量 (5个)
| 变量名 | 类型 | 初始值 | 说明 |
|:---:|:---:|:---:|:---|
| humanization_profile | String | Balanced | 人性化模板 (Aggressive/Natural/Lazy) |
| scroll_speed_factor | Double | 1.0 | 滚动速度修正系数 |
| read_time_factor | Double | 1.0 | 模拟阅读时间系数 |
| pause_frequency | Double | 0.2 | 随机停顿频率 |
| typing_error_rate | Double | 0.05 | 模拟输入错误率 |

### 2.9 错误恢复变量 (4个)
| 变量名 | 类型 | 初始值 | 说明 |
|:---:|:---:|:---:|:---|
| retry_count | Int | 0 | 当前重试次数 |
| max_retries | Int | 3 | 最大允许重试次数 |
| retry_delay_ms | Int | 5000 | 重试间隔延迟 (毫秒) |
| last_error_type | String | - | 错误分类 (Network/UI/Auth) |

### 2.10 动作控制变量 (4个)
| 变量名 | 类型 | 初始值 | 说明 |
|:---:|:---:|:---:|:---|
| action_type | String | - | 待执行动作类型 (Scroll/Click/Type) |
| action_params_json | String | {} | 动作所需参数 JSON |
| action_result | String | - | 单个动作执行结果 |
| next_action | String | - | 逻辑预测的下一个动作 |

### 2.11 Selector 变量（v4.5.1 新增，7个）
| 变量名 | 类型 | 初始值 | 说明 |
|:---:|:---:|:---:|:---|
| reddit_sel_post_unit | String | post_unit | 帖子容器 resource-id |
| reddit_sel_post_footer | String | post_footer | 帖子底部操作栏 |
| reddit_sel_upvote_button | String | post_footer_first_child | 点赞按钮 |
| reddit_sel_comment_button | String | comment_button | 评论按钮 |
| reddit_sel_submit_button | String | submit_comment | 提交评论按钮 |
| reddit_sel_follow_button | String | follow_button | 关注按钮 |
| reddit_sel_share_button | String | share_button | 分享按钮 |

---

## 3. 模块清单 (12个模块)

| 模块 | 文件 | 结果变量 | 功能说明 |
|:---|:---|:---|:---|
| **Initializer** | Modules/Initializer.cs | initializer_result | 全局环境检查，初始化目录结构与全局变量 |
| **Main** | Modules/Main.cs | main_result | 系统大脑，负责流程路由与状态切换 |
| **PersonaCreate**| Modules/PersonaCreate.cs | persona_result | 调用 AI API 生成深度定制的人格画像数据 |
| **DailyUpdate** | Modules/DailyUpdate.cs | daily_result | 更新每日权重，分发今日具体操作计划 |
| **Extension** | Modules/Extension.cs | extension_result | 处理代理 IP 切换、地理信息模拟与天气同步 |
| **SessionRunner** | Modules/SessionRunner.cs | session_result | 执行具体的人机交互动作流 |
| **MemoryManager** | Modules/MemoryManager.cs | - | 交互记录去重与互动历史管理 (v4.5.1) |
| **RuleEngine** | Modules/RuleEngine.cs | - | 帖子评估规则引擎与疲劳度管理 (v4.5.1) |
| **StateSaver** | Modules/StateSaver.cs | - | 异步保存当前执行进度，防止崩溃丢失数据 |
| **Maintenance** | Modules/Maintenance.cs | - | 定期清理 Temp 文件夹与过期日志 |
| **ReportGen** | Modules/ReportGen.cs | report_result | 汇总执行数据，生成可视化运行报告 |
| **WeeklyEvolve** | Modules/WeeklyEvolve.cs | evolve_result | 每周评估账号成长，调整行为等级与权限 |

---

## 4. 配置文件清单 (12个)

| 文件名 | 用途 | 主要使用模块 |
|:---|:---|:---|
| **AIConfig.json** | AI 模型、API 密钥及提示词全局参数 | AIService / PersonaCreate |
| **StageConfig.json** | 账号不同阶段（养号期/成熟期）的参数定义 | DailyUpdate / WeeklyEvolve |
| **BehaviorConfig.json** | 定义点击、滑动等基础动作的随机范围 | SessionRunner |
| **PlatformsConfig.json** | 包含不同 APP 的包名、Activity 及元素特征 | Initializer / SessionRunner |
| **DecisionConfig.json** | 帖子评估规则、疲劳度模型、记忆管理配置 | RuleEngine / MemoryManager (v4.5.1) |
| **ExtensionsConfig.json** | 外部插件（如代理提供商）的 API 配置 | Extension |
| **ValidationRules.json** | 定义画像和状态是否合规的验证规则 | DailyUpdate |
| **EvolutionRules.json** | 账号升级与行为演进的逻辑配置 | WeeklyEvolve |
| **MaintenanceConfig.json** | 日志保留时长、磁盘空间清理阈值 | Maintenance |
| **Apps.json** | 支持的 APP 列表及其版本号配置 | Initializer |
| **device_app_mapping.json**| 记录各设备上安装的 APP 及账号关联关系 | Initializer |
| **PersonaPrompt.txt** | 喂给 AI 的画像生成核心 Prompt 模板 | PersonaCreate |

---

## 5. 变量-模块映射表

| 变量类别 | 涉及模块 | 说明 |
|:---|:---|:---|
| **必填变量** | 所有模块 | 全局基础，尤其是 `project_root` 影响所有文件读写 |
| **配置缓存变量** | Initializer, PersonaCreate, DailyUpdate | Initializer 加载，其他模块按需读取缓存字符串 |
| **画像变量** | PersonaCreate, DailyUpdate, SessionRunner | 控制 Session 期间的行为偏好（如：孕产期画像更温和） |
| **多平台变量** | SessionRunner, Extension | 动态切换平台逻辑，管理各平台的独立冷却时间 |
| **人性化引擎变量**| SessionRunner | 实时计算动作延迟，模拟非匀速滚动 |
| **状态/结果变量** | Main, StateSaver, ReportGen | Main 负责更新，StateSaver 负责写入，ReportGen 负责展示 |

---

## 6. 配置文件-模块映射表

| 配置文件 | 读取模块 | 写入/更新模块 |
|:---|:---|:---|
| AIConfig.json | PersonaCreate | - |
| StageConfig.json | DailyUpdate, WeeklyEvolve | WeeklyEvolve (演进后更新) |
| BehaviorConfig.json | SessionRunner | - |
| Apps.json | Initializer | - |
| device_app_mapping.json| Initializer | Main (记录新绑定关系) |
| PersonaPrompt.txt | PersonaCreate | - |

---

## 7. ZD 条件分支配置参考

在 ZennoDroid 的 Logic -> If 逻辑节点中，请按以下方式配置各个结果变量的分支：

### 7.1 初始化检测
- **逻辑语句:** `{-Variable.initializer_result-}` == `Success`
- **True:** 进入 `Main` 调度
- **False:** 记录 `last_error` 并跳转至报告生成

### 7.2 画像就绪检测
- **逻辑语句:** `{-Variable.persona_result-}` == `Exists` || `{-Variable.persona_result-}` == `Created`
- **True:** 继续 `DailyUpdate`
- **False:** 跳转至 `PersonaCreate` 模块

### 7.3 会话中断恢复
- **逻辑语句:** `{-Variable.run_result-}` == `Interrupted`
- **True:** 触发 `StateSaver` 读取旧进度并尝试 `retry_count`
- **False:** 正常流程至 `Maintenance`

### 7.4 演进检查
- **逻辑语句:** `(DateTime.Now - DateTime.Parse("{-Variable.effective_date-}")).TotalDays` >= `7`
- **True:** 调度 `WeeklyEvolve` 模块
- **False:** 跳过演进流程

---

## 8. 子项目清单 (4个)

SessionRunner 在执行会话时会调用以下子项目：

| 子项目 | 文件 | 功能 | 输入变量 | 输出变量 |
|:---|:---|:---|:---|:---|
| **Reddit_Browse** | ZDProjects/Reddit_Browse.cs | 浏览帖子列表 | humanization_profile, browse_scroll_count, browse_scroll_delay, screen_width, screen_height | browse_posts_count, browse_result, browse_error |
| **Reddit_Like** | ZDProjects/Reddit_Like.cs | 点赞操作 | humanization_profile, like_post_index, like_verify_delay | like_result, like_ui_changed, like_error |
| **Reddit_Comment** | ZDProjects/Reddit_Comment.cs | 评论操作 | humanization_profile, comment_post_index, comment_enable_reply, comment_reply_text, comment_scroll_count | comment_text, comment_count, comment_reply_entered, comment_result, comment_error |
| **Reddit_ReadPost** | ZDProjects/Reddit_ReadPost.cs | 阅读帖子详情 | humanization_profile | read_result |

### 子项目调用流程

```text
SessionRunner
    |
    v
+-------------------+
| 选择动作类型       |
| (action_type)     |
+-------------------+
    |
    +---> browse ---> Reddit_Browse.cs ---> browse_result
    |
    +---> like -----> Reddit_Like.cs -----> like_result
    |
    +---> comment --> Reddit_Comment.cs --> comment_result
    |
    +---> read -----> Reddit_ReadPost.cs -> read_result
    |
    v
+-------------------+
| 检查 next_action  |
| 决定是否继续循环   |
+-------------------+
```

---

## 9. 平台操作清单

### 9.1 Reddit 平台

**操作定义**: `Config/Operations/reddit_*.json` + `Config/Selectors/reddit_selectors.json`

**支持操作**:
| 操作 | 函数 | 说明 |
|:---|:---|:---|
| Initialize | reddit_initialize | 打开 Reddit APP，验证状态 |
| Browse | reddit_browse | 滚动浏览帖子列表 |
| Like | reddit_like | 点赞（Upvote）帖子 |
| Comment | reddit_comment | 发表评论 |
| Follow | reddit_follow | 关注用户/社区 |
| Share | reddit_share | 分享帖子 |

**速率限制**:
- 最大 120 actions/hour
- 最大 40 likes/hour
- 最大 20 comments/hour
- 最大 15 follows/hour
- 动作间隔: 2000-8000ms

**UI 选择器**:
- post_unit: 帖子容器
- post_footer: 帖子底部操作栏
- upvote_button: 点赞按钮
- comment_button: 评论按钮

### 9.2 Instagram 平台

**操作定义**: `Config/Operations/instagram_*.json` + `Config/Selectors/instagram_selectors.json`

**支持操作**:
| 操作 | 函数 | 说明 |
|:---|:---|:---|
| Initialize | instagram_initialize | 打开 Instagram APP，验证状态 |
| Browse | instagram_browse | 滚动浏览 Feed |
| Like | instagram_like | 双击或点击心形按钮点赞 |
| Comment | instagram_comment | 发表评论 |
| Follow | instagram_follow | 关注用户 |
| Share | instagram_share | 分享到 Story/DM |

**速率限制** (更严格):
- 最大 60 actions/hour
- 最大 30 likes/hour
- 最大 10 comments/hour
- 最大 20 follows/hour
- 动作间隔: 3000-12000ms

**特有变量**:
| 变量名 | 说明 |
|:---|:---|
| instagram_actions_this_hour | 当前小时动作计数 |
| instagram_likes_this_hour | 当前小时点赞计数 |
| instagram_comments_this_hour | 当前小时评论计数 |
| instagram_follows_this_hour | 当前小时关注计数 |
| instagram_hour_start | 小时开始时间戳 |

---

## 10. Core 工具类清单 (14个)

| 文件 | 需要ZD配置 | 功能 | 相关变量 |
|:---|:---:|:---|:---|
| **Core/UILocator.cs** | 否 | 多策略 UI 元素定位器 | - |
| **Core/ErrorRecovery.cs** | **是** | 错误恢复与重试逻辑 | error_count_*, last_error_* |
| **Core/HumanizationEngine.cs** | 可选 | 人性化行为模拟 | humanization_profile |
| **Core/PlatformBase.cs** | 可选 | 平台模块基类 | action_log |
| **Core/ScriptHelpers.cs** | 否 | ZDProjects 公共函数库 | - |
| **Modules/Core/CoreHelper.cs** | 否 | 核心工具函数库 | - |
| **Modules/Core/JsonHelper.cs** | 否 | JSON 解析工具 | - |
| **Modules/Core/AIService.cs** | 否 | AI API 调用服务 | - |
| **Modules/Core/FileHelper.cs** | 否 | 文件操作工具 | - |
| **Modules/Core/ActionExecutor.cs** | 否 | 统一操作执行器 (JSON 步骤驱动) | - |
| **Modules/Core/PageDetector.cs** | 否 | 页面状态检测 | - |
| **Modules/Core/SelectorEngine.cs** | 否 | 选择器引擎 | - |
| **Modules/Core/ExtensionManager.cs** | 否 | 扩展管理器 | - |
| **Modules/Core/IExtension.cs** | 否 | 扩展接口定义 | - |
| **Modules/UIHelper.cs** | 否 | Android UI XML 解析 | - |

### ErrorRecovery.cs 需要的 ZD 变量

如果使用错误恢复功能，需要在 ZD 中创建以下变量：

| 变量名 | 类型 | 初始值 | 说明 |
|:---|:---:|:---:|:---|
| error_count_app_crash | String | 0 | APP 崩溃计数 |
| error_count_ui_not_found | String | 0 | UI 元素未找到计数 |
| error_count_network_error | String | 0 | 网络错误计数 |
| error_count_timeout | String | 0 | 超时计数 |
| last_error_type | String | - | 最后错误类型 |
| last_error_message | String | - | 最后错误消息 |
| last_error_time | String | - | 最后错误时间 |

---

---

## 11. 详细流程图 (含变量输入输出)

### 11.1 主流程变量流转图

```text
================================================================================
                              【 系统启动 】
================================================================================
                                    |
                                    v
+==============================================================================+
|                           [1] Initializer                                     |
|------------------------------------------------------------------------------|
| 输入变量:                                                                     |
|   - project_root (必填)                                                       |
|                                                                              |
| 输出变量:                                                                     |
|   - _init_status = "OK" / "FAILED"                                           |
|   - _config_ok = "5" (配置文件数量)                                           |
|   - _config_missing = "0" (缺失数量)                                          |
|   - _module_ok = "10" (模块数量)                                              |
|   - initializer_result = "SUCCESS" / "WARNING" / "ERROR"                     |
|   - last_error = "" (错误信息)                                                |
+==============================================================================+
                                    |
                    +---------------+---------------+
                    |                               |
                    v                               v
        [initializer_result                [initializer_result
            == "SUCCESS"]                      == "ERROR"]
                    |                               |
                    v                               v
+==============================================================================+
|                              [2] Main                                         |
|------------------------------------------------------------------------------|
| 输入变量:                                                                     |
|   - project_root                                                             |
|   - device_id                                                                |
|   - force_regenerate = "true" / "false"                                      |
|                                                                              |
| 输出变量:                                                                     |
|   - ai_config_json (从 AIConfig.json 加载)                                    |
|   - stage_config_json (从 StageConfig.json 加载)                              |
|   - behavior_config_json (从 BehaviorConfig.json 加载)                        |
|   - persona_json (从 Persons/{device_id}.json 加载)                           |
|   - session_plan_json (生成的会话计划)                                         |
|   - effective_date = "2026-02-08"                                            |
|   - main_result = "READY" / "NEED_CREATE_PERSONA" / "NEED_DAILY_UPDATE" / "ERROR" |
+==============================================================================+
                                    |
        +---------------------------+---------------------------+
        |                           |                           |
        v                           v                           v
[main_result              [main_result                [main_result
  == "READY"]           == "NEED_CREATE_PERSONA"]   == "NEED_DAILY_UPDATE"]
        |                           |                           |
        |                           v                           v
        |   +===============================================+   |
        |   |            [2a] PersonaCreate                 |   |
        |   |-----------------------------------------------|   |
        |   | 输入变量:                                      |   |
        |   |   - project_root                              |   |
        |   |   - device_id                                 |   |
        |   |   - ai_config_json                            |   |
        |   |   - pregnancy_status (可选)                    |   |
        |   |   - special_requirements (可选)                |   |
        |   |                                               |   |
        |   | 输出变量:                                      |   |
        |   |   - persona_json (AI生成的画像)                |   |
        |   |   - persona_result = "SUCCESS" / "ERROR"      |   |
        |   |   - last_error                                |   |
        |   +===============================================+   |
        |                           |                           |
        |                           v                           |
        |   +===============================================+   |
        |   |            [2b] DailyUpdate                   |   |
        |   |-----------------------------------------------|   |
        |   | 输入变量:                                      |   |
        |   |   - project_root                              |   |
        |   |   - device_id                                 |   |
        |   |   - persona_json                              |   |
        |   |                                               |   |
        |   | 输出变量:                                      |   |
        |   |   - persona_json (更新后的画像)                |   |
        |   |   - daily_result = "SUCCESS" / "ERROR"        |   |
        |   |   - last_error                                |   |
        |   +===============================================+   |
        |                           |                           |
        +---------------------------+---------------------------+
                                    |
                                    v
+==============================================================================+
|                            [3] Extension                                      |
|------------------------------------------------------------------------------|
| 输入变量:                                                                     |
|   - project_root                                                             |
|                                                                              |
| 输出变量:                                                                     |
|   - current_ip = "203.0.113.45"                                              |
|   - current_weather = "sunny" / "rainy" / "cloudy"                           |
|   - extension_result = "SUCCESS" / "SKIPPED" / "ERROR"                       |
|   - last_error                                                               |
+==============================================================================+
                                    |
                                    v
+==============================================================================+
|                          [4] SessionRunner                                    |
|------------------------------------------------------------------------------|
| 输入变量:                                                                     |
|   - project_root                                                             |
|   - device_id                                                                |
|   - persona_json                                                             |
|   - session_plan_json                                                        |
|   - behavior_config_json                                                     |
|   - current_platform = "reddit" / "instagram"                                |
|                                                                              |
| 输出变量:                                                                     |
|   - platforms_config_json (从 PlatformsConfig.json 加载)                      |
|   - current_action = "browse" / "like" / "comment" / "read" / "follow"       |
|   - pending_action = "reddit_browse" / "reddit_like" / ...                   |
|   - pending_action_type = "browse" / "like" / ...                            |
|   - action_count = "15" (已执行动作数)                                        |
|   - run_result = "SUCCESS" / "ERROR"                                         |
|   - session_result = "SUCCESS" / "ERROR"                                     |
|   - last_error                                                               |
+==============================================================================+
```

### 11.2 SessionRunner 内部动作循环

```text
================================================================================
                    SessionRunner 动作循环详细流程
================================================================================

+------------------------------------------------------------------------------+
|                         平台选择 (current_platform)                           |
+------------------------------------------------------------------------------+
                                    |
            +-----------------------+-----------------------+
            |                                               |
            v                                               v
+==========================+                 +==========================+
|     Reddit 平台          |                 |    Instagram 平台        |
|--------------------------|                 |--------------------------|
| 速率限制:                 |                 | 速率限制:                 |
|  - 120 actions/hour      |                 |  - 60 actions/hour       |
|  - 40 likes/hour         |                 |  - 30 likes/hour         |
|  - 20 comments/hour      |                 |  - 10 comments/hour      |
|                          |                 |                          |
| 变量:                     |                 | 变量:                     |
|  - platform_rate_limit   |                 |  - instagram_actions_this_hour |
|  - platform_actions_count|                 |  - instagram_likes_this_hour   |
|  - platform_last_action_time |             |  - instagram_hour_start        |
+==========================+                 +==========================+
            |                                               |
            +------------------------+----------------------+
                                     |
                                     v
+------------------------------------------------------------------------------+
|                    动作类型选择 (action_type)                                 |
+------------------------------------------------------------------------------+
                                     |
     +----------+----------+----------+----------+----------+
     |          |          |          |          |          |
     v          v          v          v          v          v
+========+ +========+ +========+ +========+ +========+ +========+
| browse | |  like  | |comment | |  read  | | follow | | share  |
+========+ +========+ +========+ +========+ +========+ +========+
     |          |          |          |          |          |
     v          v          v          v          v          v

+==============================================================================+
|                      [browse] Reddit_Browse.cs                                |
|------------------------------------------------------------------------------|
| 输入变量:                                                                     |
|   - humanization_profile = "casual" / "active" / "lurker" / "new_user" |
|   - browse_scroll_count = "3" (滚动次数)                                      |
|   - browse_scroll_delay = "2000" (滚动间隔ms)                                 |
|   - screen_width = "1080"                                                    |
|   - screen_height = "2400"                                                   |
|                                                                              |
| 输出变量:                                                                     |
|   - browse_posts_count = "12" (浏览的帖子数)                                  |
|   - browse_result = "SUCCESS" / "ERROR"                                      |
|   - browse_error = "" (错误信息)                                              |
|   - next_action = "read" / "like" / "none"                                   |
+==============================================================================+

+==============================================================================+
|                        [like] Reddit_Like.cs                                  |
|------------------------------------------------------------------------------|
| 输入变量:                                                                     |
|   - humanization_profile                                                     |
|   - like_post_index = "0" (要点赞的帖子索引)                                   |
|   - like_verify_delay = "1500" (验证延迟ms)                                   |
|                                                                              |
| 输出变量:                                                                     |
|   - like_result = "SUCCESS" / "ERROR"                                        |
|   - like_ui_changed = "true" / "false" (UI是否变化)                           |
|   - like_error = ""                                                          |
|   - next_action = "comment" / "browse" / "none"                              |
+==============================================================================+

+==============================================================================+
|                      [comment] Reddit_Comment.cs                              |
|------------------------------------------------------------------------------|
| 输入变量:                                                                     |
|   - humanization_profile                                                     |
|   - comment_post_index = "0"                                                 |
|   - comment_enable_reply = "true" / "false"                                  |
|   - comment_reply_text = "这是我的评论内容"                                    |
|   - comment_scroll_count = "2"                                               |
|   - screen_width, screen_height                                              |
|                                                                              |
| 输出变量:                                                                     |
|   - comment_text = "收集到的评论文本"                                          |
|   - comment_count = "5" (评论数量)                                            |
|   - comment_reply_entered = "true" / "false"                                 |
|   - comment_result = "SUCCESS" / "ERROR"                                     |
|   - comment_error = ""                                                       |
|   - next_action = "browse" / "none"                                          |
+==============================================================================+

+==============================================================================+
|                        [read] Reddit_ReadPost.cs                              |
|------------------------------------------------------------------------------|
| 输入变量:                                                                     |
|   - humanization_profile                                                     |
|   - read_duration = "15" (阅读时长秒)                                         |
|   - scroll_to_comments = "true" / "false"                                    |
|                                                                              |
| 输出变量:                                                                     |
|   - read_result = "SUCCESS" / "ERROR"                                        |
|   - read_error = ""                                                          |
|   - next_action = "like" / "comment" / "none"                                |
+==============================================================================+

                                     |
                                     v
+------------------------------------------------------------------------------+
|                       检查 next_action 变量                                   |
|------------------------------------------------------------------------------|
|  IF next_action != "none" --> 返回动作循环继续执行                             |
|  IF next_action == "none" --> 退出循环，进入 StateSaver                        |
+------------------------------------------------------------------------------+
```

### 11.3 收尾流程

```text
+==============================================================================+
|                           [5] StateSaver                                      |
|------------------------------------------------------------------------------|
| 输入变量:                                                                     |
|   - project_root                                                             |
|   - device_id                                                                |
|   - persona_json (要保存的画像)                                               |
|   - action_count (执行的动作数)                                               |
|                                                                              |
| 输出变量:                                                                     |
|   - state_result = "SUCCESS" / "ERROR"                                       |
|   - last_error                                                               |
|                                                                              |
| 写入文件:                                                                     |
|   - Persons/{device_id}.json (画像)                                          |
|   - Stats/{device_id}_stats.json (统计)                                      |
+==============================================================================+
                                    |
                                    v
+==============================================================================+
|                          [6] Maintenance                                      |
|------------------------------------------------------------------------------|
| 输入变量:                                                                     |
|   - project_root                                                             |
|   - device_id (可选)                                                         |
|                                                                              |
| 输出变量:                                                                     |
|   - last_error                                                               |
|                                                                              |
| 操作:                                                                         |
|   - 清理 Logs/ 目录 (保留30天)                                                |
|   - 清理 Memory/ 目录 (保留180天)                                             |
|   - 清理 Backups/ 目录 (保留30天)                                             |
+==============================================================================+
                                    |
                                    v
+==============================================================================+
|                          [7] ReportGen                                        |
|------------------------------------------------------------------------------|
| 触发条件: 当前时间 >= 17:00 且 当天未生成报告                                   |
|                                                                              |
| 输入变量:                                                                     |
|   - project_root                                                             |
|   - device_id                                                                |
|                                                                              |
| 输出变量:                                                                     |
|   - report_result = "SUCCESS" / "SKIPPED" / "ERROR"                          |
|   - last_error                                                               |
|                                                                              |
| 写入文件:                                                                     |
|   - Reports/{device_id}/{date}.json                                          |
|   - Reports/{device_id}/{date}.csv                                           |
+==============================================================================+
                                    |
                                    v
                              【 流程结束 】
```

### 11.4 独立项目: WeeklyEvolve

```text
================================================================================
                         WeeklyEvolve (每周独立运行)
================================================================================

触发条件: 每周日 或 手动触发

+==============================================================================+
|                          WeeklyEvolve                                         |
|------------------------------------------------------------------------------|
| 输入变量:                                                                     |
|   - project_root                                                             |
|   - device_id                                                                |
|   - persona_json (当前画像)                                                   |
|   - ai_config_json (AI配置)                                                  |
|                                                                              |
| 输出变量:                                                                     |
|   - persona_json (演进后的画像)                                               |
|   - evolve_result = "SUCCESS" / "SKIPPED" / "ERROR"                          |
|   - last_error                                                               |
|                                                                              |
| 演进内容:                                                                     |
|   - personality.optimism_level (乐观程度)                                     |
|   - personality.anxiety_prone (焦虑倾向)                                      |
|   - interests_and_hobbies (兴趣爱好)                                          |
|   - health_and_wellness.mental_health (心理健康)                              |
+==============================================================================+
```

### 11.5 变量流转总结表

| 阶段 | 模块 | 读取变量 | 写入变量 |
|:---|:---|:---|:---|
| 初始化 | Initializer | project_root | _init_status, _config_ok, _module_ok, initializer_result |
| 主入口 | Main | project_root, device_id, force_regenerate | ai_config_json, persona_json, session_plan_json, main_result |
| 画像生成 | PersonaCreate | ai_config_json, pregnancy_status | persona_json, persona_result |
| 每日更新 | DailyUpdate | persona_json | persona_json, daily_result |
| 扩展 | Extension | project_root | current_ip, current_weather, extension_result |
| 会话执行 | SessionRunner | persona_json, session_plan_json, behavior_config_json | current_action, action_count, session_result |
| 浏览 | Reddit_Browse | humanization_profile, browse_scroll_count | browse_posts_count, browse_result, next_action |
| 点赞 | Reddit_Like | humanization_profile, like_post_index | like_result, like_ui_changed, next_action |
| 评论 | Reddit_Comment | humanization_profile, comment_reply_text | comment_count, comment_result, next_action |
| 阅读 | Reddit_ReadPost | humanization_profile | read_result, next_action |
| 状态保存 | StateSaver | persona_json, action_count | state_result |
| 维护 | Maintenance | project_root | - |
| 报告 | ReportGen | project_root, device_id | report_result |
| 周演进 | WeeklyEvolve | persona_json, ai_config_json | persona_json, evolve_result |

---

## 12. 快速配置检查清单

### 12.1 变量创建检查
- [ ] 创建所有 54 个主变量（30 个基础 + 17 个扩展 + 7 个 selector）
- [ ] 创建 7 个 ErrorRecovery 变量（如使用错误恢复）
- [ ] 设置 project_root 初始值
- [ ] 设置 device_id 初始值
- [ ] 设置 current_app 初始值

### 12.2 配置文件检查
- [ ] AIConfig.json - 配置 API Key
- [ ] PlatformsConfig.json - 确认平台设置
- [ ] BehaviorConfig.json - 调整行为参数
- [ ] device_app_mapping.json - 配置设备映射

### 12.3 动作块检查
- [ ] 添加 Initializer Own Code
- [ ] 添加 Main Own Code
- [ ] 添加 PersonaCreate Own Code
- [ ] 添加 DailyUpdate Own Code
- [ ] 添加 Extension Own Code
- [ ] 添加 SessionRunner Own Code
- [ ] 添加 StateSaver Own Code
- [ ] 添加 Maintenance Own Code
- [ ] 添加 ReportGen Own Code
- [ ] 添加 WeeklyEvolve Own Code

### 12.4 条件分支检查
- [ ] Initializer 后的 SUCCESS/ERROR 分支
- [ ] Main 后的 READY/NEED_CREATE/NEED_UPDATE/ERROR 分支
- [ ] SessionRunner 后的 SUCCESS/ERROR 分支
- [ ] ReportGen 的时间条件 (17:00后)

---

*文档版本: v2.2 | 最后更新: 2026-02-14*
