using System;
using System.IO;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Reflection;

namespace super_toolbox
{
    public class IdeaFactory_CL3Extractor : BaseExtractor
    {
        private static string _tempExePath;
        private static string _tempDllPath;
        private int _successCount = 0;

        static IdeaFactory_CL3Extractor()
        {
            Directory.CreateDirectory(TempDllDirectory);

            _tempExePath = Path.Combine(TempDllDirectory, "Multi-Extractor.exe");
            _tempDllPath = Path.Combine(TempDllDirectory, "File Formats.dll");

            if (!File.Exists(_tempExePath))
            {
                ReleaseEmbeddedResource("embedded.Multi-Extractor.exe", _tempExePath);
            }

            if (!File.Exists(_tempDllPath))
            {
                ReleaseEmbeddedResource("embedded.File Formats.dll", _tempDllPath);
            }
        }

        private static void ReleaseEmbeddedResource(string resourceName, string targetPath)
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                    throw new FileNotFoundException($"找不到嵌入资源: {resourceName}");

                File.WriteAllBytes(targetPath, ReadAllBytes(stream));
            }
        }

        private static byte[] ReadAllBytes(Stream stream)
        {
            using (var ms = new MemoryStream())
            {
                stream.CopyTo(ms);
                return ms.ToArray();
            }
        }

        public override async Task ExtractAsync(string directoryPath, CancellationToken cancellationToken = default)
        {
            if (!Directory.Exists(directoryPath))
            {
                OnExtractionFailed("目录不存在");
                return;
            }

            var cl3Files = Directory.GetFiles(directoryPath, "*.cl3", SearchOption.AllDirectories);
            if (cl3Files.Length == 0)
            {
                OnExtractionFailed("未找到.cl3文件");
                return;
            }

            TotalFilesToExtract = cl3Files.Length;
            _successCount = 0;

            try
            {
                await Task.Run(() =>
                {
                    foreach (var cl3FilePath in cl3Files)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        string? parentDir = Path.GetDirectoryName(cl3FilePath);
                        if (string.IsNullOrEmpty(parentDir))
                        {
                            OnExtractionFailed($"无效路径: {cl3FilePath}");
                            continue;
                        }

                        var processInfo = new ProcessStartInfo
                        {
                            FileName = _tempExePath,
                            Arguments = $"\"{cl3FilePath}\"",
                            WorkingDirectory = parentDir,
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            Environment = { ["PATH"] = $"{TempDllDirectory};{Environment.GetEnvironmentVariable("PATH")}" }
                        };

                        using (var process = new Process { StartInfo = processInfo })
                        {
                            process.OutputDataReceived += (sender, e) =>
                            {
                                if (!string.IsNullOrEmpty(e.Data))
                                {
                                    OnFileExtracted(e.Data);

                                    if (e.Data.Contains("Extracted:"))
                                    {
                                        Interlocked.Increment(ref _successCount);
                                    }
                                }
                            };

                            process.ErrorDataReceived += (sender, e) =>
                            {
                                if (!string.IsNullOrEmpty(e.Data))
                                {
                                    OnExtractionFailed(e.Data);
                                }
                            };

                            process.Start();
                            process.BeginOutputReadLine();
                            process.BeginErrorReadLine();
                            process.WaitForExit();

                            if (process.ExitCode != 0)
                            {
                                OnExtractionFailed($"{Path.GetFileName(cl3FilePath)} 解包失败 (代码 {process.ExitCode})");
                            }
                        }
                    }

                    OnExtractionCompleted();
                    OnFileExtracted($"提取完成，共提取 {_successCount}/{TotalFilesToExtract} 个文件");
                }, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                OnExtractionFailed("操作已取消");
            }
            catch (Exception ex)
            {
                OnExtractionFailed($"致命错误: {ex.Message}");
            }
        }
    }
}