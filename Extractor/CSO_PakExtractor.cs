using System;
using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace super_toolbox
{
    public class CSO_PakExtractor : BaseExtractor
    {
        private static string _tempExePath;

        static CSO_PakExtractor()
        {
            _tempExePath = LoadEmbeddedExe("embedded.csopak.exe", "csopak.exe");
        }

        public override async Task ExtractAsync(string directoryPath, CancellationToken cancellationToken = default)
        {
            if (!Directory.Exists(directoryPath))
            {
                OnExtractionFailed("错误：目录不存在");
                return;
            }

            var pakFiles = Directory.GetFiles(directoryPath, "*.pak");
            if (pakFiles.Length == 0)
            {
                OnExtractionFailed("未找到.pak文件");
                return;
            }

            TotalFilesToExtract = pakFiles.Length;

            try
            {
                await Task.Run(() =>
                {
                    foreach (var pakFilePath in pakFiles)
                    {
                        ThrowIfCancellationRequested(cancellationToken);

                        try
                        {
                            var process = new Process
                            {
                                StartInfo = new ProcessStartInfo
                                {
                                    FileName = _tempExePath,
                                    Arguments = $"\"{pakFilePath}\"",
                                    WorkingDirectory = Path.GetDirectoryName(pakFilePath),
                                    UseShellExecute = false,
                                    CreateNoWindow = true,
                                    RedirectStandardOutput = true,
                                    RedirectStandardError = true
                                }
                            };

                            process.OutputDataReceived += (sender, e) =>
                            {
                                if (!string.IsNullOrEmpty(e.Data))
                                {
                                    OnFileExtracted(e.Data);
                                }
                            };

                            process.Start();
                            process.BeginOutputReadLine();
                            process.WaitForExit();

                            if (process.ExitCode != 0)
                            {
                                string error = process.StandardError.ReadToEnd();
                                throw new Exception($"处理文件 {Path.GetFileName(pakFilePath)} 失败: {error}");
                            }
                        }
                        catch (Exception ex)
                        {
                            OnExtractionFailed($"文件 {Path.GetFileName(pakFilePath)} 处理错误: {ex.Message}");
                        }
                    }

                    OnExtractionCompleted();
                }, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                OnExtractionFailed("提取操作已取消");
            }
            catch (Exception ex)
            {
                OnExtractionFailed($"提取失败: {ex.Message}");
            }
        }

        public override void Extract(string directoryPath)
        {
            ExtractAsync(directoryPath).Wait();
        }
    }
}