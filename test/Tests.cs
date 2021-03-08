using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using MediaStreams;
using Xunit;

namespace test
{
    public class MPEGTS_Tests
    {
        public Task PipeStream(Stream fs, Func<PipeReader, Task> handle)
        {
            var pipe = new Pipe();
            var reader = Task.Run(async () =>
            {
                var r = pipe.Reader;
                try
                {
                    await handle(r);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex);
                }
                finally
                {
                    await r.CompleteAsync();
                }
            });
            var writer = Task.Run(async () =>
            {
                var w = pipe.Writer;
                try
                {
                    while (true)
                    {
                        var mem = w.GetMemory();
                        var rlen = await fs.ReadAsync(mem);
                        if (rlen == 0)
                        {
                            break;
                        }
                        w.Advance(rlen);
                        var wres = await w.FlushAsync();
                        if (wres.IsCompleted)
                        {
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.ToString());
                }
                await w.CompleteAsync();
            });

            return Task.WhenAll(reader, writer);
        }

        public async Task CompareFiles(string a, string b)
        {
            using var af = new FileStream(a, FileMode.Open);
            using var bf = new FileStream(b, FileMode.Open);

            Assert.Equal(af.Length, bf.Length);

            using var sha = new SHA256Managed();
            var ha = BitConverter.ToString(await sha.ComputeHashAsync(af));
            var hb = BitConverter.ToString(await sha.ComputeHashAsync(bf));

            Assert.True(ha == hb);
        }

        public static byte[] StringToByteArray(string hex)
        {
            return Enumerable.Range(0, hex.Length)
                             .Where(x => x % 2 == 0)
                             .Select(x => Convert.ToByte(hex.Substring(x, 2), 16))
                             .ToArray();
        }

        [Fact]
        public void ParseBroken()
        {
            var pktData = new [] 
            {
                StringToByteArray("4738A80D6C61B22792CDBC9029FE6B69BB86059738BEFC0DDD48E8368FCF72D3CE2703CA21D136B3CBB2D4B8258745904E21F4D97503FB8AAC0DA90701692F19C16664CD6BABAD63543629B9E44304BAC49C7ED958B36A4C6297D22B3CD60E0A07B00AEAF39D3043E1A43617999FC75B36A96D558DA4C52E5B2713671CC1FF3049C72FEB77EC101697C01B04D78CB643B9FFE5F2B227DC26E7B8B29E04B1E7B747010018166104753F90C0B8CC8916BA2DCABCBD8EE96223C7654788"),
                StringToByteArray("47812232CECDA9FAD8D9ED57A2A73F35331242AE359D2C04C3874CE3CEEAEA27A7FCD262404997B147010015C10B14227B7F702B7118551B4F5F7EA76C95BC56A46EA2BFFBF6C706226EC21BE0CCF4431E772FC7AD5A1F175BC77C7055DFE979B71DDA60733B740E3A9C5E51D2C5C55A24A4FF30FC9DE5406E04E29A48178FF1F74E6F805A48AAB3208AF99B34199DB7F836AC1AC0B676678DC3F06062778ED60E1A6D92CEFE8EC1AE53868A4F070FA3C8312E922C84118CA7A7D39E")
            };
            foreach(var p in pktData) 
            {
                var pkt = MPEGTS.Packet.Parse(new ReadOnlySequence<byte>(p));
            }
        }

        [Fact]
        /// <summary>
        /// Read/Write a file and check the contents are identical
        /// </summary>
        /// <returns></returns>
        public async Task ReadWrite()
        {
            const string fpath = @"C:\Users\Kieran\Downloads\astra192E-ts1080-2018-05-11.ts";
            var fpath_tst = Path.ChangeExtension(fpath, ".ts.test");

            using var fsin = new FileStream(fpath, FileMode.Open, FileAccess.Read);
            using var fsout = new FileStream(fpath_tst, FileMode.Create);
            var mem = MemoryPool<byte>.Shared.Rent(MediaStreams.MPEGTS.PacketLength);
            await PipeStream(fsin, async (pr) =>
            {
                await foreach (var pkt in MPEGTSReader.TryReadPackets(pr))
                {
                    //pkt.Write(mem.Memory.Span);
                    //await fsout.WriteAsync(mem.Memory.Slice(0, MediaStreams.MPEGTS.PacketLength));
                }
            });
            fsin.Close();
            fsout.Close();

            //compare the files
            await CompareFiles(fpath, fpath_tst);
        }

        [Fact]
        public async Task ReadM3U()
        {
            const string fpath = @"C:\Users\Kieran\Downloads\tv_channels_kieran@harkin.me_plus.m3u";

            var hc = new HttpClient();
            hc.Timeout = TimeSpan.FromSeconds(2);
            hc.DefaultRequestHeaders.Add("user-agent", "libmpv");

            using var fsin = new FileStream(fpath, FileMode.Open, FileAccess.Read);
            using var fsout = new FileStream(Path.ChangeExtension(fpath, ".checked.m3u8"), FileMode.Create);
            await PipeStream(fsin, async (pr) =>
            {
                await foreach (var entry in MediaStreams.M3UReader.ReadPlaylist(pr))
                {
                    if (entry is Track t)
                    {
                        try
                        {
                            var rsp = await hc.GetAsync(t.Path, HttpCompletionOption.ResponseHeadersRead);
                            if (!rsp.IsSuccessStatusCode)
                            {
                                Console.WriteLine($"DOWN >> {t.Title}");
                                continue;
                            }
                        }
                        catch
                        {
                            Console.WriteLine($"DOWN >> {t.Title}");
                            continue;
                        }
                    }

                    Console.WriteLine($"{entry.Tag}:{entry.Value}");
                    var data = Encoding.UTF8.GetBytes(entry.Value != null ? $"{entry.Tag}:{entry.Value}" : entry.Tag);
                    await fsout.WriteAsync(data);
                    await fsout.WriteAsync(new byte[] { 13, 10 });
                    await fsout.FlushAsync();
                }
            });
            fsin.Close();
            fsout.Close();
        }
    }
}
