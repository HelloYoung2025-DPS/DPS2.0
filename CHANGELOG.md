# DPS v4.5 更新日志

## [4.5.2] - 2026-02-17

### 🐛 Bug 修复 (P0 级别)

#### ModuleLoader.cs - 缓存管理优化
- **修复**: 缓存失效不检测依赖文件删除
  - 引入 `CacheEntry` 结构化缓存条目（方法 + 依赖快照 + 访问时间）
  - 实现依赖文件删除检测（对比缓存快照与当前文件列表）
  - 添加路径规范化函数避免大小写/分隔符导致重复键
- **修复**: 缓存无界增长导致内存泄漏
  - 实现 LRU 缓存淘汰机制（上限 32 个条目）
  - 添加 `EvictOldestCacheEntry` 自动清理旧缓存

#### SessionRunner.cs - 并发安全性
- **修复**: 静态状态跨会话串扰
  - 移除静态 `_random`，改用 `[ThreadStatic]` 的 `_threadRandom`
  - 添加 `GetRandom()` 方法（线程安全的随机数生成器）
  - 创建 `SessionState` 类封装疲劳模型状态
  - 所有疲劳变量改为 `SessionState` 实例字段
- **修复**: 配置异常导致运行时崩溃
  - `GetActionDelay` 添加 min/max 边界校验
  - 添加溢出保护（上限 3600 秒）
  - 自动修正 min/max 颠倒情况

#### MemoryManager.cs - 并发写入保护
- **修复**: 并发写入导致数据丢失
  - 添加文件级锁机制（`GetFileLock` 按路径获取锁对象）
  - `RecordInteractionWithScore` 使用 `lock` 包裹读改写操作

#### DailyUpdate.cs - 数据完整性
- **修复**: 未来 conception_date 产生负孕周
  - 添加 `totalDays < 0` 检测（跳过更新并记录错误）
  - 添加负值保护（weeks/days 归零）
  - 修改正则表达式支持负数匹配
- **新增**: 产后阶段转换逻辑
  - PP0 → PP1（产后 3 个月）
  - PP1 → NP（产后 12 个月）
  - 基于 `delivery_date` 自动计算并转换阶段

### 📝 技术细节
- 所有修复严格遵守 C# 5.0 语法约束
- 保持原有代码风格和注释规范
- 通过语法验证和关键点验证

---

## [4.5.1] - 2026-02-13

### 🔧 Config-Driven Selectors (Phase 3)
- **修复** `RedditModule.cs` / `InstagramModule.cs` - 嵌套 JSON selector 对象解析 bug（`GetJsonValue` 无法解析嵌套对象，导致始终使用默认值）
- **新增** `GetSelectorValue` 辅助函数 - 正确提取 `PlatformsConfig.json` 中 `ui_selectors` 的嵌套 `value` 字段
- **新增** `RedditModule.cs` 导出 selector 变量到 ZD 变量 (`reddit_sel_*`)，供 ZDProjects 脚本使用
- **修复** `InstagramModule.cs` Like 操作中硬编码的 `media_image` fallback，改为 `cfg_mediaImage` 从配置读取
- **修改** ZDProjects 脚本 (`Reddit_Browse.cs`, `Reddit_Like.cs`, `Reddit_Comment.cs`, `Reddit_ReadPost.cs`) - 从 ZD 变量读取 selectors，不再硬编码

### ✅ Extension Integration (Phase 4 验证)
- **确认** `Extension.cs` 已完全重构为使用 `ExtensionManager`（`RegisterBuiltinExtensions` + `LoadFromRegistry` + `RunCategory`）
- **确认** `ExtensionManager.cs`, `IExtension.cs`, `ExtensionsRegistry.json` 完整且集成
- **确认** `IPLocationExtension.cs`, `WeatherExtension.cs` 独立扩展类正常工作

---

## [4.5.0] - 2026-02-07

### 🌐 多平台支持
- **新增** Reddit 平台支持 (`Platforms/Reddit/RedditModule.cs`)
- **新增** Instagram 平台支持 (`Platforms/Instagram/InstagramModule.cs`)
- **新增** 平台配置文件 `Config/PlatformsConfig.json`
- **新增** 设备应用映射 `Config/device_app_mapping.json`

### 🧩 Core Modules
- **新增** `Core/HumanizationEngine.cs` - 人性化行为引擎 (4 种配置文件)
- **新增** `Core/UILocator.cs` - 多策略 UI 元素定位器
- **新增** `Core/ErrorRecovery.cs` - 错误恢复机制 (指数退避)
- **新增** `Core/PlatformBase.cs` - 平台基类接口

