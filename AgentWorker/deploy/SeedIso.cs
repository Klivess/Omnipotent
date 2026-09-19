using System;
using System.IO;
using System.Text;

// Small ISO9660 NoCloud seed writer. No ADK, WSL, Docker or third-party executable.
public static class KASeedIso
{
    const int Block = 2048;
    static void Both16(byte[] b, int p, ushort n) { b[p]=(byte)n; b[p+1]=(byte)(n>>8); b[p+2]=b[p+1]; b[p+3]=b[p]; }
    static void Both32(byte[] b, int p, uint n) { for(int i=0;i<4;i++){ b[p+i]=(byte)(n>>(8*i)); b[p+7-i]=b[p+i]; } }
    static void Text(byte[] b,int p,int count,string value) { for(int i=0;i<count;i++) b[p+i]=(byte)' '; var bytes=Encoding.ASCII.GetBytes(value); Array.Copy(bytes,0,b,p,Math.Min(count,bytes.Length)); }
    static byte[] Record(uint sector,uint length,byte[] name,bool directory)
    {
        var b=new byte[33+name.Length+(name.Length%2==0?1:0)]; b[0]=(byte)b.Length;
        Both32(b,2,sector); Both32(b,10,length); b[18]=126;b[19]=1;b[20]=1;
        b[25]=(byte)(directory?2:0); Both16(b,28,1);b[32]=(byte)name.Length;Array.Copy(name,0,b,33,name.Length);return b;
    }
    public static void Create(string destination,string directory)
    {
        string[] names={"user-data","meta-data","network-config"};
        byte[][] files=new byte[names.Length][]; uint[] sectors=new uint[names.Length];uint next=21;
        for(int i=0;i<names.Length;i++){files[i]=File.ReadAllBytes(Path.Combine(directory,names[i]));sectors[i]=next;next+=(uint)((files[i].Length+Block-1)/Block);}
        using(var stream=new FileStream(destination,FileMode.CreateNew,FileAccess.Write))
        {
            stream.SetLength(next*Block);
            byte[] pvd=new byte[Block];pvd[0]=1;Text(pvd,1,5,"CD001");pvd[6]=1;
            Text(pvd,8,32,"OMNIPOTENT");Text(pvd,40,32,"CIDATA");Both32(pvd,80,next);
            Both16(pvd,120,1);Both16(pvd,124,1);Both16(pvd,128,Block);Both32(pvd,132,10);
            pvd[140]=18;pvd[151]=19;Array.Copy(Record(20,Block,new byte[]{0},true),0,pvd,156,34);
            pvd[881]=1; stream.Position=16*Block;stream.Write(pvd,0,Block);
            var end=new byte[Block];end[0]=255;Text(end,1,5,"CD001");end[6]=1;stream.Write(end,0,Block);
            var little=new byte[Block];little[0]=1;little[2]=20;little[6]=1;stream.Write(little,0,Block);
            var big=new byte[Block];big[0]=1;big[5]=20;big[7]=1;stream.Write(big,0,Block);
            var root=new byte[Block];int offset=0;
            foreach(byte name in new byte[]{0,1}){var record=Record(20,Block,new byte[]{name},true);Array.Copy(record,0,root,offset,record.Length);offset+=record.Length;}
            for(int i=0;i<names.Length;i++){var record=Record(sectors[i],(uint)files[i].Length,Encoding.ASCII.GetBytes(names[i].ToUpperInvariant()+";1"),false);Array.Copy(record,0,root,offset,record.Length);offset+=record.Length;}
            stream.Write(root,0,Block);
            for(int i=0;i<files.Length;i++){stream.Position=sectors[i]*Block;stream.Write(files[i],0,files[i].Length);}
            stream.Flush(true);
        }
    }
}
