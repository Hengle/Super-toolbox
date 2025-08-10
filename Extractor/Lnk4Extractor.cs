using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace super_toolbox
{
    public class LNK4Extractor : BaseExtractor
    {
        private static string _tempExePath;

        static LNK4Extractor()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "supertoolbox_temp");
            Directory.CreateDirectory(tempDir);
            _tempExePath = Path.Combine(tempDir, "exlnk4.exe");

            if (!File.Exists(_tempExePath))
            {
                using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("embedded.exlnk4.exe"))
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

            try
            {
                string[] datFiles = Directory.GetFiles(directoryPath, "*.dat", SearchOption.AllDirectories);
                if (datFiles.Length == 0)
                {
                    OnExtractionFailed("未找到.dat文件");
                    return;
                }

                string extractDir = Path.Combine(directoryPath, "Extracted");
                Directory.CreateDirectory(extractDir);

                int totalFiles = 0;
                var preExtractionFiles = new Dictionary<string, HashSet<string>>();

                foreach (var datFilePath in datFiles)
                {
                    string? workingDir = Path.GetDirectoryName(datFilePath);
                    if (string.IsNullOrEmpty(workingDir)) continue;

                    var files = new HashSet<string>(Directory.GetFiles(workingDir, "*", SearchOption.TopDirectoryOnly)
                        .Where(f => !f.EndsWith(".dat", StringComparison.OrdinalIgnoreCase))
                        .Select(f => Path.GetFileName(f)!) 
                    );

                    preExtractionFiles[datFilePath] = files;
                }

                foreach (var datFilePath in datFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    string? workingDir = Path.GetDirectoryName(datFilePath);
                    if (string.IsNullOrEmpty(workingDir)) continue;

                    var process = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = _tempExePath,
                            Arguments = $"\"{datFilePath}\"",
                            WorkingDirectory = workingDir,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        }
                    };

                    process.Start();
                    process.WaitForExit();

                    var postFiles = new HashSet<string>(Directory.GetFiles(workingDir, "*", SearchOption.TopDirectoryOnly)
                        .Where(f => !f.EndsWith(".dat", StringComparison.OrdinalIgnoreCase))
                        .Select(f => Path.GetFileName(f)!) 
                    );

                    foreach (var file in postFiles)
                    {
                        if (!preExtractionFiles[datFilePath].Contains(file))
                        {
                            totalFiles++;
                        }
                    }
                }

                TotalFilesToExtract = totalFiles;
                int extractedCount = 0;

                foreach (var datFilePath in datFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        string? workingDir = Path.GetDirectoryName(datFilePath);
                        if (string.IsNullOrEmpty(workingDir))
                        {
                            OnExtractionFailed($"无法获取文件目录: {datFilePath}");
                            continue;
                        }

                        var extractedFiles = Directory.GetFiles(workingDir, "*", SearchOption.TopDirectoryOnly)
                            .Where(f => !f.EndsWith(".dat", StringComparison.OrdinalIgnoreCase))
                            .ToList();

                        foreach (var file in extractedFiles)
                        {
                            string fileName = Path.GetFileName(file)!;
                            if (!preExtractionFiles[datFilePath].Contains(fileName))
                            {
                                string destPath = Path.Combine(extractDir, fileName);
                                if (File.Exists(destPath)) File.Delete(destPath);
                                File.Move(file, destPath);

                                extractedCount++;
                                OnFileExtracted(destPath);

                                SetExtractedFileCount(extractedCount);

                                await Task.Delay(10, cancellationToken);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        OnExtractionFailed($"处理文件 {datFilePath} 时出错: {ex.Message}");
                    }
                }

                OnExtractionCompleted();
            }
            catch (OperationCanceledException)
            {
                OnExtractionFailed("操作已取消");
            }
            catch (Exception ex)
            {
                OnExtractionFailed($"提取过程中发生错误: {ex.Message}");
            }
        }


        public override void Extract(string directoryPath)
        {
            ExtractAsync(directoryPath).GetAwaiter().GetResult();
        }
    }
}
