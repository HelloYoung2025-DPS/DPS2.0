// =====================================================
// AIService.cs - AI API 调用服务
// ⚠️ C# 5.0 语法 - 禁止使用 $""、?.、nameof() 等
// ⚠️ 从 AIConfig.json 动态读取配置
// =====================================================
using System;
using System.IO;
using System.Net;
using System.Text;

/// <summary>
/// AI API 调用服务
/// 支持 Gemini 和 OpenAI 兼容 API
/// </summary>
public class AIService
{
    // ========== 动态配置读取 ==========
    
    /// <summary>
    /// 从 AIConfig.json 调用主模型（primary）
    /// </summary>
    public static string CallPrimary(string prompt, string aiConfigJson)
    {
        return CallModel(prompt, aiConfigJson, "primary");
    }
    
    /// <summary>
    /// 从 AIConfig.json 调用备用模型（fallback）
    /// </summary>
    public static string CallFallback(string prompt, string aiConfigJson)
    {
        return CallModel(prompt, aiConfigJson, "fallback");
    }
    
    /// <summary>
    /// 从 AIConfig.json 调用备份模型（backup）
    /// </summary>
    public static string CallBackup(string prompt, string aiConfigJson)
    {
        return CallModel(prompt, aiConfigJson, "backup");
    }
    
    /// <summary>
    /// 从配置调用指定模型
    /// </summary>
    public static string CallModel(string prompt, string aiConfigJson, string modelKey)
    {
        if (string.IsNullOrEmpty(aiConfigJson))
        {
            return "ERROR: aiConfigJson 为空";
        }
        
        // 解析配置
        // 结构: { "models": { "primary": { "provider": "...", ... } } }
        string modelsSection = JsonHelper.ExtractObject(aiConfigJson, "models");
        if (string.IsNullOrEmpty(modelsSection))
        {
            return "ERROR: 找不到 models 配置节";
        }
        
        string modelSection = JsonHelper.ExtractObject(modelsSection, modelKey);
        if (string.IsNullOrEmpty(modelSection))
        {
            return "ERROR: 找不到 " + modelKey + " 模型配置";
        }
        
        // 读取配置字段
        string provider = CoreHelper.JGet(modelSection, "provider");
        string model = CoreHelper.JGet(modelSection, "model");
        string apiKey = CoreHelper.JGet(modelSection, "api_key");
        string baseUrl = CoreHelper.JGet(modelSection, "base_url");
        string timeoutStr = CoreHelper.JGet(modelSection, "timeout_ms");
        string maxTokensStr = CoreHelper.JGet(modelSection, "max_tokens");
        string temperatureStr = CoreHelper.JGet(modelSection, "temperature");
        
        int timeoutMs = 60000;
        if (!string.IsNullOrEmpty(timeoutStr))
        {
            int.TryParse(timeoutStr, out timeoutMs);
        }
        
        int maxTokens = 4096;
        if (!string.IsNullOrEmpty(maxTokensStr))
        {
            int.TryParse(maxTokensStr, out maxTokens);
        }
        
        double temperature = 0.7;
        if (!string.IsNullOrEmpty(temperatureStr))
        {
            double.TryParse(temperatureStr, out temperature);
        }
        
        if (string.IsNullOrEmpty(apiKey))
        {
            return "ERROR: " + modelKey + " 的 api_key 未配置";
        }
        
        // 根据 provider 调用不同的 API
        if (provider == "gemini")
        {
            return CallGeminiWithConfig(prompt, apiKey, model, baseUrl, timeoutMs, maxTokens, temperature);
        }
        else
        {
            // OpenAI 兼容 API（包括 Claude、自定义端点等）
            return CallOpenAICompatible(prompt, apiKey, model, baseUrl, timeoutMs, maxTokens, temperature);
        }
    }
    
