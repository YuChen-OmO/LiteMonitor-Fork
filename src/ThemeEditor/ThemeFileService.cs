using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using LiteMonitor.src.Core;

namespace LiteMonitor.ThemeEditor
{
    /// <summary>
    /// 管理主题文件：加载、保存、新建、复制、重命名、删除
    /// </summary>
    public static class ThemeFileService
    {
        /// <summary>
        /// 主题目录
        /// </summary>
        private static string Dir => ThemeManager.ThemeDir;

        /// <summary>
        /// 列出所有主题文件
        /// </summary>
        public static List<string> ListThemes()
        {
            if (!Directory.Exists(Dir))
                Directory.CreateDirectory(Dir);

            var list = new List<string>();

            foreach (var f in Directory.GetFiles(Dir, "*.json"))
                list.Add(Path.GetFileNameWithoutExtension(f));

            list.Sort();
            return list;
        }

        /// <summary>
        /// 加载主题 JSON → Theme 对象
        /// </summary>
        public static Theme LoadTheme(string name)
        {
            string path = Path.Combine(Dir, name + ".json");

            if (!File.Exists(path))
                throw new FileNotFoundException($"Theme not found: {path}");

            string json = File.ReadAllText(path);

            var theme = JsonSerializer.Deserialize<Theme>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                AllowTrailingCommas = true,
            });

            if (theme == null)
                throw new Exception($"Failed to parse theme JSON: {name}");

            theme.BuildFonts();

            return theme;
        }

        /// <summary>
        /// 保存 Theme 对象 → JSON 文件
        /// </summary>
        public static void SaveTheme(string name, Theme theme)
        {
            string path = Path.Combine(Dir, name + ".json");
            string json = JsonSerializer.Serialize(theme, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            SafeWrite(path, json);
        }

        /// <summary>
        /// 新建主题（复制模板）
        /// </summary>
        public static void CreateTheme(string name)
        {
            string path = Path.Combine(Dir, name + ".json");

            if (File.Exists(path))
                throw new Exception("Theme already exists: " + name);

            // 默认复制当前主题模板
            string template = Path.Combine(Dir, "DarkFlat_Classic.json");

            if (File.Exists(template))
                File.Copy(template, path);
            else
                File.WriteAllText(path, "{}");
        }

        /// <summary>
        /// 复制主题
        /// </summary>
        public static void DuplicateTheme(string src, string dst)
        {
            string srcPath = Path.Combine(Dir, src + ".json");
            string dstPath = Path.Combine(Dir, dst + ".json");

            if (!File.Exists(srcPath))
                throw new FileNotFoundException("Source theme missing: " + src);

            if (File.Exists(dstPath))
                throw new Exception("Target theme already exists: " + dst);

            File.Copy(srcPath, dstPath);
        }

        /// <summary>
        /// 重命名主题
        /// </summary>
        public static void RenameTheme(string src, string dst)
        {
            string srcPath = Path.Combine(Dir, src + ".json");
            string dstPath = Path.Combine(Dir, dst + ".json");

            if (!File.Exists(srcPath))
                throw new FileNotFoundException("Source theme missing: " + src);

            if (File.Exists(dstPath))
                throw new Exception("Target theme already exists: " + dst);

            File.Move(srcPath, dstPath);
        }

        /// <summary>
        /// 删除主题
        /// </summary>
        public static void DeleteTheme(string name)
        {
            string path = Path.Combine(Dir, name + ".json");
            if (File.Exists(path))
                File.Delete(path);
        }

        /// <summary>
        /// 安全写入（避免 JSON 写坏）
        /// </summary>
        private static void SafeWrite(string path, string content)
        {
            string tmp = path + ".tmp";

            File.WriteAllText(tmp, content);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);

            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }
}
