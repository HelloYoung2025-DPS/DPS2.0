// =====================================================
// FileHelper.cs - 文件操作辅助类
// ⚠️ C# 5.0 语法 - 禁止使用 $""、?.、nameof() 等
// =====================================================
using System;
using System.IO;
using System.Text;
using System.Collections.Generic;

/// <summary>
/// 文件操作辅助类
/// </summary>
public class FileHelper
{
    /// <summary>
    /// 确保目录存在
    /// </summary>
    public static void EnsureDir(string dirPath)
    {
        if (!Directory.Exists(dirPath))
        {
            Directory.CreateDirectory(dirPath);
        }
    }
    
    /// <summary>
    /// 读取文件内容
    /// </summary>
    public static string Read(string path)
    {
        if (!File.Exists(path))
        {
            return "";
        }
        return File.ReadAllText(path, Encoding.UTF8);
    }
    
    /// <summary>
    /// 读取所有行
    /// </summary>
    public static string[] ReadLines(string path)
    {
        if (!File.Exists(path))
        {
            return new string[0];
        }
        return File.ReadAllLines(path, Encoding.UTF8);
    }
    
    /// <summary>
    /// 写入文件（自动创建目录）
    /// </summary>
    public static void Write(string path, string content)
    {
        string dir = Path.GetDirectoryName(path);
        EnsureDir(dir);
        File.WriteAllText(path, content, Encoding.UTF8);
    }
    
    /// <summary>
    /// 写入所有行
    /// </summary>
    public static void WriteLines(string path, string[] lines)
    {
        string dir = Path.GetDirectoryName(path);
        EnsureDir(dir);
        File.WriteAllLines(path, lines, Encoding.UTF8);
    }
    
    /// <summary>
    /// 追加内容
    /// </summary>
    public static void Append(string path, string content)
    {
        string dir = Path.GetDirectoryName(path);
        EnsureDir(dir);
        File.AppendAllText(path, content, Encoding.UTF8);
    }
    
    /// <summary>
    /// 追加一行
    /// </summary>
    public static void AppendLine(string path, string line)
    {
        Append(path, line + Environment.NewLine);
    }
    
    /// <summary>
    /// 安全写入（原子操作，防止数据丢失）
    /// </summary>
    public static void WriteAtomic(string path, string content)
    {
        string dir = Path.GetDirectoryName(path);
        EnsureDir(dir);
        
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, content, Encoding.UTF8);
        
        if (File.Exists(path))
        {
            File.Replace(tmp, path, path + ".bak", true);
        }
        else
        {
            File.Move(tmp, path);
        }
    }
    
    /// <summary>
    /// 检查文件是否存在
    /// </summary>
    public static bool Exists(string path)
    {
        return File.Exists(path);
    }
    
    /// <summary>
    /// 检查目录是否存在
    /// </summary>
    public static bool DirExists(string path)
    {
        return Directory.Exists(path);
    }
    
    /// <summary>
    /// 删除文件
    /// </summary>
    public static void Delete(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
    
    /// <summary>
    /// 复制文件
    /// </summary>
    public static void Copy(string source, string dest, bool overwrite)
    {
        string dir = Path.GetDirectoryName(dest);
        EnsureDir(dir);
        File.Copy(source, dest, overwrite);
    }
    
    /// <summary>
    /// 移动文件
    /// </summary>
    public static void Move(string source, string dest)
    {
        string dir = Path.GetDirectoryName(dest);
        EnsureDir(dir);
        
        if (File.Exists(dest))
        {
            File.Delete(dest);
        }
        File.Move(source, dest);
    }
    
    /// <summary>
    /// 获取目录下所有文件
    /// </summary>
    public static string[] GetFiles(string dirPath, string pattern)
    {
        if (!Directory.Exists(dirPath))
        {
            return new string[0];
        }
        return Directory.GetFiles(dirPath, pattern);
    }
    
    /// <summary>
    /// 获取文件信息
    /// </summary>
    public static FileInfo GetInfo(string path)
    {
        return new FileInfo(path);
    }
    
    /// <summary>
    /// 获取文件大小（字节）
    /// </summary>
    public static long GetSize(string path)
    {
        if (!File.Exists(path)) return 0;
        return new FileInfo(path).Length;
    }
    
    /// <summary>
    /// 获取文件最后修改时间
    /// </summary>
    public static DateTime GetLastWriteTime(string path)
    {
        if (!File.Exists(path)) return DateTime.MinValue;
        return File.GetLastWriteTime(path);
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
    /// 组合路径
    /// </summary>
    public static string Combine(string path1, string path2)
    {
        return Path.Combine(path1, path2);
    }
    
    /// <summary>
    /// 获取文件名（不含路径）
    /// </summary>
    public static string GetFileName(string path)
    {
        return Path.GetFileName(path);
    }
    
    /// <summary>
    /// 获取文件名（不含扩展名）
    /// </summary>
    public static string GetFileNameWithoutExtension(string path)
    {
        return Path.GetFileNameWithoutExtension(path);
    }
    
    /// <summary>
    /// 获取目录路径
    /// </summary>
    public static string GetDirectory(string path)
    {
        return Path.GetDirectoryName(path);
    }
}