    /// <summary>
    /// 带重试调用（使用配置中的重试设置）
    /// </summary>
    public static string CallWithRetry(string prompt, string aiConfigJson)
    {
        // 解析重试配置
        string retrySection = JsonHelper.ExtractObject(aiConfigJson, "retry_config");
        int maxRetries = 3;
        int retryDelayMs = 1000;
        double backoffMultiplier = 2.0;
        
        if (!string.IsNullOrEmpty(retrySection))
        {
            string maxStr = CoreHelper.JGet(retrySection, "max_retries");
            string delayStr = CoreHelper.JGet(retrySection, "retry_delay_ms");
            
            if (!string.IsNullOrEmpty(maxStr)) int.TryParse(maxStr, out maxRetries);
            if (!string.IsNullOrEmpty(delayStr)) int.TryParse(delayStr, out retryDelayMs);
        }
        
        // 尝试 primary
        string result = CallPrimary(prompt, aiConfigJson);
        if (!result.StartsWith("ERROR:"))
        {
            return result;
        }
        
        // 重试 primary
        int delay = retryDelayMs;
        for (int i = 0; i < maxRetries - 1; i++)
        {
            System.Threading.Thread.Sleep(delay);
            result = CallPrimary(prompt, aiConfigJson);
            if (!result.StartsWith("ERROR:"))
            {
                return result;
            }
            delay = (int)(delay * backoffMultiplier);
        }
        
        // 尝试 fallback
        result = CallFallback(prompt, aiConfigJson);
        if (!result.StartsWith("ERROR:"))
        {
            return result;
        }
        
        // 尝试 backup
        result = CallBackup(prompt, aiConfigJson);
        return result;
    }
    
    // ========== Gemini API ==========
    
    /// <summary>
    /// 调用 Gemini API（带完整配置）
    /// </summary>
    public static string CallGeminiWithConfig(string prompt, string apiKey, string model, 
        string baseUrl, int timeoutMs, int maxTokens, double temperature)
    {
        if (string.IsNullOrEmpty(model))
        {
            model = "gemini-1.5-flash";
        }
        if (string.IsNullOrEmpty(baseUrl))
        {
            baseUrl = "https://generativelanguage.googleapis.com/v1beta";
        }
        if (timeoutMs <= 0)
        {
            timeoutMs = 60000;
        }
        
        // 构建 URL
        string apiUrl = baseUrl + "/models/" + model + ":generateContent?key=" + apiKey;
        
        // 转义 prompt
        string escaped = JsonHelper.Escape(prompt);
        
        // 请求体
        string body = "{\"contents\":[{\"parts\":[{\"text\":\"" + escaped + "\"}]}],"
            + "\"generationConfig\":{\"temperature\":" + temperature.ToString("F1") 
            + ",\"maxOutputTokens\":" + maxTokens + "}}";
        
        return DoHttpPost(apiUrl, body, null, timeoutMs);
    }
    
    /// <summary>
    /// 调用 Gemini API（简化版，向后兼容）
    /// </summary>
    public static string CallGemini(string prompt, string apiKey, string model, int timeoutMs)
    {
        return CallGeminiWithConfig(prompt, apiKey, model, null, timeoutMs, 8192, 0.7);
    }
    
    /// <summary>
    /// 调用 Gemini API（最简版）
    /// </summary>
    public static string CallGemini(string prompt, string apiKey)
    {
        return CallGemini(prompt, apiKey, "gemini-1.5-flash", 60000);
    }
    
    // ========== OpenAI 兼容 API ==========
    
    /// <summary>
    /// 调用 OpenAI 兼容 API（支持自定义 base_url）
    /// </summary>
    public static string CallOpenAICompatible(string prompt, string apiKey, string model,
        string baseUrl, int timeoutMs, int maxTokens, double temperature)
    {
        if (string.IsNullOrEmpty(model))
        {
            model = "gpt-4o-mini";
        }
        if (string.IsNullOrEmpty(baseUrl))
        {
            baseUrl = "https://api.openai.com/v1";
        }
        if (timeoutMs <= 0)
        {
            timeoutMs = 60000;
        }
        
        // 确保 baseUrl 不以 / 结尾
        if (baseUrl.EndsWith("/"))
        {
            baseUrl = baseUrl.Substring(0, baseUrl.Length - 1);
        }
        
        string apiUrl = baseUrl + "/chat/completions";
        
        // 转义 prompt
        string escaped = JsonHelper.Escape(prompt);
        
        // 请求体
        string body = "{\"model\":\"" + model + "\","
            + "\"messages\":[{\"role\":\"user\",\"content\":\"" + escaped + "\"}],"
            + "\"max_tokens\":" + maxTokens + ","
            + "\"temperature\":" + temperature.ToString("F1") + "}";
        
        string authHeader = "Bearer " + apiKey;
        
        return DoHttpPost(apiUrl, body, authHeader, timeoutMs);
    }
    
    /// <summary>
    /// 调用 OpenAI API（向后兼容）
    /// </summary>
    public static string CallOpenAI(string prompt, string apiKey, string model, int timeoutMs)
    {
        return CallOpenAICompatible(prompt, apiKey, model, null, timeoutMs, 4096, 0.7);
    }
    
