// =====================================================
// WeatherExtension.cs - 天气扩展
// ⚠️ C# 5.0 语法 - 禁止使用 $""、?.、nameof() 等
// 实现 IExtension 接口，从 Extension.cs 迁移
// =====================================================
using System;
using System.Collections.Generic;

/// <summary>
/// 天气扩展
/// 获取天气信息并应用行为影响因子
/// </summary>
public class WeatherExtension : IExtension
{
    private const string TAG = "Ext.Weather";
    
    private bool _enabled = true;
    private bool _useMcp = true;
    private bool _fallbackToSimulation = true;
    private double _activityImpact = 0.2;
    private double _moodImpact = 0.3;
    
    /// <summary>天气影响配置（从 ExtensionsConfig.json hooks 读取）</summary>
    private string _hooksConfigJson = "";
    
    public string Name { get { return "Weather"; } }
    public string Category { get { return "DataSource"; } }
    public string Version { get { return "1.0.0"; } }
    public bool Enabled { get { return _enabled; } }
    
    public void Initialize(object projectObj, string configJson)
    {
        if (string.IsNullOrEmpty(configJson) || configJson == "{}") return;
        
        string useMcpStr = JsonHelper.Get(configJson, "use_mcp");
        _useMcp = (useMcpStr != "false");
        
        string fallbackStr = JsonHelper.Get(configJson, "fallback_to_simulation");
        _fallbackToSimulation = (fallbackStr != "false");
        
        string impactJson = JsonHelper.ExtractObject(configJson, "impact_factors");
        if (!string.IsNullOrEmpty(impactJson))
        {
            _activityImpact = JsonHelper.GetDouble(impactJson, "activity_level", 0.2);
            _moodImpact = JsonHelper.GetDouble(impactJson, "mood", 0.3);
        }
        
        // 加载 hooks 配置（天气影响规则）
        string projectRoot = CoreHelper.GetVar("project_root", "");
        if (!string.IsNullOrEmpty(projectRoot))
        {
            projectRoot = CoreHelper.NormalizePath(projectRoot);
            string extConfigPath = projectRoot + "Config\\ExtensionsConfig.json";
            if (System.IO.File.Exists(extConfigPath))
            {
                _hooksConfigJson = CoreHelper.ReadFile(extConfigPath);
            }
        }
        
        CoreHelper.Log(TAG, string.Format("初始化: MCP={0}, 回退模拟={1}, 活动影响={2:F1}, 心情影响={3:F1}",
            _useMcp, _fallbackToSimulation, _activityImpact, _moodImpact));
    }
    
    public string Run(object projectObj)
    {
        try
        {
            CoreHelper.Log(TAG, "检查天气...");
            
            string weather = "";
            
            if (_useMcp)
            {
                // MCP 模式（待实现真实 API 调用）
                CoreHelper.Log(TAG, "MCP 模式（待实现），回退到模拟");
                
                if (_fallbackToSimulation)
                {
                    weather = SimulateWeather();
                }
                else
                {
                    CoreHelper.Log(TAG, "MCP 不可用且未启用回退模拟");
                    return "SUCCESS";
                }
            }
            else
            {
                weather = SimulateWeather();
            }
            
            if (!string.IsNullOrEmpty(weather))
            {
                CoreHelper.SetVar("current_weather", weather);
                CoreHelper.Log(TAG, "当前天气: " + weather);
                
                ApplyWeatherImpact(weather);
            }
            
            return "SUCCESS";
        }
        catch (Exception ex)
        {
            CoreHelper.LogErr(TAG, "执行失败: " + ex.Message);
            return "ERROR: " + ex.Message;
        }
    }
    
    /// <summary>
    /// 模拟天气（随机选择）
    /// </summary>
    private string SimulateWeather()
    {
        string[] weathers = new string[] { "sunny", "cloudy", "rainy", "cold", "hot" };
        Random rnd = new Random();
        return weathers[rnd.Next(weathers.Length)];
    }
    
    /// <summary>
    /// 应用天气影响到行为参数
    /// 从 ExtensionsConfig.json 的 hooks.weather_impact 读取影响规则
    /// </summary>
    private void ApplyWeatherImpact(string weather)
    {
        try
        {
            if (string.IsNullOrEmpty(_hooksConfigJson)) return;
            
            string hooksJson = JsonHelper.ExtractObject(_hooksConfigJson, "hooks");
            if (string.IsNullOrEmpty(hooksJson)) return;
            
            string weatherImpactJson = JsonHelper.ExtractObject(hooksJson, "weather_impact");
            if (string.IsNullOrEmpty(weatherImpactJson)) return;
            
            string impactJson = JsonHelper.ExtractObject(weatherImpactJson, weather);
            if (string.IsNullOrEmpty(impactJson))
            {
                CoreHelper.Log(TAG, "无 " + weather + " 的影响配置");
                return;
            }
            
            // 将影响因子存储到变量，供 SessionRunner 读取
            if (weather == "sunny")
            {
                CoreHelper.SetVar("weather_activity_boost", "0.1");
                CoreHelper.SetVar("weather_mood_boost", "0.1");
                CoreHelper.Log(TAG, "晴天：活动增加 +10%，心情 +10%");
            }
            else if (weather == "rainy")
            {
                CoreHelper.SetVar("weather_activity_boost", "0.15");
                CoreHelper.SetVar("weather_indoor_preference", "0.3");
                CoreHelper.Log(TAG, "雨天：室内活动偏好 +30%");
            }
            else if (weather == "cold")
            {
                CoreHelper.SetVar("weather_session_reduction", "0.1");
                CoreHelper.Log(TAG, "寒冷：会话时长 -10%");
            }
            else if (weather == "hot")
            {
                CoreHelper.SetVar("weather_session_reduction", "0.15");
                CoreHelper.Log(TAG, "炎热：会话时长 -15%");
            }
        }
        catch (Exception ex)
        {
            CoreHelper.Log(TAG, "应用天气影响失败: " + ex.Message);
        }
    }
}
