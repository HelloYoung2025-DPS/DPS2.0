# DPS v4.5 Project Rules

## .omo 工作流已激活

此项目使用 OhMyOpenCode 工作流标准。在执行任何实现之前，你必须遵循以下规则。

## 全局字符白名单（最高优先级）
**强制规则：所有输入与输出仅允许使用中文字符和英文字符。**
允许：中文、English、数字 `0-9`、ASCII 标点符号、常用空白符。
**严格禁止**任何其他语种字符，包括但不限于：
- 韩文（Hangul）
- 日文假名（Hiragana / Katakana）
- 阿拉伯文、泰文、俄文等非中英文字符
适用范围：
- 助手回复
- 代码注释
- 文档文本
- 提交信息
- 日志说明
若检测到非法字符，必须立即改写为仅含中文和英文字符的版本后再输出。

### 强制性工作流

1. 使用 Read 工具读取 `.omo.conf` 配置文件获取项目约束
2. 读取 `.omo/` 目录中的状态文件（context.md, decisions_架构决策.md, conventions_代码规范.md）
3. 加载 `~/.omo/global-hooks/pre-task.md` 工作流协议
4. 读取 `.omo/layers/EXECUTION_PROTOCOL.md` 与 `.omo/modules/WORKFLOW.md`
5. 对于非简单任务，先更新 `.omo/current-task/plan.md`，写明 `主层级(L1/L2/L3/L4)`、`受影响层级`、`主模块`、`文件修改顺序`、`验证顺序`、`强制运行命令`，并等待用户批准
6. 若任务涉及 `L2/L3/L4`，在修改实现文件前必须创建或更新 `.omo/modules/{ModuleName}.md`
7. 在计划文件与模块追踪文件准备完成后、任何实现文件修改前，必须运行 `pwsh -File Tools\omo_guard\Invoke-OmoGate.ps1 -Phase Preflight`
8. `Preflight` 通过后，先对已完成的计划文件 / 模块追踪文件执行 `Advance`
9. 每修改完计划中的一个文件，必须运行 `pwsh -File Tools\omo_guard\Invoke-OmoGate.ps1 -Phase Advance -FilePath "<该文件>"`
10. 先更新 `.omo` 分层登记，再更新 `Config/`、`Modules/`、`ZDProjects/`、`Tools/` 等实现文件
11. 所有变更记录到 `CHANGELOG.md`，且必须作为最后一个计划文件更新
12. 所有实现与验证完成后，必须运行 `pwsh -File Tools\omo_guard\Invoke-OmoGate.ps1 -Phase Postflight -ExecuteCommands`

### 项目约束 (来自 .omo 配置)

- **工作流**: Plan-first（中等/复杂任务必须先制定计划）
- **代码质量**: 所有 I/O 操作必须添加错误处理
- **架构原则**: 遵循现有代码库模式
- **向后兼容性**: 必须维护
- **变更记录**: 所有修改记录到 CHANGELOG.md

**注意**: 特定技术约束（如 C# 5.0 语法限制、.NET 4.5 框架要求）由开发环境决定，会触发相应的 SKILLS，不是 .omo 规范的核心要求。

### External File Loading

CRITICAL: 在每个编程任务开始时，读取以下文件：

@.omo.conf — 项目约束配置（语言、版本、平台、代码标准）
@.omo/context.md — 项目上下文（技术栈、架构、目录结构）
@.omo/decisions_架构决策.md — 架构决策记录
@.omo/conventions_代码规范.md — 代码约定
@.omo/layers/EXECUTION_PROTOCOL.md — L1/L2/L3/L4 强制执行协议
@.omo/modules/WORKFLOW.md — 模块追踪与层级落地流程
@Docs/DOCS_RULES.md — Docs/ 目录约束规则（禁止新建文件、命名规范）
@Tools/omo_guard/Invoke-OmoGate.ps1 — 脚本级 Gate（Preflight / Advance / Postflight）

如果 `~/.omo/global-hooks/pre-task.md` 存在，也必须读取以加载完整工作流协议。

### L1 / L2 / L3 / L4 强制执行规则

1. **任何修改都必须先判层级**  
   - `L1`: 项目架构、跨模块、跨平台、全局契约  
   - `L2`: 单模块边界、模块接口、模块级重构  
   - `L3`: 操作/意图/配置契约、operation 编排  
   - `L4`: step/primitive/局部代码步骤
