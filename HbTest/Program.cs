using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Auth;
using Microsoft.WindowsAzure.Storage.Blob;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace HbTest
{
    class Program
    {
        static void Main(string[] args)
        {
            var account = new CloudStorageAccount(
                new StorageCredentials(
                    "hbgittest",
                    "up4w+hjVi6jo+yXPrwi1t16G8sBPWZEocCqRMSzlaaJ2nntWfXvd3Ondk9J52FlxSLOm21fZRe26w14UcMQjLA=="
                ),
                true
            );
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
                // I couldn't figure out another way to call async methods from main since main itself can't be async :(
                var t = Task.Run(async () =>
                {
                    try
                    {
                        await ProcessBlobAsync(cb, client);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"ERROR: processing {cb.Name} has thrown:\n{ex}");
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
            await RunFfmpegAsync("-vn -i - -f wav -bitexact -", input, output); // without -bitexact ffmpeg writes additional chunks in the WAV header which complicates things

            FixFfmpegWavOutput(output);

            Console.WriteLine($"INFO: uploading audio extracted from {inputName}");

// For testing
//            using (var fs = File.Open("test.wav", FileMode.Create))
//            {
//                output.WriteTo(fs);
//            }

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

            data.Seek(0, SeekOrigin.Begin);
            await blob.UploadFromStreamAsync(data);
        }

        /// <summary>
        /// When writing to a pipe, ffmpeg can't seek back to the beginning of the stream
        /// to set size bits in the header, so we have to do it ourselves.
        /// ref: http://soundfile.sapp.org/doc/WaveFormat/
        /// </summary>
        private static void FixFfmpegWavOutput(MemoryStream output)
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

        private static async Task RunFfmpegAsync(string args, Stream input, Stream output)
        {
            var info = new ProcessStartInfo
            {
                FileName = @"ffmpeg\ffmpeg.exe",
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

                var tIn = input.CopyToAsync(proc.StandardInput.BaseStream);
                var tOut = proc.StandardOutput.BaseStream.CopyToAsync(output);
                await tIn;
                proc.StandardInput.Close();
                await tOut;
                proc.WaitForExit();

                if (proc.ExitCode != 0)
                {
                    throw new Exception($"ffmpeg error:\n{proc.StandardError.ReadToEnd()}");
                }
            }
        }

        private static async Task ExtractAndUploadFramesAsync(Stream input, string inputName, CloudBlobClient client)
        {
            Console.WriteLine($"INFO: extracting frames from {inputName}");

            var output = new MemoryStream();
            await RunFfmpegAsync("-i - -vf fps=1/3 -c:v png -f image2pipe -", input, output);

            Console.WriteLine($"INFO: uploading frames extracted from {inputName}");

            // TODO split data into separate images and upload

            throw new NotImplementedException();
        }
    }
}
