using System;
using System.Collections.Generic;

namespace MosaicCensorSystem
{
    /// <summary>
    /// 설정 관리 클래스
    /// </summary>
    public static class Config
    {
        private static readonly Dictionary<string, Dictionary<string, object>> sections = new Dictionary<string, Dictionary<string, object>>
        {
            ["capture"] = new Dictionary<string, object>
            {
                ["downscale"] = 1.0,
                ["debug_mode"] = false,
                ["debug_save_interval"] = 300,
                ["queue_size"] = 2,
                ["log_interval"] = 100
            },
            ["mosaic"] = new Dictionary<string, object>
            {
                ["default_strength"] = 25,
                ["default_targets"] = new List<string> { "얼굴", "가슴", "보지", "팬티" }
            },
            ["overlay"] = new Dictionary<string, object>
            {
                ["show_debug_info"] = false,
                ["fps_limit"] = 30
            }
        };

        public static Dictionary<string, object> GetSection(string sectionName)
        {
            if (sections.TryGetValue(sectionName, out Dictionary<string, object>? value))
            {
                return value;
            }
            return new Dictionary<string, object>();
        }

        public static T GetValue<T>(string section, string key, T defaultValue = default!)
        {
            try
            {
                if (sections.ContainsKey(section) && sections[section].ContainsKey(key))
                {
                    var value = sections[section][key];
                    if (value is T typedValue)
                        return typedValue;
                    
                    if (value != null)
                    {
                        return (T)Convert.ChangeType(value, typeof(T));
                    }
                }
                return defaultValue;
            }
            catch
            {
                return defaultValue;
            }
        }

        public static void SetValue(string section, string key, object value)
        {
            if (!sections.ContainsKey(section))
                sections[section] = new Dictionary<string, object>();
            
            sections[section][key] = value;
        }
    }

    /// <summary>
    /// Dictionary 확장 메서드
    /// </summary>
    public static class DictionaryExtensions
    {
        public static TValue GetValueOrDefault<TKey, TValue>(this Dictionary<TKey, TValue> dict, TKey key, TValue defaultValue = default!)
            where TKey : notnull
        {
            if (dict.TryGetValue(key, out TValue? value))
            {
                return value;
            }
            return defaultValue;
        }
    }
}