using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace super_toolbox
{
    public class PVR2PNG_Converter : BaseExtractor
    {
        private static string _tempExePath;

        public event EventHandler<string>? ConversionStarted;
        public event EventHandler<string>? ConversionProgress;
        public event EventHandler<string>? ConversionError;

        static PVR2PNG_Converter()
        {
            _tempExePath = LoadEmbeddedExe("embedded.PVRTexToolCLI.exe", "PVRTexToolCLI.exe");
        }

        public override async Task ExtractAsync(string directoryPath, CancellationToken cancellationToken = default)
        {
            List<string> convertedFiles = new List<string>();
            string extractedDir = Path.Combine(directoryPath, "Extracted");
            Directory.CreateDirectory(extractedDir);

            if (!Directory.Exists(directoryPath))
            {
                ConversionError?.Invoke(this, $"源文件夹 {directoryPath} 不存在");
                OnExtractionFailed($"源文件夹 {directoryPath} 不存在");
                return;
            }

            ConversionStarted?.Invoke(this, $"开始处理目录: {directoryPath}");
            TotalFilesToExtract = Directory.GetFiles(directoryPath, "*.pvr", SearchOption.AllDirectories).Length;

            var pvrFiles = Directory.EnumerateFiles(directoryPath, "*.pvr", SearchOption.AllDirectories);
            int successCount = 0;

            try
            {
                foreach (var pvrFilePath in pvrFiles)
                {
                    ThrowIfCancellationRequested(cancellationToken);
                    ConversionProgress?.Invoke(this, $"正在处理: {Path.GetFileName(pvrFilePath)}");

                    string fileName = Path.GetFileNameWithoutExtension(pvrFilePath);
                    string fileDirectory = Path.GetDirectoryName(pvrFilePath) ?? string.Empty;
                    fileName = fileName.Replace(".png", "", StringComparison.OrdinalIgnoreCase);

                    string tempPngPath = Path.Combine(fileDirectory, $"{fileName}.temp.png");
                    string finalPngPath = Path.Combine(extractedDir, $"{fileName}.png");

                    try
                    {
                        var processStartInfo = new ProcessStartInfo
                        {
                            FileName = _tempExePath,
                            Arguments = $"-i \"{pvrFilePath}\" -d \"{tempPngPath}\" -noout",
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
                                ConversionError?.Invoke(this, $"无法启动转换进程: {Path.GetFileName(pvrFilePath)}");
                                OnExtractionFailed($"无法启动转换进程: {pvrFilePath}");
                                continue;
                            }

                            process.OutputDataReceived += (sender, e) =>
                            {
                                if (!string.IsNullOrEmpty(e.Data))
                                    ConversionProgress?.Invoke(this, e.Data);
                            };

                            process.ErrorDataReceived += (sender, e) =>
                            {
                                if (!string.IsNullOrEmpty(e.Data))
                                    ConversionError?.Invoke(this, $"错误: {e.Data}");
                            };

                            process.BeginOutputReadLine();
                            process.BeginErrorReadLine();

                            await process.WaitForExitAsync(cancellationToken);

                            if (process.ExitCode != 0)
                            {
                                ConversionError?.Invoke(this, $"{fileName}.pvr 转换失败，错误代码: {process.ExitCode}");
                                OnExtractionFailed($"{fileName}.pvr 转换失败，错误代码: {process.ExitCode}");
                                continue;
                            }
                        }

                        if (File.Exists(tempPngPath))
                        {
                            if (File.Exists(finalPngPath))
                            {
                                File.Delete(finalPngPath);
                                ConversionProgress?.Invoke(this, $"覆盖已存在文件: {Path.GetFileName(finalPngPath)}");
                            }

                            File.Move(tempPngPath, finalPngPath);
                            successCount++;
                            convertedFiles.Add(finalPngPath);
                            ConversionProgress?.Invoke(this, $"转换成功: {Path.GetFileName(finalPngPath)}");
                            OnFileExtracted(finalPngPath);
                        }
                        else
                        {
                            ConversionError?.Invoke(this, $"{fileName}.pvr 转换成功，但未找到输出文件");
                            OnExtractionFailed($"{fileName}.pvr 转换成功，但未找到输出文件");
                        }
                    }
                    catch (Exception ex)
                    {
                        ConversionError?.Invoke(this, $"转换异常: {ex.Message}");
                        OnExtractionFailed($"{fileName}.pvr 处理错误: {ex.Message}");

                        if (File.Exists(tempPngPath))
                        {
                            File.Delete(tempPngPath);
                        }
                    }
                }

                if (successCount > 0)
                {
                    ConversionProgress?.Invoke(this, $"转换完成，成功转换 {successCount}/{TotalFilesToExtract} 个文件");
                }
                else
                {
                    ConversionProgress?.Invoke(this, "转换完成，但未成功转换任何文件");
                }

                OnExtractionCompleted();
            }
            catch (OperationCanceledException)
            {
                ConversionError?.Invoke(this, "操作已取消");
                OnExtractionFailed("操作已取消");
            }
            catch (Exception ex)
            {
                ConversionError?.Invoke(this, $"严重错误: {ex.Message}");
                OnExtractionFailed($"严重错误: {ex.Message}");
            }
        }
    }
}