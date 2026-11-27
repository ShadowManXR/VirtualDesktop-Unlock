using System;
using System.Text;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.ComponentModel;
namespace VDUnlockInstaller { public static class ProcessControl {
 [StructLayout(LayoutKind.Sequential)] public struct Unique {public int Id; public System.Runtime.InteropServices.ComTypes.FILETIME Start;}
 [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] public struct Info { public Unique Process; [MarshalAs(UnmanagedType.ByValTStr,SizeConst=256)]public string Name;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=64)]public string Service;public uint Type,Status,Session;[MarshalAs(UnmanagedType.Bool)]public bool Restartable;}
 [DllImport("rstrtmgr.dll",CharSet=CharSet.Unicode)] static extern int RmStartSession(out uint session,int flags,StringBuilder key);
 [DllImport("rstrtmgr.dll",CharSet=CharSet.Unicode)] static extern int RmRegisterResources(uint session,uint count,string[] files,uint apps,IntPtr app,uint services,IntPtr service);
 [DllImport("rstrtmgr.dll")] static extern int RmGetList(uint session,out uint needed,ref uint count,[In,Out] Info[] infos,ref uint reasons);
 [DllImport("rstrtmgr.dll")] static extern int RmEndSession(uint session);
 [DllImport("kernel32.dll",SetLastError=true)] static extern IntPtr OpenProcess(uint access,bool inherit,int id);
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool QueryFullProcessImageName(IntPtr process,int flags,StringBuilder value,ref int size);
 [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
 public static string ExecutablePath(int id) {var h=OpenProcess(0x1000,false,id);if(h==IntPtr.Zero) {int error=Marshal.GetLastWin32Error(); if(error==87)return null;throw new Win32Exception(error,"Cannot identify process "+id);}try{var text=new StringBuilder(32768);int size=text.Capacity;if(!QueryFullProcessImageName(h,0,text,ref size))throw new Win32Exception(Marshal.GetLastWin32Error(),"Cannot identify process "+id);return text.ToString();}finally{CloseHandle(h);}}
 public static string DescribeLocks(string[] files) {
  uint session;int code=RmStartSession(out session,0,new StringBuilder(33));if(code!=0)return "Lock-owner query unavailable (Windows error "+code+").";
  try {
   code=RmRegisterResources(session,(uint)files.Length,files,0,IntPtr.Zero,0,IntPtr.Zero);if(code!=0)return "Lock-owner registration failed (Windows error "+code+").";
   for(int attempt=0;attempt<3;attempt++) {
    uint needed=0,count=0,reasons=0;code=RmGetList(session,out needed,ref count,null,ref reasons);if(code==0)return "No process is currently reported by Windows Restart Manager.";
    if(code!=234)return "Lock-owner query failed (Windows error "+code+").";
    var infos=new Info[needed];count=needed;code=RmGetList(session,out needed,ref count,infos,ref reasons);if(code==234)continue;
    if(code!=0)return "Lock-owner query failed (Windows error "+code+").";
    var result=new StringBuilder();for(int i=0;i<count;i++)result.Append(infos[i].Name).Append(" (PID ").Append(infos[i].Process.Id).Append(") ");return result.ToString();
   }
   return "The locking processes changed during inspection.";
  } finally {RmEndSession(session);}
 }
}
}
