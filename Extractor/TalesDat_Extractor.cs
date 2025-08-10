using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace super_toolbox
{
    public class TalesDat_Extractor : BaseExtractor
    {
        public event EventHandler<string>? ExtractionStarted;
        public event EventHandler<string>? ExtractionProgress;
        public event EventHandler<string>? ExtractionError;

        private static string _tempExePath;

        static TalesDat_Extractor()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "supertoolbox_temp");
            Directory.CreateDirectory(tempDir);
            _tempExePath = Path.Combine(tempDir, "ToBTools.exe");

            if (!File.Exists(_tempExePath))
            {
                using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("embedded.ToBTools.exe"))
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
            if (string.IsNullOrWhiteSpace(directoryPath))
            {
                OnExtractionFailed("错误：目录路径为空");
                return;
            }

            if (!Directory.Exists(directoryPath))
            {
                OnExtractionFailed($"错误：目录不存在: {directoryPath}");
                return;
            }

            ExtractionStarted?.Invoke(this, $"开始处理目录: {directoryPath}");

            try
            {
                var filesByDirectory = Directory.GetFiles(directoryPath, "*.*", SearchOption.AllDirectories)
                    .Where(f => f.EndsWith(".TLDAT", StringComparison.OrdinalIgnoreCase) ||
                               f.EndsWith(".TOFHDB", StringComparison.OrdinalIgnoreCase))
                    .GroupBy(Path.GetDirectoryName)
                    .Where(g => g.Count() >= 2);

                if (!filesByDirectory.Any())
                {
                    OnExtractionFailed("未找到配对的.TLDAT和.TOFHDB文件");
                    return;
                }

                await Task.Run(() =>
                {
                    foreach (var directoryGroup in filesByDirectory)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var tldatFiles = directoryGroup.Where(f => f.EndsWith(".TLDAT", StringComparison.OrdinalIgnoreCase)).ToList();
                        var tofhdbFiles = directoryGroup.Where(f => f.EndsWith(".TOFHDB", StringComparison.OrdinalIgnoreCase)).ToList();

                        int pairCount = Math.Min(tldatFiles.Count, tofhdbFiles.Count);
                        if (pairCount == 0) continue;

                        for (int i = 0; i < pairCount; i++)
                        {
                            try
                            {
                                string tldatFile = tldatFiles[i];
                                string tofhdbFile = tofhdbFiles[i];

                                ExtractionProgress?.Invoke(this, $"正在处理: {Path.GetFileName(tldatFile)} 和 {Path.GetFileName(tofhdbFile)}");

                                var process = new Process
                                {
                                    StartInfo = new ProcessStartInfo
                                    {
                                        FileName = _tempExePath,
                                        Arguments = $"unpack \"{tldatFile}\" \"{tofhdbFile}\"",
                                        WorkingDirectory = directoryGroup.Key,
                                        UseShellExecute = false,
                                        CreateNoWindow = true
                                    }
                                };

                                process.Start();
                                process.WaitForExit();

                                ExtractionProgress?.Invoke(this, $"已完成: {Path.GetFileName(tldatFile)}");
                            }
                            catch (Exception ex)
                            {
                                ExtractionError?.Invoke(this, $"处理文件对时出错: {ex.Message}");
                                OnExtractionFailed($"处理文件对时出错: {ex.Message}");
                            }
                        }
                    }

                    ExtractionProgress?.Invoke(this, "处理完成");
                    OnExtractionCompleted();
                }, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                ExtractionError?.Invoke(this, "操作已取消");
                OnExtractionFailed("操作已取消");
            }
            catch (Exception ex)
            {
                ExtractionError?.Invoke(this, $"提取过程中发生错误: {ex.Message}");
                OnExtractionFailed($"提取过程中发生错误: {ex.Message}");
            }
        }

        public override void Extract(string directoryPath)
        {
            ExtractAsync(directoryPath).Wait();
        }
    }
}