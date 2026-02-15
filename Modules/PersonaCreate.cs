// =====================================================
// PersonaCreate.cs - 画像生成模块
// ⚠️ C# 5.0 语法 - 禁止使用 $""、?.、nameof() 等
// ⚠️ 从 AIConfig.json 动态读取 API 配置
// =====================================================
using System;
using System.IO;
using System.Text;

/// <summary>
/// 画像生成模块
/// 使用 AI 生成用户画像
/// </summary>
public class PersonaCreate
{
    private static dynamic _project;
    private const string TAG = "PersonaCreate";
    
    /// <summary>
    /// 模块入口点
    /// </summary>
    public static string Run(object projectObj)
    {
        _project = projectObj;
        
        try
        {
            CoreHelper.Init(projectObj);
            
            string projectRoot = CoreHelper.GetVar("project_root", "");
            string deviceId = CoreHelper.GetVar("device_id", "");
            string aiConfigJson = CoreHelper.GetVar("ai_config_json", "");
            
            if (string.IsNullOrEmpty(projectRoot) || string.IsNullOrEmpty(deviceId))
            {
                CoreHelper.LogErr(TAG, "project_root 或 device_id 未设置");
                CoreHelper.SetVar("persona_result", "ERROR");
                return "ERROR: 变量未设置";
            }
            
            projectRoot = CoreHelper.NormalizePath(projectRoot);
            
            // 如果 aiConfigJson 为空，从文件读取
            if (string.IsNullOrEmpty(aiConfigJson) || aiConfigJson == "{}")
            {
                string configPath = projectRoot + "Config\\AIConfig.json";
                if (File.Exists(configPath))
                {
                    aiConfigJson = CoreHelper.ReadFile(configPath);
                    CoreHelper.SetVar("ai_config_json", aiConfigJson);
                    CoreHelper.Log(TAG, "已从文件加载 AI 配置");
                }
                else
                {
                    CoreHelper.LogErr(TAG, "AIConfig.json 不存在");
                    CoreHelper.SetVar("persona_result", "ERROR");
                    return "ERROR: AI_CONFIG_NOT_FOUND";
                }
            }
            
            CoreHelper.Log(TAG, "开始生成画像: " + deviceId);
            
            // 读取 Prompt 模板
            string promptPath = projectRoot + "Config\\PersonaPrompt.txt";
            if (!File.Exists(promptPath))
            {
                CoreHelper.LogErr(TAG, "Prompt模板不存在: " + promptPath);
                CoreHelper.SetVar("persona_result", "ERROR");
                return "ERROR: PROMPT_NOT_FOUND";
            }
            string promptTemplate = CoreHelper.ReadFile(promptPath);
            
            // 替换变量
            string currentDate = CoreHelper.GetToday();
            string pregnancyStatus = CoreHelper.GetVar("pregnancy_status", "怀孕中期(20周)");
            string specialRequirements = CoreHelper.GetVar("special_requirements", "无");
            
            string prompt = promptTemplate
                .Replace("{CURRENT_DATE}", currentDate)
                .Replace("{PREGNANCY_STATUS}", pregnancyStatus)
                .Replace("{SPECIAL_REQUIREMENTS}", specialRequirements);
            
            CoreHelper.Log(TAG, "调用AI生成画像（带重试和备用模型）...");
            
            // 使用新的动态配置调用（自动重试 + 备用模型）
            string responseText = AIService.CallWithRetry(prompt, aiConfigJson);
            
            if (responseText.StartsWith("ERROR:"))
            {
                CoreHelper.LogErr(TAG, "API调用失败: " + responseText);
                CoreHelper.SetVar("persona_result", "ERROR");
                return responseText;
            }
            
            CoreHelper.Log(TAG, "AI响应已收到");
            
            // 判断使用的是哪种 provider 来解析响应
            string modelsSection = JsonHelper.ExtractObject(aiConfigJson, "models");
            string primarySection = JsonHelper.ExtractObject(modelsSection, "primary");
            string provider = JsonHelper.Get(primarySection, "provider");
            
            // 提取文本内容
            string aiText = AIService.ExtractText(responseText, provider);
            
            // 提取 JSON
            string personaJson = AIService.ExtractJson(aiText);
            
            // 验证 JSON 基本结构
            if (!personaJson.StartsWith("{") || !personaJson.EndsWith("}"))
            {
                CoreHelper.LogErr(TAG, "生成的画像格式无效");
                CoreHelper.LogErr(TAG, "原始响应: " + aiText.Substring(0, Math.Min(200, aiText.Length)));
                CoreHelper.SetVar("persona_result", "ERROR");
                return "ERROR: INVALID_PERSONA_FORMAT";
            }
            
            // 添加元数据
            string now = CoreHelper.GetNowISO();
            personaJson = JsonHelper.Set(personaJson, "device_id", deviceId, true);
            personaJson = JsonHelper.Set(personaJson, "_meta", 
                "{\"created_at\": \"" + now + "\", \"last_updated\": \"" + CoreHelper.GetToday() + "\", \"version\": \"4.0\"}", 
                false);
            
            // 保存画像
            string personaPath = projectRoot + "Persons\\" + deviceId + ".json";
            CoreHelper.WriteFile(personaPath, personaJson);
            
            CoreHelper.SetVar("persona_json", personaJson);
            
            CoreHelper.Log(TAG, "画像已保存: " + personaPath);
            CoreHelper.SetVar("persona_result", "SUCCESS");
            return "SUCCESS";
        }
        catch (Exception ex)
        {
            CoreHelper.LogErr(TAG, "异常: " + ex.Message);
            CoreHelper.SetVar("last_error", ex.Message);
            CoreHelper.SetVar("persona_result", "ERROR");
            return "ERROR: " + ex.Message;
        }
    }
}