    /// <summary>
    /// 调用 OpenAI API（最简版）
    /// </summary>
    public static string CallOpenAI(string prompt, string apiKey)
    {
        return CallOpenAI(prompt, apiKey, "gpt-4o-mini", 60000);
    }
    
    // ========== 响应解析 ==========
    
    /// <summary>
    /// 自动检测并提取 AI 响应文本
    /// </summary>
    public static string ExtractText(string response, string provider)
    {
        if (string.IsNullOrEmpty(response)) return "";
        if (response.StartsWith("ERROR:")) return response;
        
        string normalizedProvider = provider != null ? provider.Trim().ToLowerInvariant() : "";
        
        if (LooksLikeGeminiResponse(response))
        {
            string geminiText = ExtractGeminiText(response);
            if (!string.IsNullOrEmpty(geminiText) && geminiText != response)
            {
                return geminiText;
            }
        }
        
        if (LooksLikeOpenAIResponse(response))
        {
            string openAiText = ExtractOpenAIText(response);
            if (!string.IsNullOrEmpty(openAiText) && openAiText != response)
            {
                return openAiText;
            }
        }
        
        if (normalizedProvider == "gemini")
        {
            string geminiText = ExtractGeminiText(response);
            if (!string.IsNullOrEmpty(geminiText) && geminiText != response)
            {
                return geminiText;
            }
            return ExtractOpenAIText(response);
        }
        
        string fallbackOpenAiText = ExtractOpenAIText(response);
        if (!string.IsNullOrEmpty(fallbackOpenAiText) && fallbackOpenAiText != response)
        {
            return fallbackOpenAiText;
        }
        
        string fallbackGeminiText = ExtractGeminiText(response);
        if (!string.IsNullOrEmpty(fallbackGeminiText) && fallbackGeminiText != response)
        {
            return fallbackGeminiText;
        }
        
        return response;
    }
    
    /// <summary>
    /// 从 Gemini 响应中提取文本
    /// </summary>
    public static string ExtractGeminiText(string response)
    {
        if (string.IsNullOrEmpty(response)) return "";
        
        // 检查 API 错误
        string errorObj = JsonHelper.ExtractObject(response, "error");
        if (!string.IsNullOrEmpty(errorObj))
        {
            string errorMsg = JsonHelper.Get(errorObj, "message");
            if (!string.IsNullOrEmpty(errorMsg))
            {
                return "ERROR: " + errorMsg;
            }
        }
        
        // Gemini 响应结构: { "candidates": [{ "content": { "parts": [{ "text": "..." }] } }] }
        string candidatesArr = JsonHelper.ExtractArray(response, "candidates");
        if (string.IsNullOrEmpty(candidatesArr) || candidatesArr == "[]")
        {
            return response;
        }
        
        string firstCandidate = JsonHelper.GetArrayElement(candidatesArr, 0);
        if (string.IsNullOrEmpty(firstCandidate))
        {
            return response;
        }
        
        string contentObj = JsonHelper.ExtractObject(firstCandidate, "content");
        if (string.IsNullOrEmpty(contentObj))
        {
            return response;
        }
        
        string partsArr = JsonHelper.ExtractArray(contentObj, "parts");
        if (string.IsNullOrEmpty(partsArr) || partsArr == "[]")
        {
            return response;
        }
        
        string firstPart = JsonHelper.GetArrayElement(partsArr, 0);
        if (string.IsNullOrEmpty(firstPart))
        {
            return response;
        }
        
        string text = JsonHelper.Get(firstPart, "text");
        return string.IsNullOrEmpty(text) ? response : text;
    }
    
    /// <summary>
    /// 从 OpenAI 响应中提取文本
    /// </summary>
    public static string ExtractOpenAIText(string response)
    {
        if (string.IsNullOrEmpty(response)) return "";
        
        // 检查 API 错误
        string errorObj = JsonHelper.ExtractObject(response, "error");
        if (!string.IsNullOrEmpty(errorObj))
        {
            string errorMsg = JsonHelper.Get(errorObj, "message");
            if (!string.IsNullOrEmpty(errorMsg))
            {
                return "ERROR: " + errorMsg;
            }
        }
        
        // OpenAI 响应结构: { "choices": [{ "message": { "content": "..." } }] }
        string choicesArr = JsonHelper.ExtractArray(response, "choices");
        if (string.IsNullOrEmpty(choicesArr) || choicesArr == "[]")
        {
            return response;
        }
        
        string firstChoice = JsonHelper.GetArrayElement(choicesArr, 0);
        if (string.IsNullOrEmpty(firstChoice))
        {
            return response;
        }
        
        string messageObj = JsonHelper.ExtractObject(firstChoice, "message");
        if (string.IsNullOrEmpty(messageObj))
        {
            return response;
        }
        
        string content = JsonHelper.Get(messageObj, "content");
        return string.IsNullOrEmpty(content) ? response : content;
    }
    
