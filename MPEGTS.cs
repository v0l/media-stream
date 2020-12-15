using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace TSER
{
    public class MPEGTS
    {
        public enum TransportScramblingControl
        {
            NotScrambeled = 0,
            Reserved = 1,
            ScrambeledEvent = 2,
            ScrambeledOdd = 3
        }

        public enum AdaptationFieldControl
        {
            Reserved = 0,
            PayloadOnly = 1,
            AdaptationFieldOnly = 2,
            AdaptationFieldAndPayload = 3
        }

        [Flags]
        public enum AdaptationFieldFlags
        {
            /// <summary>
            /// Set when adaptation extension data is present
            /// </summary>
            AdaptationFieldExtension = 1,

            /// <summary>
            /// Set when transport private data is present
            /// </summary>
            TransportPrivateData = 2,

            /// <summary>
            /// Set when splice countdown field is present
            /// </summary>
            SplicingPoint = 4,

            /// <summary>
            /// Set when OPCR field is present
            /// </summary>
            OPCR = 8,

            /// <summary>
            /// Set when PCR field is present
            /// </summary>
            PCR = 16,

            /// <summary>
            /// Set when this stream should be considered "high priority"
            /// </summary>
            ElementaryStreamPriority = 32,

            /// <summary>
            /// Set when the stream may be decoded without errors from this point
            /// </summary>
            RandomAccess = 64,

            /// <summary>
            /// Set if current TS packet is in a discontinuity state with respect to either the continuity counter or the program clock reference
            /// </summary>
            Discontinuity = 128
        }

        public enum AdaptationFieldExtensionFlags
        {
            SeamlessSplice = 0x0020,
            PiecewiseRate = 0x0040,
            LegalTimeWindow = 0x0080
        }

        public struct AdaptationFieldExtension
        {
            public AdaptationFieldExtensionFlags Flags { get; set; }

            public short LegalTimeWindow { get; set; }

            public int PiecewiseRate { get; set; }

            public byte[] SeamlessSplice { get; set; }

            public override string ToString()
            {
                return $@"Flags = {Flags}, LTW = {LegalTimeWindow}, PWR = {PiecewiseRate}, SS = {(SeamlessSplice != null ? BitConverter.ToString(SeamlessSplice).Replace("-", "") : null)}";
            }
        }

        public struct AdaptationField
        {
            public AdaptationFieldFlags Flags { get; set; }

            public long PCR { get; set; }

            public long OPCR { get; set; }

            public sbyte SpliceCountdown { get; set; }

            public byte[] TransportPrivateData { get; set; }

            public AdaptationFieldExtension AdaptationFieldExtension { get; set; }

            public override string ToString()
            {
                return $@"
Flags                 : {Flags}
PCR                   : {PCR}
OPCR                  : {OPCR}
SpliceCountdown       : {SpliceCountdown}
TransportPrivateData  : {TransportPrivateData?.Length}
Extension             : {AdaptationFieldExtension}";
            }
        }

        public struct TSPacket
        {
            public bool TransportError { get; private set; }

            public bool PayloadUnitStart { get; private set; }

            public bool TransportPriority { get; private set; }

            public short Pid { get; private set; }

            public TransportScramblingControl Scrambling { get; private set; }

            public AdaptationFieldControl AdaptationFieldControl { get; private set; }

            public byte Continuity { get; private set; }

            public AdaptationField AdaptationField { get; private set; }

            public byte[] Payload { get; private set; }

            public override string ToString()
            {
                return $@"=====================
PID                 : {Pid}
TransportError      : {TransportError}
PayloadUnitStart    : {PayloadUnitStart}
TransportPriority   : {TransportPriority}
Scrambling          : {Scrambling}
AdaptionControl     : {AdaptationFieldControl}
Continuity          : {Continuity}
AdaptationField     : {AdaptationField}
Payload             : {Payload?.Length ?? 0}
=====================";
            }

            /// <summary>
            /// Read the header portion of a TSPacket
            /// </summary>
            /// <param name="header"></param>
            /// <returns></returns>
            public static TSPacket ParseHeader(ReadOnlySequence<byte> header)
            {
                var sr = new SequenceReader<byte>(header);
                if (sr.TryReadBigEndian(out int hv))
                {
                    var pkt = new TSPacket()
                    {
                        TransportError = (hv & 0x800000) > 1,
                        PayloadUnitStart = (hv & 0x400000) > 1,
                        TransportPriority = (hv & 0x200000) > 1,
                        Pid = (short)((hv & 0x001fff) >> 8),
                        Scrambling = (TransportScramblingControl)((hv & 0xc0) >> 6),
                        AdaptationFieldControl = (AdaptationFieldControl)((hv & 0x30) >> 4),
                        Continuity = (byte)(hv & 0x0f)
                    };
                    if (!((int)pkt.AdaptationFieldControl >= 1 && (int)pkt.AdaptationFieldControl <= 3))
                    {
                        throw new Exception("Invalid AFC");
                    }
                    return pkt;
                }
                return default;
            }

            /// <summary>
            /// Read the Payload for this packet
            /// </summary>
            /// <param name="pl"></param>
            /// <returns></returns>
            public bool ReadPayload(int length, ReadOnlySequence<byte> pl)
            {
                var sr = new SequenceReader<byte>(pl);

                Payload = new byte[length];
                return sr.TryCopyTo(Payload);
            }

            /// <summary>
            /// Read the adaption field for this packet
            /// <para><see cref="af"/> must start at the begingging of the Adaption Field</para>
            /// </summary>
            /// <param name="af"></param>
            /// <returns>True if parsing was a success</returns>
            public bool ReadAdaptionField(ReadOnlySequence<byte> af, out byte afLength)
            {
                var sr = new SequenceReader<byte>(af);
                if (sr.TryRead(out afLength) && sr.TryRead(out byte afFlags))
                {
                    var ret = new AdaptationField()
                    {
                        Flags = (AdaptationFieldFlags)afFlags
                    };

                    if (ret.Flags.HasFlag(AdaptationFieldFlags.PCR))
                    {
                        if (sr.TryReadBigEndian(out int low) && sr.TryRead(out byte high))
                        {
                            var pcr = ((long)high << 32) & low;
                            ret.PCR = pcr;
                        }
                        else
                        {
                            return false;
                        }
                    }

                    if (ret.Flags.HasFlag(AdaptationFieldFlags.OPCR))
                    {
                        if (sr.TryReadBigEndian(out int low) && sr.TryRead(out byte high))
                        {
                            var pcr = ((long)high << 32) & low;
                            ret.OPCR = pcr;
                        }
                        else
                        {
                            return false;
                        }
                    }

                    if (ret.Flags.HasFlag(AdaptationFieldFlags.SplicingPoint))
                    {
                        if (sr.TryRead(out byte sp))
                        {
                            ret.SpliceCountdown = (sbyte)sp;
                        }
                        else
                        {
                            return false;
                        }
                    }

                    if (ret.Flags.HasFlag(AdaptationFieldFlags.TransportPrivateData))
                    {
                        if (sr.TryRead(out byte tpdLen))
                        {
                            var buf = new byte[tpdLen];
                            if (sr.TryCopyTo(buf))
                            {
                                ret.TransportPrivateData = buf;
                            }
                            else
                            {
                                return false;
                            }
                        }
                        else
                        {
                            return false;
                        }
                    }

                    if (ret.Flags.HasFlag(AdaptationFieldFlags.AdaptationFieldExtension))
                    {
                        if (sr.TryRead(out byte afeLen) && sr.TryRead(out byte afeFlags))
                        {
                            var afe = new AdaptationFieldExtension()
                            {
                                Flags = (AdaptationFieldExtensionFlags)afeFlags
                            };

                            if (afe.Flags.HasFlag(AdaptationFieldExtensionFlags.LegalTimeWindow))
                            {
                                if (sr.TryReadLittleEndian(out short ltw))
                                {
                                    afe.LegalTimeWindow = ltw;
                                }
                                else
                                {
                                    return false;
                                }
                            }

                            if (afe.Flags.HasFlag(AdaptationFieldExtensionFlags.PiecewiseRate))
                            {
                                if (sr.TryRead(out byte pw0) && sr.TryRead(out byte pw1) && sr.TryRead(out byte pw2))
                                {
                                    var pwv = pw0 << 16
                                        & pw1 << 8
                                        & pw2;
                                    afe.PiecewiseRate = pwv;
                                }
                                else
                                {
                                    return false;
                                }
                            }

                            if (afe.Flags.HasFlag(AdaptationFieldExtensionFlags.SeamlessSplice))
                            {
                                var ss = new byte[5];
                                if (sr.TryCopyTo(ss))
                                {
                                    afe.SeamlessSplice = ss;
                                }
                                else
                                {
                                    return false;
                                }
                            }
                            ret.AdaptationFieldExtension = afe;
                        }
                        else
                        {
                            return false;
                        }
                    }

                    AdaptationField = ret;
                }
                else
                {
                    return false;
                }

                return true;
            }
        }

        public static async IAsyncEnumerable<TSPacket> TryReadPackets(PipeReader pr, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            const byte SyncMarker = 0x47;
            const int PacketLength = 188;

            while (!cancellationToken.IsCancellationRequested)
            {
                var data = await pr.ReadAsync(cancellationToken);
                if (data.IsCompleted)
                {
                    break;
                }
                var buf = data.Buffer;
                SequencePosition? sp = null;
                do
                {
                    sp = buf.PositionOf(SyncMarker);
                    if (sp != null && buf.Length >= PacketLength)
                    {
                        //try to parse a packet starting at sp
                        var packetData = buf.Slice(sp.Value, PacketLength);

                        var pkt = TSPacket.ParseHeader(packetData);
                        if (!pkt.Equals(default))
                        {
                            var pktOffset = 4;
                            if (pkt.AdaptationFieldControl == AdaptationFieldControl.AdaptationFieldAndPayload
                                    || pkt.AdaptationFieldControl == AdaptationFieldControl.AdaptationFieldOnly)
                            {
                                if (pkt.ReadAdaptionField(packetData.Slice(packetData.GetPosition(pktOffset, sp.Value)), out byte afLength))
                                {
                                    pktOffset += afLength + 1;
                                }
                                else
                                {
                                    throw new Exception("Failed to read adaption field");
                                }
                            }

                            if (pkt.AdaptationFieldControl == AdaptationFieldControl.AdaptationFieldAndPayload
                                || pkt.AdaptationFieldControl == AdaptationFieldControl.PayloadOnly)
                            {
                                if (!pkt.ReadPayload(PacketLength - pktOffset, packetData.Slice(packetData.GetPosition(pktOffset, sp.Value))))
                                {
                                    throw new Exception("Failed to read payload");
                                }
                            }
                            yield return pkt;
                        }

                        buf = buf.Slice(buf.GetPosition(PacketLength, sp.Value));
                    }
                    else
                    {
                        break;
                    }
                } while (sp != null && !cancellationToken.IsCancellationRequested);
                if (sp != null)
                {
                    pr.AdvanceTo(sp.Value);
                }
            }
        }
    }
}