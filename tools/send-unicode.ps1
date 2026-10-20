param([Parameter(Mandatory=$true)][string]$Text, [int]$ProcessId = 0)
Add-Type -AssemblyName System.Windows.Forms
$sig = @'
using System;
using System.Runtime.InteropServices;
public class UnicodeInput {
  [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public INPUTUNION u; }
  [StructLayout(LayoutKind.Explicit)] public struct INPUTUNION { [FieldOffset(0)] public KEYBDINPUT ki; }
  [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
  [DllImport("user32.dll")] public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  const uint KEYEVENTF_UNICODE = 0x0004;
  const uint KEYEVENTF_KEYUP = 0x0002;
  public static void Send(string text) {
    foreach (char c in text) {
      INPUT down = new INPUT(); down.type = 1; down.u.ki.wScan = (ushort)c; down.u.ki.dwFlags = KEYEVENTF_UNICODE;
      INPUT up = new INPUT(); up.type = 1; up.u.ki.wScan = (ushort)c; up.u.ki.dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP;
      INPUT[] pair = new INPUT[] { down, up };
      SendInput(2, pair, Marshal.SizeOf(typeof(INPUT)));
    }
  }
}
'@
Add-Type -TypeDefinition $sig
if ($ProcessId -gt 0) {
  $p = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
  if ($p) { [UnicodeInput]::SetForegroundWindow($p.MainWindowHandle) | Out-Null; Start-Sleep -Milliseconds 500 }
}
Write-Output ("foreground=" + [UnicodeInput]::GetForegroundWindow())
[UnicodeInput]::Send($Text)
Write-Output "sent"
