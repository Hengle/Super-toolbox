using System;
using System.IO;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace super_toolbox
{
    public class GustPak_Extractor : BaseExtractor
    {
        private static string _tempExePath;
        public event EventHandler<string>? DebugOutput;

        static GustPak_Extractor()
        {
            _tempExePath = LoadEmbeddedExe("embedded.gust_pak.exe", "gust_pak.exe");
        }

        public override async Task ExtractAsync(string directoryPath, CancellationToken cancellationToken = default)
        {
            if (!Directory.Exists(directoryPath))
            {
                OnExtractionFailed("错误：目录不存在");
                return;
            }

            var pakFiles = Directory.GetFiles(directoryPath, "*.pak", SearchOption.AllDirectories);
            if (pakFiles.Length == 0)
            {
                OnExtractionFailed("未找到.pak文件");
                return;
            }

            DebugOutput?.Invoke(this, $"找到 {pakFiles.Length} 个.pak文件");

            try
            {
                await Task.Run(() =>
                {
                    foreach (var pakFilePath in pakFiles)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        string? parentDir = Path.GetDirectoryName(pakFilePath);
                        if (string.IsNullOrEmpty(parentDir))
                        {
                            OnExtractionFailed($"无法获取文件目录: {pakFilePath}");
                            continue;
                        }

                        DebugOutput?.Invoke(this, $"开始解包: {pakFilePath}");

                        try
                        {
                            var process = new Process
                            {
                                StartInfo = new ProcessStartInfo
                                {
                                    FileName = _tempExePath,
                                    Arguments = $"\"{pakFilePath}\"",
                                    WorkingDirectory = parentDir,
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
                                    DebugOutput?.Invoke(this, e.Data);

                                    if (e.Data.Contains("Extracted:") || File.Exists(e.Data))
                                    {
                                        OnFileExtracted(e.Data);
                                    }
                                }
                            };

                            process.ErrorDataReceived += (sender, e) =>
                            {
                                if (!string.IsNullOrEmpty(e.Data))
                                {
                                    DebugOutput?.Invoke(this, $"错误: {e.Data}");
                                }
                            };

                            process.Start();
                            process.BeginOutputReadLine();
                            process.BeginErrorReadLine();
                            process.WaitForExit();

                            if (process.ExitCode != 0)
                            {
                                OnExtractionFailed($"{Path.GetFileName(pakFilePath)} 解包失败，错误代码: {process.ExitCode}");
                            }
                            else
                            {
                                DebugOutput?.Invoke(this, $"{Path.GetFileName(pakFilePath)} 解包完成");
                            }
                        }
                        catch (Exception ex)
                        {
                            DebugOutput?.Invoke(this, $"解包异常: {ex.Message}");
                            OnExtractionFailed($"{Path.GetFileName(pakFilePath)} 处理错误: {ex.Message}");
                        }
                    }

                    OnExtractionCompleted();
                }, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                OnExtractionFailed("操作已取消");
            }
            catch (Exception ex)
            {
                OnExtractionFailed($"严重错误: {ex.Message}");
            }
        }
    }
}