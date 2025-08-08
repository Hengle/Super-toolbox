using System;
using System.IO;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace super_toolbox
{
    public class GustEbm_Extractor : BaseExtractor
    {
        private static string _tempExePath;

        static GustEbm_Extractor()
        {
            _tempExePath = LoadEmbeddedExe("embedded.gust_ebm.exe", "gust_ebm.exe");
        }

        public override async Task ExtractAsync(string directoryPath, CancellationToken cancellationToken = default)
        {
            if (!Directory.Exists(directoryPath))
            {
                OnExtractionFailed("错误：目录不存在");
                return;
            }

            var ebmFiles = Directory.GetFiles(directoryPath, "*.ebm", SearchOption.AllDirectories);
            if (ebmFiles.Length == 0)
            {
                OnExtractionFailed("未找到.ebm文件");
                return;
            }

            TotalFilesToExtract = ebmFiles.Length;

            try
            {
                await Task.Run(() =>
                {
                    foreach (var ebmFilePath in ebmFiles)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        string outputJsonPath = Path.ChangeExtension(ebmFilePath, ".json");

                        var process = new Process
                        {
                            StartInfo = new ProcessStartInfo
                            {
                                FileName = _tempExePath,
                                Arguments = $"\"{ebmFilePath}\"",
                                WorkingDirectory = Path.GetDirectoryName(ebmFilePath),
                                UseShellExecute = false,
                                CreateNoWindow = true,
                                RedirectStandardOutput = true,
                                RedirectStandardError = true
                            }
                        };

                        process.Start();
                        process.WaitForExit();

                        if (process.ExitCode == 0 && File.Exists(outputJsonPath))
                        {
                            OnFileExtracted(outputJsonPath);
                        }
                        else
                        {
                            string error = process.StandardError.ReadToEnd();
                            OnExtractionFailed($"{Path.GetFileName(ebmFilePath)} 错误: {error}");
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