### 📚 Documentation
- **新增** `Docs/GETTING_STARTED.md` - 新人入门指南
- **新增** `Docs/QuickSetup_Flowchart.md` - 快速配置流程图
- **新增** `Docs/CopyPaste_Setup.md` - 复制粘贴配置手册
- **新增** `Docs/MultiPlatformFramework.md` - 多平台框架文档
- **新增** `Docs/PersonaSchema_MultiPlatform.md` - 多平台画像 Schema
- **更新** 所有文档版本号升级至 v4.5

### 🔧 架构改进
- **新增** 混合架构模式 - 共享核心框架 + 平台独立模块
- **新增** 相对坐标系统 (百分比) - 多分辨率适配
- **新增** 速率限制系统 - Reddit 120/小时, Instagram 60/小时

---

## [4.1.0] - 2026-02-05

### 🚀 性能优化

#### ModuleLoader.cs
- **新增** 静态编译缓存机制，避免重复编译
- **新增** 文件时间戳检测，仅在源码变更时重新编译
- **新增** 线程安全的缓存访问 (`lock`)
- **性能** 第二次运行从 ~500ms 降至 <10ms

### 🔧 架构改进

#### JsonHelper.cs (完全重写)
- **重写** 使用栈式状态机实现健壮的 JSON 解析器
- **修复** 嵌套对象中同名键的正确匹配（深度感知）
- **修复** 转义字符处理（包括 `\\\"` 和 `\\\\`）
- **修复** Unicode 转义序列 `\uXXXX` 完整支持
- **新增** `GetArrayElement(arrayJson, index)` - 按索引获取数组元素
- **新增** `IsValidJson(json)` - JSON 格式验证
- **新增** `CreateArray(values)` - 创建 JSON 数组

#### CoreHelper.cs
- **重构** `JGet/JGetNested/JSet` 现在委托给 `JsonHelper`
- **移除** 重复的 JSON 解析逻辑

#### AIService.cs
- **改进** 使用 `JsonHelper` 解析 API 响应
- **新增** API 错误检测（检查响应中的 `error` 字段）
- **改进** Gemini/OpenAI 响应解析更加健壮

### 📊 测试验证

所有修改通过以下测试用例：
- 嵌套对象同名键: `{"data": {"name": "inner"}, "name": "outer"}` → 正确返回 `"outer"`
- 转义引号: `{"msg": "He said \"hello\""}` → 正确解析
- Unicode: `{"text": "\u0048\u0065\u006c\u006c\u006f"}` → 返回 `"Hello"`
- 嵌套路径: `user.profile.name` → 正确遍历

---

## [4.0.2] - 2026-02-04

### 🐛 Bug 修复

#### JsonHelper.cs
- **修复** `Get` 方法现在是上下文感知的，不会错误匹配字符串值中的键名
- **修复** `Unescape` 方法现在支持 Unicode 转义序列 `\uXXXX`

#### CoreHelper.cs
- **修复** `WriteFileAtomic` 添加异常处理，当 `.bak` 文件被锁定时回退到直接覆盖
- **新增** `CountOccurrences(text, pattern)` - 统一的字符串计数方法
- **新增** `ValidateDeviceId(deviceId)` - 防止路径遍历攻击的安全验证
- **新增** `GetSafeDeviceId(deviceId, defaultValue)` - 安全获取设备ID

#### WeeklyEvolve.cs
- **修复** AI 返回的进化建议现在会实际应用到画像
- **新增** 解析 `changes` 数组并应用字段修改
- **新增** 进化前自动备份画像
- **新增** 设备ID安全验证
- **移除** 重复的 `CountOccurrences` 方法，改用 `CoreHelper.CountOccurrences`

#### Extension.cs
- **修复** 配置检查逻辑，正确读取 `extensions.ip_location.enabled` 和 `extensions.weather.enabled`
- **修复** 使用 `JsonHelper.ExtractObject` 替代不可靠的 `JGet` 检查

#### ReportGen.cs
- **修复** 文件名一致性：检查和保存都使用 `{date}_weekly.json`
- **新增** 设备ID安全验证
- **移除** 重复的 `CountOccurrences` 方法，改用 `CoreHelper.CountOccurrences`

#### Maintenance.cs
- **移除** 重复的 `CountOccurrences` 方法，改用 `CoreHelper.CountOccurrences`

