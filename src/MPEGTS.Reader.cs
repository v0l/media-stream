using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Threading;

namespace MediaStreams
{
    public static class MPEGTSReader
    {
        private static IEnumerable<(SequencePosition pos, MPEGTS.Packet packet)> ReadPackets(ReadOnlySequence<byte> buf, CancellationToken cancellationToken = default)
        {
            SequencePosition? sp = null;
            do
            {
                sp = buf.PositionOf(MPEGTS.SyncMarker);
                if (sp != null && buf.Length >= MPEGTS.PacketLength)
                {
                    //try to parse a packet starting at sp
                    var packetData = buf.Slice(sp.Value, MPEGTS.PacketLength);

                    var pkt = MPEGTS.Packet.Parse(packetData);
                    pkt.OriginalData = packetData;

                    yield return (sp.Value, pkt);

                    var nextMsgPos = buf.GetPosition(MPEGTS.PacketLength, sp.Value);
                    buf = buf.Slice(nextMsgPos);
                }
                else
                {
                    break;
                }
            } while (sp != null && !cancellationToken.IsCancellationRequested);
        }

        public static async IAsyncEnumerable<MPEGTS.Packet> TryReadPackets(PipeReader pr, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var data = await pr.ReadAsync(cancellationToken);
                var buf = data.Buffer;

                SequencePosition? sp = null;
                foreach (var pkt in ReadPackets(buf, cancellationToken))
                {
                    yield return pkt.packet;
                    sp = pkt.pos;
                }

                if (sp != null)
                {
                    // sp is the start of the last parsed packet
                    // advance to the end of the last parsed packet
                    pr.AdvanceTo(buf.GetPosition(MPEGTS.PacketLength, sp.Value));
                }
                else
                {
                    pr.AdvanceTo(buf.Start);
                }

                //no more data coming 
                if (data.IsCompleted)
                {
                    break;
                }
            }
        }
    }
}