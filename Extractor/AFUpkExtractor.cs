using System;
using System.IO;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace super_toolbox
{
    public class AFUpkExtractor : BaseExtractor
    {
        private static string _tempExePath;
        public event EventHandler<string>? DebugOutput;

        static AFUpkExtractor()
        {
            _tempExePath = LoadEmbeddedExe("embedded.AFUpkExtractor.exe", "AFUpkExtractor.exe");
        }

        public override async Task ExtractAsync(string directoryPath, CancellationToken cancellationToken = default)
        {
            if (!Directory.Exists(directoryPath))
            {
                OnExtractionFailed("错误：选择的目录不存在");
                return;
            }

            var upkFiles = Directory.GetFiles(directoryPath, "*.upk", SearchOption.AllDirectories);

            if (upkFiles.Length == 0)
            {
                OnExtractionFailed("未找到任何.upk文件");
                return;
            }

            DebugOutput?.Invoke(this, $"找到 {upkFiles.Length} 个.upk文件，开始解包...");

            try
            {
                await Task.Run(() =>
                {
                    foreach (var upkFilePath in upkFiles)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        string fileDirectory = Path.GetDirectoryName(upkFilePath) ?? string.Empty;
                        string fileName = Path.GetFileName(upkFilePath);

                        DebugOutput?.Invoke(this, $"正在解包: {fileName}");

                        try
                        {
                            var processStartInfo = new ProcessStartInfo
                            {
                                FileName = _tempExePath,
                                Arguments = $"\"{upkFilePath}\"",
                                WorkingDirectory = fileDirectory,
                                UseShellExecute = false,
                                CreateNoWindow = true,
                                RedirectStandardOutput = true,
                                RedirectStandardError = true
                            };

                            using (var process = Process.Start(processStartInfo))
                            {
                                if (process == null)
                                {
                                    OnExtractionFailed($"无法启动解包进程: {fileName}");
                                    continue;
                                }

                                process.OutputDataReceived += (sender, e) =>
                                {
                                    if (!string.IsNullOrEmpty(e.Data))
                                    {
                                        DebugOutput?.Invoke(this, e.Data);
                                    }
                                };

                                process.ErrorDataReceived += (sender, e) =>
                                {
                                    if (!string.IsNullOrEmpty(e.Data))
                                    {
                                        DebugOutput?.Invoke(this, $"错误: {e.Data}");
                                    }
                                };

                                process.BeginOutputReadLine();
                                process.BeginErrorReadLine();
                                process.WaitForExit();

                                if (process.ExitCode != 0)
                                {
                                    OnExtractionFailed($"{fileName} 解包失败，错误代码: {process.ExitCode}");
                                }
                                else
                                {
                                    DebugOutput?.Invoke(this, $"解包成功: {fileName}");
                                    OnFileExtracted(upkFilePath);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            DebugOutput?.Invoke(this, $"解包异常: {ex.Message}");
                            OnExtractionFailed($"{fileName} 处理错误: {ex.Message}");
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