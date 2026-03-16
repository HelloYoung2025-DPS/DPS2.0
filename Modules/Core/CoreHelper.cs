// =====================================================
// CoreHelper.cs - 核心辅助函数库
// 供其他模块调用的公共函数
// ⚠️ C# 5.0 语法 - 禁止使用 $""、?.、nameof() 等
// v4.0.1 - 修复 WriteFileAtomic、添加 CountOccurrences、ValidateDeviceId
// =====================================================
using System;
using System.IO;
using System.Text;
using System.Collections.Generic;

/// <summary>
/// 核心辅助函数库
/// 所有方法都是静态的，接收 project 对象作为第一个参数
/// </summary>
public class CoreHelper
{
    private static dynamic _project;
    private static dynamic _instance;
    
    // 虚拟变量存储：当 ZD 变量未预建时的回退存储
    // SetVar 写入 ZD 变量失败时自动写入此字典
    // GetVar 在 ZD 变量为空时自动查询此字典
    private static Dictionary<string, string> _virtualVars = new Dictionary<string, string>();
    
    /// <summary>
    /// 初始化辅助函数库（仅 project）
    /// </summary>
    public static void Init(object projectObj)
    {
        _project = projectObj;
    }
    
    /// <summary>
    /// 初始化辅助函数库（project + ZD instance）
    /// </summary>
    public static void Init(object projectObj, object instanceObj)
    {
        _project = projectObj;
        _instance = instanceObj;
    }
    
    /// <summary>
    /// 单独注入 ZD instance（供 ModuleLoader 在 Init 之后调用）
    /// </summary>
    public static void InitInstance(object instanceObj)
    {
        _instance = instanceObj;
    }
    
    /// <summary>
    /// 获取 DroidInstance 对象
    /// </summary>
    public static dynamic GetDroid()
    {
        if (_instance == null)
        {
            LogErr("CoreHelper", "DroidInstance 未初始化，请先调用 InitInstance()");
            return null;
        }
        return _instance.DroidInstance;
    }
    
    /// <summary>
    /// 获取 DroidInstance.Input（点击、滑动等输入操作）
    /// </summary>
    public static dynamic GetInput()
    {
        dynamic droid = GetDroid();
        if (droid == null) return null;
        return droid.Input;
    }
    
    /// <summary>
    /// 获取 DroidInstance.Hierarchy（UI 层级树）
    /// </summary>
    public static dynamic GetHierarchy()
    {
        dynamic droid = GetDroid();
        if (droid == null) return null;
        return droid.Hierarchy;
    }
    
    /// <summary>
    /// 获取 DroidInstance.App（应用管理）
    /// </summary>
    public static dynamic GetApp()
    {
        dynamic droid = GetDroid();
        if (droid == null) return null;
        return droid.App;
    }
    
    /// <summary>
    /// 便捷方法：获取当前 UI 布局 XML
    /// </summary>
    public static string GetLayout()
    {
        dynamic hierarchy = GetHierarchy();
        if (hierarchy == null) return "";
        try
        {
            return hierarchy.GetLayout();
        }
        catch (Exception ex)
        {
            LogErr("CoreHelper", "GetLayout 失败: " + ex.Message);
            return "";
        }
    }
    
    /// <summary>
    /// 检查 instance 是否已初始化
    /// </summary>
    public static bool HasInstance()
    {
        return _instance != null;
    }
    
    // ========== 日志函数 ==========
    
    public static void Log(string message)
    {
        if (_project != null)
        {
            _project.SendInfoToLog(message);
        }
    }
    
    public static void Log(string tag, string message)
    {
        Log(string.Format("[{0}] {1}", tag, message));
    }
    
    public static void LogWarn(string message)
    {
        if (_project != null)
        {
            _project.SendWarningToLog(message);
        }
    }
    
    public static void LogWarn(string tag, string message)
    {
        LogWarn(string.Format("[{0}] {1}", tag, message));
    }
    
    public static void LogErr(string message)
    {
        if (_project != null)
        {
            _project.SendErrorToLog(message);
        }
    }
    
    public static void LogErr(string tag, string message)
    {
        LogErr(string.Format("[{0}] {1}", tag, message));
    }
    
    // ========== 变量函数 ==========
    
