using System;
using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace super_toolbox
{
    public class AwExtractor : BaseExtractor
    {
        private static string _tempExePath;

        static AwExtractor()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "supertoolbox_temp");
            Directory.CreateDirectory(tempDir);
            _tempExePath = Path.Combine(tempDir, "wsyster.exe");

            if (!File.Exists(_tempExePath))
            {
                using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("embedded.wsyster.exe"))
                {
                    if (stream == null)
                        throw new FileNotFoundException("嵌入的EXE资源未找到");

                    byte[] buffer = new byte[stream.Length];
                    stream.Read(buffer, 0, buffer.Length);
                    File.WriteAllBytes(_tempExePath, buffer);
                }
            }
        }

        public override async Task ExtractAsync(string directoryPath, CancellationToken cancellationToken = default)
        {
            if (!Directory.Exists(directoryPath))
            {
                OnExtractionFailed("错误：目录不存在");
                return;
            }

            var wsysFiles = Directory.GetFiles(directoryPath, "*.wsys");
            if (wsysFiles.Length == 0)
            {
                OnExtractionFailed("未找到.wsys文件");
                return;
            }

            string extractDir = Path.Combine(directoryPath, "Extracted");
            Directory.CreateDirectory(extractDir);

            try
            {
                await Task.Run(() =>
                {
                    foreach (var file in Directory.GetFiles(extractDir, "*.wav"))
                    {
                        File.Delete(file);
                    }

                    foreach (var wsysFilePath in wsysFiles)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var process = new Process
                        {
                            StartInfo = new ProcessStartInfo
                            {
                                FileName = _tempExePath,
                                Arguments = $"\"{wsysFilePath}\"",
                                WorkingDirectory = Path.GetDirectoryName(wsysFilePath),
                                UseShellExecute = false,
                                CreateNoWindow = true
                            }
                        };
                        process.Start();
                        process.WaitForExit();
                    }

                    var wavFiles = Directory.GetFiles(directoryPath, "*.wav");
                    foreach (var wavFile in wavFiles)
                    {
                        string destPath = Path.Combine(extractDir, Path.GetFileName(wavFile));
                        if (File.Exists(destPath)) File.Delete(destPath);
                        File.Move(wavFile, destPath);
                    }

                    var finalFiles = Directory.GetFiles(extractDir, "*.wav");
                    TotalFilesToExtract = finalFiles.Length;

                    foreach (var file in finalFiles)
                    {
                        OnFileExtracted(file);
                    }

                    OnExtractionCompleted();
                }, cancellationToken);
            }
            catch (Exception ex)
            {
                OnExtractionFailed($"处理失败: {ex.Message}");
            }
        }

        public override void Extract(string directoryPath)
        {
            ExtractAsync(directoryPath).Wait();
        }
    }
}