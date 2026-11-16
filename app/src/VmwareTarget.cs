using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace Mtaalam.V2V
{
    // ------------------------------------------------------------------------
    // Reads one disk of a live copy from the Mtaalam V2V Agent: AES-256-CTR
    // decryption, then (offset, data) records in ascending order.
    static class AgentDiskReader
    {
        public static void Read(CloneStream cs, Action<long, byte[], int> onData, Action<long, long> progress, Func<bool> cancelled)
        {
            var req = (HttpWebRequest)WebRequest.Create(cs.Url);
            req.Proxy = null;
            req.Timeout = 60000;
            req.ReadWriteTimeout = 600000;
            req.KeepAlive = false;
            HttpWebResponse resp;
            try { resp = (HttpWebResponse)req.GetResponse(); }
            catch (WebException ex)
            {
                var r = ex.Response as HttpWebResponse;
                if (r != null && (int)r.StatusCode == 403) throw new Exception("The agent refused the disk copy (session expired or wrong pairing key).");
                throw new Exception("Cannot read the disk from the agent: " + ex.Message);
            }
            using (resp)
            using (var s = new CtrReadStream(resp.GetResponseStream(), cs.Key, cs.Iv))
            {
                var head = new byte[24];
                ReadExact(s, head, 24);
                if (Encoding.ASCII.GetString(head, 0, 8) != "MTV2VDSK") throw new Exception("Not a GeuzaVirt disk stream (wrong pairing key?)");
                long size = BitConverter.ToInt64(head, 16);
                var rec = new byte[16];
                var buf = new byte[8 << 20];
                var clock = Stopwatch.StartNew();
                long last = -1;
                while (true)
                {
                    if (cancelled()) { req.Abort(); throw new OperationCanceledException(); }
                    ReadExact(s, rec, 16);
                    long off = BitConverter.ToInt64(rec, 0);
                    int len = BitConverter.ToInt32(rec, 8);
                    if (len == 0) break;
                    if (len < 0 || len > buf.Length) throw new Exception("The disk stream from the agent is damaged");
                    ReadExact(s, buf, len);
                    onData(off, buf, len);
                    if (clock.ElapsedMilliseconds - last >= 500) { last = clock.ElapsedMilliseconds; progress(off, size); }
                }
                progress(size, size);
            }
        }

        static void ReadExact(Stream s, byte[] buf, int count)
        {
            int got = 0;
            while (got < count)
            {
                int n = s.Read(buf, got, count - got);
                if (n <= 0) throw new Exception("The disk stream from the agent ended early (connection lost or agent stopped)");
                got += n;
            }
        }
    }

    // Decrypting read stream, AES-256-CTR with a 128-bit big-endian counter.
    class CtrReadStream : Stream
    {
        readonly Stream inner;
        readonly ICryptoTransform aes;
        readonly byte[] counter = new byte[16];
        readonly byte[] blocks = new byte[65536];
        readonly byte[] key = new byte[65536];
        int pos = 65536;

        public CtrReadStream(Stream inner, byte[] secret, byte[] iv)
        {
            this.inner = inner;
            Array.Copy(iv, counter, 16);
            var a = new AesCryptoServiceProvider { Mode = CipherMode.ECB, Padding = PaddingMode.None, KeySize = 256 };
            aes = a.CreateEncryptor(secret, new byte[16]);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = inner.Read(buffer, offset, count);
            for (int i = 0; i < n; i++)
            {
                if (pos == key.Length)
                {
                    for (int b = 0; b < blocks.Length; b += 16)
                    {
                        Array.Copy(counter, 0, blocks, b, 16);
                        for (int j = 15; j >= 0; j--) if (++counter[j] != 0) break;
                    }
                    aes.TransformBlock(blocks, 0, blocks.Length, key, 0);
                    pos = 0;
                }
                buffer[offset + i] ^= key[pos++];
            }
            return n;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { inner.Dispose(); aes.Dispose(); }
            base.Dispose(disposing);
        }

        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
    }

    // ------------------------------------------------------------------------
    // Writes a stream-optimized VMDK (the format of OVF packages and of ESXi's
    // NFC import): compressed 64 KB grains, empty grains left out, grain tables
    // and directory at the end. Data must arrive in ascending offsets.
    // A disk made larger or smaller gets its GPT moved to the new end.
    class StreamVmdkWriter : IDisposable
    {
        const int Grain = 65536;
        const long Overhead = 128;   // sectors before the first grain (header + descriptor)

        readonly FileStream fs;
        readonly long capacity, sourceSize;
        readonly uint[] gtes;
        readonly byte[] grain = new byte[Grain];
        readonly int descSectors;
        long cur = -1;
        bool dirty, finished;
        byte[] backupEntries, backupHeader;
        long backupEntriesAt, backupHeaderAt;

        public long Populated;     // bytes of data in the disk
        public long Capacity { get { return capacity; } }

        public StreamVmdkWriter(string path, long capacityBytes, long sourceBytes, string adapter)
        {
            capacity = (capacityBytes + 1048575) / 1048576 * 1048576;
            sourceSize = sourceBytes;
            long sectors = capacity / 512;
            gtes = new uint[(sectors + 127) / 128];
            var rnd = new byte[4];
            using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(rnd);
            string desc =
                "# Disk DescriptorFile\nversion=1\nCID=" + BitConverter.ToUInt32(rnd, 0).ToString("x8") + "\nparentCID=ffffffff\n" +
                "createType=\"streamOptimized\"\n\n# Extent description\nRDONLY " + sectors + " SPARSE \"" + Path.GetFileName(path) + "\"\n\n" +
                "# The Disk Data Base\n#DDB\n\nddb.adapterType = \"" + adapter + "\"\n" +
                "ddb.geometry.cylinders = \"" + Math.Min(65535, sectors / (255 * 63)) + "\"\nddb.geometry.heads = \"255\"\n" +
                "ddb.geometry.sectors = \"63\"\nddb.virtualHWVersion = \"4\"\nddb.toolsInstallType = \"0\"\n";
            byte[] db = Encoding.ASCII.GetBytes(desc);
            descSectors = (db.Length + 511) / 512;
            fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
            fs.Write(Header(ulong.MaxValue), 0, 512);   // GD_AT_END: the footer has the real offset
            var area = new byte[(Overhead - 1) * 512];
            Array.Copy(db, area, db.Length);
            fs.Write(area, 0, area.Length);
        }

        byte[] Header(ulong gdOffset)
        {
            var h = new byte[512];
            Put32(h, 0, 0x564D444B);
            Put32(h, 4, 3);
            Put32(h, 8, 0x30001);                 // valid newline test, compressed grains, markers
            Put64(h, 12, (ulong)(capacity / 512));
            Put64(h, 20, 128);                    // grain size in sectors
            Put64(h, 28, 1);                      // descriptor offset
            Put64(h, 36, (ulong)descSectors);
            Put32(h, 44, 512);                    // grain table entries per table
            Put64(h, 48, 0);
            Put64(h, 56, gdOffset);
            Put64(h, 64, (ulong)Overhead);
            h[73] = (byte)'\n'; h[74] = (byte)' '; h[75] = (byte)'\r'; h[76] = (byte)'\n';
            h[77] = 1;                            // deflate
            return h;
        }

        public void Write(long offset, byte[] buf, int len)
        {
            int start = 0;
            while (len > 0 && offset < capacity)
            {
                long g = offset / Grain;
                int within = (int)(offset % Grain);
                if (g != cur)
                {
                    if (g < cur) throw new Exception("disk data out of order");
                    FlushGrain();
                    cur = g;
                    Array.Clear(grain, 0, Grain);
                }
                int n = (int)Math.Min(Math.Min(len, Grain - within), capacity - offset);
                Buffer.BlockCopy(buf, start, grain, within, n);
                dirty = true;
                offset += n; start += n; len -= n;
            }
        }

        void FlushGrain()
        {
            if (cur < 0 || !dirty) return;
            dirty = false;
            if (cur == 0 && capacity != sourceSize) MoveGpt();
            if (IsZero(grain)) return;
            byte[] z = Zlib(grain);
            long sector = fs.Position / 512;
            var m = new byte[12];
            Put64(m, 0, (ulong)(cur * 128));
            Put32(m, 8, (uint)z.Length);
            fs.Write(m, 0, 12);
            fs.Write(z, 0, z.Length);
            Pad();
            gtes[cur] = (uint)sector;
            Populated += Grain;
        }

        // Disk resized: the primary GPT points to the new end, and a backup GPT is
        // written there (and the protective MBR covers the new size).
        void MoveGpt()
        {
            if (Encoding.ASCII.GetString(grain, 512, 8) != "EFI PART") return;
            int hsize = (int)Get32(grain, 512 + 12);
            long entriesLba = (long)Get64(grain, 512 + 72);
            long count = Get32(grain, 512 + 80), esize = Get32(grain, 512 + 84);
            long entriesBytes = count * esize;
            if (hsize < 92 || hsize > 512 || entriesLba * 512 + entriesBytes > Grain) return;
            long entriesSectors = (entriesBytes + 511) / 512;
            long last = capacity / 512 - 1;

            var header = new byte[512];
            Array.Copy(grain, 512, header, 0, 512);
            Put64(grain, 512 + 32, (ulong)last);                          // AlternateLBA
            Put64(grain, 512 + 48, (ulong)(last - entriesSectors - 1));   // LastUsableLBA
            Put32(grain, 512 + 16, 0);
            Put32(grain, 512 + 16, Crc32(grain, 512, hsize));

            Array.Copy(grain, 512, header, 0, 512);
            Put64(header, 24, (ulong)last);                     // MyLBA
            Put64(header, 32, 1);                               // AlternateLBA
            Put64(header, 72, (ulong)(last - entriesSectors));  // PartitionEntryLBA
            Put32(header, 16, 0);
            Put32(header, 16, Crc32(header, 0, hsize));
            backupHeader = header;
            backupHeaderAt = last * 512;
            backupEntries = new byte[entriesSectors * 512];
            Array.Copy(grain, (int)(entriesLba * 512), backupEntries, 0, (int)entriesBytes);
            backupEntriesAt = (last - entriesSectors) * 512;

            for (int i = 0; i < 4; i++)   // protective MBR entry
            {
                int e = 446 + i * 16;
                if (grain[e + 4] == 0xEE) Put32(grain, e + 12, (uint)Math.Min(last, 0xFFFFFFFFL));
            }
        }

        public void Finish()
        {
            if (finished) return;
            finished = true;
            if (backupHeader != null)
            {
                try
                {
                    Write(backupEntriesAt, backupEntries, backupEntries.Length);
                    Write(backupHeaderAt, backupHeader, 512);
                }
                catch (Exception) { }   // Windows repairs a missing backup GPT
            }
            FlushGrain();

            long tables = (gtes.Length + 511) / 512;
            var gd = new uint[tables];
            var gt = new byte[2048];
            for (long t = 0; t < tables; t++)
            {
                Marker(4, 1);
                gd[t] = (uint)(fs.Position / 512);
                Array.Clear(gt, 0, gt.Length);
                for (int e = 0; e < 512; e++)
                {
                    long i = t * 512 + e;
                    if (i < gtes.Length) Put32(gt, e * 4, gtes[i]);
                }
                fs.Write(gt, 0, gt.Length);
            }
            long gdSectors = (tables * 4 + 511) / 512;
            Marker(gdSectors, 2);
            ulong gdOffset = (ulong)(fs.Position / 512);
            var gdBytes = new byte[gdSectors * 512];
            for (long t = 0; t < tables; t++) Put32(gdBytes, (int)(t * 4), gd[t]);
            fs.Write(gdBytes, 0, gdBytes.Length);
            Marker(1, 3);
            fs.Write(Header(gdOffset), 0, 512);
            fs.Write(new byte[512], 0, 512);   // end-of-stream marker
            fs.Flush();
        }

        void Marker(long sectors, uint type)
        {
            var m = new byte[512];
            Put64(m, 0, (ulong)sectors);
            Put32(m, 12, type);
            fs.Write(m, 0, 512);
        }

        void Pad()
        {
            int r = (int)(fs.Position % 512);
            if (r != 0) fs.Write(new byte[512 - r], 0, 512 - r);
        }

        public void Dispose() { fs.Dispose(); }

        static byte[] Zlib(byte[] data)
        {
            using (var ms = new MemoryStream(Grain / 2))
            {
                ms.WriteByte(0x78);
                ms.WriteByte(0x01);
                using (var ds = new DeflateStream(ms, CompressionLevel.Fastest, true)) ds.Write(data, 0, data.Length);
                uint a = 1, b = 0;
                for (int i = 0; i < data.Length; )
                {
                    int end = Math.Min(data.Length, i + 5552);
                    for (; i < end; i++) { a += data[i]; b += a; }
                    a %= 65521; b %= 65521;
                }
                uint adler = (b << 16) | a;
                ms.WriteByte((byte)(adler >> 24)); ms.WriteByte((byte)(adler >> 16)); ms.WriteByte((byte)(adler >> 8)); ms.WriteByte((byte)adler);
                return ms.ToArray();
            }
        }

        static bool IsZero(byte[] b)
        {
            for (int i = 0; i < b.Length; i += 8) if (BitConverter.ToInt64(b, i) != 0) return false;
            return true;
        }

        static uint[] crcTable;

        static uint Crc32(byte[] b, int off, int len)
        {
            if (crcTable == null)
            {
                var t = new uint[256];
                for (uint i = 0; i < 256; i++)
                {
                    uint c = i;
                    for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                    t[i] = c;
                }
                crcTable = t;
            }
            uint crc = 0xFFFFFFFF;
            for (int i = off; i < off + len; i++) crc = crcTable[(crc ^ b[i]) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFF;
        }

        static void Put32(byte[] b, int o, uint v) { BitConverter.GetBytes(v).CopyTo(b, o); }
        static void Put64(byte[] b, int o, ulong v) { BitConverter.GetBytes(v).CopyTo(b, o); }
        static uint Get32(byte[] b, int o) { return BitConverter.ToUInt32(b, o); }
        static ulong Get64(byte[] b, int o) { return BitConverter.ToUInt64(b, o); }
    }

    // ------------------------------------------------------------------------
    // OVF 1.0 descriptor for a machine copied by the agent (VMware hardware:
    // LSI Logic SAS, E1000e / E1000, BIOS or EFI as the source booted).
    static class OvfBuilder
    {
        public class Disk { public string File; public long FileSize, Capacity, Populated; }

        public static string Build(VmxInfo vm, List<Disk> disks, string network, int hwVersion, string nicType)
        {
            string name = Esc(vm.ProxmoxName);
            bool oldWindows = (vm.AgentOs ?? "").Contains("2008") || (vm.AgentOs ?? "").Contains("Windows 7");
            string nic = !string.IsNullOrEmpty(nicType) ? nicType : oldWindows ? "E1000" : "E1000e";
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
            sb.Append("<Envelope vmw:buildId=\"mtaalam-v2v\" xmlns=\"http://schemas.dmtf.org/ovf/envelope/1\" " +
                      "xmlns:cim=\"http://schemas.dmtf.org/wbem/wscim/1/common\" xmlns:ovf=\"http://schemas.dmtf.org/ovf/envelope/1\" " +
                      "xmlns:rasd=\"http://schemas.dmtf.org/wbem/wscim/1/cim-schema/2/CIM_ResourceAllocationSettingData\" " +
                      "xmlns:vmw=\"http://www.vmware.com/schema/ovf\" " +
                      "xmlns:vssd=\"http://schemas.dmtf.org/wbem/wscim/1/cim-schema/2/CIM_VirtualSystemSettingData\" " +
                      "xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">\n");
            sb.Append("  <References>\n");
            for (int i = 0; i < disks.Count; i++)
                sb.AppendFormat("    <File ovf:href=\"{0}\" ovf:id=\"file{1}\" ovf:size=\"{2}\"/>\n", Esc(disks[i].File), i + 1, disks[i].FileSize);
            sb.Append("  </References>\n  <DiskSection>\n    <Info>Virtual disk information</Info>\n");
            for (int i = 0; i < disks.Count; i++)
                sb.AppendFormat("    <Disk ovf:capacity=\"{0}\" ovf:capacityAllocationUnits=\"byte\" ovf:diskId=\"vmdisk{1}\" ovf:fileRef=\"file{1}\" " +
                    "ovf:format=\"http://www.vmware.com/interfaces/specifications/vmdk.html#streamOptimized\" ovf:populatedSize=\"{2}\"/>\n",
                    disks[i].Capacity, i + 1, disks[i].Populated);
            sb.Append("  </DiskSection>\n  <NetworkSection>\n    <Info>The list of logical networks</Info>\n");
            sb.AppendFormat("    <Network ovf:name=\"{0}\">\n      <Description>The {0} network</Description>\n    </Network>\n", Esc(network));
            sb.Append("  </NetworkSection>\n");
            sb.AppendFormat("  <VirtualSystem ovf:id=\"{0}\">\n    <Info>A virtual machine</Info>\n    <Name>{0}</Name>\n", name);
            sb.AppendFormat("    <OperatingSystemSection ovf:id=\"1\" vmw:osType=\"{0}\">\n      <Info>The kind of installed guest operating system</Info>\n" +
                            "      <Description>{1}</Description>\n    </OperatingSystemSection>\n", Esc(vm.VmwareGuestId), Esc(vm.GuestOS ?? ""));
            sb.Append("    <VirtualHardwareSection>\n      <Info>Virtual hardware requirements</Info>\n      <System>\n" +
                      "        <vssd:ElementName>Virtual Hardware Family</vssd:ElementName>\n        <vssd:InstanceID>0</vssd:InstanceID>\n");
            sb.AppendFormat("        <vssd:VirtualSystemIdentifier>{0}</vssd:VirtualSystemIdentifier>\n", name);
            sb.Append("        <vssd:VirtualSystemType>vmx-" + (hwVersion >= 7 ? hwVersion : 10) + "</vssd:VirtualSystemType>\n      </System>\n");
            Item(sb, new[] { "AllocationUnits", "hertz * 10^6", "Description", "Number of Virtual CPUs", "ElementName", vm.ProxmoxCores + " virtual CPU(s)",
                             "InstanceID", "1", "ResourceType", "3", "VirtualQuantity", vm.ProxmoxCores.ToString() });
            Item(sb, new[] { "AllocationUnits", "byte * 2^20", "Description", "Memory Size", "ElementName", vm.ProxmoxMemoryMb + "MB of memory",
                             "InstanceID", "2", "ResourceType", "4", "VirtualQuantity", vm.ProxmoxMemoryMb.ToString() });
            Item(sb, new[] { "Address", "0", "Description", "SCSI Controller", "ElementName", "SCSI controller 0", "InstanceID", "3",
                             "ResourceSubType", vm.IsWindows ? "lsilogicsas" : "lsilogic", "ResourceType", "6" });
            for (int i = 0; i < disks.Count; i++)
            {
                int unit = i >= 7 ? i + 1 : i;
                Item(sb, new[] { "AddressOnParent", unit.ToString(), "ElementName", "Hard disk " + (i + 1), "HostResource", "ovf:/disk/vmdisk" + (i + 1),
                                 "InstanceID", (10 + i).ToString(), "Parent", "3", "ResourceType", "17" });
            }
            int nics = Math.Max(1, vm.Macs.Count);
            for (int i = 0; i < nics; i++)
                Item(sb, new[] { "AddressOnParent", (7 + i).ToString(), "AutomaticAllocation", "true", "Connection", network,
                                 "Description", nic + " ethernet adapter on \"" + network + "\"", "ElementName", "Network adapter " + (i + 1),
                                 "InstanceID", (30 + i).ToString(), "ResourceSubType", nic, "ResourceType", "10" });
            if (vm.Uefi) sb.Append("      <vmw:Config ovf:required=\"false\" vmw:key=\"firmware\" vmw:value=\"efi\"/>\n");
            sb.Append("    </VirtualHardwareSection>\n  </VirtualSystem>\n</Envelope>\n");
            return sb.ToString();
        }

        static void Item(StringBuilder sb, string[] kv)
        {
            sb.Append("      <Item>\n");
            for (int i = 0; i < kv.Length; i += 2)
                sb.Append("        <rasd:").Append(kv[i]).Append('>').Append(Esc(kv[i + 1])).Append("</rasd:").Append(kv[i]).Append(">\n");
            sb.Append("      </Item>\n");
        }

        static string Esc(string s) { return SecurityElement.Escape(s ?? ""); }

        // CPU / memory changes (Target settings) applied to an OVF that ESXi wrote.
        public static string Patch(string ovf, int cores, int memoryMb)
        {
            if (cores <= 0 && memoryMb <= 0) return ovf;
            var doc = new XmlDocument();
            doc.LoadXml(ovf);
            foreach (XmlElement item in doc.GetElementsByTagName("Item", "http://schemas.dmtf.org/ovf/envelope/1"))
            {
                string type = "";
                XmlElement qty = null;
                foreach (XmlNode c in item.ChildNodes)
                {
                    if (c.LocalName == "ResourceType") type = c.InnerText.Trim();
                    if (c.LocalName == "VirtualQuantity") qty = (XmlElement)c;
                }
                if (qty == null) continue;
                if (type == "3" && cores > 0) qty.InnerText = cores.ToString(CultureInfo.InvariantCulture);
                if (type == "4" && memoryMb > 0)
                {
                    qty.InnerText = memoryMb.ToString(CultureInfo.InvariantCulture);
                    foreach (XmlNode c in item.ChildNodes) if (c.LocalName == "AllocationUnits") c.InnerText = "byte * 2^20";
                }
            }
            using (var sw = new StringWriter())
            {
                doc.Save(sw);
                return sw.ToString();
            }
        }

        // OVF file references: href -> full local path.
        public static Dictionary<string, string> Files(string ovf, string folder)
        {
            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var doc = new XmlDocument();
            doc.LoadXml(ovf);
            foreach (XmlElement f in doc.GetElementsByTagName("File", "http://schemas.dmtf.org/ovf/envelope/1"))
            {
                string href = f.GetAttribute("href", "http://schemas.dmtf.org/ovf/envelope/1");
                if (href.Length > 0) files[href] = Path.Combine(folder, href);
            }
            return files;
        }
    }

    // ------------------------------------------------------------------------
    // Converts machines to VMware: an ESXi VM, or an OVF package (VMware template).

    class VmwareOptions
    {
        public bool ImportToEsxi;          // false = OVF package only
        public EsxiClient Target;
        public EsxiInventory Inventory;
        public EsxiDatastore Datastore;
        public EsxiNetwork Network;
        public bool Thin = true;
        public string Folder;              // OVF output folder, or staging folder for the ESXi import
        public bool KeepFiles;             // keep the staged OVF after an ESXi import
        public bool PowerOn;
        public bool ShutdownSource;        // live copies: shut the source down afterwards
        public bool RestoreNetwork = true;
        public EsxiClient SourceEsxi;
        public bool ShutdownRunning;       // ESXi sources: shut running VMs down for the export
        public int HwVersion = 10;         // OVF virtual hardware version (vmx-NN)
        public string NicType;             // E1000e / E1000 / VmxNet3; null = by guest OS
    }

    class VmwareConverter
    {
        public event Action<string, LogKind> Log;
        public event Action<VmxInfo> VmChanged;
        public event Action<string, double, double> Progress;
        public event Action<long> Moved;                      // bytes copied since the last call (speed display)
        volatile bool cancel;
        readonly ByteCounter counter = new ByteCounter();
        volatile SshClient activeSsh;

        void Bytes(long cumulative)
        {
            long d = counter.Delta(cumulative);
            if (d > 0 && Moved != null) Moved(d);
        }

        public void Cancel()
        {
            cancel = true;
            var s = activeSsh;
            if (s != null) s.Kill();
        }

        void Emit(string text, LogKind kind) { if (Log != null) Log(text, kind); }
        void Changed(VmxInfo vm) { if (VmChanged != null) VmChanged(vm); }

        public void Run(IList<VmxInfo> vms, VmwareOptions o)
        {
            int ok = 0;
            for (int i = 0; i < vms.Count && !cancel; i++)
            {
                var vm = vms[i];
                vm.State = VmState.Running;
                vm.StatusText = "Starting...";
                Changed(vm);
                int index = i;
                Action<string, double> progress = (label, f) => { if (Progress != null) Progress(label, f, (index + f) / vms.Count); };
                try
                {
                    ConvertOne(vm, o, i, vms.Count, progress);
                    ok++;
                }
                catch (Exception ex)
                {
                    if (cancel || ex is OperationCanceledException)
                    {
                        vm.State = VmState.Cancelled;
                        vm.StatusText = "Cancelled";
                        Emit(vm.DisplayName + ": cancelled", LogKind.Warn);
                    }
                    else
                    {
                        vm.State = VmState.Failed;
                        vm.StatusText = "Failed: " + ex.Message.Split('\n')[0];
                        Emit(vm.DisplayName + ": " + ex.Message, LogKind.Error);
                    }
                }
                Changed(vm);
            }
            if (Progress != null) Progress(cancel ? "Cancelled" : "Finished", cancel ? 0 : 1, cancel ? 0 : 1);
            Emit(string.Format("Finished: {0} of {1} machine(s) converted.", ok, vms.Count), ok == vms.Count ? LogKind.Ok : LogKind.Warn);
        }

        void ConvertOne(VmxInfo vm, VmwareOptions o, int index, int count, Action<string, double> progress)
        {
            string dest = o.ImportToEsxi ? "ESXi " + o.Target.Host + " (datastore " + o.Datastore.Name + ")" : "OVF package in " + o.Folder;
            Emit(string.Format("[{0}/{1}] {2}  ->  {3}", index + 1, count, vm.DisplayName, dest), LogKind.Step);
            counter.Reset();
            if (vm.Linux != null)
                throw new Exception("Live copies of Linux machines go to Proxmox VE only. Pick a Proxmox VE destination.");
            if (!vm.HotClone && vm.Esxi == null && vm.Pve == null)
                throw new Exception("VMware Workstation / local .vmx files can only be converted to Proxmox VE here (use Workstation's File > Export to OVF for VMware).");
            foreach (var d in vm.Disks)
                if (d.VolumeResize)
                    throw new Exception("Disk " + d.Slot + ": changing volume sizes is only available for Proxmox VE destinations. " +
                                        "Set the volumes to \"Maintain size\" under Options > Data to copy. Nothing was changed.");
                else if (d.TargetBytes > 0 && d.TargetBytes < d.CapacityBytes && d.MinBytes > d.TargetBytes)
                    throw new Exception(string.Format("Disk {0} is set to {1}, but its partitions reach {2}. Nothing was changed.",
                        d.Slot, Theme.Bytes(d.TargetBytes), Theme.Bytes(d.MinBytes)));

            string folder = Path.Combine(o.Folder, EsxiClient.SafeFile(vm.ProxmoxName));
            Directory.CreateDirectory(folder);
            CheckSpace(folder, vm);
            double copyShare = o.ImportToEsxi ? 0.6 : 1.0;
            Action<string, double> copyProgress = (l, f) => progress(l, f * copyShare);

            string ovfPath = vm.HotClone ? FromAgent(vm, folder, o, copyProgress)
                           : vm.Pve != null ? FromProxmox(vm, folder, o, copyProgress)
                           : FromEsxi(vm, folder, o, copyProgress);
            if (!o.ImportToEsxi)
            {
                Done(vm, "OVF package ready: " + ovfPath);
                Emit("  VMware template ready: " + ovfPath + "  (deploy it with \"Deploy OVF Template\" on ESXi / vCenter; in vCenter you can convert the VM to a template)", LogKind.Ok);
                progress("Exported " + vm.DisplayName, 1);
                return;
            }

            vm.StatusText = "Uploading to ESXi";
            Changed(vm);
            string ovf = File.ReadAllText(ovfPath);
            var clock = Stopwatch.StartNew();
            string moref = o.Target.ImportOvf(ovf, vm.ProxmoxName, o.Inventory, o.Datastore, o.Network, o.Thin, OvfBuilder.Files(ovf, folder),
                msg => Emit("  " + msg, LogKind.Info),
                (done, total) => progress(string.Format("Uploading {0} to ESXi: {1} / {2}  ({3}/s)", vm.ProxmoxName, Theme.Bytes(done), Theme.Bytes(total),
                    Theme.Bytes((long)(done / Math.Max(clock.Elapsed.TotalSeconds, 0.001)))), copyShare + (1 - copyShare) * (total > 0 ? (double)done / total : 0)),
                () => cancel);
            Emit("  VM " + vm.ProxmoxName + " created on ESXi " + o.Target.Host + ".", LogKind.Ok);
            if (vm.HotClone && vm.Agent != null && o.ShutdownSource)
            {
                Emit(Converter.AfterLiveCopyText(vm, true), LogKind.Info);
                try
                {
                    vm.Agent.Shutdown();
                    if (o.PowerOn && !WaitOff(vm.Agent.Host, TimeSpan.FromMinutes(5)))
                        Emit("  " + vm.DisplayName + " still answers on the network after 5 minutes; check that it is off before both use the same IP address.", LogKind.Warn);
                }
                catch (Exception ex) { Emit("  Could not shut down the source machine: " + ex.Message, LogKind.Warn); }
            }
            if (!o.KeepFiles)
            {
                try { Directory.Delete(folder, true); }
                catch (Exception ex) { Emit("  Could not delete the staging folder " + folder + ": " + ex.Message, LogKind.Warn); }
            }
            if (o.PowerOn && moref != null)
            {
                try { o.Target.PowerOn(moref); Emit("  Powered on.", LogKind.Info); }
                catch (Exception ex) { Emit("  Could not power the VM on: " + ex.Message, LogKind.Warn); }
            }
            Done(vm, "Created on ESXi " + o.Target.Host);
            progress("Created " + vm.ProxmoxName + " on ESXi", 1);
        }

        // Waits until `host` stops answering pings (3 misses in a row), so the copy does not start
        // while the source still holds the IP address. False after `limit`.
        static bool WaitOff(string host, TimeSpan limit)
        {
            var until = DateTime.UtcNow + limit;
            int misses = 0;
            using (var ping = new System.Net.NetworkInformation.Ping())
                while (DateTime.UtcNow < until)
                {
                    bool up;
                    try { up = ping.Send(host, 1000).Status == System.Net.NetworkInformation.IPStatus.Success; }
                    catch (Exception) { up = false; }
                    misses = up ? 0 : misses + 1;
                    if (misses >= 3) return true;
                    System.Threading.Thread.Sleep(2000);
                }
            return false;
        }

        void Done(VmxInfo vm, string text)
        {
            vm.State = VmState.Done;
            vm.StatusText = text;
            Changed(vm);
        }

        void CheckSpace(string folder, VmxInfo vm)
        {
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder)));
                long need = vm.TotalBytes > 0 ? vm.TotalBytes * 6 / 10 : vm.VirtualBytes / 2;   // compressed used blocks, rough
                if (drive.AvailableFreeSpace < need)
                    Emit(string.Format("  {0} has {1} free; the compressed disks of {2} may need about {3}.", drive.Name,
                        Theme.Bytes(drive.AvailableFreeSpace), vm.DisplayName, Theme.Bytes(need)), LogKind.Warn);
            }
            catch (Exception) { }
        }

        // Live copy from the agent into stream-optimized VMDKs + OVF descriptor.
        string FromAgent(VmxInfo vm, string folder, VmwareOptions o, Action<string, double> progress)
        {
            var agent = vm.Agent;
            vm.StatusText = "Taking VSS snapshots in the guest";
            Changed(vm);
            Emit("  The agent prepares the boot drivers (LSI Logic SAS), saves the network settings and takes VSS snapshots...", LogKind.Info);
            var numbers = new List<int>();
            foreach (var d in vm.Disks) numbers.Add(d.GuestDisk);
            if (numbers.Contains(-1)) throw new Exception("The disks of " + vm.DisplayName + " could not be matched to the guest's disks - connect the agent again.");
            var prep = agent.ClonePrepare(numbers, "vmware", o.RestoreNetwork && o.ImportToEsxi);
            string session = AgentReport.Str(prep, "session");
            Emit("  Snapshots taken; the machine keeps running. Copying the disks (used blocks only, encrypted)...", LogKind.Info);
            string noSnapshot = GuestAgent.NoSnapshotNote(prep);
            if (noSnapshot != null) Emit(noSnapshot, LogKind.Warn);
            var disks = new List<OvfBuilder.Disk>();
            try
            {
                long total = 0, before = 0;
                foreach (var d in vm.Disks) total += Math.Max(d.UsedBytes, 0);
                for (int i = 0; i < vm.Disks.Count; i++)
                {
                    var d = vm.Disks[i];
                    string file = EsxiClient.SafeFile(vm.ProxmoxName) + "-disk" + (i + 1) + ".vmdk";
                    string path = Path.Combine(folder, file);
                    vm.StatusText = "Copying disk " + (i + 1) + " of " + vm.Disks.Count;
                    Changed(vm);
                    var clock = Stopwatch.StartNew();
                    using (var w = new StreamVmdkWriter(path, d.FinalBytes, d.CapacityBytes, vm.IsWindows ? "lsilogic" : "lsilogic"))
                    {
                        long data = 0;
                        int disk = i;
                        counter.Reset();
                        AgentDiskReader.Read(agent.StreamFor(session, d.GuestDisk), (off, buf, len) => { w.Write(off, buf, len); data += len; Bytes(data); },
                            (pos, size) => progress(string.Format("Copying disk {0} of {1}: {2:0.0}%  ({3} read, {4}/s)", disk + 1, vm.Disks.Count,
                                100.0 * pos / Math.Max(size, 1), Theme.Bytes(data), Theme.Bytes((long)(data / Math.Max(clock.Elapsed.TotalSeconds, 0.001)))),
                                total > 0 ? Math.Min(1.0, (before + data) / (double)total) : (disk + (double)pos / Math.Max(size, 1)) / vm.Disks.Count),
                            () => cancel);
                        w.Finish();
                        before += data;
                        disks.Add(new OvfBuilder.Disk { File = file, Capacity = w.Capacity, Populated = w.Populated });
                    }
                    disks[i].FileSize = new FileInfo(path).Length;
                    Emit(string.Format("  Disk {0}: {1} -> {2} ({3} compressed)", i + 1, Theme.Bytes(d.CapacityBytes), Theme.Bytes(disks[i].Capacity), Theme.Bytes(disks[i].FileSize)), LogKind.Info);
                }
            }
            finally
            {
                try { agent.CloneFinish(session); Emit("  Snapshots deleted in the guest.", LogKind.Info); }
                catch (Exception ex) { Emit("  Could not delete the snapshots in the guest (the agent removes them within 48 h): " + ex.Message, LogKind.Warn); }
            }
            string network = o.Network != null ? o.Network.Name : "VM Network";
            string ovfPath = Path.Combine(folder, EsxiClient.SafeFile(vm.ProxmoxName) + ".ovf");
            File.WriteAllText(ovfPath, OvfBuilder.Build(vm, disks, network, o.HwVersion, o.NicType), new UTF8Encoding(false));

            // with an ESXi import the source is shut down only once the VM exists there (ConvertOne)
            if (!(o.ShutdownSource && o.ImportToEsxi)) Emit(Converter.AfterLiveCopyText(vm, false), LogKind.Warn);
            return ovfPath;
        }

        // Powered-off ESXi VM exported through an NFC lease (ESXi writes the stream-optimized disks).
        string FromEsxi(VmxInfo vm, string folder, VmwareOptions o, Action<string, double> progress)
        {
            var esxi = o.SourceEsxi;
            var state = esxi.Refresh(vm.Esxi.Moref);
            if (state.PowerState != "poweredOff")
            {
                if (!o.ShutdownRunning)
                    throw new Exception("VM is running on ESXi. Shut it down, or tick \"Shut down running VMs on ESXi\".");
                vm.StatusText = "Shutting down on ESXi";
                Changed(vm);
                var agent = vm.Agent;
                Func<string> agentShutdown = null;
                if (agent != null)
                    agentShutdown = () => { try { agent.Shutdown(); return null; } catch (Exception ex) { return ex.Message; } };
                esxi.ShutDown(vm.Esxi.Moref, TimeSpan.FromMinutes(5), msg => Emit("  " + msg, LogKind.Info), () => cancel, agentShutdown);
                vm.Esxi.PowerState = "poweredOff";
                Emit("  VM is powered off on ESXi.", LogKind.Info);
            }
            if (vm.Resized) Emit("  Disk sizes stay as they are for VMware targets from ESXi (resize the disks in the new VM).", LogKind.Warn);
            vm.StatusText = "Exporting from ESXi";
            Changed(vm);
            var clock = Stopwatch.StartNew();
            string ovfPath = esxi.ExportOvf(vm.Esxi.Moref, vm.ProxmoxName, folder, msg => Emit("  " + msg, LogKind.Info),
                (done, total) => progress(string.Format("Exporting {0}: {1}  ({2}/s)", vm.DisplayName, Theme.Bytes(done),
                    Theme.Bytes((long)(done / Math.Max(clock.Elapsed.TotalSeconds, 0.001)))), total > 0 ? Math.Min(0.99, (double)done / total) : 0.5),
                () => cancel);
            if (vm.TargetCores > 0 || vm.TargetMemoryMb > 0)
                File.WriteAllText(ovfPath, OvfBuilder.Patch(File.ReadAllText(ovfPath), vm.TargetCores, vm.TargetMemoryMb), new UTF8Encoding(false));
            return ovfPath;
        }

        static readonly System.Text.RegularExpressions.Regex QemuProgress =
            new System.Text.RegularExpressions.Regex("\\((\\d+(?:\\.\\d+)?)/100%\\)");

        // Proxmox VE VM: the source host writes each disk as a stream-optimized VMDK
        // (qemu-img, compressed, used data only) into a temp folder, this PC downloads
        // it, then the OVF descriptor is built here. The VM must be powered off.
        string FromProxmox(VmxInfo vm, string folder, VmwareOptions o, Action<string, double> progress)
        {
            var pve = vm.Pve;
            var ssh = new SshClient(pve.Host);
            activeSsh = ssh;
            int code;
            try
            {
                string state = ssh.Capture("qm status " + pve.Vmid + " | awk '{print $2}'; command -v qemu-img >/dev/null || echo noqemuimg", out code);
                if (code != 0) throw new Exception("Cannot connect to " + pve.Host.Host + ": " + ssh.Explain(code));
                if (state.Contains("noqemuimg")) throw new Exception("qemu-img is missing on " + pve.Host.Host + ".");
                if (state.Trim().StartsWith("running"))
                {
                    if (!o.ShutdownRunning)
                        throw new Exception("VM " + pve.Vmid + " is running on " + pve.Host.Host + ". Shut it down, or allow the converter to shut it down.");
                    vm.StatusText = "Shutting down on Proxmox";
                    Changed(vm);
                    Emit("  Shutting down VM " + pve.Vmid + " on " + pve.Host.Host + " (up to 5 minutes, then it is stopped)...", LogKind.Info);
                    ssh.Capture("qm shutdown " + pve.Vmid + " --timeout 300 --forceStop 1", out code);
                    if (code != 0) throw new Exception("Could not shut down VM " + pve.Vmid + ": " + ssh.Explain(code));
                    pve.Running = false;
                    Emit("  VM is powered off.", LogKind.Info);
                }
                if (vm.Resized) Emit("  Disk sizes stay as they are for VMware targets from Proxmox (resize the disks in the new VM).", LogKind.Warn);
                if (vm.IsWindows)
                    Emit("  Windows from Proxmox VE boots on VMware only if its LSI Logic SAS driver is enabled; if it stops with INACCESSIBLE_BOOT_DEVICE, " +
                         "use the live copy (Remote Windows machine) instead, which prepares the driver.", LogKind.Warn);

                // temp folder on the source host: the directory storage with the most free space
                string tmp = ssh.Capture(
                    "best=/var/tmp; bf=$(df -PB1 /var/tmp | awk 'NR==2{print $4}')\n" +
                    "for p in $(awk '/^[a-z]+: / {t=$1} /^[ \\t]+path / && t==\"dir:\" {print $2}' /etc/pve/storage.cfg 2>/dev/null); do\n" +
                    "  [ -d \"$p\" ] || continue; f=$(df -PB1 \"$p\" | awk 'NR==2{print $4}')\n" +
                    "  [ \"${f:-0}\" -gt \"${bf:-0}\" ] && { best=$p; bf=$f; }\ndone\n" +
                    "d=\"$best/mtaalam-v2v-export-" + pve.Vmid + "\"; mkdir -p \"$d\" && echo \"$d $bf\"", out code).Trim();
                string[] tf = tmp.Split(' ');
                if (code != 0 || tf.Length < 2) throw new Exception("Cannot create a temp folder on " + pve.Host.Host + ": " + ssh.Explain(code));
                string remoteDir = tf[0];
                long remoteFree;
                long.TryParse(tf[1], out remoteFree);
                long largest = 0;
                foreach (var d in vm.Disks) largest = Math.Max(largest, d.UsedBytes > 0 ? d.UsedBytes : d.CapacityBytes / 2);
                if (remoteFree > 0 && remoteFree < largest)
                    Emit(string.Format("  {0} on {1} has {2} free; the largest compressed disk may need up to {3}.", remoteDir, pve.Host.Host,
                        Theme.Bytes(remoteFree), Theme.Bytes(largest)), LogKind.Warn);

                var disks = new List<OvfBuilder.Disk>();
                try
                {
                    for (int i = 0; i < vm.Disks.Count; i++)
                    {
                        if (cancel) throw new OperationCanceledException();
                        var d = vm.Disks[i];
                        var drive = pve.Drives[i];
                        string file = EsxiClient.SafeFile(vm.ProxmoxName) + "-disk" + (i + 1) + ".vmdk";
                        string remote = remoteDir + "/disk" + (i + 1) + ".vmdk";
                        int disk = i;
                        vm.StatusText = "Converting disk " + (i + 1) + " of " + vm.Disks.Count + " on Proxmox";
                        Changed(vm);
                        Emit("  Disk " + (i + 1) + " (" + drive[1] + "): writing a compressed VMDK on " + pve.Host.Host + "...", LogKind.Info);
                        int rc = ssh.Run("set -e\np=$(pvesm path" + SshClient.Q(drive[1]) + ")\n" +
                                "rm -f " + SshClient.Q(remote) + "\n" +
                                "qemu-img convert -p -f " + SshClient.Q(drive[2]) + " -O vmdk -o subformat=streamOptimized,adapter_type=lsilogic \"$p\" " + SshClient.Q(remote),
                            line =>
                            {
                                var m = QemuProgress.Match(line);
                                double pct;
                                if (m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out pct))
                                    progress(string.Format("Converting disk {0} of {1} on Proxmox: {2:0.0}%", disk + 1, vm.Disks.Count, pct),
                                        (disk + 0.5 * pct / 100) / vm.Disks.Count);
                                else if (line.Trim().Length > 0 && !line.Contains("/100%")) Emit("  " + line.Trim(), LogKind.Remote);
                            });
                        if (rc != 0)
                            throw new Exception("qemu-img could not write disk " + (i + 1) + " on " + pve.Host.Host + ": " + ssh.Explain(rc));
                        string sz = ssh.Capture("stat -c %s " + SshClient.Q(remote), out code).Trim();
                        long remoteSize;
                        if (code != 0 || !long.TryParse(sz, out remoteSize))
                            throw new Exception("The converted disk " + (i + 1) + " is missing on " + pve.Host.Host + ": " + ssh.Explain(code));

                        string path = Path.Combine(folder, file);
                        var clock = Stopwatch.StartNew();
                        counter.Reset();
                        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                            ssh.Download("cat " + SshClient.Q(remote), fs, n =>
                            {
                                Bytes(n);
                                progress(string.Format("Downloading disk {0} of {1}: {2} / {3}  ({4}/s)", disk + 1, vm.Disks.Count, Theme.Bytes(n), Theme.Bytes(remoteSize),
                                    Theme.Bytes((long)(n / Math.Max(clock.Elapsed.TotalSeconds, 0.001)))),
                                    (disk + 0.5 + 0.5 * n / Math.Max(remoteSize, 1)) / vm.Disks.Count);
                            });
                        ssh.Capture("rm -f " + SshClient.Q(remote), out code);
                        long local = new FileInfo(path).Length;
                        if (local != remoteSize) throw new Exception("Disk " + (i + 1) + " download is incomplete (" + Theme.Bytes(local) + " of " + Theme.Bytes(remoteSize) + ").");
                        disks.Add(new OvfBuilder.Disk { File = file, FileSize = local, Capacity = d.CapacityBytes, Populated = Math.Max(local, d.UsedBytes) });
                        Emit(string.Format("  Disk {0}: {1} ({2} compressed)", i + 1, Theme.Bytes(d.CapacityBytes), Theme.Bytes(local)), LogKind.Info);
                    }
                }
                finally
                {
                    ssh.Reset();
                    try { ssh.Capture("rm -rf -- " + SshClient.Q(remoteDir), out code); } catch (Exception) { }
                }
                string network = o.Network != null ? o.Network.Name : "VM Network";
                string ovfPath = Path.Combine(folder, EsxiClient.SafeFile(vm.ProxmoxName) + ".ovf");
                File.WriteAllText(ovfPath, OvfBuilder.Build(vm, disks, network, o.HwVersion, o.NicType), new UTF8Encoding(false));
                return ovfPath;
            }
            finally
            {
                activeSsh = null;
            }
        }
    }
}
