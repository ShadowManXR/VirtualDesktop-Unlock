using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;

// Inspect PE import descriptors without loading or executing native libraries.
internal static class NativeDependencies
{
    internal static IEnumerable<string> Inspect(string executable)
    {
        byte[] bytes=File.ReadAllBytes(executable);
        using(var reader=new BinaryReader(new MemoryStream(bytes)))
        {
            if(bytes.Length<256 || reader.ReadUInt16()!=0x5A4D) throw new InvalidDataException("Invalid PE image.");
            reader.BaseStream.Position=0x3C; int pe=reader.ReadInt32();
            if(pe<64 || pe>bytes.Length-256) throw new InvalidDataException("Invalid PE header.");
            reader.BaseStream.Position=pe;
            if(reader.ReadUInt32()!=0x4550) throw new InvalidDataException("Invalid PE signature.");
            reader.ReadUInt16(); int count=reader.ReadUInt16(); reader.BaseStream.Position=pe+20; int optional=reader.ReadUInt16();
            reader.BaseStream.Position=pe+24; int magic=reader.ReadUInt16();
            int directories=pe+24+(magic==0x20B?112:96);
            reader.BaseStream.Position=directories+8; uint importRva=reader.ReadUInt32();
            var sections=new List<uint[]>();
            for(int i=0;i<count;i++) {reader.BaseStream.Position=pe+24+optional+i*40+8; uint size=reader.ReadUInt32(),rva=reader.ReadUInt32(),rawSize=reader.ReadUInt32(),offset=reader.ReadUInt32();sections.Add(new[]{rva,Math.Max(size,rawSize),offset});}
            Func<uint,int> offsetOf=rva=> {foreach(var section in sections) if(rva>=section[0] && (ulong)rva<(ulong)section[0]+section[1]) return checked((int)(rva-section[0]+section[2]));throw new InvalidDataException("Invalid PE import address.");};
            if(importRva==0) yield break;
            int imports=offsetOf(importRva);
            for(int i=0;i<512;i++)
            {
                reader.BaseStream.Position=imports+i*20;
                uint lookup=reader.ReadUInt32(),time=reader.ReadUInt32(),chain=reader.ReadUInt32(),nameRva=reader.ReadUInt32(),thunks=reader.ReadUInt32();
                if(lookup==0 && nameRva==0 && thunks==0) yield break;
                int nameOffset=offsetOf(nameRva),end=nameOffset;
                while(end<bytes.Length && bytes[end]!=0 && end-nameOffset<260)end++;
                if(end==bytes.Length || end-nameOffset>=260)throw new InvalidDataException("Invalid PE import name.");
                string name=System.Text.Encoding.ASCII.GetString(bytes,nameOffset,end-nameOffset);
                if(Path.GetFileName(name)!=name)throw new InvalidDataException("Unsafe native dependency name.");
                bool api=name.StartsWith("api-ms-",StringComparison.OrdinalIgnoreCase) || name.StartsWith("ext-ms-",StringComparison.OrdinalIgnoreCase);
                string resolved=new[]{Path.GetDirectoryName(executable),Environment.SystemDirectory}.Select(dir=>Path.Combine(dir,name)).FirstOrDefault(File.Exists);
                if(!api && resolved==null)throw new FileNotFoundException("A native Streamer dependency is missing: "+name+". Repair Virtual Desktop Streamer and its prerequisites.");
                yield return "Native dependency available: "+name+(api?" (Windows API set)":"");
            }
            throw new InvalidDataException("Too many native PE imports.");
        }
    }
}
