using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Windows.Forms;

namespace WirelessBattery
{
    /// <summary>
    /// 托盘图标是通过 Shell_NotifyIcon 交给资源管理器（Explorer）注册的。
    /// 如果 Explorer 以管理员（High）完整性运行，而本程序是普通（Medium）权限，
    /// Windows 的 UIPI 会阻止 Medium → High 的窗口消息，Shell_NotifyIcon 返回失败，
    /// 表现为“托盘暂不可用”，关闭窗口后没有图标。
    ///
    /// 这里在启动时检测这种“权限倒挂”：只有 Explorer 比本程序高、本程序不是管理员、
    /// 且托盘探测失败时，才用 runas 重新以管理员启动。普通 Explorer（Medium）下不会弹 UAC。
    /// </summary>
    internal static class Elevation
    {
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const uint TOKEN_QUERY = 0x0008;
        private const int TokenIntegrityLevel = 25;
        private const int TokenLinkedToken = 19;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length, out int returnLength);

        [DllImport("advapi32.dll")]
        private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);

        [DllImport("advapi32.dll")]
        private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        public static int GetIntegrityLevel(int pid)
        {
            IntPtr process = IntPtr.Zero;
            IntPtr token = IntPtr.Zero;
            IntPtr buffer = IntPtr.Zero;
            try
            {
                process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (process == IntPtr.Zero) return -1;
                if (!OpenProcessToken(process, TOKEN_QUERY, out token)) return -1;

                int length;
                GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out length);
                if (length <= 0) return -1;
                buffer = Marshal.AllocHGlobal(length);
                int returned;
                if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, length, out returned)) return -1;

                IntPtr sid = Marshal.ReadIntPtr(buffer);
                IntPtr countPtr = GetSidSubAuthorityCount(sid);
                if (countPtr == IntPtr.Zero) return -1;
                byte count = Marshal.ReadByte(countPtr);
                if (count == 0) return -1;
                IntPtr sub = GetSidSubAuthority(sid, (uint)(count - 1));
                if (sub == IntPtr.Zero) return -1;
                return Marshal.ReadInt32(sub);
            }
            catch { return -1; }
            finally
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                if (token != IntPtr.Zero) CloseHandle(token);
                if (process != IntPtr.Zero) CloseHandle(process);
            }
        }

        public static bool IsElevated()
        {
            return GetIntegrityLevel(Process.GetCurrentProcess().Id) >= 0x2000;
        }

        /// <summary>
        /// 过滤令牌里的 Administrators 组是“仅拒绝”，.NET 的 Groups 不返回它，
        /// 但 UAC 会保留一个指向完整管理员令牌的 LinkedToken。
        /// 有 LinkedToken 就说明当前用户具备提权能力。
        /// </summary>
        private static bool HasLinkedToken()
        {
            IntPtr process = IntPtr.Zero;
            IntPtr token = IntPtr.Zero;
            IntPtr buffer = IntPtr.Zero;
            try
            {
                process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, Process.GetCurrentProcess().Id);
                if (process == IntPtr.Zero) return false;
                if (!OpenProcessToken(process, TOKEN_QUERY, out token)) return false;

                int length;
                GetTokenInformation(token, TokenLinkedToken, IntPtr.Zero, 0, out length);
                if (length <= 0) return false;
                buffer = Marshal.AllocHGlobal(length);
                int returned;
                if (!GetTokenInformation(token, TokenLinkedToken, buffer, length, out returned)) return false;
                IntPtr linked = Marshal.ReadIntPtr(buffer);
                if (linked == IntPtr.Zero) return false;
                CloseHandle(linked);
                return true;
            }
            catch { return false; }
            finally
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                if (token != IntPtr.Zero) CloseHandle(token);
                if (process != IntPtr.Zero) CloseHandle(process);
            }
        }

        public static bool HasAdminGroup()
        {
            if (HasLinkedToken()) return true;
            try
            {
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                {
                    foreach (IdentityReference group in identity.Groups)
                    {
                        if (string.Equals(group.Value, "S-1-5-32-544", StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
            }
            catch { }
            return false;
        }

        /// <summary>同会话里是否有比当前进程完整性更高的 Explorer。</summary>
        public static bool ExplorerIsHigher()
        {
            int current = GetIntegrityLevel(Process.GetCurrentProcess().Id);
            if (current < 0) return false;
            int session = Process.GetCurrentProcess().SessionId;
            Process[] explorers = Process.GetProcessesByName("explorer");
            try
            {
                for (int i = 0; i < explorers.Length; i++)
                {
                    try
                    {
                        if (explorers[i].SessionId != session) continue;
                        int level = GetIntegrityLevel(explorers[i].Id);
                        if (level > current) return true;
                    }
                    catch { }
                }
            }
            finally
            {
                for (int i = 0; i < explorers.Length; i++)
                {
                    try { explorers[i].Dispose(); } catch { }
                }
            }
            return false;
        }

        public static bool NeedsElevation()
        {
            if (IsElevated()) return false;
            // 只要能拿到管理员组（即使当前是“仅用于拒绝”的过滤令牌），
            // 且托盘探测失败，就值得用 runas 试一次。Explorer 的完整性
            // 从普通权限进程可能读不到，不能作为唯一判断依据。
            return HasAdminGroup();
        }

        public static bool RelaunchElevated(string[] args)
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                bool hasElevatedArg = false;
                for (int i = 0; i < args.Length; i++)
                {
                    string a = args[i] ?? "";
                    if (a.Equals("--elevated", StringComparison.OrdinalIgnoreCase)) hasElevatedArg = true;
                    if (sb.Length > 0) sb.Append(' ');
                    if (a.IndexOf(' ') >= 0 || a.IndexOf('\t') >= 0)
                    {
                        sb.Append('"');
                        sb.Append(a.Replace("\"", "\\\""));
                        sb.Append('"');
                    }
                    else sb.Append(a);
                }
                if (!hasElevatedArg)
                {
                    if (sb.Length > 0) sb.Append(' ');
                    sb.Append("--elevated");
                }

                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = Application.ExecutablePath;
                psi.Arguments = sb.ToString();
                psi.WorkingDirectory = Application.StartupPath;
                psi.Verb = "runas";
                psi.UseShellExecute = true;
                Process.Start(psi);
                return true;
            }
            catch
            {
                // 用户取消 UAC 或没有管理员权限：继续以普通权限运行，
                // 托盘不可用时由 TrayContext 退回“最小化到任务栏”。
                return false;
            }
        }
    }
}
