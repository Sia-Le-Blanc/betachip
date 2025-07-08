#nullable disable
using System;
using System.Collections.Generic;
using MosaicCensorSystem.Detection;

namespace MosaicCensorSystem
{
    /// <summary>
    /// 설정 관리 클래스
    /// </summary>
    public static class Config
    {
        private static readonly Dictionary<string, Dictionary<string, object>> defaultConfig = new()
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
                ["default_strength"] = 15,
                ["default_targets"] = new List<string> { "눈", "손" }, // 가이드 권장 타겟
                ["conf_threshold"] = 0.3f, // 가이드 권장 신뢰도
                ["default_censor_type"] = CensorType.Mosaic,
                ["blur_kernel_multiplier"] = 2, // 블러 커널 크기 배수
                ["cache_enabled"] = true,
                ["nms_threshold"] = 0.45f
            },
            ["overlay"] = new Dictionary<string, object>
            {
                ["show_debug_info"] = false,
                ["fps_limit"] = 60,
                ["click_through"] = true,
                ["capture_protection"] = true,
                ["topmost_enforcement"] = true
            },
            ["detection"] = new Dictionary<string, object>
            {
                ["model_path"] = "Resources/best.onnx",
                ["input_size"] = 640,
                ["num_classes"] = 14,
                ["num_detections"] = 8400,
                ["tracking_enabled"] = true,
                ["stable_frame_threshold"] = 2,
                ["cache_cleanup_interval"] = 30
            },
            ["performance"] = new Dictionary<string, object>
            {
                ["gpu_priority"] = true,
                ["cuda_enabled"] = true,
                ["directml_enabled"] = true,
                ["cpu_threads"] = Environment.ProcessorCount,
                ["memory_optimization"] = true,
                ["mat_pool_size"] = 5
            }
        };

        /// <summary>
        /// 특정 섹션의 설정을 가져옵니다
        /// </summary>
        public static Dictionary<string, object> GetSection(string section)
        {
            if (defaultConfig.ContainsKey(section))
            {
                return new Dictionary<string, object>(defaultConfig[section]);
            }
            return new Dictionary<string, object>();
        }

        /// <summary>
        /// 특정 설정값을 가져옵니다
        /// </summary>
        public static T Get<T>(string section, string key, T defaultValue = default)
        {
            try
            {
                if (defaultConfig.ContainsKey(section) && 
                    defaultConfig[section].ContainsKey(key))
                {
                    var value = defaultConfig[section][key];
                    
                    // null 체크 먼저
                    if (value == null)
                    {
                        return defaultValue;
                    }
                    
                    // 타입 변환 처리
                    if (value is T directValue)
                    {
                        return directValue;
                    }
                    
                    // Convert.ChangeType을 사용한 타입 변환
                    if (typeof(T) == typeof(float) && value is double doubleValue)
                    {
                        return (T)(object)(float)doubleValue;
                    }
                    
                    var convertedValue = Convert.ChangeType(value, typeof(T));
                    if (convertedValue is T result)
                    {
                        return result;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ 설정 읽기 실패 [{section}.{key}]: {ex.Message}");
            }
            
            return defaultValue;
        }

        /// <summary>
        /// 설정값을 업데이트합니다
        /// </summary>
        public static void Set(string section, string key, object value)
        {
            try
            {
                if (!defaultConfig.ContainsKey(section))
                {
                    defaultConfig[section] = new Dictionary<string, object>();
                }
                
                defaultConfig[section][key] = value;
                Console.WriteLine($"⚙️ 설정 업데이트: [{section}.{key}] = {value}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ 설정 쓰기 실패 [{section}.{key}]: {ex.Message}");
            }
        }

        /// <summary>
        /// Dictionary의 확장 메서드 (GetValueOrDefault 대체)
        /// </summary>
        public static TValue GetValueOrDefault<TKey, TValue>(
            this Dictionary<TKey, TValue> dictionary, 
            TKey key, 
            TValue defaultValue = default)
        {
            if (dictionary.TryGetValue(key, out TValue value))
            {
                return value;
            }
            return defaultValue;
        }

        /// <summary>
        /// object를 특정 타입으로 안전하게 변환
        /// </summary>
        public static T GetValueOrDefault<T>(this Dictionary<string, object> dictionary, string key, T defaultValue = default)
        {
            try
            {
                if (dictionary.TryGetValue(key, out object value) && value != null)
                {
                    if (value is T directValue)
                    {
                        return directValue;
                    }
                    
                    // 타입 변환 시도
                    if (typeof(T) == typeof(float) && value is double doubleValue)
                    {
                        return (T)(object)(float)doubleValue;
                    }
                    
                    if (typeof(T) == typeof(bool) && value is string stringValue)
                    {
                        if (bool.TryParse(stringValue, out bool boolResult))
                        {
                            return (T)(object)boolResult;
                        }
                    }
                    
                    var convertedValue = Convert.ChangeType(value, typeof(T));
                    if (convertedValue is T result)
                    {
                        return result;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ 값 변환 실패 [{key}]: {ex.Message}");
            }
            
            return defaultValue;
        }

        /// <summary>
        /// 모든 설정을 출력합니다 (디버깅용)
        /// </summary>
        public static void PrintAllSettings()
        {
            Console.WriteLine("📋 현재 설정:");
            Console.WriteLine("=" + new string('=', 50));
            
            foreach (var section in defaultConfig)
            {
                Console.WriteLine($"[{section.Key}]");
                foreach (var setting in section.Value)
                {
                    string valueStr = setting.Value?.ToString() ?? "null";
                    if (setting.Value is List<string> list)
                    {
                        valueStr = $"[{string.Join(", ", list)}]";
                    }
                    Console.WriteLine($"  {setting.Key} = {valueStr}");
                }
                Console.WriteLine();
            }
        }

        /// <summary>
        /// 가이드에 맞는 기본 클래스 이름 매핑 가져오기
        /// </summary>
        public static Dictionary<int, string> GetDefaultClassNames()
        {
            return new Dictionary<int, string>
            {
                {0, "얼굴"}, {1, "가슴"}, {2, "겨드랑이"}, {3, "보지"}, {4, "발"},
                {5, "몸 전체"}, {6, "자지"}, {7, "팬티"}, {8, "눈"}, {9, "손"},
                {10, "교미"}, {11, "신발"}, {12, "가슴_옷"}, {13, "여성"}
            };
        }

        /// <summary>
        /// 가이드 권장 타겟 클래스 가져오기 (눈: 8, 손: 9)
        /// </summary>
        public static int[] GetDefaultTargetClasses()
        {
            return new int[] { 8, 9 };
        }

        /// <summary>
        /// NMS 임계값 설정 가져오기
        /// </summary>
        public static Dictionary<string, float> GetNMSThresholds()
        {
            return new Dictionary<string, float>
            {
                ["얼굴"] = 0.3f, ["가슴"] = 0.4f, ["겨드랑이"] = 0.4f, ["보지"] = 0.3f, ["발"] = 0.5f,
                ["몸 전체"] = 0.6f, ["자지"] = 0.3f, ["팬티"] = 0.4f, ["눈"] = 0.2f, ["손"] = 0.5f,
                ["교미"] = 0.3f, ["신발"] = 0.5f, ["가슴_옷"] = 0.4f, ["여성"] = 0.7f
            };
        }

        /// <summary>
        /// 검열 효과별 기본 강도 가져오기
        /// </summary>
        public static Dictionary<CensorType, int> GetDefaultStrengths()
        {
            return new Dictionary<CensorType, int>
            {
                [CensorType.Mosaic] = 15,  // 가이드 권장값
                [CensorType.Blur] = 10     // 블러는 좀 더 약하게
            };
        }

        /// <summary>
        /// 성능 최적화 설정 확인
        /// </summary>
        public static bool IsPerformanceOptimizationEnabled()
        {
            return Get<bool>("performance", "memory_optimization", true);
        }

        /// <summary>
        /// 트래킹 설정 확인
        /// </summary>
        public static bool IsTrackingEnabled()
        {
            return Get<bool>("detection", "tracking_enabled", true);
        }

        /// <summary>
        /// CUDA 사용 설정 확인
        /// </summary>
        public static bool IsCudaEnabled()
        {
            return Get<bool>("performance", "cuda_enabled", true);
        }

        /// <summary>
        /// DirectML 사용 설정 확인
        /// </summary>
        public static bool IsDirectMLEnabled()
        {
            return Get<bool>("performance", "directml_enabled", true);
        }
    }
}