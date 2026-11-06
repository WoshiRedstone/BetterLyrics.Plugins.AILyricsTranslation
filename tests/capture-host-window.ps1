param(
    [Parameter(Mandatory = $true)][string]$OutPath,
    [string]$TitlePart = [char]0x6807 + [char]0x51C6 + [char]0x6A21 + [char]0x5F0F
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$code = @"
using System;
using System.Text;
using System.Drawing;
using System.Runtime.InteropServices;
public class WinShot {
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
  public delegate bool EnumProc(IntPtr h, IntPtr p);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern IntPtr GetWindowDC(IntPtr h);
  [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
  [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
  [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
  [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
  [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
  [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr o);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }

  public static IntPtr Find(uint pid, string want) {
    IntPtr found = IntPtr.Zero;
    EnumWindows(delegate(IntPtr h, IntPtr p) {
      uint wp; GetWindowThreadProcessId(h, out wp);
      if (wp != pid || !IsWindowVisible(h)) return true;
      var sb = new StringBuilder(512);
      GetWindowTextW(h, sb, 512);
      if (sb.ToString().IndexOf(want, StringComparison.Ordinal) >= 0) { found = h; return false; }
      return true;
    }, IntPtr.Zero);
    return found;
  }

  public static string Capture(uint pid, string want, string outPath) {
    IntPtr h = Find(pid, want);
    if (h == IntPtr.Zero) return "WINDOW NOT FOUND";
    RECT r; GetWindowRect(h, out r);
    int w = r.R - r.L, ht = r.B - r.T;
    if (w <= 0 || ht <= 0) return "BAD RECT " + w + "x" + ht;
    IntPtr dc = GetWindowDC(h);
    IntPtr mem = CreateCompatibleDC(dc);
    IntPtr bmp = CreateCompatibleBitmap(dc, w, ht);
    IntPtr old = SelectObject(mem, bmp);
    bool ok = PrintWindow(h, mem, 2);
    using (Bitmap img = Image.FromHbitmap(bmp)) { img.Save(outPath, System.Drawing.Imaging.ImageFormat.Png); }
    SelectObject(mem, old); DeleteObject(bmp); DeleteDC(mem); ReleaseDC(h, dc);
    return "saved " + w + "x" + ht + " printWindow=" + ok;
  }
}
"@
Add-Type -TypeDefinition $code -ReferencedAssemblies System.Drawing

$proc = Get-Process -Name BetterLyrics.WinUI3 -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { Write-Output 'NO PROCESS'; exit 1 }
Write-Output ('pid=' + $proc.Id)
Write-Output ([WinShot]::Capture([uint32]$proc.Id, $TitlePart, $OutPath))
