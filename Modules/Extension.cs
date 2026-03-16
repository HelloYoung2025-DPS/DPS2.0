// =====================================================
// Extension.cs - 扩展功能模块（重构版）
// ⚠️ C# 5.0 语法 - 禁止使用 $""、?.、nameof() 等
// v4.5.0 - 使用 ExtensionManager 统一管理扩展
// =====================================================
using System;
using System.IO;

/// <summary>
/// 扩展功能模块
/// 通过 ExtensionManager 注册并执行所有扩展
/// </summary>
public class Extension
{
    private static dynamic _project;
    private const string TAG = "Extension";
    
    /// <summary>
    /// 模块入口点
    /// </summary>
    public static string Run(object projectObj)
    {
        _project = projectObj;
        
        try
        {
            CoreHelper.Init(projectObj);
            
            CoreHelper.Log(TAG, "========================================");
            CoreHelper.Log(TAG, "  扩展功能检查 (v4.5 ExtensionManager)");
            CoreHelper.Log(TAG, "========================================");
            
            string projectRoot = CoreHelper.GetVar("project_root", "");
            if (string.IsNullOrEmpty(projectRoot))
            {
                CoreHelper.LogErr(TAG, "project_root 未设置");
                CoreHelper.SetVar("extension_result", "ERROR");
                return "ERROR: project_root 未设置";
            }
            
            projectRoot = CoreHelper.NormalizePath(projectRoot);
            
            // 注册所有内置扩展
            RegisterBuiltinExtensions();
            
            // 从注册表加载配置并初始化
            ExtensionManager.LoadFromRegistry(projectObj, projectRoot);
            
            // 执行所有 DataSource 类别扩展
            string dataResult = ExtensionManager.RunCategory("DataSource", projectObj);
            CoreHelper.Log(TAG, "DataSource 扩展结果: " + dataResult);
            
            // 执行所有 Hook 类别扩展（未来扩展）
            ExtensionManager.RunCategory("Hook", projectObj);
            
            // 输出状态摘要
            string summary = ExtensionManager.GetStatusSummary();
            CoreHelper.Log(TAG, "扩展状态: " + summary);
            
            CoreHelper.Log(TAG, "扩展功能检查完成");
            CoreHelper.SetVar("extension_result", "SUCCESS");
            return "SUCCESS";
        }
        catch (Exception ex)
        {
            CoreHelper.LogErr(TAG, "异常: " + ex.Message);
            CoreHelper.SetVar("last_error", ex.Message);
            CoreHelper.SetVar("extension_result", "ERROR");
            return "ERROR: " + ex.Message;
        }
    }
    
    /// <summary>
    /// 注册所有内置扩展实例
    /// 新增扩展时在此处添加 Register 调用
    /// </summary>
    private static void RegisterBuiltinExtensions()
    {
        TryRegisterBuiltinExtension("IPLocationExtension");
        TryRegisterBuiltinExtension("WeatherExtension");
        
        CoreHelper.Log(TAG, "内置扩展注册完成");
    }

    private static void TryRegisterBuiltinExtension(string typeName)
    {
        if (string.IsNullOrEmpty(typeName))
        {
            return;
        }

        Type extensionType = FindType(typeName);
        if (extensionType == null)
        {
            CoreHelper.LogWarn(TAG, "未找到内置扩展类型: " + typeName);
            return;
        }

        if (!typeof(IExtension).IsAssignableFrom(extensionType))
        {
            CoreHelper.LogWarn(TAG, "扩展类型未实现 IExtension: " + typeName);
            return;
        }

        try
        {
            IExtension extension = (IExtension)Activator.CreateInstance(extensionType);
            ExtensionManager.Register(extension);
        }
        catch (Exception ex)
        {
            CoreHelper.LogWarn(TAG, "注册扩展失败 " + typeName + ": " + ex.Message);
        }
    }

    private static Type FindType(string typeName)
    {
        Type type = Type.GetType(typeName);
        if (type != null)
        {
            return type;
        }

        System.Reflection.Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
        int i = 0;
        for (i = 0; i < assemblies.Length; i++)
        {
            type = assemblies[i].GetType(typeName);
            if (type != null)
            {
                return type;
            }
        }

        return null;
    }
}