#### StateSaver.cs
- **新增** 设备ID安全验证
- **移除** 未使用的 `SaveMemory` 方法（记忆由 SessionRunner 保存）
- **修复** 路径拼接一致性

---

## [4.0.1] - 2026-01-31

### 🔧 动态配置支持
所有模块已更新为从配置文件动态读取参数，不再使用硬编码值。

#### AIService.cs
- **新增** `CallWithRetry(prompt, aiConfigJson)` - 自动重试 + 备用模型
- **新增** `CallPrimary/CallFallback/CallBackup` - 分别调用三个模型
- **新增** `CallOpenAICompatible` - 支持自定义 base_url
- **修改** 所有参数从 `AIConfig.json` 动态读取：
  - model, api_key, base_url
  - timeout_ms, max_tokens, temperature

#### JsonHelper.cs
- **新增** `ExtractObject(json, key)` - 提取嵌套对象
- **新增** `ExtractArray(json, key)` - 提取数组

#### PersonaCreate.cs
- **修改** 使用 `AIService.CallWithRetry` 替代 `CallGemini`
- **修改** 自动从文件加载 AI 配置（如变量为空）
- **删除** 废弃的 `ExtractApiKey` 方法

#### WeeklyEvolve.cs
- **修改** 使用 `AIService.CallWithRetry` 替代 `CallGemini`
- **修改** 自动从文件加载 AI 配置（如变量为空）
- **删除** 废弃的 `ExtractApiKey` 方法

#### SessionRunner.cs
- **修改** 动作权重从 `BehaviorConfig.json` 读取
- **修改** 打字速度从配置的 typing 节读取
- **修改** 动作延迟从配置的 duration_sec_min/max 读取
- **修改** 会话时长限制从配置读取

#### Maintenance.cs
- **新增** 支持从 `MaintenanceConfig.json` 读取保留期限
- **修改** 日志/记忆/备份保留天数可配置

---

### ⬆️ 版本升级支持（增强版）
新增 `force_regenerate` 变量，解决源码更新后运行时数据不同步问题。

#### Main.cs
- **新增** 读取 `force_regenerate` 变量（true/1/yes）
- **新增** `ClearRuntimeData()` 方法，统一清理所有运行时数据
- **新增** 启用时清理以下内容：
  - 画像文件 `Persons/{device_id}.json`
  - 记忆文件 `Memory/{device_id}/*.json`
  - 报告文件 `Reports/{device_id}/*.json`
- **新增** 所有文件备份到 `Backups/Upgrade_{date}/`
- **新增** 清空缓存变量 `persona_json`, `session_plan_json`
- **新增** 强制执行每日更新
- **新增** 执行完成后自动重置 `force_regenerate = false`

#### 备份目录结构
```
Backups/
└── Upgrade_2026-01-31/
    ├── persona_device_001.json
    ├── Memory_device_001/
    │   ├── 2026-01-30.json
    │   └── 2026-01-29.json
    └── Reports_device_001/
        └── 2026-01-31_weekly.json
```

---

### 📄 新增配置文件

#### Config/MaintenanceConfig.json
```json
{
    "log_retention_days": 30,
    "memory_retention_days": 180,
    "backup_retention_days": 30
}
```

---

### 📋 ZennoDroid 变量更新

新增需要在 ZD 中创建的变量：

| 变量名 | 类型 | 初始值 | 用途 |
|--------|------|--------|------|
| `force_regenerate` | 文本 | `false` | 设为 `true` 强制重新生成所有内容 |

---

## 升级指南

### 从旧版本升级

1. **复制最新代码**
   - 将 `Modules/` 目录下所有 `.cs` 文件覆盖
   - 将 `Config/MaintenanceConfig.json` 复制到项目

2. **更新 ZD 变量**
   - 在 ZennoDroid 中新增 `force_regenerate` 变量，初始值 `false`

3. **强制重新生成（可选）**
   - 如需重新生成画像等内容，设置 `force_regenerate = true`
   - 运行 Main 模块，系统会自动备份旧内容并重新生成
   - 完成后变量自动重置为 `false`

### 模块加载器 (ZDProjects/*_OwnCode.cs)

这些文件**无需更新**，除非日志显示编译错误。模块加载器只负责编译外部文件，业务逻辑更新会自动生效。

---

## [4.0.0] - 2026-01-30

### 初始版本
- 动态编译架构
- 模块化设计
- AI 画像生成
- 会话模拟
- 每日/每周更新