    /// <summary>
    /// 获取变量值
    /// 优先读取 ZD 变量，若为空则回退到虚拟变量存储
    /// </summary>
    public static string GetVar(string name, string defaultValue)
    {
        // 先尝试从 ZD 变量读取
        try
        {
            string v = _project.Variables[name].Value;
            if (!string.IsNullOrEmpty(v))
            {
                return v;
            }
        }
        catch
        {
            // ZD 变量不存在，继续尝试虚拟变量
        }
        
        // 回退到虚拟变量存储
        if (_virtualVars.ContainsKey(name))
        {
            string vv = _virtualVars[name];
            if (!string.IsNullOrEmpty(vv))
            {
                return vv;
            }
        }
        
        return defaultValue;
    }
    
    /// <summary>
    /// 设置变量值
    /// 同时写入 ZD 变量和虚拟变量存储，确保即使 ZD 变量未预建也不丢失
    /// </summary>
    public static void SetVar(string name, string value)
    {
        string safeValue = value ?? "";
        
        // 始终写入虚拟变量（保底存储）
        _virtualVars[name] = safeValue;
        
        // 尝试写入 ZD 变量
        try
        {
            _project.Variables[name].Value = safeValue;
        }
        catch
        {
            // ZD 变量不存在时静默忽略，值已保存到虚拟变量
        }
    }
    
    /// <summary>
    /// 获取整数变量
    /// </summary>
    public static int GetVarInt(string name, int defaultValue)
    {
        string v = GetVar(name, "");
        int result;
        if (int.TryParse(v, out result))
        {
            return result;
        }
        return defaultValue;
    }
    
    /// <summary>
    /// 获取布尔变量
    /// </summary>
    public static bool GetVarBool(string name, bool defaultValue)
    {
        string v = GetVar(name, "").ToLower();
        if (v == "true" || v == "1" || v == "yes") return true;
        if (v == "false" || v == "0" || v == "no") return false;
        return defaultValue;
    }
    
    // ========== 文件函数 ==========
    
    /// <summary>
    /// 确保目录存在
    /// </summary>
    public static void EnsureDir(string dirPath)
    {
        if (string.IsNullOrEmpty(dirPath))
        {
            return;
        }
        
        try
        {
            if (!Directory.Exists(dirPath))
            {
                Directory.CreateDirectory(dirPath);
            }
        }
        catch (Exception ex)
        {
            LogErr("CoreHelper", "EnsureDir 失败 [" + dirPath + "]: " + ex.Message);
        }
    }
    
