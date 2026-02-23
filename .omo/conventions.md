# DPS v4.5 代码规范 (conventions.md)

本文件定义了 DPS (Digital Persona System) v4.5 项目的代码规范。所有开发人员必须严格遵守，以确保在 ZennoDroid 环境中的兼容性和代码一致性。

## 1. C# 语法约束（最高优先级）

### Modules/*.cs 层：C# 5.0 严格限制
由于 ZennoDroid 内部编译环境限制，Modules 目录下的所有代码必须遵循 C# 5.0 语法。

| 禁止项 | ❌ 禁止 (C# 6.0+) | ✅ 替代方案 (C# 5.0) |
| :--- | :--- | :--- |
| **字符串插值** | `$"Hello {name}"` | `string.Format("Hello {0}", name)` 或拼接 |
| **Null 条件运算符** | `obj?.Property` | `if (obj != null) { ... obj.Property ... }` |
| **nameof 运算符** | `nameof(MyMethod)` | `"MyMethod"` (硬编码字符串) |
| **Null 合并赋值** | `x ??= y` | `if (x == null) x = y;` |
| **out 变量声明** | `int.TryParse(s, out var i)` | `int i; int.TryParse(s, out i);` |
| **模式匹配** | `if (o is string s)` | `string s = o as string; if (s != null)` |
| **本地函数** | `void Parent() { void Child() {} }` | 使用私有方法或委托 |
| **异常过滤** | `catch (Exception ex) when (...)` | `catch (Exception ex) { if (...) { ... } else throw; }` |
| **default 字面量** | `return default;` | `return default(string);` |
| **switch 表达式** | `var r = x switch { ... }` | 传统的 `switch (x) { case ... }` |
| **using 声明** | `using var stream = ...` | `using (var stream = ...) { ... }` |

### ZDProjects/*.cs 层：C# 7.0+
ZennoDroid 的 "OwnCode" 脚本层支持较新语法，允许使用字符串插值等特性。

---

## 2. 两层代码架构

### ZD 脚本层 (ZDProjects/, Core/, Platforms/)
*   **特点**：直接在 ZennoDroid 脚本编辑器中运行，无 `class` 包装。
*   **函数定义**：使用 `Func<...>` 或 `Action<...>` 委托定义。
*   **状态共享**：通过 `project.Variables` 或全局静态对象。

### 编译类层 (Modules/)
*   **特点**：标准 C# 5.0 静态类。
*   **入口**：必须包含 `public static string Run(object projectObj)`。

---

## 3. 命名约定

*   **类名**：`PascalCase` (例如: `SessionRunner`, `CoreHelper`)
*   **方法名**：`PascalCase` (例如: `Run`, `ExecuteStep`, `DeterminePlatform`)
*   **私有字段**：`_camelCase` (例如: `_project`, `_logTag`, `_threadRandom`)
*   **局部变量**：`camelCase` (例如: `projectRoot`, `configPath`)
*   **常量**：`UPPER_SNAKE` (例如: `TAG`, `MAX_CACHE_ENTRIES`, `MAX_FILE_LOCKS`)
*   **嵌套私有类**：`PascalCase` (例如: `SessionState`)
*   **文件命名**：`PascalCase.cs` (例如: `SessionRunner.cs`)
*   **ZD 入口文件**：以 `*_OwnCode.cs` 结尾。

---

## 4. 文件头格式
每个 .cs 文件必须包含以下头信息：

```csharp
// =====================================================
// FileName.cs - 简短功能描述
// ⚠️ C# 5.0 语法 - 禁止使用 $""、?.、nameof() 等
// v4.5.x - 变更记录/日期
// =====================================================
```

---

## 5. 模块入口签名
所有 Modules 层的核心入口必须遵循：

```csharp
public static string Run(object projectObj) { ... }
// 或者如果需要 instance 对象：
public static string Run(object projectObj, object instanceObj) { ... }
```

---

## 6. 初始化模式
模块内部初始化标准流程：

```csharp
private const string TAG = "MyModule";

public static string Run(object projectObj) {
    // 1. 初始化核心助手
    CoreHelper.Init(projectObj);
    
    // 2. 获取并校验必要变量
    string projectRoot = CoreHelper.GetVar("project_root", "");
    if (string.IsNullOrEmpty(projectRoot)) {
        CoreHelper.LogErr(TAG, "project_root 未设置");
        return "ERROR: project_root 未设置";
    }
    
    // 3. 路径规范化
    projectRoot = CoreHelper.NormalizePath(projectRoot);
    
    // ... 执行逻辑
}
```

---

## 7. 返回值约定

| 返回值 | 含义 |
| :--- | :--- |
| **"SUCCESS"** | 模块任务圆满成功 |
| **"ERROR: 原因"** | 模块级错误，停止当前流程 |
| **"SKIP"** | 步骤级跳过（非错误） |
| **"ABORT: 原因"** | 步骤级严重错误，建议中止整个任务 |
| **"OK"** | 原子步骤执行成功 |
| **"READY"** | 资源已就绪，可进行下一步 |
| **"SESSION_COMPLETE"** | 会话流程已全部结束 |

---

## 8. 日志约定

*   **模块层 (Modules)**：使用 `CoreHelper`。
    *   `CoreHelper.Log(TAG, msg)`
    *   `CoreHelper.LogWarn(TAG, msg)`
    *   `CoreHelper.LogErr(TAG, msg)`
*   **ZD 脚本层**：使用委托。
    *   `Log("msg")`
    *   `LogErr("msg")`
*   **TAG 定义**：`private const string TAG = "ModuleName";`

---

## 9. 字符串格式化

*   ✅ `string.Format("[{0}] {1}", tag, message)`
*   ✅ `string msg = "[" + tag + "] " + content;`
*   ✅ `StringBuilder` (用于复杂拼接，需引用 `System.Text`)
*   ❌ `$"{tag}: {content}"` (**Modules 层绝对禁止**)

---

## 10. JSON 操作
**严禁引用 Newtonsoft.Json**，统一使用项目自带的 `JsonHelper` (基于系统自带或轻量级实现)。

*   `JsonHelper.Get(json, "field", "default")`
*   `JsonHelper.GetNested(json, "parent.child", "default")`
*   `JsonHelper.ExtractObject(json, "section")`
*   `JsonHelper.GetInt(json, "field", 0)`
*   `JsonHelper.Set(json, "field", "value")`

---

## 11. 错误处理模式
必须包含 `try-catch` 块并记录错误至全局变量。

```csharp
try {
    // 业务逻辑
    return "SUCCESS";
} catch (Exception ex) {
    CoreHelper.LogErr(TAG, "异常: " + ex.Message);
    // 将错误记录回 ZD 变量供调试
    CoreHelper.SetVar("last_error", ex.Message); 
    return "ERROR: " + ex.Message;
}
```

---

## 12. 时间/计时模式

*   **当前时间**：`var now = System.DateTime.Now;`
*   **耗时计算**：
    ```csharp
    var startTime = System.DateTime.Now;
    // ...
    var duration = (int)(System.DateTime.Now - startTime).TotalMilliseconds;
    ```
*   **格式化**：`DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")`

---

## 13. 数值解析安全模式

*   ✅ `int.TryParse(v, out result)` — **推荐**。
*   ❌ `int.Parse(v)` — 禁止（除非百分百确定内容）。
*   **浮点数处理**：解析 double 时需考虑区域文化 (Culture)，防止 `,` 和 `.` 混淆。
    *   `double.TryParse(v, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out result)`

---

## 14. 配置读取模式

1.  **路径构建**：使用 Windows 反斜杠 `\`。
    `string configPath = projectRoot + "Config\\BehaviorConfig.json";`
2.  **安全性检查**：
    ```csharp
    if (!System.IO.File.Exists(configPath)) {
        CoreHelper.LogWarn(TAG, "配置文件缺失，使用默认值: " + configPath);
        return DefaultConfig;
    }
    ```
3.  **读取与解析**：
    `string content = CoreHelper.ReadFile(configPath);`
    `string val = JsonHelper.Get(content, "Key", "Default");`
