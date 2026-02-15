// =====================================================
// Initializer.cs - 项目初始化模块
// ⚠️ C# 5.0 语法 - 禁止使用 $""、?.、nameof() 等
// =====================================================
using System;
using System.IO;
using System.Text;

/// <summary>
/// 项目初始化模块
/// 创建必要的目录结构，验证配置文件
/// </summary>
public class Initializer
{
    private static dynamic _project;
    private const string TAG = "Initializer";
    
    /// <summary>
    /// 模块入口点
    /// </summary>
    public static string Run(object projectObj)
    {
        _project = projectObj;
        
        try
        {
            CoreHelper.Init(projectObj);
            
            // 读取项目根目录
            string projectRoot = CoreHelper.GetVar("project_root", "");
            
            if (string.IsNullOrEmpty(projectRoot))
            {
                CoreHelper.LogErr(TAG, "错误: project_root 变量未设置！");
                CoreHelper.LogErr(TAG, "请在 ZD 变量中设置 project_root，例如: C:\\DPS_v4.0");
                CoreHelper.SetVar("initializer_result", "ERROR");
                return "ERROR: project_root 未设置";
            }
            
            projectRoot = CoreHelper.NormalizePath(projectRoot);
            
            CoreHelper.Log(TAG, "========================================");
CoreHelper.Log(TAG, "  DPS v4.5 Persona - 初始化程序");
            CoreHelper.Log(TAG, "  版本: 4.5.0-Modular");
            CoreHelper.Log(TAG, "  日期: " + CoreHelper.GetNow());
            CoreHelper.Log(TAG, "========================================");
            CoreHelper.Log(TAG, "");
            CoreHelper.Log(TAG, "[配置] 项目根目录: " + projectRoot);
            CoreHelper.Log(TAG, "");
            
            // 创建所有目录
            CoreHelper.Log(TAG, "[目录] 开始创建目录结构...");
            
            CreateDir(projectRoot + "Config");
            CreateDir(projectRoot + "Config\\Operations");
            CreateDir(projectRoot + "Config\\Selectors");
            CreateDir(projectRoot + "Modules");
            CreateDir(projectRoot + "Modules\\Core");
            CreateDir(projectRoot + "Core");
            CreateDir(projectRoot + "Persons");
            CreateDir(projectRoot + "Persons\\Backups");
            CreateDir(projectRoot + "Memory");
            CreateDir(projectRoot + "Data");
            CreateDir(projectRoot + "Logs");
            CreateDir(projectRoot + "Reports");
            CreateDir(projectRoot + "Extensions");
            CreateDir(projectRoot + "Extensions\\DataSources");
            CreateDir(projectRoot + "Extensions\\Hooks");
            
            CoreHelper.Log(TAG, "");
            CoreHelper.Log(TAG, "[目录] 完成!");
            CoreHelper.Log(TAG, "");
            
            // 验证配置文件
            int configOk = 0;
            int configMissing = 0;
            
            string[] requiredConfigs = new string[] {
                "AIConfig.json",
                "StageConfig.json",
                "BehaviorConfig.json",
                "DecisionConfig.json",
                "PlatformsConfig.json",
                "PersonaPrompt.txt"
            };
            
            foreach (string cfg in requiredConfigs)
            {
                string cfgPath = projectRoot + "Config\\" + cfg;
                if (File.Exists(cfgPath))
                {
                    CoreHelper.Log(TAG, "[配置] " + cfg + " ✓");
                    configOk++;
                    
                    // 特别检查 API Key
                    if (cfg == "AIConfig.json")
                    {
                        string content = CoreHelper.ReadFile(cfgPath);
                        if (content.Contains("YOUR_") && content.Contains("API_KEY"))
                        {
                            CoreHelper.LogWarn(TAG, "[配置] 警告: API Key 未配置，请编辑 AIConfig.json");
                        }
                    }
                }
                else
                {
                    CoreHelper.LogErr(TAG, "[配置] " + cfg + " 缺失！");
                    configMissing++;
                }
            }
            
            // 验证核心模块
            CoreHelper.Log(TAG, "");
            CoreHelper.Log(TAG, "[模块] 检查核心模块...");
            
            string[] requiredModules = new string[] {
                "Modules\\Core\\CoreHelper.cs",
                "Modules\\Core\\JsonHelper.cs",
                "Modules\\Core\\AIService.cs",
                "Modules\\Core\\FileHelper.cs",
                "Modules\\Core\\SelectorEngine.cs",
                "Modules\\Core\\PageDetector.cs",
                "Modules\\Core\\ActionExecutor.cs",
                "Modules\\Core\\IExtension.cs",
                "Modules\\Core\\ExtensionManager.cs",
                "Modules\\Main.cs",
                "Modules\\SessionRunner.cs",
                "Modules\\MemoryManager.cs",
                "Modules\\RuleEngine.cs"
            };
            
            int moduleOk = 0;
            int moduleMissing = 0;
            
            foreach (string mod in requiredModules)
            {
                string modPath = projectRoot + mod;
                if (File.Exists(modPath))
                {
                    CoreHelper.Log(TAG, "[模块] " + mod + " ✓");
                    moduleOk++;
                }
                else
                {
                    CoreHelper.LogErr(TAG, "[模块] " + mod + " 缺失！");
                    moduleMissing++;
                }
            }
            
            // 保存状态
            CoreHelper.SetVar("_init_status", "SUCCESS");
            CoreHelper.SetVar("_config_ok", configOk.ToString());
            CoreHelper.SetVar("_config_missing", configMissing.ToString());
            CoreHelper.SetVar("_module_ok", moduleOk.ToString());
            CoreHelper.SetVar("_module_missing", moduleMissing.ToString());
            
            CoreHelper.Log(TAG, "");
            CoreHelper.Log(TAG, "========================================");
            CoreHelper.Log(TAG, "  初始化完成！");
            CoreHelper.Log(TAG, string.Format("  配置: {0} OK, {1} 缺失", configOk, configMissing));
            CoreHelper.Log(TAG, string.Format("  模块: {0} OK, {1} 缺失", moduleOk, moduleMissing));
            CoreHelper.Log(TAG, "  下一步: 运行 Main 或 PersonaCreate");
            CoreHelper.Log(TAG, "========================================");
            
            if (configMissing > 0 || moduleMissing > 0)
            {
                CoreHelper.SetVar("initializer_result", "WARNING");
                return "WARNING: 有缺失的配置或模块";
            }
            
            CoreHelper.SetVar("initializer_result", "SUCCESS");
            return "SUCCESS";
        }
        catch (Exception ex)
        {
            CoreHelper.LogErr(TAG, "异常: " + ex.Message);
            CoreHelper.SetVar("last_error", ex.Message);
            CoreHelper.SetVar("initializer_result", "ERROR");
            return "ERROR: " + ex.Message;
        }
    }
    
    /// <summary>
    /// 创建目录并记录日志
    /// </summary>
    private static void CreateDir(string dir)
    {
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
            CoreHelper.Log(TAG, "  ✓ 创建: " + dir);
        }
    }
}
