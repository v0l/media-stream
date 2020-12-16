using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Threading;

namespace MediaStreams
{
    public class MPEGTS
    {
        public const byte SyncMarker = 0x47;
        public const byte PacketLength = 188;

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
            AdaptationFieldExtension = 0x01,

            /// <summary>
            /// Set when transport private data is present
            /// </summary>
            TransportPrivateData = 0x02,

            /// <summary>
            /// Set when splice countdown field is present
            /// </summary>
            SplicingPoint = 0x04,

            /// <summary>
            /// Set when OPCR field is present
            /// </summary>
            OPCR = 0x08,

            /// <summary>
            /// Set when PCR field is present
            /// </summary>
            PCR = 0x10,

            /// <summary>
            /// Set when this stream should be considered "high priority"
            /// </summary>
            ElementaryStreamPriority = 0x20,

            /// <summary>
            /// Set when the stream may be decoded without errors from this point
            /// </summary>
            RandomAccess = 0x40,

            /// <summary>
            /// Set if current TS packet is in a discontinuity state with respect to either the continuity counter or the program clock reference
            /// </summary>
            Discontinuity = 0x80,

            /// <summary>
            /// All bit flags are set
            /// </summary>
            All = 0x100
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

            /// <summary>
            /// The length of this message in bytes
            /// </summary>
            /// <returns></returns>
            public int Length =>
                1 /* Length byte */ +
                1 /* Flags */ +
                (Flags.HasFlag(AdaptationFieldExtensionFlags.LegalTimeWindow) ? 2 : 0) +
                (Flags.HasFlag(AdaptationFieldExtensionFlags.PiecewiseRate) ? 3 : 0) +
                (Flags.HasFlag(AdaptationFieldExtensionFlags.SeamlessSplice) ? 5 : 0);

            public override string ToString()
            {
                return $@"Flags = {Flags}, LTW = {LegalTimeWindow}, PWR = {PiecewiseRate}, SS = {(SeamlessSplice != null ? BitConverter.ToString(SeamlessSplice).Replace("-", "") : null)}";
            }

            public static AdaptationFieldExtension Parse(ReadOnlySequence<byte> buf)
            {
                var sr = new SequenceReader<byte>(buf);
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
                            return default;
                        }
                    }

                    if (afe.Flags.HasFlag(AdaptationFieldExtensionFlags.PiecewiseRate))
                    {
                        if (sr.TryRead(out byte pw0) && sr.TryRead(out byte pw1) && sr.TryRead(out byte pw2))
                        {
                            //TODO: test this
                            var pwv = pw0 << 16
                                & pw1 << 8
                                & pw2;
                            afe.PiecewiseRate = pwv;
                        }
                        else
                        {
                            return default;
                        }
                    }