    /// <summary>
    /// 读取文件内容
    /// </summary>
    public static string ReadFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return "";
            }
            return File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            LogErr("CoreHelper", "ReadFile 失败 [" + path + "]: " + ex.Message);
            return "";
        }
    }
    
    /// <summary>
    /// 写入文件（自动创建目录）
    /// </summary>
    public static void WriteFile(string path, string content)
    {
        try
        {
            string dir = Path.GetDirectoryName(path);
            EnsureDir(dir);
            File.WriteAllText(path, content, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            LogErr("CoreHelper", "WriteFile 失败 [" + path + "]: " + ex.Message);
        }
    }
    
    /// <summary>
    /// 追加文件内容
    /// </summary>
    public static void AppendFile(string path, string content)
    {
        try
        {
            string dir = Path.GetDirectoryName(path);
            EnsureDir(dir);
            File.AppendAllText(path, content, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            LogErr("CoreHelper", "AppendFile 失败 [" + path + "]: " + ex.Message);
        }
    }
    
    /// <summary>
    /// 安全写入（原子操作，带异常处理）
    /// </summary>
    public static void WriteFileAtomic(string path, string content)
    {
        string dir = Path.GetDirectoryName(path);
        EnsureDir(dir);
        
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, content, Encoding.UTF8);
        
        try
        {
            if (File.Exists(path))
            {
                File.Replace(tmp, path, path + ".bak", true);
            }
            else
            {
                File.Move(tmp, path);
            }
        }
        catch (Exception replaceEx)
        {
            // File.Replace 失败时回退到直接覆盖
            // 常见原因：跨卷操作、权限问题、文件被锁定
            if (File.Exists(tmp))
            {
                File.Copy(tmp, path, true);
                try { File.Delete(tmp); } catch (Exception delEx) { /* 临时文件删除失败可忽略，不影响主逻辑 */ }
            }
        }
    }
    
    // ========== JSON 函数（手动解析）==========
    
    /// <summary>
    /// 获取 JSON 字段值
    /// </summary>
    public static string JGet(string json, string key)
    {
        return JsonHelper.Get(json, key);
    }
    
    /// <summary>
    /// 获取嵌套 JSON 字段值（如 "_meta.created_at"）
    /// </summary>
    public static string JGetNested(string json, string path)
    {
        return JsonHelper.GetNested(json, path);
    }
    
    /// <summary>
    /// 设置 JSON 字段值
    /// </summary>
    public static string JSet(string json, string key, string newValue, bool isString)
    {
        return JsonHelper.Set(json, key, newValue, isString);
    }
    
    // ========== 时间函数 ==========
    
    /// <summary>
    /// 获取今天日期 (yyyy-MM-dd)
    /// </summary>
    public static string GetToday()
    {
        return DateTime.Now.ToString("yyyy-MM-dd");
    }
    
    /// <summary>
    /// 获取当前时间戳 (yyyy-MM-dd HH:mm:ss)
    /// </summary>
    public static string GetNow()
    {
        return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
    }
    
    /// <summary>
    /// 获取ISO时间戳
    /// </summary>
    public static string GetNowISO()
    {
        return DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
    }
    
    // ========== 工具函数 ==========
    
    /// <summary>
    /// 安全解析整数
    /// </summary>
    public static int SafeParseInt(string s, int defaultValue)
    {
        int result;
        if (int.TryParse(s, out result))
        {
            return result;
        }
        return defaultValue;
    }
    
    /// <summary>
    /// 安全解析双精度
    /// </summary>
    public static double SafeParseDouble(string s, double defaultValue)
    {
        double result;
        // BUG-03 fix: 使用 InvariantCulture 确保 "0.5" 在任何 locale 下都正确解析
        if (double.TryParse(s, System.Globalization.NumberStyles.Float | System.Globalization.NumberStyles.AllowLeadingSign,
            System.Globalization.CultureInfo.InvariantCulture, out result))
        {
            return result;
        }
        return defaultValue;
    }
    
    /// <summary>
    /// 规范化路径（确保以反斜杠结尾）
    /// </summary>
    public static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        return path.EndsWith("\\") ? path : path + "\\";
    }
    
    /// <summary>
    /// 计算字符串出现次数
    /// </summary>
    public static int CountOccurrences(string text, string pattern)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(pattern))
            return 0;
        
        int count = 0;
        int idx = 0;
        while ((idx = text.IndexOf(pattern, idx)) != -1)
        {
            count++;
            idx++;
        }
        return count;
    }
    
    /// <summary>
    /// 返回第一个非空字符串（按顺序兜底）
    /// </summary>
    public static string FirstNonEmpty(params string[] values)
    {
        if (values == null) return "";
        for (int i = 0; i < values.Length; i++)
        {
            if (!string.IsNullOrEmpty(values[i]))
            {
                return values[i];
            }
        }
        return "";
    }
    
    /// <summary>
    /// 将 "1.2k"/"3,421" 等数字文本标准化为 JSON 数字字符串
    /// </summary>
    public static string NormalizeCountForPostJson(string raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return "";
        }

        string s = raw.Trim().ToLowerInvariant().Replace(",", "");
        double multiplier = 1.0;
        if (s.EndsWith("k"))
        {
            multiplier = 1000.0;
            s = s.Substring(0, s.Length - 1);
        }
        else if (s.EndsWith("m"))
        {
            multiplier = 1000000.0;
            s = s.Substring(0, s.Length - 1);
        }

        StringBuilder numeric = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if ((c >= '0' && c <= '9') || c == '.')
            {
                numeric.Append(c);
            }
        }

        if (numeric.Length == 0)
        {
            return "";
        }

        double val;
        if (double.TryParse(numeric.ToString(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out val))
        {
            long n = (long)Math.Round(val * multiplier);
            if (n < 0) n = 0;
            return n.ToString();
        }

        return "";
    }
    
    /// <summary>
    /// 验证设备ID是否安全（防止路径遍历攻击）
    /// </summary>
    public static bool ValidateDeviceId(string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
            return false;
        
        // 检查路径遍历字符
        if (deviceId.Contains("..") || deviceId.Contains("/") || 
            deviceId.Contains("\\") || deviceId.Contains(":"))
            return false;
        
        // 检查非法文件名字符
        char[] invalidChars = Path.GetInvalidFileNameChars();
        foreach (char c in invalidChars)
        {
            if (deviceId.IndexOf(c) >= 0)
                return false;
        }
        
        return true;
    }
    
    /// <summary>
    /// 获取安全的设备ID（如果无效则返回默认值）
    /// </summary>
    public static string GetSafeDeviceId(string deviceId, string defaultValue)
    {
        return ValidateDeviceId(deviceId) ? deviceId : defaultValue;
    }
}
