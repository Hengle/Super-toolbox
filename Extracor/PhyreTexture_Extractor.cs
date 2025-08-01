using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace super_toolbox
{
    public class PhyreTexture_Extractor : BaseExtractor
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetDllDirectory(string lpPathName);

        [DllImport("dds-phyre-tool.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool ProcessPhyreFile(
            [MarshalAs(UnmanagedType.LPWStr)] string inputFile,
            [MarshalAs(UnmanagedType.LPWStr)] StringBuilder errorMessage,
            int errorMessageSize);

        [DllImport("dds-phyre-tool.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool IsValidPhyreFile(
            [MarshalAs(UnmanagedType.LPWStr)] string filePath);

        static PhyreTexture_Extractor()
        {
            LoadEmbeddedDll();
        }

        private static void LoadEmbeddedDll()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "supertoolbox_temp");
            Directory.CreateDirectory(tempDir);
            string dllPath = Path.Combine(tempDir, "dds-phyre-tool.dll");

            if (!File.Exists(dllPath))
            {
                using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("embedded.dds_phyre_tool.dll"))
                {
                    if (stream == null)
                        throw new FileNotFoundException("嵌入的DLL资源未找到");

                    byte[] buffer = new byte[stream.Length];
                    stream.Read(buffer, 0, buffer.Length);
                    File.WriteAllBytes(dllPath, buffer);
                }
            }

            SetDllDirectory(tempDir);
            AppDomain.CurrentDomain.ProcessExit += (s, e) =>
            {
                try { File.Delete(dllPath); } catch { }
            };
        }

        public override async Task ExtractAsync(string directoryPath, CancellationToken cancellationToken = default)
        {
            if (!Directory.Exists(directoryPath))
            {
                OnExtractionFailed($"错误: {directoryPath} 不是有效的目录");
                return;
            }

            string extractedRootDir = Path.Combine(directoryPath, "Extracted");
            Directory.CreateDirectory(extractedRootDir);

            var phyreFiles = Directory.EnumerateFiles(directoryPath, "*.*", SearchOption.AllDirectories)
                .Where(file => !file.StartsWith(extractedRootDir, StringComparison.OrdinalIgnoreCase) &&
                               IsValidPhyreFile(file))
                .ToList();

            TotalFilesToExtract = phyreFiles.Count;
            if (TotalFilesToExtract == 0)
            {
                OnExtractionCompleted();
                return;
            }

            try
            {
                await Task.Run(() =>
                {
                    Parallel.ForEach(phyreFiles, new ParallelOptions
                    {
                        MaxDegreeOfParallelism = Environment.ProcessorCount,
                        CancellationToken = cancellationToken
                    }, filePath =>
                    {
                        try
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            string relativePath = Path.GetRelativePath(directoryPath, filePath);
                            string outputDir = Path.Combine(extractedRootDir, Path.GetDirectoryName(relativePath)!);
                            Directory.CreateDirectory(outputDir);

                            var errorMessage = new StringBuilder(512);
                            bool success = ProcessPhyreFile(filePath, errorMessage, errorMessage.Capacity);

                            if (success)
                            {
                                string outputFile = filePath + ".dds";
                                if (File.Exists(outputFile))
                                {
                                    string destPath = Path.Combine(outputDir, Path.GetFileName(outputFile));
                                    File.Move(outputFile, destPath, overwrite: true);
                                    OnFileExtracted(destPath);
                                }
                            }
                            else
                            {
                                OnExtractionFailed($"处理 {Path.GetFileName(filePath)} 时出错: {errorMessage.ToString()}");
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            OnExtractionFailed($"处理 {Path.GetFileName(filePath)} 时出错: {ex.Message}");
                        }
                    });
                }, cancellationToken);

                OnExtractionCompleted();
            }
            catch (OperationCanceledException)
            {
                OnExtractionFailed("操作已取消");
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