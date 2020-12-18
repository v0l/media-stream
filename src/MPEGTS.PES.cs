using System;

namespace MediaStreams
{
    public partial class MPEGTS
    {
        public enum DSMTrickModeControl
        {
            FastForward,
            SlowMotion,
            FreezeFrame,
            FastReverse,
            SlowReverse
        }

        [Flags]
        public enum PESFlags
        {
            //MarkerBits = 0,1,2
            ScramblingControl = 1 << 2,
            ScramblingControl2 = 1 << 3,
            Priority = 1 << 4,
            DataAlignment = 1 << 5,
            Copyright = 1 << 6,
            Original = 1 << 7,
            //Forbidden 1 << 8
            PTSDTS = (1 << 9 | 1 << 8),
            PTS = 1 << 9,
            ESCR = 1 << 10,
            ESRate = 1 << 11,
            DSMTrickMode = 1 << 12,
            AdditionalCopyInfo = 1 << 13,
            CRC = 1 << 14,
            Extension = 1 << 15
        }

        public struct DSMTrickMode
        {
            public DSMTrickModeControl TrickModeControl { get; set; }

            public byte FieldId { get; set; }
            public bool IntraSliceRefresh { get; set; }
            public byte FrequencyTruncation { get; set; }
            public byte RepControl { get; set; }
        }

        public enum PESHeaderExtensionFlags
        {
            PrivateData = 1 << 0,
            PackHeader = 1 << 1,
            ProgramPacketSequenceCounter = 1 << 2,
            PSTDBuffer = 1 << 3,
            Extension = 1 << 7
        }

        public struct PackHeader
        {

        }

        public struct PESHeaderExtension
        {
            public PESHeaderExtensionFlags Flags { get; set; }

            public Memory<byte> PrivateData { get; set; } // 32bytes

            public PackHeader PackHeader { get; set; }
            
        }

        public struct PESHeader 
        {
            public PESFlags Flags { get; set; }
            public byte DataLen { get; set; }

            public long PTS { get; set; }
            public long DTS { get; set; }
            public long ESCR { get; set; }
            public int ESRate { get; set; }

            public DSMTrickMode DSMTrickMode { get; set; }

            public byte AdditionalCopyInfo {get;set;}
            public ushort PreviousCRC { get; set; }
        }

        public struct PES 
        {
            public byte StreamId { get; set; }

            public ushort PacketLen { get; set; }

            public Memory<byte> Data { get; set; }
        }
    }
}