    /// <summary>
    /// 提取 JSON（从 ```json 代码块或直接 { 开始）
    /// </summary>
    public static string ExtractJson(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        
        if (LooksLikeWrappedAiResponse(text))
        {
            string extractedText = ExtractText(text, "");
            if (!string.IsNullOrEmpty(extractedText) && extractedText != text && !extractedText.StartsWith("ERROR:"))
            {
                text = extractedText;
            }
        }
        
        // 尝试从 ```json 代码块提取
        int jsonStart = text.IndexOf("```json");
        if (jsonStart >= 0)
        {
            jsonStart = text.IndexOf("\n", jsonStart) + 1;
            int jsonEnd = text.IndexOf("```", jsonStart);
            if (jsonEnd > jsonStart)
            {
                return text.Substring(jsonStart, jsonEnd - jsonStart).Trim();
            }
        }
        
        // 尝试从 ``` 代码块提取
        jsonStart = text.IndexOf("```");
        if (jsonStart >= 0)
        {
            jsonStart = text.IndexOf("\n", jsonStart) + 1;
            int jsonEnd = text.IndexOf("```", jsonStart);
            if (jsonEnd > jsonStart)
            {
                string candidate = text.Substring(jsonStart, jsonEnd - jsonStart).Trim();
                if (candidate.StartsWith("{") || candidate.StartsWith("["))
                {
                    return candidate;
                }
            }
        }
        
        // 尝试直接查找 JSON
        jsonStart = text.IndexOf("{");
        if (jsonStart >= 0)
        {
            int lastBrace = text.LastIndexOf("}");
            if (lastBrace > jsonStart)
            {
                return text.Substring(jsonStart, lastBrace - jsonStart + 1);
            }
        }
        
        return text;
    }

