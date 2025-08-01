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
        private static extern bool ProcessPhyreData(
            byte[] data,
            int dataSize,
            [MarshalAs(UnmanagedType.LPWStr)] StringBuilder errorMessage,
            int errorMessageSize);

        [DllImport("dds-phyre-tool.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool IsValidPhyreFile(
            [MarshalAs(UnmanagedType.LPWStr)] string filePath);

        // 魔术头定义
        private static readonly byte[] OFS3_MAGIC = { 0x4F, 0x46, 0x53, 0x33 }; // "OFS3"
        private static readonly byte[] RYHP_MAGIC = { 0x52, 0x59, 0x48, 0x50 }; // "RYHP"

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

        private bool CheckFileHeader(string filePath, byte[] expectedMagic)
        {
            try
            {
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
                using (var reader = new BinaryReader(fs))
                {
                    byte[] header = reader.ReadBytes(expectedMagic.Length);
                    return header.SequenceEqual(expectedMagic);
                }
            }
            catch
            {
                return false;
            }
        }

        private List<byte[]> ExtractAllPhyreFragments(byte[] fileData)
        {
            List<byte[]> fragments = new List<byte[]>();
            int position = 0;

            while (position <= fileData.Length - RYHP_MAGIC.Length)
            {
                bool isMatch = true;
                for (int i = 0; i < RYHP_MAGIC.Length; i++)
                {
                    if (fileData[position + i] != RYHP_MAGIC[i])
                    {
                        isMatch = false;
                        break;
                    }
                }

                if (isMatch)
                {
                    int startPos = position;
                    int nextHeaderPos = -1;

                    for (int i = position + RYHP_MAGIC.Length; i <= fileData.Length - RYHP_MAGIC.Length; i++)
                    {
                        bool nextMatch = true;
                        for (int j = 0; j < RYHP_MAGIC.Length; j++)
                        {
                            if (fileData[i + j] != RYHP_MAGIC[j])
                            {
                                nextMatch = false;
                                break;
                            }
                        }

                        if (nextMatch)
                        {
                            nextHeaderPos = i;
                            break;
                        }
                    }

                    int fragmentLength;
                    if (nextHeaderPos == -1)
                    {
                        fragmentLength = fileData.Length - startPos;
                    }
                    else
                    {
                        fragmentLength = nextHeaderPos - startPos;
                    }

                    byte[] fragment = new byte[fragmentLength];
                    Array.Copy(fileData, startPos, fragment, 0, fragmentLength);
                    fragments.Add(fragment);

                    position += fragmentLength;
                }
                else
                {
                    position++;
                }
            }

            return fragments;
        }

        private bool ProcessAndSavePhyreFragment(byte[] phyreData, string originalFilePath, string outputDir, int fragmentIndex)
        {
            var errorMessage = new StringBuilder(512);

            string tempFile = Path.GetTempFileName();
            try
            {
                File.WriteAllBytes(tempFile, phyreData);

                bool success = ProcessPhyreFile(tempFile, errorMessage, errorMessage.Capacity);

                if (success)
                {
                    string originalName = Path.GetFileNameWithoutExtension(originalFilePath);
                    string outputName = $"{originalName}_{fragmentIndex}.dds";
                    string destPath = Path.Combine(outputDir, outputName);

                    string tempOutput = tempFile + ".dds";
                    if (File.Exists(tempOutput))
                    {
                        File.Move(tempOutput, destPath, true);
                        OnFileExtracted(destPath);
                        return true;
                    }
                }
                else
                {
                    OnExtractionFailed($"处理片段 {fragmentIndex} 时出错: {errorMessage.ToString()}");
                }
            }
            finally
            {
                try { File.Delete(tempFile); } catch { }
                try { File.Delete(tempFile + ".dds"); } catch { }
            }

            return false;
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

            var allFiles = Directory.EnumerateFiles(directoryPath, "*.*", SearchOption.AllDirectories)
                .Where(file => !file.StartsWith(extractedRootDir, StringComparison.OrdinalIgnoreCase))
                .ToList();

            TotalFilesToExtract = allFiles.Count;
            if (TotalFilesToExtract == 0)
            {
                OnExtractionCompleted();
                return;
            }

            try
            {
                await Task.Run(() =>
                {
                    Parallel.ForEach(allFiles, new ParallelOptions
                    {
                        MaxDegreeOfParallelism = Environment.ProcessorCount,
                        CancellationToken = cancellationToken
                    }, filePath =>
                    {
                        try
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            string relativePath = Path.GetRelativePath(directoryPath, filePath);
                            string outputDir = Path.Combine(extractedRootDir, Path.GetDirectoryName(relativePath) ?? string.Empty);
                            Directory.CreateDirectory(outputDir);

                            if (CheckFileHeader(filePath, OFS3_MAGIC))
                            {
                                byte[] fileData = File.ReadAllBytes(filePath);
                                var fragments = ExtractAllPhyreFragments(fileData);

                                if (fragments.Count == 0)
                                {
                                    OnExtractionFailed($"在 {Path.GetFileName(filePath)} 中未找到PHYRE片段");
                                    return;
                                }

                                for (int i = 0; i < fragments.Count; i++)
                                {
                                    cancellationToken.ThrowIfCancellationRequested();
                                    ProcessAndSavePhyreFragment(fragments[i], filePath, outputDir, i);
                                }
                            }
                            else if (CheckFileHeader(filePath, RYHP_MAGIC))
                            {
                                ProcessAndSavePhyreFragment(File.ReadAllBytes(filePath), filePath, outputDir, 0);
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
