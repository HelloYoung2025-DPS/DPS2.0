// =====================================================
// IPLocationExtension.cs - IP 定位扩展
// ⚠️ C# 5.0 语法 - 禁止使用 $""、?.、nameof() 等
// 实现 IExtension 接口，从 Extension.cs 迁移
// =====================================================
using System;
using System.Net;

/// <summary>
/// IP 定位扩展
/// 获取当前代理的外部 IP 并存储到变量
/// </summary>
public class IPLocationExtension : IExtension
{
    private const string TAG = "Ext.IP";
    
    private bool _enabled = true;
    private int _cacheDurationHours = 1;
    private string[] _providers;
    
    public string Name { get { return "IPLocation"; } }
    public string Category { get { return "DataSource"; } }
    public string Version { get { return "1.0.0"; } }
    public bool Enabled { get { return _enabled; } }
    
    public void Initialize(object projectObj, string configJson)
    {
        if (string.IsNullOrEmpty(configJson) || configJson == "{}") return;
        
        _cacheDurationHours = JsonHelper.GetInt(configJson, "cache_duration_hours", 1);
        
        // 解析 providers 数组（简单处理，取第一个）
        string providersRaw = JsonHelper.ExtractArray(configJson, "providers");
        if (!string.IsNullOrEmpty(providersRaw))
        {
            // 简单解析：提取引号内的 URL
            var list = new System.Collections.Generic.List<string>();
            int idx = 0;
            while (idx < providersRaw.Length)
            {
                int start = providersRaw.IndexOf('"', idx);
                if (start < 0) break;
                int end = providersRaw.IndexOf('"', start + 1);
                if (end < 0) break;
                string url = providersRaw.Substring(start + 1, end - start - 1);
                if (url.StartsWith("http")) list.Add(url);
                idx = end + 1;
            }
            if (list.Count > 0) _providers = list.ToArray();
        }
        
        if (_providers == null || _providers.Length == 0)
        {
            _providers = new string[] { "https://api.ipify.org" };
        }
        
        CoreHelper.Log(TAG, string.Format("初始化: 缓存{0}h, {1}个提供商", _cacheDurationHours, _providers.Length));
    }
    
    public string Run(object projectObj)
    {
        try
        {
            CoreHelper.Log(TAG, "检查 IP 定位...");
            
            // 检查缓存是否有效
            string cachedIp = CoreHelper.GetVar("current_ip", "");
            string cachedTime = CoreHelper.GetVar("ip_cache_time", "");
            
            if (!string.IsNullOrEmpty(cachedIp) && !string.IsNullOrEmpty(cachedTime))
            {
                DateTime cached;
                if (DateTime.TryParse(cachedTime, out cached))
                {
                    if ((DateTime.Now - cached).TotalHours < _cacheDurationHours)
                    {
                        CoreHelper.Log(TAG, "使用缓存 IP: " + cachedIp);
                        return "SUCCESS";
                    }
                }
            }
            
            string ip = GetExternalIP();
            if (string.IsNullOrEmpty(ip))
            {
                CoreHelper.Log(TAG, "无法获取外部 IP，使用模拟模式");
                return "SUCCESS";
            }
            
            CoreHelper.Log(TAG, "外部 IP: " + ip);
            CoreHelper.SetVar("current_ip", ip);
            CoreHelper.SetVar("ip_cache_time", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            
            return "SUCCESS";
        }
        catch (Exception ex)
        {
            CoreHelper.LogErr(TAG, "执行失败: " + ex.Message);
            return "ERROR: " + ex.Message;
        }
    }
    
    /// <summary>
    /// 从多个提供商获取外部 IP，带回退
    /// </summary>
    private string GetExternalIP()
    {
        for (int i = 0; i < _providers.Length; i++)
        {
            try
            {
                string url = _providers[i];
                // 跳过需要 IP 参数的模板 URL
                if (url.Contains("{ip}")) continue;
                
                using (WebClient client = new WebClient())
                {
                    string response = client.DownloadString(url);
                    string ip = response.Trim();
                    if (!string.IsNullOrEmpty(ip) && ip.Length < 50)
                    {
                        return ip;
                    }
                }
            }
            catch (Exception ex)
            {
                CoreHelper.Log(TAG, "提供商 " + i + " 失败: " + ex.Message);
            }
        }
        return null;
    }
}
