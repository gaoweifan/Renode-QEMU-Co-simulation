/**
 * @file SharedMemory.cs
 * @author Weifan Gao
 * @brief Share memory with qemu using file in /dev/shm
 * @date 2024-07-10
 */
using System;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.CSharp.RuntimeBinder;
using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.CPU;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Utilities;
using Antmicro.Renode.Exceptions;
using Endianess = ELFSharp.ELF.Endianess;

namespace Antmicro.Renode.Peripherals.Memory{
    public class SharedMemory : 
        IBytePeripheral, IWordPeripheral, IDoubleWordPeripheral, IQuadWordPeripheral, IMultibyteWritePeripheral,
        IMapped, IMemory, IKnownSize, ICanLoadFiles, IEndiannessAware, IDisposable {
        public SharedMemory(IMachine machine, long offset, long size, string filePath="/dev/shm/qemu-ram", int? segmentSize = null){
            this.machine = machine;
            Size = size;

            memoryMappedFileFd = LibCWrapper.Open(filePath,O_RDWR);
            if(memoryMappedFileFd < 0){
                throw new ConstructionException($"Failed to open memory mapped file: {filePath}, return code: {memoryMappedFileFd}");
            }

            memoryMappedPtr = Mmap(IntPtr.Zero, size, PROT_READ|PROT_WRITE, MAP_SHARED, memoryMappedFileFd, offset);
            if(memoryMappedPtr.ToInt64() < 0){
                throw new ConstructionException($"Memory map failed, file: {filePath}");
            }

            if(segmentSize == null){
                var proposedSegmentSize = Math.Min(MaximalSegmentSize, Math.Max(MinimalSegmentSize, size / RecommendedNumberOfSegments));
                // align it
                segmentSize = (int)(Math.Ceiling(1.0 * proposedSegmentSize / MinimalSegmentSize) * MinimalSegmentSize);
                this.DebugLog("Segment size automatically calculated to value {0}B", Misc.NormalizeBinary(segmentSize.Value));
            }
            SegmentSize = segmentSize.Value;
            
            PrepareSegments();
        }

        public void ReadBytes(long offset, int count, byte[] destination, int startIndex){
            Marshal.Copy(new IntPtr(memoryMappedPtr.ToInt64() + offset), destination, startIndex, count);
        }

        public void WriteBytes(long offset, byte[] array, int startingIndex, int count, ICPU context = null){
            Marshal.Copy(array, startingIndex, new IntPtr(memoryMappedPtr.ToInt64() + offset), count);
            InvalidateMemoryFragment(offset, count);
        }

        public byte[] ReadBytes(long offset, int count, ICPU context = null){
            var result = new byte[count];
            ReadBytes(offset, count, result, 0);
            return result;
        }

        public void WriteBytes(long offset, byte[] value){
            WriteBytes(offset, value, 0, value.Length);
        }

        public virtual ulong ReadQuadWord(long offset){
            return unchecked((ulong)Marshal.ReadInt64(new IntPtr(memoryMappedPtr.ToInt64() + offset)));
        }

        public virtual void WriteQuadWord(long offset, ulong value){
            Marshal.WriteInt64(new IntPtr(memoryMappedPtr.ToInt64() + offset), unchecked((long)value));
            InvalidateMemoryFragment(offset, sizeof(ulong));
        }

        public uint ReadDoubleWord(long offset){
            return unchecked((uint)Marshal.ReadInt32(new IntPtr(memoryMappedPtr.ToInt64() + offset)));
        }

        public virtual void WriteDoubleWord(long offset, uint value){
            Marshal.WriteInt32(new IntPtr(memoryMappedPtr.ToInt64() + offset), unchecked((int)value));
            InvalidateMemoryFragment(offset, sizeof(uint));
        }

        public ushort ReadWord(long offset){
            return unchecked((ushort)Marshal.ReadInt16(new IntPtr(memoryMappedPtr.ToInt64() + offset)));
        }

        public virtual void WriteWord(long offset, ushort value){
            Marshal.WriteInt16(new IntPtr(memoryMappedPtr.ToInt64() + offset), unchecked((short)value));
            InvalidateMemoryFragment(offset, sizeof(ushort));
        }

        public byte ReadByte(long offset){
            return Marshal.ReadByte(new IntPtr(memoryMappedPtr.ToInt64() + offset));
        }

        public virtual void WriteByte(long offset, byte value){
            Marshal.WriteByte(new IntPtr(memoryMappedPtr.ToInt64() + offset), value);
            InvalidateMemoryFragment(offset, 1);
        }

        public void LoadFileChunks(string path, IEnumerable<FileChunk> chunks, ICPU cpu){
            this.LoadFileChunks(chunks, cpu);
        }

        public void Reset(){
            // nothing happens
        }

        public void Dispose(){
            if(!disposed){
                Munmap(memoryMappedPtr, Size);
                LibCWrapper.Close(memoryMappedFileFd);
                disposed = true;
            }
        }

        public IntPtr GetSegment(int segmentNo){
            return segments[segmentNo];
        }

        public void TouchSegment(int segmentNo){
            IntPtr segmentPtr = memoryMappedPtr + segmentNo*SegmentSize;
            this.DebugLog(string.Format("Segment No.{1} allocated at 0x{0:X}.",
                    segmentPtr.ToInt64(), segmentNo));
            segments[segmentNo] = segmentPtr;
        }

        private void PrepareSegments(){
            if(segments != null){
                // this is because in case of loading the starting memory snapshot
                // after deserialization (i.e. resetting after deserialization)
                // memory segments would have been lost
                return;
            }
            
            // how many segments we need?
            var segmentsNo = Size / SegmentSize + (Size % SegmentSize != 0 ? 1 : 0);
            this.DebugLog(string.Format("Preparing {0} segments for {1}B of memory, each {2}B long.",
                segmentsNo, Misc.NormalizeBinary(Size), Misc.NormalizeBinary(SegmentSize)));
            
            // init segments
            segments = new IntPtr[segmentsNo];
            
            // init all describedSegments except last one
            describedSegments = new IMappedSegment[segmentsNo];
            for(var i = 0; i < describedSegments.Length - 1; i++){
                describedSegments[i] = new MappedSegment(this, i, (uint)SegmentSize);
            }
            
            // calc last segment size, might smaller than others
            var last = describedSegments.Length - 1;
            var sizeOfLast = (uint)(Size % SegmentSize);
            if(sizeOfLast == 0){
                sizeOfLast = (uint)SegmentSize;
            }

            // init last describedSegment
            describedSegments[last] = new MappedSegment(this, last, sizeOfLast);
        }

        [DllImport("libc", EntryPoint = "mmap")]
        private static extern IntPtr Mmap(IntPtr addr, long length, int prot, int flags, int fd, long offset);

        [DllImport("libc", EntryPoint = "mmap")]
        private static extern long Munmap(IntPtr addr, long length);

        private List<long> GetRegistrationPoints(){
            if(registrationPointsCached == null){
                registrationPointsCached = machine.SystemBus.GetRegistrationPoints(this).Select(x => (long)(x.Range.StartAddress + x.Offset)).ToList();
            }
            return registrationPointsCached;
        }

        private void InvalidateMemoryFragment(long start, int length){
            if(machine == null){
                // this peripheral is not connected to any machine, so there is nothing we can do
                return;
            }

            this.DebugLog("Invalidating memory fragment at 0x{0:X} of size {1} bytes.", start, length);

            var registrationPoints = GetRegistrationPoints();
            foreach(var cpu in machine.SystemBus.GetCPUs().OfType<CPU.ICPU>()){
                foreach(var regPoint in registrationPoints){
                    try{
                        //it's dynamic to avoid cyclic dependency to TranslationCPU
                        ((dynamic)cpu).InvalidateTranslationBlocks(new IntPtr(regPoint + start), new IntPtr(regPoint + start + length));
                    }catch(RuntimeBinderException){
                        // CPU does not implement `InvalidateTranslationBlocks`, there is not much we can do
                    }
                }
            }
        }

        public IEnumerable<IMappedSegment> MappedSegments => describedSegments;
        public int SegmentSize { get; private set; }
        private IMappedSegment[] describedSegments;
        private IntPtr[] segments;
        private const int MinimalSegmentSize = 64 * 1024;
        private const int MaximalSegmentSize = 16 * 1024 * 1024;
        private const int RecommendedNumberOfSegments = 16;

        private const int O_RDWR = 2;
        private const int PROT_READ = 1;
        private const int PROT_WRITE = 2;
        private const int MAP_SHARED = 1;

        protected readonly IntPtr memoryMappedPtr;
        protected readonly int memoryMappedFileFd;

        private readonly IMachine machine;
        private bool disposed;
        private List<long> registrationPointsCached;

        public long Size { get; private set; }
        
        // SharedMemory matches the host endianness because host-endian MemoryMappedViews are used for accesses wider than a byte.
        public Endianess Endianness => BitConverter.IsLittleEndian ? Endianess.LittleEndian : Endianess.BigEndian;

        private class MappedSegment : IMappedSegment{
            public IntPtr Pointer => parent.GetSegment(index);

            public ulong Size => size;

            public ulong StartingOffset{
                get => checked((ulong)index * (ulong)parent.SegmentSize);
            }

            public MappedSegment(SharedMemory parent, int index, uint size){
                this.index = index;
                this.parent = parent;
                this.size = size;
            }

            public void Touch(){
                parent.TouchSegment(index);
            }

            public override string ToString(){
                return string.Format("[MappedSegment: Size=0x{0:X}, StartingOffset=0x{1:X}]", Size, StartingOffset);
            }

            private readonly SharedMemory parent;
            private readonly int index;
            private readonly uint size;
        }
    }
}
