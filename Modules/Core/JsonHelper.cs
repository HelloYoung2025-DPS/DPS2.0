// =====================================================
// JsonHelper.cs - JSON Processing Helper Class
// C# 5.0 ONLY - No $"", ?., nameof(), ??=, out var
// v4.1.0 - Robust stack-based parser with proper nesting
// =====================================================
using System;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Generic;

/// <summary>
/// JSON Processing Helper Class
/// Manual JSON parsing without external dependencies
/// Uses stack-based approach for robust nested structure handling
/// </summary>
public class JsonHelper
{
    // =========================================================================
    // CORE PARSING INFRASTRUCTURE
    // =========================================================================
    
    /// <summary>
    /// Skip whitespace characters starting from given index
    /// </summary>
    private static int SkipWhitespace(string json, int index)
    {
        while (index < json.Length && char.IsWhiteSpace(json[index]))
        {
            index++;
        }
        return index;
    }
    
    /// <summary>
    /// Parse a JSON string value starting at the opening quote
    /// Returns the end index (after closing quote) and the unescaped string content
    /// </summary>
    private static int ParseString(string json, int startIndex, out string value)
    {
        value = null;
        if (startIndex >= json.Length || json[startIndex] != '"')
        {
            return startIndex;
        }
        
        var sb = new StringBuilder();
        int i = startIndex + 1; // Skip opening quote
        
        while (i < json.Length)
        {
            char c = json[i];
            
            if (c == '"')
            {
                // End of string
                value = sb.ToString();
                return i + 1; // Return position after closing quote
            }
            
            if (c == '\\' && i + 1 < json.Length)
            {
                // Escape sequence
                char next = json[i + 1];
                switch (next)
                {
                    case '"':
                        sb.Append('"');
                        i += 2;
                        break;
                    case '\\':
                        sb.Append('\\');
                        i += 2;
                        break;
                    case '/':
                        sb.Append('/');
                        i += 2;
                        break;
                    case 'b':
                        sb.Append('\b');
                        i += 2;
                        break;
                    case 'f':
                        sb.Append('\f');
                        i += 2;
                        break;
                    case 'n':
                        sb.Append('\n');
                        i += 2;
                        break;
                    case 'r':
                        sb.Append('\r');
                        i += 2;
                        break;
                    case 't':
                        sb.Append('\t');
                        i += 2;
                        break;
                    case 'u':
                        // Unicode escape \uXXXX
                        if (i + 5 < json.Length)
                        {
                            string hex = json.Substring(i + 2, 4);
                            int code;
                            if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out code))
                            {
                                sb.Append((char)code);
                                i += 6;
                            }
                            else
                            {
                                sb.Append(c);
                                i++;
                            }
                        }
                        else
                        {
                            sb.Append(c);
                            i++;
                        }
                        break;
                    default:
                        sb.Append(c);
                        i++;
                        break;
                }
            }
            else
            {
                sb.Append(c);
                i++;
            }
        }
        
        // Unterminated string
        value = sb.ToString();
        return i;
    }
    
    /// <summary>
    /// Skip a JSON string value (without parsing content)
    /// Returns index after the closing quote
    /// </summary>
    private static int SkipString(string json, int startIndex)
    {
        if (startIndex >= json.Length || json[startIndex] != '"')
        {
            return startIndex;
        }
        
        int i = startIndex + 1;
        while (i < json.Length)
        {
            char c = json[i];
            if (c == '"')
            {
                return i + 1;
            }
            if (c == '\\' && i + 1 < json.Length)
            {
                // Skip escaped character
                i += 2;
            }
            else
            {
                i++;
            }
        }
        return i;
    }
    
    /// <summary>
    /// Skip a JSON value (any type) and return the end index
    /// Properly handles nested objects/arrays and strings with special chars
    /// </summary>
    private static int SkipValue(string json, int startIndex)
    {
        int i = SkipWhitespace(json, startIndex);
        if (i >= json.Length) return i;
        
        char c = json[i];
        
        // String
        if (c == '"')
        {
            return SkipString(json, i);
        }
        
        // Object
        if (c == '{')
        {
            int depth = 1;
            i++;
            while (i < json.Length && depth > 0)
            {
                c = json[i];
                if (c == '"')
                {
                    // Skip string to avoid counting braces inside strings
                    i = SkipString(json, i);
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
            return i;
        }
        
        // Array
        if (c == '[')
        {
            int depth = 1;
            i++;
            while (i < json.Length && depth > 0)
            {
                c = json[i];
                if (c == '"')
                {
                    i = SkipString(json, i);
                }
                else if (c == '[')
                {
                    depth++;
                    i++;
                }
                else if (c == ']')
                {
                    depth--;
                    i++;
                }
                else if (c == '{')
                {
                    // Nested object inside array
                    i = SkipValue(json, i);
                }
                else
                {
                    i++;
                }
            }
            return i;
        }
        
        // Number, boolean, null - read until delimiter
        while (i < json.Length)
        {
            c = json[i];
            if (c == ',' || c == '}' || c == ']' || char.IsWhiteSpace(c))
            {
                break;
            }
            i++;
        }
        return i;
    }
    
    /// <summary>
    /// Extract a raw JSON value (as string) starting at given index
    /// Returns the raw JSON text (including quotes for strings)
    /// </summary>
    private static string ExtractRawValue(string json, int startIndex, out int endIndex)
    {
        int i = SkipWhitespace(json, startIndex);
        endIndex = SkipValue(json, i);
        if (endIndex > i)
        {
            return json.Substring(i, endIndex - i);
        }
        return null;
    }
    
    // =========================================================================
    // PUBLIC API - GET METHODS
    // =========================================================================
    
    /// <summary>
    /// Get JSON field value (top-level only, depth-aware)
    /// Only matches keys at depth 1 (direct children of root object)
    /// Returns unescaped string for string values, raw text for others
    /// </summary>
    public static string Get(string json, string key)
    {
        if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key))
        {
            return null;
        }
        
        int i = SkipWhitespace(json, 0);
        if (i >= json.Length || json[i] != '{')
        {
            return null; // Not a JSON object
        }
        
        i++; // Skip opening brace
        int depth = 1; // We're inside the root object
        
        while (i < json.Length && depth > 0)
        {
            i = SkipWhitespace(json, i);
            if (i >= json.Length) break;
            
            char c = json[i];
            
            // End of current object
            if (c == '}')
            {
                depth--;
                i++;
                continue;
            }
            
            // Comma separator
            if (c == ',')
            {
                i++;
                continue;
            }
            
            // At depth 1, we look for keys
            if (depth == 1 && c == '"')
            {
                // Parse the key
                string currentKey;
                int keyEnd = ParseString(json, i, out currentKey);
                
                // Skip to colon
                int colonIdx = SkipWhitespace(json, keyEnd);
                if (colonIdx >= json.Length || json[colonIdx] != ':')
                {
                    i = keyEnd;
                    continue;
                }
                
                // Skip colon and whitespace
                int valueStart = SkipWhitespace(json, colonIdx + 1);
                
                // Check if this is the key we're looking for
                if (currentKey == key)
                {
                    // Found it! Extract the value
                    if (valueStart >= json.Length)
                    {
                        return null;
                    }
                    
                    char vc = json[valueStart];
                    
                    // String value - return unescaped
                    if (vc == '"')
                    {
                        string strValue;
                        ParseString(json, valueStart, out strValue);
                        return strValue;
                    }
                    
                    // Object or array - return raw JSON
                    if (vc == '{' || vc == '[')
                    {
                        int endIdx;
                        return ExtractRawValue(json, valueStart, out endIdx);
                    }
                    
                    // Number, boolean, null - return raw text
                    int valEnd = valueStart;
                    while (valEnd < json.Length)
                    {
                        char ec = json[valEnd];
                        if (ec == ',' || ec == '}' || ec == ']' || char.IsWhiteSpace(ec))
                        {
                            break;
                        }
                        valEnd++;
                    }
                    return json.Substring(valueStart, valEnd - valueStart);
                }
                
                // Not our key, skip the value
                i = SkipValue(json, valueStart);
            }
            else if (c == '"')
            {
                // Key at deeper depth - skip key and value
                int keyEnd = SkipString(json, i);
                int colonIdx = SkipWhitespace(json, keyEnd);
                if (colonIdx < json.Length && json[colonIdx] == ':')
                {
                    i = SkipValue(json, colonIdx + 1);
                }
                else
                {
                    i = keyEnd;
                }
            }
            else if (c == '{')
            {
                // Entering nested object
                depth++;
                i++;
            }
            else if (c == '[')
            {
                // Array - skip it entirely
                i = SkipValue(json, i);
            }
            else
            {
                i++;
            }
        }
        
        return null; // Key not found
    }
    
    /// <summary>
    /// Get nested field value using dot notation (e.g., "user.profile.name")
    /// </summary>
    public static string GetNested(string json, string path)
    {
        if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(path))
        {
            return null;
        }
        
        string[] parts = path.Split('.');
        string current = json;
        
        foreach (string p in parts)
        {
            current = Get(current, p);
            if (current == null)
            {
                return null;
            }
        }
        
        return current;
    }
    
    /// <summary>
    /// Extract nested JSON object (alias for Get with clearer semantics)
    /// </summary>
    public static string ExtractObject(string json, string key)
    {
        return Get(json, key);
    }
    
    /// <summary>
    /// Extract JSON array
    /// </summary>
    public static string ExtractArray(string json, string key)
    {
        return Get(json, key);
    }
    
    /// <summary>
    /// Get array elements as string array
    /// </summary>
    public static string[] GetArray(string json, string key)
    {
        string arrayJson = json;
        if (!string.IsNullOrEmpty(key))
        {
            arrayJson = Get(json, key);
        }
        if (string.IsNullOrEmpty(arrayJson))
        {
            return new string[0];
        }
        
        return ParseArrayElements(arrayJson);
    }
    
    /// <summary>
    /// Parse a JSON array string into individual element strings
    /// </summary>
    private static string[] ParseArrayElements(string arrayJson)
    {
        if (string.IsNullOrEmpty(arrayJson))
        {
            return new string[0];
        }
        
        int i = SkipWhitespace(arrayJson, 0);
        if (i >= arrayJson.Length || arrayJson[i] != '[')
        {
            return new string[0];
        }
        
        var elements = new List<string>();
        i++; // Skip opening bracket
        
        while (i < arrayJson.Length)
        {
            i = SkipWhitespace(arrayJson, i);
            if (i >= arrayJson.Length) break;
            
            char c = arrayJson[i];
            
            if (c == ']')
            {
                break; // End of array
            }
            
            if (c == ',')
            {
                i++;
                continue;
            }
            
            // Extract element value
            int endIdx;
            string element = ExtractRawValue(arrayJson, i, out endIdx);
            
            if (element != null)
            {
                // For string elements, unescape them
                if (element.Length >= 2 && element[0] == '"' && element[element.Length - 1] == '"')
                {
                    string unescaped;
                    ParseString(element, 0, out unescaped);
                    elements.Add(unescaped);
                }
                else
                {
                    elements.Add(element);
                }
            }
            
            i = endIdx;
        }
        
        return elements.ToArray();
    }
    
    /// <summary>
    /// Get a specific element from a JSON array by index (returns raw JSON)
    /// </summary>
    public static string GetArrayElement(string arrayJson, int index)
    {
        if (string.IsNullOrEmpty(arrayJson) || index < 0)
        {
            return null;
        }
        
        int i = SkipWhitespace(arrayJson, 0);
        if (i >= arrayJson.Length || arrayJson[i] != '[')
        {
            return null;
        }
        
        i++;
        int currentIndex = 0;
        
        while (i < arrayJson.Length)
        {
            i = SkipWhitespace(arrayJson, i);
            if (i >= arrayJson.Length) break;
            
            char c = arrayJson[i];
            
            if (c == ']')
            {
                break;
            }
            
            if (c == ',')
            {
                i++;
                continue;
            }
            
            int endIdx;
            string element = ExtractRawValue(arrayJson, i, out endIdx);
            
            if (currentIndex == index)
            {
                return element;
            }
            
            currentIndex++;
            i = endIdx;
        }
        
        return null;
    }
    
    /// <summary>
    /// Get integer value with default
    /// </summary>
    public static int GetInt(string json, string key, int defaultValue)
    {
        string v = Get(json, key);
        int result;
        if (int.TryParse(v, out result))
        {
            return result;
        }
        return defaultValue;
    }
    
    /// <summary>
    /// Get double value with default
    /// </summary>
    public static double GetDouble(string json, string key, double defaultValue)
    {
        string v = Get(json, key);
        double result;
        // BUG-03 fix: 使用 InvariantCulture 确保 "0.5" 在任何 locale 下都正确解析
        if (double.TryParse(v, System.Globalization.NumberStyles.Float | System.Globalization.NumberStyles.AllowLeadingSign,
            System.Globalization.CultureInfo.InvariantCulture, out result))
        {
            return result;
        }
        return defaultValue;
    }
    
    /// <summary>
    /// Get boolean value with default
    /// </summary>
    public static bool GetBool(string json, string key, bool defaultValue)
    {
        string v = Get(json, key);
        if (string.IsNullOrEmpty(v))
        {
            return defaultValue;
        }
        v = v.ToLower().Trim();
        if (v == "true") return true;
        if (v == "false") return false;
        return defaultValue;
    }
    
    // =========================================================================
    // PUBLIC API - SET METHOD
    // =========================================================================
    
    /// <summary>
    /// Set field value in JSON (top-level only)
    /// Creates new field if not exists, updates if exists
    /// </summary>
    public static string Set(string json, string key, string newValue, bool isString)
    {
        if (string.IsNullOrEmpty(json))
        {
            if (isString)
            {
                return "{\"" + key + "\": \"" + Escape(newValue) + "\"}";
            }
            return "{\"" + key + "\": " + newValue + "}";
        }
        
        // Find the key at top level
        int i = SkipWhitespace(json, 0);
        if (i >= json.Length || json[i] != '{')
        {
            // Not a valid object, return as-is
            return json;
        }
        
        i++; // Skip opening brace
        int depth = 1;
        int keyStart = -1;
        int valueStart = -1;
        int valueEnd = -1;
        
        while (i < json.Length && depth > 0)
        {
            i = SkipWhitespace(json, i);
            if (i >= json.Length) break;
            
            char c = json[i];
            
            if (c == '}')
            {
                depth--;
                i++;
                continue;
            }
            
            if (c == ',')
            {
                i++;
                continue;
            }
            
            if (depth == 1 && c == '"')
            {
                int currentKeyStart = i;
                string currentKey;
                int keyEnd = ParseString(json, i, out currentKey);
                
                int colonIdx = SkipWhitespace(json, keyEnd);
                if (colonIdx >= json.Length || json[colonIdx] != ':')
                {
                    i = keyEnd;
                    continue;
                }
                
                int valStart = SkipWhitespace(json, colonIdx + 1);
                int valEnd = SkipValue(json, valStart);
                
                if (currentKey == key)
                {
                    // Found the key to update
                    keyStart = currentKeyStart;
                    valueStart = valStart;
                    valueEnd = valEnd;
                    break;
                }
                
                i = valEnd;
            }
            else if (c == '{')
            {
                depth++;
                i++;
            }
            else if (c == '[')
            {
                i = SkipValue(json, i);
            }
            else
            {
                i++;
            }
        }
        
        string formattedValue;
        if (isString)
        {
            formattedValue = "\"" + Escape(newValue) + "\"";
        }
        else
        {
            formattedValue = newValue;
        }
        
        if (valueStart >= 0 && valueEnd >= 0)
        {
            return json.Substring(0, valueStart) + formattedValue + json.Substring(valueEnd);
        }
        else
        {
            // Add new field before closing brace
            int lastBrace = json.LastIndexOf('}');
            if (lastBrace < 0)
            {
                return json;
            }
            
            // Check if object is empty
            string beforeBrace = json.Substring(0, lastBrace).Trim();
            string insert;
            if (beforeBrace.EndsWith("{"))
            {
                // Empty object
                insert = "\"" + key + "\": " + formattedValue;
            }
            else
            {
                insert = ", \"" + key + "\": " + formattedValue;
            }
            
            return json.Substring(0, lastBrace) + insert + "}";
        }
    }
    
    // =========================================================================
    // PUBLIC API - ESCAPE/UNESCAPE
    // =========================================================================
    
    /// <summary>
    /// Escape string for JSON (for HTTP requests)
    /// </summary>
    public static string Escape(string s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return "";
        }
        
        var sb = new StringBuilder();
        foreach (char c in s)
        {
            switch (c)
            {
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                case '\b':
                    sb.Append("\\b");
                    break;
                case '\f':
                    sb.Append("\\f");
                    break;
                default:
                    if (c < 32)
                    {
                        // Control character - use unicode escape
                        sb.Append("\\u");
                        sb.Append(((int)c).ToString("x4"));
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
        return sb.ToString();
    }
    
    /// <summary>
    /// Unescape JSON string (for HTTP responses)
    /// Supports Unicode escape sequences \uXXXX
    /// </summary>
    public static string Unescape(string s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return "";
        }
        
        // Use ParseString logic for consistent unescaping
        // Wrap in quotes to use the parser
        string wrapped = "\"" + s + "\"";
        string result;
        ParseString(wrapped, 0, out result);
        
        if (result != null)
        {
            return result;
        }
        
        // Fallback to regex-based approach
        s = Regex.Replace(s, @"\\u([0-9a-fA-F]{4})", delegate(Match m) {
            int code;
            if (int.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, null, out code))
            {
                return ((char)code).ToString();
            }
            return m.Value;
        });
        
        return s
            .Replace("\\n", "\n")
            .Replace("\\r", "\r")
            .Replace("\\t", "\t")
            .Replace("\\\"", "\"")
            .Replace("\\\\", "\\");
    }
    
    // =========================================================================
    // PUBLIC API - VALIDATION
    // =========================================================================
    
    /// <summary>
    /// Check if string is valid JSON
    /// </summary>
    public static bool IsValidJson(string json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return false;
        }
        
        try
        {
            int i = SkipWhitespace(json, 0);
            if (i >= json.Length)
            {
                return false;
            }
            
            char c = json[i];
            
            // Must start with { or [
            if (c != '{' && c != '[')
            {
                return false;
            }
            
            // Try to skip the entire value
            int endIdx = SkipValue(json, i);
            
            // Check that we consumed the whole string (minus trailing whitespace)
            int remaining = SkipWhitespace(json, endIdx);
            return remaining >= json.Length;
        }
        catch (Exception)
        {
            // 任何解析异常都表示 JSON 无效
            return false;
        }
    }
    
    // =========================================================================
    // PUBLIC API - CREATE OBJECT
    // =========================================================================
    
    /// <summary>
    /// Create simple JSON object from key-value pairs
    /// </summary>
    public static string CreateObject(params KeyValuePair<string, object>[] pairs)
    {
        var sb = new StringBuilder("{");
        for (int i = 0; i < pairs.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }
            
            sb.Append("\"");
            sb.Append(Escape(pairs[i].Key));
            sb.Append("\": ");
            
            object val = pairs[i].Value;
            if (val == null)
            {
                sb.Append("null");
            }
            else if (val is string)
            {
                sb.Append("\"");
                sb.Append(Escape((string)val));
                sb.Append("\"");
            }
            else if (val is bool)
            {
                sb.Append((bool)val ? "true" : "false");
            }
            else if (val is int || val is long || val is float || val is double || val is decimal)
            {
                sb.Append(val.ToString());
            }
            else
            {
                // Treat as string
                sb.Append("\"");
                sb.Append(Escape(val.ToString()));
                sb.Append("\"");
            }
        }
        sb.Append("}");
        return sb.ToString();
    }
    
    /// <summary>
    /// Create JSON array from string values
    /// </summary>
    public static string CreateArray(params string[] values)
    {
        var sb = new StringBuilder("[");
        for (int i = 0; i < values.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }
            
            if (values[i] == null)
            {
                sb.Append("null");
            }
            else
            {
                sb.Append("\"");
                sb.Append(Escape(values[i]));
                sb.Append("\"");
            }
        }
        sb.Append("]");
        return sb.ToString();
    }
}
