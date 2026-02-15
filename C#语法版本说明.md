# C# 语法版本说明

## 概述

DPS v4.5 项目使用两种不同的 C# 环境，它们支持不同的语法版本：

| 环境 | C# 版本 | 编译器 | 位置 |
|------|---------|--------|------|
| **Own Code** | ~7.0+ | ZD内置 | `ZDProjects/*.cs` |
| **外部模块** | ~5.0 | CSharpCodeProvider | `Modules/*.cs` |

---

## 为什么会有版本差异？

1. **Own Code** 使用 ZennoDroid 内置的编译器，支持较新的 C# 语法
2. **外部 .cs 文件** 使用 `CSharpCodeProvider` 动态编译，它基于旧版 .NET Framework 编译器

---

## Own Code 可用语法（C# 7.0+）

```csharp
// ✅ 字符串插值
string msg = $"用户: {userName}, 年龄: {age}";

// ✅ null 条件运算符（需测试）
// string name = user?.Name ?? "未知";

// ✅ 表达式体成员
Func<int, int> Double = x => x * 2;

// ✅ var 关键字
var list = new System.Collections.Generic.List<string>();
```

---

## 外部模块禁用语法（C# 6.0+）

### ❌ 字符串插值
```csharp
// ❌ 禁止
string msg = $"用户: {name}";

// ✅ 替代方案
string msg = "用户: " + name;
string msg = string.Format("用户: {0}", name);
```

### ❌ Null 条件运算符
```csharp
// ❌ 禁止
string name = user?.Name;

// ✅ 替代方案
string name = null;
if (user != null)
{
    name = user.Name;
}
```

### ❌ nameof 运算符
```csharp
// ❌ 禁止
throw new ArgumentNullException(nameof(parameter));

// ✅ 替代方案
throw new ArgumentNullException("parameter");
```

### ❌ 异常过滤器
```csharp
// ❌ 禁止
catch (Exception ex) when (ex.Message.Contains("timeout"))

// ✅ 替代方案
catch (Exception ex)
{
    if (ex.Message.Contains("timeout"))
    {
        // 处理
    }
}
```

### ❌ 模式匹配
```csharp
// ❌ 禁止
if (obj is string s)
{
    Console.WriteLine(s);
}

// ✅ 替代方案
if (obj is string)
{
    string s = (string)obj;
    Console.WriteLine(s);
}
```

### ❌ out 变量声明
```csharp
// ❌ 禁止
if (int.TryParse(str, out int result))

// ✅ 替代方案
int result;
if (int.TryParse(str, out result))
```

### ❌ 本地函数
```csharp
// ❌ 禁止
void OuterMethod()
{
    void LocalFunction() { }
}

// ✅ 替代方案（外部模块中已经是这种模式）
Func<string> LocalFunction = () => { return ""; };
```

---

## 完整禁用列表

| 语法 | C# 版本 | 替代方案 |
|------|---------|----------|
| `$""` 字符串插值 | 6.0 | `+` 或 `string.Format()` |
| `?.` 空条件 | 6.0 | `if (x != null)` |
| `nameof()` | 6.0 | 直接写字符串 |
| `when` 异常过滤 | 6.0 | `catch` 内 `if` |
| `is T x` 模式匹配 | 7.0 | 先 `is` 后强转 |
| `(a, b)` 元组解构 | 7.0 | 分别赋值 |
| `out int x` | 7.0 | 先声明后传入 |
| 本地函数 | 7.0 | `Func<>` 委托 |
| `throw` 表达式 | 7.0 | `if-throw` |
| `default` 字面量 | 7.1 | `default(T)` |
| `??=` | 8.0 | `if (x == null) x = y;` |
| `switch` 表达式 | 8.0 | 传统 `switch` |
| `using` 声明 | 8.0 | `using () { }` 块 |
| `^` 索引 | 8.0 | `arr[arr.Length - 1]` |
| `..` 范围 | 8.0 | 手动切片 |

---

## 检查工具

编译时如果使用了禁止的语法，会看到类似错误：

```
[模块名] 编译错误 行42: Feature '字符串插值' is not available in C# 5. Please use language version 6 or greater.
```

遇到此类错误时，请参照上表进行语法替换。
