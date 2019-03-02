using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Auth;
using Microsoft.WindowsAzure.Storage.Blob;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace HbTest
{
    class Program
    {
        private const string FFmpegCmd = "ffmpeg.exe";
        private const double FrameDelay = 3; // seconds

        static void Main(string[] args)
        {
            if (args.Length != 2)
            {
                Console.WriteLine("Usage:\n\nHbTest <accountName> <keyValue>\n");
                return;
            }

            var account = new CloudStorageAccount(new StorageCredentials(args[0], args[1]), true);
            var client = account.CreateCloudBlobClient();

            var vc = client.GetContainerReference("videos");
            if (!vc.Exists())
            {
                Console.WriteLine("ERROR: Container 'videos' does not exist");
                Environment.Exit(1);
            }

            var taskList = new List<Task>();
            var blob = vc.ListBlobs().First();
            if (blob is CloudBlob cb)
            {
                // Couldn't figure out another way to call async methods from main since main itself can't be async :(
                var t = Task.Run(async () =>
                {
                    try
                    {
                        await ProcessBlobAsync(cb, client);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"ERROR: processing {cb.Name} has thrown:\n{ex}");
                        Environment.ExitCode = 1;
                    }
                });
                taskList.Add(t);
            }
            else
            {
                Console.WriteLine($"WARN: skipping {blob} (not a CloudBlob)");
            }

            Task.WaitAll(taskList.ToArray());
        }

        private static async Task ProcessBlobAsync(CloudBlob blob, CloudBlobClient client)
        {
            if (blob.Properties.ContentType != "video/mp4")
            {
                Console.WriteLine($"WARN: skipping {blob.Name} (not an mp4 file)");
                return;
            }

            Console.WriteLine($"INFO: processing {blob.Name}: {blob.Uri}");

            var ba = new byte[blob.Properties.Length];
            await blob.DownloadToStreamAsync(new MemoryStream(ba));

            var t1 = ExtractAndUploadAudioAsync(new MemoryStream(ba), blob.Name, client);
            var t2 = ExtractAndUploadFramesAsync(new MemoryStream(ba), blob.Name, client);

            await t1;
            await t2;

            Console.WriteLine($"INFO: finished processing {blob.Name}");
        }

        private static async Task ExtractAndUploadAudioAsync(Stream input, string inputName, CloudBlobClient client)
        {
            Console.WriteLine($"INFO: extracting audio from {inputName}");

            var output = new MemoryStream();
            await RunFFmpegAsync("-vn -i - -f wav -bitexact -", input, output); // without -bitexact ffmpeg writes additional chunks in the WAV header which complicates things

            FixFFmpegWavOutput(output);

            output.Seek(0, SeekOrigin.Begin);

            // For testing
//            using (var fs = File.Open("test.wav", FileMode.Create))
//            {
//                output.CopyTo(fs);
//            }

            Console.WriteLine($"INFO: uploading audio extracted from {inputName}");
            await UploadBlob("audios", $"{inputName}-audio-ng42.wav", output, client);
        }

        private static async Task UploadBlob(string containerName, string blobName, Stream data, CloudBlobClient client)
        {
            var container = client.GetContainerReference(containerName);
            if (!container.Exists())
            {
                throw new Exception($"container '{containerName}' does not exist");
            }

            var blob = container.GetBlockBlobReference(blobName);

            await blob.UploadFromStreamAsync(data);
        }

        /// <summary>
        /// When writing to a pipe, ffmpeg can't seek back to the beginning of the stream
        /// to set size bits in the header, so we have to do it ourselves.
        /// ref: http://soundfile.sapp.org/doc/WaveFormat/
        /// </summary>
        private static void FixFFmpegWavOutput(MemoryStream output)
        {
            var chunkSize = (int) output.Length - 8;
            var csb = BitConverter.GetBytes(chunkSize);
            var b = output.GetBuffer();
            b[4] = csb[0];
            b[5] = csb[1];
            b[6] = csb[2];
            b[7] = csb[3];

            var subchunk2Size = (int) output.Length - 44;
            var scsb = BitConverter.GetBytes(subchunk2Size);
            b[40] = scsb[0];
            b[41] = scsb[1];
            b[42] = scsb[2];
            b[43] = scsb[3];
        }

        private static async Task RunFFmpegAsync(string args, Stream input, Stream output, string command = FFmpegCmd)
        {
            var info = new ProcessStartInfo
            {
                FileName = $@"ffmpeg\{command}",
                Arguments = args,
                CreateNoWindow = false,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                RedirectStandardInput = true,
            };
            using (var proc = Process.Start(info))
            {
                if (null == proc)
                {
                    throw new Exception("ffmpeg failed to start");
                }

                var stdErr = new StringBuilder();
                proc.BeginErrorReadLine();
                proc.ErrorDataReceived += (sender, eventArgs) => stdErr.Append(eventArgs.Data);

                var tIn = input.CopyToAsync(proc.StandardInput.BaseStream);
                var tOut = proc.StandardOutput.BaseStream.CopyToAsync(output);
                try { await tIn; proc.StandardInput.Close(); } catch {/*Ignore stream errors: ffmpeg may exit before we finish writing to the stream, which is normal, and we'll see ffmpeg error output later*/}
                try { await tOut; proc.StandardOutput.Close(); } catch {/*Ignore stream errors: we'll see ffmpeg error output later*/}
                proc.WaitForExit();

                if (proc.ExitCode != 0)
                {
                    throw new Exception($"ffmpeg error:\n{stdErr}");
                }
            }
        }

        private static async Task ExtractAndUploadFramesAsync(Stream input, string inputName, CloudBlobClient client)
        {
            Console.WriteLine($"INFO: extracting frames from {inputName}");

            var ffmpegOutput = new MemoryStream();
            await RunFFmpegAsync($"-i - -vf fps=1/{FrameDelay} -c:v png -f image2pipe -", input, ffmpegOutput);

            ffmpegOutput.Seek(0, SeekOrigin.Begin);

            Console.WriteLine($"INFO: parsing frames extracted from {inputName}");
            var images = ParseConcatenatedPNGs(ffmpegOutput);

            Console.WriteLine($"INFO: uploading frames extracted from {inputName}");
            Task.WaitAll(
                images
                    .Select(image => new MemoryStream(image))
                    .Select((stream, idx) => UploadBlob("frames", $@"{inputName}-image-{idx}.png", stream, client))
                    .ToArray()
            );
        }

        private static IEnumerable<byte[]> ParseConcatenatedPNGs(Stream stream)
        {
            var list = new List<byte[]>();
            byte[] pngBytes;
            
            while ((pngBytes = ReadOnePNG(stream)) != null)
            {
                list.Add(pngBytes);
            }

            return list;
        }

        private static readonly byte[] PngSignature = {0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A};

        private static byte[] ReadOnePNG(Stream stream)
        {
            var signature = new byte[8];
            stream.Read(signature, 0, 8);
            if (!signature.SequenceEqual(PngSignature))
            {
                return null; // PNG signature not found, assume end of stream
            }

            var ms = new MemoryStream();

            ms.Write(signature, 0, signature.Length);

            string chunkType = null;
            while (chunkType != "IEND")
            {
                var length = CopyUInt32BE(stream, ms);
                chunkType = CopyChunkType(stream, ms);
                CopyChunk(length + 4, stream, ms, 4100); // length + 4 to copy CRC, too; bufsize == 4100 b/c typical data+crc length for PNG
            }

            return ms.GetBuffer();
        }

        private static void CopyChunk(long length, Stream input, Stream output, int bufSize = 81920)
        {
            while (length > 0)
            {
                var b = new byte[Math.Min(length, bufSize)];
                var read = input.Read(b, 0, b.Length);
                output.Write(b, 0, b.Length);
                length -= read;
            }
        }

        private static uint CopyUInt32BE(Stream input, Stream output)
        {
            var b = new byte[4];

            input.Read(b, 0, 4);
            output.Write(b, 0, 4);

            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(b);
            }

            return BitConverter.ToUInt32(b, 0);
        }

        private static string CopyChunkType(Stream input, Stream output)
        {
            var b = new byte[4];

            input.Read(b, 0, 4);
            output.Write(b, 0, 4);

            return Encoding.ASCII.GetString(b, 0, 4);
        }
    }
}