    private static bool LooksLikeGeminiResponse(string response)
    {
        return !string.IsNullOrEmpty(response)
            && response.IndexOf("\"candidates\"", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool LooksLikeOpenAIResponse(string response)
    {
        return !string.IsNullOrEmpty(response)
            && response.IndexOf("\"choices\"", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool LooksLikeWrappedAiResponse(string response)
    {
        return LooksLikeGeminiResponse(response) || LooksLikeOpenAIResponse(response);
    }
    
    // ========== HTTP 请求 ==========
    
    /// <summary>
    /// 执行 HTTP POST 请求
    /// </summary>
    private static string DoHttpPost(string url, string body, string authHeader, int timeoutMs)
    {
        try
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "POST";
            req.ContentType = "application/json; charset=utf-8";
            req.Timeout = timeoutMs;
            
            if (!string.IsNullOrEmpty(authHeader))
            {
                req.Headers.Add("Authorization", authHeader);
            }
            
            byte[] bytes = Encoding.UTF8.GetBytes(body);
            req.ContentLength = bytes.Length;
            
            using (var stream = req.GetRequestStream())
            {
                stream.Write(bytes, 0, bytes.Length);
            }
            
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var reader = new StreamReader(resp.GetResponseStream()))
            {
                return reader.ReadToEnd();
            }
        }
        catch (WebException webEx)
        {
            string errMsg = webEx.Message;
            if (webEx.Response != null)
            {
                try
                {
                    using (var s = webEx.Response.GetResponseStream())
                    using (var r = new StreamReader(s))
                    {
                        errMsg = r.ReadToEnd();
                    }
                }
                catch { }
            }
            return "ERROR: " + errMsg;
        }
        catch (Exception ex)
        {
            return "ERROR: " + ex.Message;
        }
    }
    
    // ========== 图片上传支持 ==========
    
    /// <summary>
    /// 读取文件并转为 Base64 字符串
    /// </summary>
    public static string ReadFileAsBase64(string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
        {
            return "";
        }
        
        byte[] fileBytes = File.ReadAllBytes(filePath);
        return Convert.ToBase64String(fileBytes);
    }
    
    /// <summary>
    /// 根据文件扩展名获取 MIME 类型
    /// </summary>
    public static string GetMimeType(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return "application/octet-stream";
        }
        
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (ext == ".png") return "image/png";
        if (ext == ".jpg" || ext == ".jpeg") return "image/jpeg";
        if (ext == ".gif") return "image/gif";
        if (ext == ".webp") return "image/webp";
        if (ext == ".bmp") return "image/bmp";
        return "application/octet-stream";
    }
    
    /// <summary>
    /// 从 AIConfig.json 调用主模型（带图片）
    /// </summary>
    public static string CallPrimaryWithImage(string prompt, string imagePath, string aiConfigJson)
    {
        return CallModelWithImage(prompt, imagePath, aiConfigJson, "primary");
    }
    
    /// <summary>
    /// 从配置调用指定模型（带图片）
    /// </summary>
    public static string CallModelWithImage(string prompt, string imagePath, string aiConfigJson, string modelKey)
    {
        if (string.IsNullOrEmpty(aiConfigJson))
        {
            return "ERROR: aiConfigJson 为空";
        }
        
        string modelsSection = JsonHelper.ExtractObject(aiConfigJson, "models");
        if (string.IsNullOrEmpty(modelsSection))
        {
            return "ERROR: 找不到 models 配置节";
        }
        
        string modelSection = JsonHelper.ExtractObject(modelsSection, modelKey);
        if (string.IsNullOrEmpty(modelSection))
        {
            return "ERROR: 找不到 " + modelKey + " 模型配置";
        }
        
        string provider = CoreHelper.JGet(modelSection, "provider");
        string model = CoreHelper.JGet(modelSection, "model");
        string apiKey = CoreHelper.JGet(modelSection, "api_key");
        string baseUrl = CoreHelper.JGet(modelSection, "base_url");
        string timeoutStr = CoreHelper.JGet(modelSection, "timeout_ms");
        string maxTokensStr = CoreHelper.JGet(modelSection, "max_tokens");
        string temperatureStr = CoreHelper.JGet(modelSection, "temperature");
        
        int timeoutMs = 60000;
        if (!string.IsNullOrEmpty(timeoutStr))
        {
            int.TryParse(timeoutStr, out timeoutMs);
        }
        
        int maxTokens = 4096;
        if (!string.IsNullOrEmpty(maxTokensStr))
        {
            int.TryParse(maxTokensStr, out maxTokens);
        }
        
        double temperature = 0.7;
        if (!string.IsNullOrEmpty(temperatureStr))
        {
            double.TryParse(temperatureStr, out temperature);
        }
        
        if (string.IsNullOrEmpty(apiKey))
        {
            return "ERROR: " + modelKey + " 的 api_key 未配置";
        }
        
        // 读取图片
        string base64Data = ReadFileAsBase64(imagePath);
        if (string.IsNullOrEmpty(base64Data))
        {
            return "ERROR: 无法读取图片文件: " + (imagePath ?? "null");
        }
        
        string mimeType = GetMimeType(imagePath);
        
        if (provider == "gemini")
        {
            return CallGeminiWithImage(prompt, base64Data, mimeType, apiKey, model, baseUrl, timeoutMs, maxTokens, temperature);
        }
        else
        {
            return CallOpenAIWithImage(prompt, base64Data, mimeType, apiKey, model, baseUrl, timeoutMs, maxTokens, temperature);
        }
    }
    
    /// <summary>
    /// 调用 Gemini API（带图片，inline_data base64 格式）
    /// </summary>
    public static string CallGeminiWithImage(string prompt, string base64Data, string mimeType,
        string apiKey, string model, string baseUrl, int timeoutMs, int maxTokens, double temperature)
    {
        if (string.IsNullOrEmpty(model))
        {
            model = "gemini-1.5-flash";
        }
        if (string.IsNullOrEmpty(baseUrl))
        {
            baseUrl = "https://generativelanguage.googleapis.com/v1beta";
        }
        if (timeoutMs <= 0)
        {
            timeoutMs = 60000;
        }
        
        string apiUrl = baseUrl + "/models/" + model + ":generateContent?key=" + apiKey;
        
        string escaped = JsonHelper.Escape(prompt);
        
        // Gemini 格式: parts 数组包含 text + inline_data
        string body = "{\"contents\":[{\"parts\":["
            + "{\"text\":\"" + escaped + "\"},"
            + "{\"inline_data\":{\"mime_type\":\"" + mimeType + "\",\"data\":\"" + base64Data + "\"}}"
            + "]}],"
            + "\"generationConfig\":{\"temperature\":" + temperature.ToString("F1")
            + ",\"maxOutputTokens\":" + maxTokens + "}}";
        
        return DoHttpPost(apiUrl, body, null, timeoutMs);
    }
    
    /// <summary>
    /// 调用 OpenAI 兼容 API（带图片，vision content 格式）
    /// </summary>
    public static string CallOpenAIWithImage(string prompt, string base64Data, string mimeType,
        string apiKey, string model, string baseUrl, int timeoutMs, int maxTokens, double temperature)
    {
        if (string.IsNullOrEmpty(model))
        {
            model = "gpt-4o-mini";
        }
        if (string.IsNullOrEmpty(baseUrl))
        {
            baseUrl = "https://api.openai.com/v1";
        }
        if (timeoutMs <= 0)
        {
            timeoutMs = 60000;
        }
        
        if (baseUrl.EndsWith("/"))
        {
            baseUrl = baseUrl.Substring(0, baseUrl.Length - 1);
        }
        
        string apiUrl = baseUrl + "/chat/completions";
        
        string escaped = JsonHelper.Escape(prompt);
        string dataUrl = "data:" + mimeType + ";base64," + base64Data;
        
        // OpenAI Vision 格式: content 数组包含 text + image_url
        string body = "{\"model\":\"" + model + "\","
            + "\"messages\":[{\"role\":\"user\",\"content\":["
            + "{\"type\":\"text\",\"text\":\"" + escaped + "\"},"
            + "{\"type\":\"image_url\",\"image_url\":{\"url\":\"" + dataUrl + "\"}}"
            + "]}],"
            + "\"max_tokens\":" + maxTokens + ","
            + "\"temperature\":" + temperature.ToString("F1") + "}";
        
        string authHeader = "Bearer " + apiKey;
        
        return DoHttpPost(apiUrl, body, authHeader, timeoutMs);
    }
    
    /// <summary>
    /// 带重试调用（带图片，使用配置中的重试设置）
    /// </summary>
    public static string CallWithRetryAndImage(string prompt, string imagePath, string aiConfigJson)
    {
        string retrySection = JsonHelper.ExtractObject(aiConfigJson, "retry_config");
        int maxRetries = 3;
        int retryDelayMs = 1000;
        double backoffMultiplier = 2.0;
        
        if (!string.IsNullOrEmpty(retrySection))
        {
            string maxStr = CoreHelper.JGet(retrySection, "max_retries");
            string delayStr = CoreHelper.JGet(retrySection, "retry_delay_ms");
            
            if (!string.IsNullOrEmpty(maxStr)) int.TryParse(maxStr, out maxRetries);
            if (!string.IsNullOrEmpty(delayStr)) int.TryParse(delayStr, out retryDelayMs);
        }
        
        // 尝试 primary
        string result = CallModelWithImage(prompt, imagePath, aiConfigJson, "primary");
        if (!result.StartsWith("ERROR:"))
        {
            return result;
        }
        
        // 重试 primary
        int delay = retryDelayMs;
        for (int i = 0; i < maxRetries - 1; i++)
        {
            System.Threading.Thread.Sleep(delay);
            result = CallModelWithImage(prompt, imagePath, aiConfigJson, "primary");
            if (!result.StartsWith("ERROR:"))
            {
                return result;
            }
            delay = (int)(delay * backoffMultiplier);
        }
        
        // 尝试 fallback
        result = CallModelWithImage(prompt, imagePath, aiConfigJson, "fallback");
        if (!result.StartsWith("ERROR:"))
        {
            return result;
        }
        
        // 尝试 backup
        result = CallModelWithImage(prompt, imagePath, aiConfigJson, "backup");
        return result;
    }
    
    // ========== HTTP 请求 ==========
    
    /// <summary>
    /// 执行 HTTP GET 请求
    /// </summary>
    public static string DoHttpGet(string url, int timeoutMs)
    {
        try
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            req.Timeout = timeoutMs;
            
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var reader = new StreamReader(resp.GetResponseStream()))
            {
                return reader.ReadToEnd();
            }
        }
        catch (WebException webEx)
        {
            return "ERROR: " + webEx.Message;
        }
        catch (Exception ex)
        {
            return "ERROR: " + ex.Message;
        }
    }
}
