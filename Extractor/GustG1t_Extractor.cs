using System;
using System.IO;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace super_toolbox
{
    public class GustG1t_Extractor : BaseExtractor
    {
        private static string _tempExePath;
        public event EventHandler<string>? DebugOutput; 

        static GustG1t_Extractor()
        {
            _tempExePath = LoadEmbeddedExe("embedded.gust_g1t.exe", "gust_g1t.exe");
        }

        public override async Task ExtractAsync(string directoryPath, CancellationToken cancellationToken = default)
        {
            if (!Directory.Exists(directoryPath))
            {
                OnExtractionFailed("错误：目录不存在");
                return;
            }

            var g1tFiles = Directory.GetFiles(directoryPath, "*.g1t", SearchOption.AllDirectories);
            if (g1tFiles.Length == 0)
            {
                OnExtractionFailed("未找到.g1t文件");
                return;
            }

            TotalFilesToExtract = g1tFiles.Length;
            DebugOutput?.Invoke(this, $"找到 {g1tFiles.Length} 个.g1t文件");

            try
            {
                await Task.Run(() =>
                {
                    foreach (var g1tFilePath in g1tFiles)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        string? parentDir = Path.GetDirectoryName(g1tFilePath);
                        if (string.IsNullOrEmpty(parentDir))
                        {
                            OnExtractionFailed($"无法获取文件目录: {g1tFilePath}");
                            continue;
                        }

                        string fileNameWithoutExt = Path.GetFileNameWithoutExtension(g1tFilePath);
                        string outputDir = Path.Combine(parentDir, fileNameWithoutExt);

                        DebugOutput?.Invoke(this, $"处理文件: {g1tFilePath}");
                        DebugOutput?.Invoke(this, $"输出目录: {outputDir}");

                        try
                        {
                            if (!Directory.Exists(outputDir))
                            {
                                Directory.CreateDirectory(outputDir);
                            }

                            var process = new Process
                            {
                                StartInfo = new ProcessStartInfo
                                {
                                    FileName = _tempExePath,
                                    Arguments = $"\"{g1tFilePath}\"",
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
                                    DebugOutput?.Invoke(this, $"工具输出: {e.Data}");
                                    if (e.Data.EndsWith(".dds"))
                                    {
                                        OnFileExtracted(e.Data);
                                    }
                                }
                            };

                            process.ErrorDataReceived += (sender, e) =>
                            {
                                if (!string.IsNullOrEmpty(e.Data))
                                {
                                    DebugOutput?.Invoke(this, $"工具错误: {e.Data}");
                                }
                            };

                            DebugOutput?.Invoke(this, $"启动工具: {process.StartInfo.FileName} {process.StartInfo.Arguments}");
                            process.Start();
                            process.BeginOutputReadLine();
                            process.BeginErrorReadLine();
                            process.WaitForExit();

                            DebugOutput?.Invoke(this, $"工具退出代码: {process.ExitCode}");

                            if (process.ExitCode != 0)
                            {
                                string error = process.StandardError.ReadToEnd();
                                OnExtractionFailed($"{Path.GetFileName(g1tFilePath)} 错误: {error}");
                            }
                            else
                            {
                                var ddsFiles = Directory.GetFiles(outputDir, "*.dds");
                                foreach (var ddsFile in ddsFiles)
                                {
                                    OnFileExtracted(ddsFile);
                                }
                                DebugOutput?.Invoke(this, $"生成 {ddsFiles.Length} 个DDS文件");
                            }
                        }
                        catch (Exception ex)
                        {
                            DebugOutput?.Invoke(this, $"处理异常: {ex}");
                            OnExtractionFailed($"{Path.GetFileName(g1tFilePath)} 处理错误: {ex.Message}");
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