// =====================================================
// ExtensionManager.cs - 扩展管理器
// ⚠️ C# 5.0 语法 - 禁止使用 $""、?.、nameof() 等
// v1.0.0 - 注册、发现、执行扩展
// =====================================================
using System;
using System.IO;
using System.Text;
using System.Collections.Generic;

/// <summary>
/// 扩展管理器
/// 负责从 ExtensionsRegistry.json 加载扩展配置，
/// 注册扩展实例，按类别执行扩展
/// </summary>
public static class ExtensionManager
{
    private const string TAG = "ExtMgr";
    
    /// <summary>已注册的扩展列表</summary>
    private static List<IExtension> _extensions = new List<IExtension>();
    
    /// <summary>是否已初始化</summary>
    private static bool _initialized = false;
    
    /// <summary>
    /// 注册一个扩展实例
    /// </summary>
    public static void Register(IExtension extension)
    {
        if (extension == null) return;
        
        // 防止重复注册
        for (int i = 0; i < _extensions.Count; i++)
        {
            if (_extensions[i].Name == extension.Name)
            {
                CoreHelper.LogWarn(TAG, "扩展已存在，跳过重复注册: " + extension.Name);
                return;
            }
        }
        
        _extensions.Add(extension);
        CoreHelper.Log(TAG, "注册扩展: " + extension.Name + " [" + extension.Category + "] v" + extension.Version);
    }
    
    /// <summary>
    /// 从 ExtensionsRegistry.json 加载并注册所有扩展
    /// </summary>
    /// <param name="projectObj">ZD project 对象</param>
    /// <param name="projectRoot">项目根目录</param>
    public static void LoadFromRegistry(object projectObj, string projectRoot)
    {
        if (_initialized)
        {
            CoreHelper.Log(TAG, "已初始化，跳过重复加载");
            return;
        }
        
        string registryPath = projectRoot + "Config\\ExtensionsRegistry.json";
        if (!File.Exists(registryPath))
        {
            CoreHelper.Log(TAG, "ExtensionsRegistry.json 不存在，跳过扩展加载");
            _initialized = true;
            return;
        }
        
        string registryJson = CoreHelper.ReadFile(registryPath);
        string extensionsArray = JsonHelper.ExtractArray(registryJson, "extensions");
        
        if (string.IsNullOrEmpty(extensionsArray))
        {
            CoreHelper.Log(TAG, "注册表中无扩展定义");
            _initialized = true;
            return;
        }
        
        // 解析扩展数组中的每个对象
        string[] elements = ParseObjectArray(extensionsArray);
        int loaded = 0;
        int skipped = 0;
        
        foreach (string extJson in elements)
        {
            string name = JsonHelper.Get(extJson, "name");
            string enabled = JsonHelper.Get(extJson, "enabled");
            string configJson = JsonHelper.ExtractObject(extJson, "config");
            
            if (string.IsNullOrEmpty(name))
            {
                CoreHelper.LogWarn(TAG, "扩展定义缺少 name 字段，跳过");
                skipped++;
                continue;
            }
            
            if (enabled == "false")
            {
                CoreHelper.Log(TAG, "扩展已禁用: " + name);
                skipped++;
                continue;
            }
            
            // 通过名称查找已注册的扩展实例并初始化
            IExtension ext = GetByName(name);
            if (ext != null)
            {
                try
                {
                    ext.Initialize(projectObj, string.IsNullOrEmpty(configJson) ? "{}" : configJson);
                    loaded++;
                    CoreHelper.Log(TAG, "初始化扩展: " + name);
                }
                catch (Exception ex)
                {
                    CoreHelper.LogErr(TAG, "初始化扩展失败 [" + name + "]: " + ex.Message);
                    skipped++;
                }
            }
            else
            {
                CoreHelper.LogWarn(TAG, "扩展未注册（代码中未 Register）: " + name);
                skipped++;
            }
        }
        
        _initialized = true;
        CoreHelper.Log(TAG, string.Format("扩展加载完成: {0} 已加载, {1} 跳过, 总注册 {2}", 
            loaded, skipped, _extensions.Count));
    }
    
    /// <summary>
    /// 按名称获取扩展
    /// </summary>
    public static IExtension GetByName(string name)
    {
        for (int i = 0; i < _extensions.Count; i++)
        {
            if (_extensions[i].Name == name)
            {
                return _extensions[i];
            }
        }
        return null;
    }
    
    /// <summary>
    /// 获取指定类别的所有已启用扩展
    /// </summary>
    public static List<IExtension> GetByCategory(string category)
    {
        var result = new List<IExtension>();
        for (int i = 0; i < _extensions.Count; i++)
        {
            if (_extensions[i].Category == category && _extensions[i].Enabled)
            {
                result.Add(_extensions[i]);
            }
        }
        return result;
    }
    
