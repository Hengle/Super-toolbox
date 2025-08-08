using System;
using System.IO;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace super_toolbox
{
    public class GustElixir_Extractor : BaseExtractor
    {
        private static string _tempExePath;

        public event EventHandler<string>? OutputMessage;

        static GustElixir_Extractor()
        {
            _tempExePath = LoadEmbeddedExe("embedded.gust_elixir.exe", "gust_elixir.exe");
        }

        public override async Task ExtractAsync(string directoryPath, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(directoryPath) || !Directory.Exists(directoryPath))
            {
                OutputMessage?.Invoke(this, "[错误] 目录不存在或路径为空");
                return;
            }

            var elixirFiles = Directory.GetFiles(directoryPath, "*.elixir*", SearchOption.AllDirectories);
            if (elixirFiles.Length == 0)
            {
                OutputMessage?.Invoke(this, "[错误] 未找到.elixir文件");
                return;
            }

            try
            {
                OutputMessage?.Invoke(this, $"开始处理 {elixirFiles.Length} 个.elixir文件...");

                await Task.Run(() =>
                {
                    Parallel.ForEach(elixirFiles, new ParallelOptions
                    {
                        CancellationToken = cancellationToken,
                        MaxDegreeOfParallelism = Environment.ProcessorCount
                    }, filePath =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        string? parentDir = Path.GetDirectoryName(filePath);
                        if (string.IsNullOrEmpty(parentDir)) return;

                        lock (this)
                        {
                            OutputMessage?.Invoke(this, $"\n正在解包: {Path.GetFileName(filePath)}");
                        }

                        var process = new Process
                        {
                            StartInfo = new ProcessStartInfo
                            {
                                FileName = _tempExePath,
                                Arguments = $"\"{filePath}\"",
                                WorkingDirectory = parentDir,
                                UseShellExecute = false,
                                CreateNoWindow = true,
                                RedirectStandardOutput = true,
                                RedirectStandardError = true
                            }
                        };

                        var outputLock = new object();

                        process.OutputDataReceived += (sender, e) =>
                        {
                            if (!string.IsNullOrEmpty(e.Data))
                            {
                                lock (outputLock)
                                {
                                    OutputMessage?.Invoke(this, e.Data);
                                }
                            }
                        };

                        process.ErrorDataReceived += (sender, e) =>
                        {
                            if (!string.IsNullOrEmpty(e.Data))
                            {
                                lock (outputLock)
                                {
                                    OutputMessage?.Invoke(this, $"[错误] {e.Data}");
                                }
                            }
                        };

                        process.Start();
                        process.BeginOutputReadLine();
                        process.BeginErrorReadLine();
                        process.WaitForExit();

                        lock (this)
                        {
                            OutputMessage?.Invoke(this, $"√ 完成处理: {Path.GetFileName(filePath)}");
                        }
                    });
                }, cancellationToken);

                OutputMessage?.Invoke(this, "\n所有文件处理完成！");
            }
            catch (OperationCanceledException)
            {
                OutputMessage?.Invoke(this, "[用户取消] 操作已中止");
            }
            catch (Exception ex)
            {
                OutputMessage?.Invoke(this, $"[严重错误] {ex.Message}");
            }
        }
    }
}