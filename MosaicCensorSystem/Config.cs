using System;
using System.Collections.Generic;

namespace MosaicCensorSystem
{
    /// <summary>
    /// 애플리케이션 설정 관리 클래스
    /// </summary>
    public static class Config
    {
        private static readonly Dictionary<string, Dictionary<string, object>> configData = 
            new Dictionary<string, Dictionary<string, object>>
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
                    ["model_path"] = "Resources/best.onnx",
                    ["conf_threshold"] = 0.1,
                    ["default_targets"] = new List<string> { "얼굴", "가슴", "보지", "팬티" },
                    ["default_strength"] = 15
                },
                ["overlay"] = new Dictionary<string, object>
                {
                    ["show_debug_info"] = false,
                    ["fps_limit"] = 30
                },
                ["models"] = new Dictionary<string, object>
                {
                    ["class_names"] = new List<string>
                    {
                        "얼굴", "가슴", "겨드랑이", "보지", "발", "몸 전체",
                        "자지", "팬티", "눈", "손", "교미", "신발",
                        "가슴_옷", "보지_옷", "여성"
                    }
                }
            };

        public static Dictionary<string, object> GetSection(string section)
        {
            return configData.ContainsKey(section) ? configData[section] : new Dictionary<string, object>();
        }

        public static T Get<T>(string section, string key, T defaultValue = default!)
        {
            try
            {
                if (configData.ContainsKey(section) && configData[section].ContainsKey(key))
                {
                    var value = configData[section][key];
                    if (value is T directValue)
                        return directValue;
                    
                    // 타입 변환 시도
                    if (typeof(T) == typeof(bool) && value is bool boolValue)
                        return (T)(object)boolValue;
                    if (typeof(T) == typeof(int) && value is int intValue)
                        return (T)(object)intValue;
                    if (typeof(T) == typeof(double) && value is double doubleValue)
                        return (T)(object)doubleValue;
                    if (typeof(T) == typeof(float))
                    {
                        if (value is double d)
                            return (T)(object)(float)d;
                        if (value is float f)
                            return (T)(object)f;
                    }
                    if (typeof(T) == typeof(string) && value is string stringValue)
                        return (T)(object)stringValue;
                    if (typeof(T) == typeof(List<string>) && value is List<string> listValue)
                        return (T)(object)listValue;
                    
                    // 일반적인 타입 변환
                    return (T)Convert.ChangeType(value, typeof(T));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ 설정 읽기 오류 [{section}.{key}]: {ex.Message}");
            }
            
            return defaultValue;
        }

        public static void Set(string section, string key, object value)
        {
            if (!configData.ContainsKey(section))
                configData[section] = new Dictionary<string, object>();
            
            configData[section][key] = value;
        }
    }

    /// <summary>
    /// Dictionary 확장 메서드
    /// </summary>
    public static class DictionaryExtensions
    {
        public static T GetValueOrDefault<T>(this Dictionary<string, object>? dict, string key, T defaultValue = default!)
        {
            if (dict == null || !dict.ContainsKey(key))
                return defaultValue;

            try
            {
                var value = dict[key];
                if (value is T directValue)
                    return directValue;

                // 특별한 타입 변환들
                if (typeof(T) == typeof(bool) && value is bool boolValue)
                    return (T)(object)boolValue;
                if (typeof(T) == typeof(int) && value is int intValue)
                    return (T)(object)intValue;
                if (typeof(T) == typeof(double) && value is double doubleValue)
                    return (T)(object)doubleValue;
                if (typeof(T) == typeof(float))
                {
                    if (value is double d)
                        return (T)(object)(float)d;
                    if (value is float f)
                        return (T)(object)f;
                }
                if (typeof(T) == typeof(string) && value is string stringValue)
                    return (T)(object)stringValue;
                if (typeof(T) == typeof(List<string>) && value is List<string> listValue)
                    return (T)(object)listValue;
                
                // 일반적인 타입 변환
                return (T)Convert.ChangeType(value, typeof(T));
            }
            catch
            {
                return defaultValue;
            }
        }
    }
}