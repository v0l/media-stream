using System;
using System.IO;
using System.IO.Pipelines;
using System.Threading.Tasks;

namespace MediaStreams
{
    class Program
    {
        static Task Main(string[] args)
        {
            var pipe = new Pipe();
            var reader = Task.Run(async () =>
            {
                var r = pipe.Reader;
                try
                {
                    await foreach (var pkt in MPEGTS.TryReadPackets(r))
                    {
                        if(pkt.IsPESPayload) 
                        {
                            Console.WriteLine(pkt);
                        }
                    }
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
                    using var fs = new FileStream(@"C:\Users\Kieran\Downloads\68E_12610.912_H_6710.ts", FileMode.Open);
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
    }
}