                    if (afe.Flags.HasFlag(AdaptationFieldExtensionFlags.SeamlessSplice))
                    {
                        //TODO: parse this properly
                        var ss = new byte[5];
                        if (sr.TryCopyTo(ss))
                        {
                            afe.SeamlessSplice = ss;
                        }
                        else
                        {
                            return default;
                        }
                    }
                    return afe;
                }
                else
                {
                    return default;
                }
            }

            public bool Write(Span<byte> mem)
            {
                var len = Length;
                if (mem.Length < len)
                {
                    throw new Exception($"Not enough space to write {nameof(AdaptationFieldExtension)}, need {len} bytes, have {mem.Length} bytes.");
                }

                var span = mem.Slice(0, len);
                var offset = 0;

                span[offset++] = (byte)len;
                span[offset++] = (byte)Flags;

                if (Flags.HasFlag(AdaptationFieldExtensionFlags.LegalTimeWindow))
                {
                    if (!BitConverter.TryWriteBytes(span.Slice(offset), LegalTimeWindow))
                    {
                        return false;
                    }
                    offset += 2;
                }

                if (Flags.HasFlag(AdaptationFieldExtensionFlags.PiecewiseRate))
                {
                    //TODO: write pwr
                    offset += 3;
                }

                if (Flags.HasFlag(AdaptationFieldExtensionFlags.SeamlessSplice))
                {
                    SeamlessSplice.CopyTo(span.Slice(offset));
                    offset += 5;
                }

                return true;
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

            public byte Length { get; set; }

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

            public void Validate()
            {
                if ((int)Flags < 0 || Flags > AdaptationFieldFlags.All)
                {
                    throw new Exception("Invalid AFF");
                }
                if (Flags.HasFlag(AdaptationFieldFlags.TransportPrivateData) && TransportPrivateData == null)
                {
                    throw new Exception("TPD flag was set but was not loaded");
                }
            }

            /// <summary>
            /// The length of this message in bytes
            /// </summary>
            public int CalculatedLength =>
                1 /* Flags */ +
                (Flags.HasFlag(AdaptationFieldFlags.PCR) ? 5 : 0) +
                (Flags.HasFlag(AdaptationFieldFlags.OPCR) ? 5 : 0) +
                (Flags.HasFlag(AdaptationFieldFlags.SplicingPoint) ? 1 : 0) +
                (Flags.HasFlag(AdaptationFieldFlags.TransportPrivateData) ? 1 + (TransportPrivateData?.Length ?? 0) : 0) +
                (Flags.HasFlag(AdaptationFieldFlags.AdaptationFieldExtension) ? 1 + AdaptationFieldExtension.Length : 0);

            /// <summary>
            /// Read the adaption field for this packet
            /// <para><see cref="af"/> must start at the begingging of the Adaption Field</para>
            /// </summary>
            /// <param name="af"></param>
            /// <returns>True if parsing was a success</returns>
            public static AdaptationField Parse(ReadOnlySequence<byte> af)
            {
                var sr = new SequenceReader<byte>(af);
                if (sr.TryRead(out byte adaptionFieldLength))
                {
                    var ret = new AdaptationField()
                    {
                        Length = adaptionFieldLength
                    };

                    if (ret.Length > 0 && sr.TryRead(out byte afFlags))
                    {
                        if (ret.Flags.HasFlag(AdaptationFieldFlags.PCR))
                        {
                            if (sr.TryReadBigEndian(out int low) && sr.TryRead(out byte high))
                            {
                                var pcr = ((long)high << 32) & low;
                                ret.PCR = pcr;
                            }
                            else
                            {
                                return default;
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
                                return default;
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
                                return default;
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
                                    return default;
                                }
                            }
                            else
                            {
                                return default;
                            }
                        }

                        if (ret.Flags.HasFlag(AdaptationFieldFlags.AdaptationFieldExtension))
                        {
                            var afe = AdaptationFieldExtension.Parse(af.Slice(sr.Position));
                            if (!afe.Equals(default))
                            {
                                ret.AdaptationFieldExtension = afe;
                            }
                        }
                    }
                    return ret;
                }
                return default;
            }

            public bool Write(Span<byte> mem)
            {
                var len = Length;
                if (mem.Length < Length + 1)
                {
                    throw new Exception($"Not enough space to write {nameof(AdaptationField)}, need {len} bytes, have {mem.Length} bytes.");
                }

                var span = mem.Slice(0, len + 1);

                span[0] = (byte)len;
                if(len > 0) 
                {
                    span[1] = (byte)Flags;

                    int offset = 2;
                    if (Flags.HasFlag(AdaptationFieldFlags.PCR))
                    {
                        //TODO: write PCR
                        offset += 5;
                    }
                    if (Flags.HasFlag(AdaptationFieldFlags.OPCR))
                    {
                        //TODO: write OPCR
                        offset += 5;
                    }
                    if (Flags.HasFlag(AdaptationFieldFlags.SplicingPoint))
                    {
                        span[offset] = (byte)SpliceCountdown;
                        offset++;
                    }
                    if (Flags.HasFlag(AdaptationFieldFlags.TransportPrivateData))
                    {
                        span[offset] = (byte)TransportPrivateData.Length;
                        offset++;
                        TransportPrivateData.CopyTo(span.Slice(offset));
                        offset += TransportPrivateData.Length;
                    }
                    if (Flags.HasFlag(AdaptationFieldFlags.AdaptationFieldExtension))
                    {
                        AdaptationFieldExtension.Write(span.Slice(offset));
                        offset += AdaptationFieldExtension.Length;
                    }

                    //fill remaining with stuffing
                    if (offset < Length - 1)
                    {
                        span.Slice(offset).Fill(0xff);
                    }
                }
                return true;
            }
        }

        public struct TSPacket
        {
            public bool TransportError { get; set; }

            public bool PayloadUnitStart { get; set; }

            public bool TransportPriority { get; set; }

            public ushort Pid { get; set; }

            public TransportScramblingControl Scrambling { get; set; }

            public AdaptationFieldControl AdaptationFieldControl { get; set; }

            public byte Continuity { get; set; }

            public AdaptationField AdaptationField { get; set; }

            public byte[] Payload { get; set; }

            public override string ToString()
            {
                return $@"=====================
PID                 : {Pid}
TransportError      : {TransportError}
PayloadUnitStart    : {PayloadUnitStart}
TransportPriority   : {TransportPriority}
Scrambling          : {Scrambling}
AdaptionControl     : {AdaptationFieldControl}
Continuity          : {Continuity}{(HasAdaptionField ? $@"
AdaptationField     : {AdaptationField}" : string.Empty)}
Payload             : {Payload?.Length ?? 0} bytes{(IsPESPayload ? " [PES]" : string.Empty)}";
            }

            public bool HasAdaptionField =>
                AdaptationFieldControl == AdaptationFieldControl.AdaptationFieldAndPayload
                || AdaptationFieldControl == AdaptationFieldControl.AdaptationFieldOnly;

            public bool HasPayload =>
                AdaptationFieldControl == AdaptationFieldControl.AdaptationFieldAndPayload
                || AdaptationFieldControl == AdaptationFieldControl.PayloadOnly;

            public bool IsPESPayload => Payload != null && Payload[0] == 0x00 && Payload[1] == 0x00 && Payload[2] == 0x01;

            public void Validate()
            {
                if (!((int)AdaptationFieldControl >= 1 && (int)AdaptationFieldControl <= 3))
                {
                    throw new Exception("Invalid AFC");
                }
                if (Pid > 8191)
                {
                    throw new Exception("Invalid PID");
                }
                if (!((int)Scrambling >= 0 && (int)Scrambling <= 3))
                {
                    throw new Exception("Invalid TSC");
                }
                if (HasAdaptionField)
                {
                    AdaptationField.Validate();
                }
                if (HasPayload && Payload == null)
                {
                    throw new Exception("Payload was null");
                }
            }

            /// <summary>
            /// Read the header portion of a TSPacket
            /// </summary>
            /// <param name="header"></param>
            /// <returns></returns>
            public static TSPacket Parse(ReadOnlySequence<byte> header)
            {
                var sr = new SequenceReader<byte>(header);
                if (sr.TryReadBigEndian(out int hv))
                {
                    var pkt = new TSPacket()
                    {
                        TransportError = (hv & 0x800000) > 1,
                        PayloadUnitStart = (hv & 0x400000) > 1,
                        TransportPriority = (hv & 0x200000) > 1,
                        Pid = (ushort)((hv & 0x001fff) >> 8),
                        Scrambling = (TransportScramblingControl)((hv & 0xc0) >> 6),
                        AdaptationFieldControl = (AdaptationFieldControl)((hv & 0x30) >> 4),
                        Continuity = (byte)(hv & 0x0f)
                    };

                    if (pkt.HasAdaptionField)
                    {
                        var af = AdaptationField.Parse(header.Slice(sr.Position));
                        if (!af.Equals(default))
                        {
                            pkt.AdaptationField = af;
                            sr.Advance(af.Length + 1);
                        }
                    }

                    if (pkt.HasPayload)
                    {
                        pkt.ReadPayload(header.Slice(sr.Position));
                        sr.Advance(pkt.Payload.Length);
                    }
                    //Transport error packets are not valid
                    if(!pkt.TransportError) 
                    {
                        pkt.Validate();
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
            public bool ReadPayload(ReadOnlySequence<byte> pl)
            {
                var sr = new SequenceReader<byte>(pl);

                Payload = new byte[sr.Length];
                return sr.TryCopyTo(Payload);
            }

            public bool Write(Span<byte> mem)
            {
                if (mem.Length < PacketLength)
                {
                    throw new Exception($"Not enough memory to write packet, must have at least {PacketLength} bytes, got {mem.Length} bytes");
                }
                var span = mem.Slice(0, PacketLength);

                uint hv = (SyncMarker << 24)
                    & (TransportError ? 1 << 23 : UInt32.MaxValue)
                    & (PayloadUnitStart ? 1 << 22 : UInt32.MaxValue)
                    & (uint)Pid << 8
                    & (uint)Scrambling << 6
                    & (uint)AdaptationFieldControl << 4
                    & Continuity;

                BitConverter.TryWriteBytes(span, hv);

                var offset = 4;
                if (HasAdaptionField)
                {
                    AdaptationField.Write(span.Slice(offset));
                    offset += AdaptationField.Length;
                }

                if (HasPayload)
                {
                    Payload.CopyTo(span.Slice(offset));
                    offset += Payload.Length;
                }

                if (offset < PacketLength)
                {
                    span.Slice(offset).Fill(0xff);
                }
                return true;
            }
        }

        private static IEnumerable<(SequencePosition pos, TSPacket packet)> ReadPackets(ReadOnlySequence<byte> buf, CancellationToken cancellationToken = default)
        {
            SequencePosition? sp = null;
            do
            {
                sp = buf.PositionOf(SyncMarker);
                if (sp != null && buf.Length >= PacketLength)
                {
                    //try to parse a packet starting at sp
                    var packetData = buf.Slice(sp.Value, PacketLength);

                    yield return (sp.Value, TSPacket.Parse(packetData));

                    var nextMsgPos = buf.GetPosition(PacketLength, sp.Value);
                    buf = buf.Slice(nextMsgPos);
                }
                else
                {
                    break;
                }
            } while (sp != null && !cancellationToken.IsCancellationRequested);
        }

        public static async IAsyncEnumerable<TSPacket> TryReadPackets(PipeReader pr, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var data = await pr.ReadAsync(cancellationToken);
                if (data.IsCompleted)
                {
                    break;
                }
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
                    pr.AdvanceTo(buf.GetPosition(PacketLength, sp.Value));
                }
                else
                {
                    pr.AdvanceTo(buf.Start);
                }
            }
        }
    }
}