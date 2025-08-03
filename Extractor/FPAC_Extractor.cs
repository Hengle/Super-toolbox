using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace super_toolbox
{
    public class FPAC_Extractor : BaseExtractor
    {
        private static readonly byte[] FPAC_SIGNATURE = { 0x46, 0x50, 0x41, 0x43 };
        private static readonly byte[] HIP_SIGNATURE = { 0x48, 0x49, 0x50, 0x00 };
        private static readonly byte[] VAG_START_SEQUENCE = { 0x56, 0x41, 0x47, 0x70 };
        private static readonly byte[] VAG_END_SEQUENCE = { 0x00, 0x07, 0x77, 0x77, 0x77, 0x77, 0x77, 0x77, 0x77, 0x77, 0x77, 0x77 };

        private const uint HIPHDR_TYPE1 = 0x0000;
        private const uint HIPHDR_TYPE2 = 0x2001;
        private const uint HIPHDR_COMPRESSION_TYPE_8BIT = 0x0001;
        private const uint HIPHDR_COMPRESSION_TYPE_8BIT_RLE = 0x0101;
        private const uint HIPHDR_COMPRESSION_TYPE_GREYSCALE_RLE = 0x0104;
        private const uint HIPHDR_COMPRESSION_TYPE_32BIT_RLE = 0x0110;
        private const uint HIPHDR_COMPRESSION_TYPE_32BIT_LONGRLE = 0x1010;
        private const uint HIPHDR_COMPRESSION_TYPE_LZ = 0x0210;
        private const uint HIPHDR_COMPRESSION_TYPE_SEGS = 0x0810;

        public override async Task ExtractAsync(string directoryPath, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(directoryPath))
                throw new ArgumentNullException(nameof(directoryPath));

            string extractedDir = Path.Combine(directoryPath, "Extracted");
            Directory.CreateDirectory(extractedDir);
            CreateSubdirectory(extractedDir, "bmp");
            CreateSubdirectory(extractedDir, "vag");
            CreateSubdirectory(extractedDir, "unknown");

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
                            if (ProcessFile(Path.GetFileNameWithoutExtension(filePath), content, extractedDir, extractedFiles))
                            {
                            }
                        }
                        catch (Exception ex)
                        {
                            OnExtractionFailed(ex.Message);
                        }
                    });
                }, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("提取操作已取消。");
            }

            sw.Stop();
            Console.WriteLine($"处理完成，耗时{sw.Elapsed.TotalSeconds:F2}秒");
            Console.WriteLine($"共提取出 {ExtractedFileCount} 个文件");
        }

        private void CreateSubdirectory(string parentDir, string subDirName)
        {
            string subDirPath = Path.Combine(parentDir, subDirName);
            Directory.CreateDirectory(subDirPath);
        }

        private string GetOutputPath(string outputDir, string fileName, string extension)
        {
            fileName = string.Join("_", fileName.Split(Path.GetInvalidFileNameChars()));

            string subDir = extension switch
            {
                "bmp" => "bmp",
                "vag" => "vag",
                _ => "unknown"
            };

            string subDirPath = Path.Combine(outputDir, subDir);
            string fullFileName = $"{fileName}.{extension}";

            return Path.Combine(subDirPath, fullFileName);
        }

        private bool ProcessFile(string prefix, byte[] buff, string outputDir, ConcurrentBag<string> extractedFiles)
        {
            return ProcessFPAC(prefix, buff, outputDir, extractedFiles) ||
                   ProcessHIP(prefix, buff, outputDir, extractedFiles) ||
                   ProcessVAG(prefix, buff, outputDir, extractedFiles);
        }

        private bool ProcessFPAC(string prefix, byte[] buff, string outputDir, ConcurrentBag<string> extractedFiles)
        {
            if (buff.Length < 4 || !buff.Take(4).SequenceEqual(FPAC_SIGNATURE))
                return false;

            try
            {
                using (var ms = new MemoryStream(buff))
                using (var br = new BinaryReader(ms))
                {
                    br.ReadBytes(4);
                    uint dataBase = SwapEndian(br.ReadUInt32());
                    uint fileLength = SwapEndian(br.ReadUInt32());
                    uint entryCount = SwapEndian(br.ReadUInt32());
                    br.ReadUInt32();
                    uint filenameLength = SwapEndian(br.ReadUInt32());
                    br.ReadUInt32();
                    br.ReadUInt32();

                    for (uint i = 0; i < entryCount; i++)
                    {
                        string filename = Encoding.ASCII.GetString(br.ReadBytes((int)filenameLength)).TrimEnd('\0');

                        uint index = SwapEndian(br.ReadUInt32());
                        uint offset = SwapEndian(br.ReadUInt32());
                        uint length = SwapEndian(br.ReadUInt32());

                        int align = ((int)filenameLength + 12) % 16;
                        if (align > 0)
                            br.ReadBytes(16 - align);

                        long oldPos = ms.Position;
                        ms.Seek(dataBase + offset, SeekOrigin.Begin);
                        byte[] entryData = br.ReadBytes((int)length);
                        ms.Seek(oldPos, SeekOrigin.Begin);

                        string newPrefix = $"{prefix}_{filename}";
                        ProcessFile(newPrefix, entryData, outputDir, extractedFiles);
                    }
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool ProcessVAG(string prefix, byte[] buff, string outputDir, ConcurrentBag<string> extractedFiles)
        {
            try
            {
                var vagFiles = ExtractVAGFiles(buff, prefix);
                if (vagFiles.Count == 0)
                    return false;

                foreach (var vagFile in vagFiles)
                {
                    string outputPath = GetOutputPath(outputDir, Path.GetFileNameWithoutExtension(vagFile.FileName), "vag");
                    File.WriteAllBytes(outputPath, vagFile.Data);
                    extractedFiles.Add(outputPath);
                    OnFileExtracted(outputPath);
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private List<VAGFile> ExtractVAGFiles(byte[] data, string filePrefix)
        {
            var result = new List<VAGFile>();
            int startIndex = 0;
            int fileCount = 0;

            while (true)
            {
                startIndex = IndexOf(data, VAG_START_SEQUENCE, startIndex);
                if (startIndex == -1)
                    break;

                int endIndex = IndexOf(data, VAG_END_SEQUENCE, startIndex);
                if (endIndex == -1)
                    break;

                endIndex += VAG_END_SEQUENCE.Length;
                int vagLength = endIndex - startIndex;

                byte[] vagData = new byte[vagLength];
                Array.Copy(data, startIndex, vagData, 0, vagLength);

                result.Add(new VAGFile
                {
                    FileName = $"{filePrefix}_{fileCount++}.vag",
                    Data = vagData
                });

                startIndex = endIndex;
            }

            return result;
        }

        private int IndexOf(byte[] source, byte[] pattern, int startIndex = 0)
        {
            for (int i = startIndex; i <= source.Length - pattern.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < pattern.Length; j++)
                {
                    if (source[i + j] != pattern[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match) return i;
            }
            return -1;
        }

        private bool ProcessHIP(string prefix, byte[] buff, string outputDir, ConcurrentBag<string> extractedFiles)
        {
            if (buff.Length < 8 || !buff.Take(3).SequenceEqual(HIP_SIGNATURE.Take(3)))
                return false;

            try
            {
                using (var ms = new MemoryStream(buff))
                using (var br = new BinaryReader(ms))
                {
                    br.ReadBytes(4);
                    uint unknown1 = br.ReadUInt32();
                    bool bigEndian = unknown1 != 0x0125;

                    uint fileLength = bigEndian ? SwapEndian(br.ReadUInt32()) : br.ReadUInt32();
                    br.ReadUInt32();
                    uint width = bigEndian ? SwapEndian(br.ReadUInt32()) : br.ReadUInt32();
                    uint height = bigEndian ? SwapEndian(br.ReadUInt32()) : br.ReadUInt32();
                    uint flags = bigEndian ? SwapEndian(br.ReadUInt32()) : br.ReadUInt32();
                    br.ReadUInt32();

                    uint type = flags >> 16;
                    uint compressionType = flags & 0xFFFF;

                    string outFilename = prefix;
                    if (type == HIPHDR_TYPE2)
                    {
                        uint width2 = bigEndian ? SwapEndian(br.ReadUInt32()) : br.ReadUInt32();
                        uint height2 = bigEndian ? SwapEndian(br.ReadUInt32()) : br.ReadUInt32();
                        uint offsetX = bigEndian ? SwapEndian(br.ReadUInt32()) : br.ReadUInt32();
                        uint offsetY = bigEndian ? SwapEndian(br.ReadUInt32()) : br.ReadUInt32();
                        br.ReadBytes(16);

                        outFilename += $"_x{offsetX}y{offsetY}";
                        width = width2;
                        height = height2;
                    }

                    byte[]? outBuff = null;
                    uint depth = 0;
                    byte[]? palette = null;

                    switch (compressionType)
                    {
                        case HIPHDR_COMPRESSION_TYPE_8BIT:
                            palette = br.ReadBytes(1024);
                            outBuff = br.ReadBytes((int)(width * height));
                            depth = 1;
                            break;

                        case HIPHDR_COMPRESSION_TYPE_8BIT_RLE:
                            palette = br.ReadBytes(1024);
                            outBuff = DecompressRLE(br, width * height, 1);
                            depth = 1;
                            break;

                        case HIPHDR_COMPRESSION_TYPE_GREYSCALE_RLE:
                            outBuff = DecompressGreyscaleRLE(br, width * height);
                            depth = 4;
                            break;

                        case HIPHDR_COMPRESSION_TYPE_32BIT_RLE:
                            outBuff = DecompressRLE(br, width * height, 4);
                            depth = 4;
                            break;

                        case HIPHDR_COMPRESSION_TYPE_32BIT_LONGRLE:
                            outBuff = DecompressLongRLE(br, width * height);
                            depth = 4;
                            break;

                        case HIPHDR_COMPRESSION_TYPE_LZ:
                            outBuff = DecompressLZ(br, width * height);
                            depth = 4;
                            break;

                        case HIPHDR_COMPRESSION_TYPE_SEGS:
                            var segsData = br.ReadBytes((int)(fileLength - (uint)ms.Position));
                            outBuff = Unsegs(segsData);
                            depth = 4;
                            break;

                        default:
                            return false;
                    }

                    if (outBuff == null)
                    {
                        return false;
                    }

                    string outputPath = GetOutputPath(outputDir, outFilename, "bmp");
                    WriteBMP(outputPath, outBuff, width, height, depth, palette, bigEndian);
                    extractedFiles.Add(outputPath);
                    OnFileExtracted(outputPath); // 使用基类的方法通知文件提取完成
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        private byte[] Unsegs(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var br = new BinaryReader(ms))
            {
                br.ReadBytes(4);
                ushort unknown1 = SwapEndian(br.ReadUInt16());
                ushort entryCount = SwapEndian(br.ReadUInt16());
                uint originalLength = SwapEndian(br.ReadUInt32());
                uint length = SwapEndian(br.ReadUInt32());

                var entries = new List<SEGSEntry>();
                for (int i = 0; i < entryCount; i++)
                {
                    entries.Add(new SEGSEntry
                    {
                        Length = SwapEndian(br.ReadUInt16()),
                        OriginalLength = SwapEndian(br.ReadUInt16()),
                        Offset = SwapEndian(br.ReadUInt32())
                    });
                }

                var output = new MemoryStream((int)originalLength);
                foreach (var entry in entries)
                {
                    uint chunkLen = entry.Length == 0 ? 65536 : (uint)entry.Length;
                    uint chunkOutLen = entry.OriginalLength == 0 ? 65536 : (uint)entry.OriginalLength;
                    uint offset = entry.Offset & ~1u;

                    ms.Seek(offset, SeekOrigin.Begin);
                    byte[] chunkData = br.ReadBytes((int)chunkLen);

                    if (chunkLen != chunkOutLen)
                    {
                        try
                        {
                            using (var compressedStream = new MemoryStream(chunkData, 2, chunkData.Length - 2))
                            using (var decompressedStream = new MemoryStream())
                            using (var deflateStream = new DeflateStream(compressedStream, CompressionMode.Decompress))
                            {
                                deflateStream.CopyTo(decompressedStream);
                                chunkData = decompressedStream.ToArray();
                            }
                        }
                        catch
                        {
                            chunkData = new byte[chunkOutLen];
                            Array.Copy(br.ReadBytes((int)chunkOutLen), chunkData, chunkOutLen);
                        }
                    }

                    output.Write(chunkData, 0, chunkData.Length);
                }

                return output.ToArray();
            }
        }

        private byte[] DecompressRLE(BinaryReader br, uint pixelCount, uint pixelSize)
        {
            var output = new byte[pixelCount * pixelSize];
            int index = 0;

            while (index < output.Length)
            {
                byte[] pixel = br.ReadBytes((int)pixelSize);
                byte count = br.ReadByte();

                for (int i = 0; i < count; i++)
                {
                    Array.Copy(pixel, 0, output, index, pixelSize);
                    index += (int)pixelSize;
                }
            }

            return output;
        }

        private byte[] DecompressLongRLE(BinaryReader br, uint pixelCount)
        {
            var output = new byte[pixelCount * 4];
            int index = 0;

            while (index < output.Length)
            {
                uint n = SwapEndian(br.ReadUInt32());
                bool isRaw = (n & 0x80000000) != 0;
                n &= 0x7FFFFFFF;

                if (isRaw)
                {
                    for (int i = 0; i < n; i++)
                    {
                        output[index++] = br.ReadByte();
                        output[index++] = br.ReadByte();
                        output[index++] = br.ReadByte();
                        output[index++] = br.ReadByte();
                    }
                }
                else
                {
                    byte[] pixel = br.ReadBytes(4);
                    for (int i = 0; i < n; i++)
                    {
                        Array.Copy(pixel, 0, output, index, 4);
                        index += 4;
                    }
                }
            }

            return output;
        }

        private byte[] DecompressGreyscaleRLE(BinaryReader br, uint pixelCount)
        {
            var output = new byte[pixelCount * 4];
            int index = 0;

            while (index < output.Length)
            {
                byte r = br.ReadByte();
                byte g = br.ReadByte();
                byte count = br.ReadByte();

                for (int i = 0; i < count; i++)
                {
                    output[index++] = g;
                    output[index++] = g;
                    output[index++] = g;
                    output[index++] = r;
                }
            }

            return output;
        }

        private byte[] DecompressLZ(BinaryReader br, uint pixelCount)
        {
            byte control = br.ReadByte();
            byte depth = br.ReadByte();
            byte[] bg = br.ReadBytes(4);

            var output = new byte[pixelCount * depth];
            int index = 0;

            while (index < output.Length)
            {
                byte c = br.ReadByte();

                if (c == control && br.PeekChar() != control)
                {
                    byte p = br.ReadByte();
                    byte n = br.ReadByte();

                    if (p == 0xFF) p = control;
                    p++;
                    p *= 4;

                    for (int i = 0; i < n; i++)
                    {
                        for (int j = 0; j < depth; j++)
                        {
                            output[index] = (index - p < 0) ? bg[j] : output[index - p];
                            index++;
                        }
                    }
                }
                else
                {
                    if (c == control) c = br.ReadByte();
                    output[index++] = c;

                    for (int j = 1; j < depth; j++)
                    {
                        output[index++] = br.ReadByte();
                    }
                }
            }

            return output;
        }

        private byte[] InflateData(byte[] compressedData, int outputLength)
        {
            using (var output = new MemoryStream())
            using (var compressedStream = new MemoryStream(compressedData))
            {
                compressedStream.Seek(2, SeekOrigin.Begin);

                using (var deflateStream = new DeflateStream(compressedStream, CompressionMode.Decompress))
                {
                    deflateStream.CopyTo(output);
                    return output.ToArray();
                }
            }
        }

        private void WriteBMP(string path, byte[] pixelData, uint width, uint height, uint depth, byte[]? palette, bool bigEndian)
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using (var fs = new FileStream(path, FileMode.Create))
            using (var bw = new BinaryWriter(fs))
            {
                bw.Write((byte)'B');
                bw.Write((byte)'M');
                bw.Write((uint)(14 + 40 + (palette != null ? 1024 : 0) + pixelData.Length));
                bw.Write((ushort)0);
                bw.Write((ushort)0);
                bw.Write((uint)(14 + 40 + (palette != null ? 1024 : 0)));

                bw.Write((uint)40);
                bw.Write((int)width);
                bw.Write((int)height);
                bw.Write((ushort)1);
                bw.Write((ushort)(depth * 8));
                bw.Write((uint)0);
                bw.Write((uint)pixelData.Length);
                bw.Write((int)0);
                bw.Write((int)0);
                bw.Write((uint)0);
                bw.Write((uint)0);

                if (palette != null)
                {
                    for (int i = 0; i < 256; i++)
                    {
                        bw.Write(palette[i * 4 + 2]);
                        bw.Write(palette[i * 4 + 1]);
                        bw.Write(palette[i * 4 + 0]);
                        bw.Write((byte)0);
                    }
                }

                int stride = (int)(width * depth);
                for (int y = (int)height - 1; y >= 0; y--)
                {
                    bw.Write(pixelData, y * stride, stride);
                }
            }
        }

        private uint SwapEndian(uint value)
        {
            return (value & 0x000000FFU) << 24 | (value & 0x0000FF00U) << 8 |
                   (value & 0x00FF0000U) >> 8 | (value & 0xFF000000U) >> 24;
        }

        private ushort SwapEndian(ushort value)
        {
            return (ushort)((value & 0x00FF) << 8 | (value & 0xFF00) >> 8);
        }

        private struct SEGSEntry
        {
            public ushort Length;
            public ushort OriginalLength;
            public uint Offset;
        }

        private class VAGFile
        {
            public required string FileName { get; set; }
            public required byte[] Data { get; set; }
        }
    }
}