    /// <summary>
    /// 执行指定类别的所有已启用扩展
    /// </summary>
    /// <returns>执行结果摘要: "N/M succeeded"</returns>
    public static string RunCategory(string category, object projectObj)
    {
        var exts = GetByCategory(category);
        if (exts.Count == 0)
        {
            CoreHelper.Log(TAG, "无 [" + category + "] 类别的扩展");
            return "0/0 succeeded";
        }
        
        int success = 0;
        int total = exts.Count;
        
        for (int i = 0; i < exts.Count; i++)
        {
            try
            {
                string result = exts[i].Run(projectObj);
                if (result == "SUCCESS")
                {
                    success++;
                }
                else
                {
                    CoreHelper.LogWarn(TAG, "扩展 [" + exts[i].Name + "] 返回: " + result);
                }
            }
            catch (Exception ex)
            {
                CoreHelper.LogErr(TAG, "扩展 [" + exts[i].Name + "] 执行异常: " + ex.Message);
            }
        }
        
        string summary = string.Format("{0}/{1} succeeded", success, total);
        CoreHelper.Log(TAG, "[" + category + "] 执行结果: " + summary);
        return summary;
    }
    
    /// <summary>
    /// 执行指定名称的单个扩展
    /// </summary>
    public static string RunByName(string name, object projectObj)
    {
        IExtension ext = GetByName(name);
        if (ext == null)
        {
            CoreHelper.LogErr(TAG, "扩展不存在: " + name);
            return "ERROR: 扩展不存在";
        }
        
        if (!ext.Enabled)
        {
            CoreHelper.Log(TAG, "扩展已禁用: " + name);
            return "SKIPPED: 已禁用";
        }
        
        try
        {
            return ext.Run(projectObj);
        }
        catch (Exception ex)
        {
            CoreHelper.LogErr(TAG, "扩展 [" + name + "] 执行异常: " + ex.Message);
            return "ERROR: " + ex.Message;
        }
    }
    
    /// <summary>
    /// 获取所有已注册扩展的状态摘要
    /// </summary>
    public static string GetStatusSummary()
    {
        var sb = new StringBuilder();
        sb.Append("{\"total\": " + _extensions.Count);
        sb.Append(", \"initialized\": " + (_initialized ? "true" : "false"));
        sb.Append(", \"extensions\": [");
        
        for (int i = 0; i < _extensions.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append("{\"name\": \"" + _extensions[i].Name + "\"");
            sb.Append(", \"category\": \"" + _extensions[i].Category + "\"");
            sb.Append(", \"enabled\": " + (_extensions[i].Enabled ? "true" : "false"));
            sb.Append(", \"version\": \"" + _extensions[i].Version + "\"}");
        }
        
        sb.Append("]}");
        return sb.ToString();
    }
    
    /// <summary>
    /// 重置管理器状态（用于测试或重新加载）
    /// </summary>
    public static void Reset()
    {
        _extensions.Clear();
        _initialized = false;
        CoreHelper.Log(TAG, "扩展管理器已重置");
    }
    
    /// <summary>
    /// 解析 JSON 数组中的对象元素
    /// 返回每个对象的 JSON 字符串
    /// </summary>
    private static string[] ParseObjectArray(string arrayJson)
    {
        var results = new List<string>();
        if (string.IsNullOrEmpty(arrayJson)) return results.ToArray();
        
        // 跳过开头的 [
        int i = 0;
        while (i < arrayJson.Length && arrayJson[i] != '[') i++;
        if (i >= arrayJson.Length) return results.ToArray();
        i++; // 跳过 [
        
        while (i < arrayJson.Length)
        {
            // 跳过空白和逗号
            while (i < arrayJson.Length && (char.IsWhiteSpace(arrayJson[i]) || arrayJson[i] == ',')) i++;
            
            if (i >= arrayJson.Length || arrayJson[i] == ']') break;
            
            if (arrayJson[i] == '{')
            {
                // 找到对象开始，提取完整对象
                int depth = 1;
                int start = i;
                i++;
                
                while (i < arrayJson.Length && depth > 0)
                {
                    char c = arrayJson[i];
                    if (c == '"')
                    {
                        // 跳过字符串
                        i++;
                        while (i < arrayJson.Length)
                        {
                            if (arrayJson[i] == '\\' && i + 1 < arrayJson.Length)
                            {
                                i += 2;
                            }
                            else if (arrayJson[i] == '"')
                            {
                                i++;
                                break;
                            }
                            else
                            {
                                i++;
                            }
                        }
                    }
                    else if (c == '{')
                    {
                        depth++;
                        i++;
                    }
                    else if (c == '}')
                    {
                        depth--;
                        i++;
                    }
                    else
                    {
                        i++;
                    }
                }
                
                results.Add(arrayJson.Substring(start, i - start));
            }
            else
            {
                i++;
            }
        }
        
        return results.ToArray();
    }
}
