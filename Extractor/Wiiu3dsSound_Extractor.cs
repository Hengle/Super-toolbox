using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Syroot.BinaryData;

namespace super_toolbox
{
    public class Wiiu3dsSound_Extractor : BaseExtractor
    {
        private static readonly string[] MagicNumbers = {
            "CWSD", "FWSD", "CSEQ", "FSEQ", "CBNK", "FBNK",
            "CGRP", "FGRP", "CSTM", "FSTM", "CWAV", "FWAV",
            "CWAR", "FWAR"
        };
        private readonly uint[] _magicNumbersBin;

        public Wiiu3dsSound_Extractor()
        {
            _magicNumbersBin = new uint[MagicNumbers.Length];
            for (int i = 0; i < MagicNumbers.Length; i++)
            {
                using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(MagicNumbers[i])))
                using (var br = new BinaryDataReader(ms) { ByteOrder = ByteOrder.BigEndian })
                {
                    _magicNumbersBin[i] = br.ReadUInt32();
                }
            }
        }

        public override async Task ExtractAsync(string directoryPath, CancellationToken cancellationToken = default)
        {
            string extractedDir = Path.Combine(directoryPath, "Extracted");
            Directory.CreateDirectory(extractedDir);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var files = Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories)
                .Where(file => !file.StartsWith(extractedDir, StringComparison.OrdinalIgnoreCase))
                .ToList();

            TotalFilesToExtract = files.Count;

            var extractedFiles = new ConcurrentBag<string>();

            try
            {
                await Task.Run(() =>
                {
                    Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount * 2 }, filePath =>
                    {
                        ThrowIfCancellationRequested(cancellationToken);
                        try
                        {
                            byte[] content = File.ReadAllBytes(filePath);
                            ExtractSoundFiles(content, filePath, extractedDir, extractedFiles, cancellationToken);
                        }
                        catch (Exception ex)
                        {
                            OnExtractionFailed($"提取文件 {filePath} 时发生错误: {ex.Message}");
                        }
                    });
                }, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("提取操作已取消。");
            }

            sw.Stop();

            int actualExtractedCount = Directory.EnumerateFiles(extractedDir, "*.*", SearchOption.AllDirectories).Count();
            Console.WriteLine($"处理完成，耗时 {sw.Elapsed.TotalSeconds:F2} 秒");
            Console.WriteLine($"共提取出 {actualExtractedCount} 个音频相关文件，统计提取文件数量: {ExtractedFileCount}");
            if (ExtractedFileCount != actualExtractedCount)
            {
                Console.WriteLine("警告: 统计数量与实际数量不符，可能存在文件操作异常。");
            }
        }

        private void ExtractSoundFiles(byte[] content, string sourceFilePath, string baseOutputDir,
            ConcurrentBag<string> extractedFiles, CancellationToken cancellationToken)
        {
            using (var ms = new MemoryStream(content))
            using (var br = new BinaryDataReader(ms))
            {
                string fileKey = Path.GetFileNameWithoutExtension(sourceFilePath);
                var mapFileEntries = new List<string>
                {
                    "格式如何工作:偏移量；大小；文件名；要读取的注入器名称"
                };

                int fileNumber = 0;
                br.ReadUInt32(); // 跳过初始4字节

                while (br.Position <= content.Length - 4)
                {
                    ThrowIfCancellationRequested(cancellationToken);

                    br.ByteOrder = ByteOrder.BigEndian;
                    uint magic = br.ReadUInt32();
                    bool found = false;

                    for (int i = 0; i < _magicNumbersBin.Length; i++)
                    {
                        if (magic == _magicNumbersBin[i])
                        {
                            ushort t = br.ReadUInt16();
                            br.ByteOrder = t == 0xFFFE ? ByteOrder.LittleEndian : ByteOrder.BigEndian;

                            br.ReadUInt16s(3); 
                            uint length = br.ReadUInt32();

                            long position = br.Position - 16; 
                            br.Position = position;
                            byte[] fileData = br.ReadBytes((int)length);

                            string magicDir = Path.Combine(baseOutputDir, MagicNumbers[i]);
                            Directory.CreateDirectory(magicDir);

                            string fileName = $"{fileKey}_{fileNumber:D4}.b{MagicNumbers[i].ToLower()}";
                            string outputPath = Path.Combine(magicDir, fileName);

                            File.WriteAllBytes(outputPath, fileData);
                            extractedFiles.Add(outputPath);
                            OnFileExtracted(outputPath);

                            mapFileEntries.Add($"{position}; {length}; {MagicNumbers[i]}/{fileName}; ADD_CUSTOM_NAME_HERE!");

                            if (i == 0 || i == 1 || i == 6 || i == 7 || i == 12 || i == 13)
                            {
                                string subDir = Path.Combine(magicDir, $"{fileNumber:D4}{MagicNumbers[i]}");
                                Directory.CreateDirectory(subDir);
                                ExtractSoundFiles(fileData, fileName, subDir, extractedFiles, cancellationToken);
                            }

                            fileNumber++;
                            found = true;
                            break;
                        }
                    }

                    if (!found)
                    {
                        br.Position--; 
                    }
                }

                string mapFilePath = Path.Combine(baseOutputDir, $"{fileKey}_fileMap.txt");
                File.WriteAllLines(mapFilePath, mapFileEntries);
            }
        }
    }
}