2. **混合变更按高层到低层修改**  
   - 修改顺序固定为 `L1 -> L2 -> L3 -> L4`
   - 验证顺序固定为 `L4 -> L3 -> L2 -> L1`
3. **禁止直接从代码开始**  
   - 不允许先改 `Modules/`、`Config/`、`ZDProjects/` 再补 `.omo`
4. **禁止先改 ZDProjects 入口再改业务主链**  
   - `ZDProjects/*_OwnCode.cs` 只能在模块接口确实变化后，最后同步
5. **配置优先**  
   - 能通过 `Config/ActionCatalog.json`、`Config/IntentMappings/*`、`Config/Operations/*` 解决的问题，不应先改 `Modules/`
6. **必须通过 Gate**  
   - 开始前跑 `Preflight`
   - 每完成一个文件跑 `Advance`
   - 结束前跑 `Postflight`
7. **不清楚时必须先提问**  
   - 若无法准确判定主层级、影响模块或验证范围，先用简短的苏格拉底式问题澄清，再开始编码

### 强制修改顺序

- `L1` 主变更：`.omo/current-task/plan.md` → `.omo/decisions_架构决策.md` → `.omo/layers/l1-project.yaml` → 受影响的 `.omo/layers/l2-module.yaml` / `.omo/layers/l3-operation.yaml` / `.omo/layers/l4-step.yaml` → `Config/` / `Modules/` / `Tools/` → `ZDProjects/` / 测试资产 → `CHANGELOG.md`
- `L2` 主变更：`.omo/current-task/plan.md` → `.omo/modules/{ModuleName}.md` → `.omo/layers/l2-module.yaml` → 受影响的 `l3/l4` 登记 → 模块源码 → 相关配置/入口/测试 → `CHANGELOG.md`
- `L3` 主变更：`.omo/current-task/plan.md` → `.omo/modules/{ModuleName}.md` → `.omo/layers/l3-operation.yaml` → `Config/ActionCatalog.json`（如动作语义变化）→ `Config/IntentMappings/*` → `Config/Operations/*` → `Modules/SessionRunner.cs` / `Modules/Core/IntentTranslator.cs` → 测试 → `CHANGELOG.md`
- `L4` 主变更：`.omo/current-task/plan.md` → `.omo/modules/{ModuleName}.md` → `.omo/layers/l4-step.yaml` → step 所属源码（通常是 `Modules/Core/ActionExecutor.cs` 或具体叶子模块）→ 受影响 operation 配置 → 测试 → `CHANGELOG.md`

### 强制验证规则

1. 修改 `Config/*.json`、`Config/Operations/*.json`、`Config/IntentMappings/*.json` 后，必须做 JSON 结构校验
2. 修改 `Modules/`、`Modules/Core/`、`ZDProjects/` 后，必须做针对性的编译或装载校验
3. 修改平台 operation / selector / intent 后，必须做对应平台的操作级验证；优先使用 `ZDProjects/RuntimeTestRunner.cs` 或 `ZDProjects/Tests/*`
4. 若当前环境不能运行 ZennoDroid / 设备测试，必须明确声明验证缺口，不能宣称“已完全验证”
5. `Postflight` 未通过时，不得结束任务

### 编码与优化硬规则

- **全层通用**
  - 先修根因，不做表面补丁
  - 保持向后兼容
  - 不新增重复逻辑；优先复用 `Modules/Core/*`
  - 所有 I/O 必须有错误处理
  - 变更说明必须落入 `.omo` 与 `CHANGELOG.md`
  - 不得跳过 Gate；`-NoStateWrite` 仅允许用于只读演练或沙箱限制

