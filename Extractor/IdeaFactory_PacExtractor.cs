using System;
using System.IO;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace super_toolbox
{
    public class IdeaFactory_PacExtractor : BaseExtractor
    {
        private static string _tempExePath;
        private static string _tempDllPath;

        static IdeaFactory_PacExtractor()
        {
            _tempExePath = LoadEmbeddedExe("embedded.pac_unpack.exe", "pac_unpack.exe");
            _tempDllPath = Path.Combine(TempDllDirectory, "libpac.dll");

            if (!File.Exists(_tempDllPath))
            {
                LoadEmbeddedDll("embedded.libpac.dll", "libpac.dll");
            }
        }

        public override async Task ExtractAsync(string directoryPath, CancellationToken cancellationToken = default)
        {
            if (!Directory.Exists(directoryPath))
            {
                OnExtractionFailed("错误：目录不存在");
                return;
            }

            var pacFiles = Directory.GetFiles(directoryPath, "*.pac");
            if (pacFiles.Length == 0)
            {
                OnExtractionFailed("未找到.pac文件");
                return;
            }

            TotalFilesToExtract = pacFiles.Length;

            try
            {
                await Task.Run(() =>
                {
                    foreach (var pacFilePath in pacFiles)
                    {
                        ThrowIfCancellationRequested(cancellationToken);

                        try
                        {
                            var process = new Process
                            {
                                StartInfo = new ProcessStartInfo
                                {
                                    FileName = _tempExePath,
                                    Arguments = $"\"{pacFilePath}\"",
                                    WorkingDirectory = Path.GetDirectoryName(pacFilePath),
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
                                throw new Exception($"处理文件 {Path.GetFileName(pacFilePath)} 失败: {error}");
                            }
                        }
                        catch (Exception ex)
                        {
                            OnExtractionFailed($"文件 {Path.GetFileName(pacFilePath)} 处理错误: {ex.Message}");
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