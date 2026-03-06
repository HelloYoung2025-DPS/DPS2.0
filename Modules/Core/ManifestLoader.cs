// =====================================================
// ManifestLoader.cs - Manifest JSON 文件加载和验证
// ⚠️ C# 5.0 语法 - 禁止使用 $""、?.、nameof() 等
// v4.5.0 - 初始版本
// =====================================================
using System;
using System.IO;

/// <summary>
/// Manifest 加载器
/// 负责从文件系统加载 manifest JSON 并验证必需字段
/// </summary>
public class ManifestLoader
{
    private const string TAG = "ManifestLoader";
    
    /// <summary>
    /// 加载指定 appId 的 manifest 文件
    /// 从 Configs/Manifests/{appId}.json 读取
    /// </summary>
    /// <param name="appId">应用标识符（如 reddit, instagram）</param>
    /// <returns>JSON 字符串，失败时返回以 "ERROR:" 开头的错误信息</returns>
    public static string Load(string appId)
    {
        try
        {
            // 验证 appId 参数
            if (string.IsNullOrEmpty(appId))
            {
                CoreHelper.LogErr(TAG, "appId 参数为空");
                return "ERROR: appId cannot be empty";
            }
            
            // 构建 manifest 文件路径
            string manifestPath = GetManifestPath(appId);
            
            // 检查文件是否存在
            if (!File.Exists(manifestPath))
            {
                CoreHelper.LogErr(TAG, "Manifest 文件不存在: " + manifestPath);
                return "ERROR: Manifest file not found: " + manifestPath;
            }
            
            // 读取文件内容
            string manifestJson = FileHelper.Read(manifestPath);
            
            // 检查读取是否成功
            if (string.IsNullOrEmpty(manifestJson))
            {
                CoreHelper.LogErr(TAG, "Manifest 文件为空或读取失败: " + manifestPath);
                return "ERROR: Failed to read manifest file: " + manifestPath;
            }
            
            // 验证 JSON 格式
            if (!JsonHelper.IsValidJson(manifestJson))
            {
                CoreHelper.LogErr(TAG, "Manifest JSON 格式无效: " + appId);
                return "ERROR: Invalid JSON format";
            }
            
            // 验证必需字段
            string validationResult = Validate(manifestJson);
            if (validationResult != null)
            {
                CoreHelper.LogErr(TAG, "Manifest 验证失败: " + validationResult);
                return validationResult;
            }
            
            CoreHelper.Log(TAG, "成功加载 manifest: " + appId);
            return manifestJson;
        }
        catch (Exception ex)
        {
            CoreHelper.LogErr(TAG, "加载 manifest 异常 [" + appId + "]: " + ex.Message);
            return "ERROR: Exception loading manifest: " + ex.Message;
        }
    }
    
    /// <summary>
    /// 验证 manifest JSON 包含所有必需字段
    /// 必需字段：app_id, package, screens, selectors, operations
    /// </summary>
    /// <param name="manifestJson">待验证的 JSON 字符串</param>
    /// <returns>null 表示验证通过，否则返回以 "ERROR:" 开头的错误信息</returns>
    public static string Validate(string manifestJson)
    {
        if (string.IsNullOrEmpty(manifestJson))
        {
            return "ERROR: Manifest JSON is empty";
        }
        
        // 定义必需字段列表
        string[] requiredFields = new string[] 
        { 
            "app_id", 
            "package", 
            "screens", 
            "selectors", 
            "operations" 
        };
        
        // 逐个检查必需字段
        foreach (string field in requiredFields)
        {
            string value = JsonHelper.Get(manifestJson, field);
            
            // JsonHelper.Get 返回 null 表示字段不存在
            if (value == null)
            {
                return "ERROR: Missing required field: " + field;
            }
        }
        
        // 所有必需字段都存在
        return null;
    }
    
    /// <summary>
    /// 构建 manifest 文件的完整路径
    /// 路径格式：{projectRoot}\Configs\Manifests\{appId}.json
    /// </summary>
    /// <param name="appId">应用标识符</param>
    /// <returns>manifest 文件的完整路径</returns>
    public static string GetManifestPath(string appId)
    {
        // 从 project 变量获取项目根目录
        string projectRoot = CoreHelper.GetVar("project_root", "");
        
        // 如果未设置 project_root，尝试从其他来源获取
        if (string.IsNullOrEmpty(projectRoot))
        {
            CoreHelper.LogWarn(TAG, "project_root 变量未设置，使用当前目录");
            projectRoot = Directory.GetCurrentDirectory();
        }
        
        // 规范化路径（确保以反斜杠结尾）
        projectRoot = CoreHelper.NormalizePath(projectRoot);
        
        // 拼接路径：projectRoot + Configs\Manifests\ + appId + .json
        string manifestPath = projectRoot + "Configs\\Manifests\\" + appId + ".json";
        
        return manifestPath;
    }
}