- **UTF-8 强制规则（全层通用）**

  所有源码文件、配置文件、日志输出必须使用 UTF-8 编码。以下 7 条为硬性要求：

  1. **源码文件编码** — 所有 `.cs` 文件保存为 **UTF-8 with BOM**（`EF BB BF`）。Visual Studio / ZennoDroid 编辑器默认行为，禁止手动改为 ANSI 或其他编码。

  2. **文件读写必须显式指定 UTF-8** — 禁止使用无编码参数的 `File.ReadAllText()` / `StreamReader` 等重载，必须显式传入 `Encoding.UTF8`。
     ```csharp
     // 正确
     string content = File.ReadAllText(path, Encoding.UTF8);
     File.WriteAllText(path, content, new UTF8Encoding(false)); // 无 BOM 写配置文件

     // 错误 — 依赖系统默认编码，跨平台不可靠
     string content = File.ReadAllText(path);
     ```

  3. **JSON / YAML 配置文件** — 一律使用 **UTF-8 without BOM**。写入时使用 `new UTF8Encoding(false)`。
     ```csharp
     // 写 JSON 配置
     File.WriteAllText(jsonPath, jsonString, new UTF8Encoding(false));
     ```

  4. **日志输出** — `project.SendInfoToLog()` 传入的字符串必须为合法 UTF-8。若包含外部来源数据，先做编码校验或转换：
     ```csharp
     // 安全做法：确保外部数据为 UTF-8
     byte[] raw = Encoding.UTF8.GetBytes(externalString);
     string safe = Encoding.UTF8.GetString(raw);
     project.SendInfoToLog(safe);
     ```

  5. **HTTP 请求与响应** — 发送和接收 HTTP 数据时，Content-Type 必须声明 `charset=utf-8`，响应体解码必须使用 UTF-8：
     ```csharp
     // 请求
     request.ContentType = "application/json; charset=utf-8";

     // 响应解码
     byte[] responseBytes = ...;
     string responseBody = Encoding.UTF8.GetString(responseBytes);
     ```

  6. **字符串拼接与模板** — 禁止在字符串中硬编码非 ASCII 字符的字节值。所有中文、特殊字符直接以 Unicode 字面量写入源码（源码本身已是 UTF-8 with BOM，编译器会正确处理）。
     ```csharp
     // 正确 — 直接写中文
     string msg = "操作成功";

     // 错误 — 手动拼字节
     string msg = Encoding.GetEncoding("GB2312").GetString(new byte[] { ... });
     ```

  7. **跨模块数据传递** — 模块间通过 `Dictionary<string, string>` 或 JSON 传递数据时，值必须为合法 UTF-8 字符串。禁止传递未经编码转换的 byte[] 或 Base64 编码的非 UTF-8 数据。

- **L1**
  - 先写架构决策，再动实现
  - 不允许隐式改变目录职责或主执行链
- **L2**
  - 模块职责单一，不能把 L3/L4 细节回灌到入口层
  - 不允许把 ZennoDroid 物理 API 直接散落到业务模块
- **L3**
  - 优先维护 action → intent → operation 契约一致性
  - operation 变化必须同步映射与验证
- **L4**
  - 最小改动，显式错误路径，禁止偷偷改变 primitive 语义
  - 新 step 若会影响 operation 契约，必须回写到 L3

### 根目录 .md 文件控制规则

根目录仅允许以下 .md 文件存在：

```
DPS_v4.5/
├── AGENTS.md          # AI 工具链配置（OpenCode 自动加载）
├── README.md          # 项目主页入口
└── CHANGELOG.md       # 版本变更记录（.omo.conf 要求）
```

**禁止在根目录新建任何 .md 文件**，包括但不限于：
- `*_REPORT.md` — AI 工作报告 → 存到 `.omo/history/`
- `*_GUIDE.md` / `*_PLAN.md` — 技术文档 → 合并到 `Docs/` 目录现有文件
- `*_INDEX.md` — 索引/导航 → 合并到 `.omo/AI_SESSION_GUIDE_AI会话指南.md`
- `*_Checklist.md` — 检查清单 → 合并到 `Docs/` 对应文档

**文档归属原则**:

| 文档类型 | 存放位置 | 说明 |
|---------|---------|------|
| 项目入口 | 根目录 `README.md` | 唯一的根目录文档入口 |
| 版本记录 | 根目录 `CHANGELOG.md` | .omo 工作流要求 |
| AI 工具配置 | 根目录 `AGENTS.md` | 工具链硬依赖 |
| 技术文档 | `Docs/` | 持久性技术文档，遵循 `Docs/DOCS_RULES.md` |
| 平台指南 | `Docs/Platforms/` | 按平台组织 |
| AI/工作流上下文 | `.omo/` | AI 会话恢复、架构决策、代码规范 |
| AI 工作报告 | `.omo/history/` | 一次性报告，按日期归档 |
| 子目录说明 | 各目录 `README.md` | 仅说明该目录用途 |

**AI 助手必须遵守**:
1. 不在根目录创建新 .md 文件
2. 任务报告写入 `.omo/history/{date}-{task}/` 目录
3. 技术文档更新合并到 `Docs/` 现有文件
4. 如确需新建根目录文件，必须先向用户提出请求并等待批准
