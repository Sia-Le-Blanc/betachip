using System;
using System.IO;
using System.Windows.Forms;
using Microsoft.ML.OnnxRuntime;

namespace MosaicCensorSystem
{
    internal static class Program
    {
        // PyInstaller 환경과 유사하게 리소스 경로 처리
        public static string ONNX_MODEL_PATH { get; private set; } = "";

        [STAThread]
        static void Main()
        {
            Console.WriteLine("✅ 프로그램 시작됨");

            // 리소스 경로 설정
            ONNX_MODEL_PATH = ResourcePath("Resources/best.onnx");

            // ONNX 모델 로딩 테스트
            Console.WriteLine("📡 ONNX 모델 로딩 시도");
            try
            {
                using (var session = new InferenceSession(ONNX_MODEL_PATH))
                {
                    Console.WriteLine("✅ 모델 로딩 성공");
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"❌ 모델 로딩 실패: {e.Message}");
            }

            Console.WriteLine("🪟 GUI 루프 진입 준비됨");

            // Windows Forms 설정
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            
            // 메인 앱 실행
            var app = new MosaicApp();
            Application.Run(app.Root);
        }

        /// <summary>
        /// PyInstaller 환경에서도 리소스 경로를 안전하게 불러오기
        /// </summary>
        public static string ResourcePath(string relativePath)
        {
            // 실행 파일이 있는 디렉토리를 기준으로 경로 설정
            string basePath = AppDomain.CurrentDomain.BaseDirectory;
            return Path.Combine(basePath, relativePath);
        }
    }
}