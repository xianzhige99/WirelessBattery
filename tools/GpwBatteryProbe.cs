// GpwBatteryProbe.cs —— 罗技 G PRO Wireless 电量读取与换算验证工具（单文件、零依赖）
//
// 用途：把 WirelessBattery 1.4.1 里「设备 PID → 设备曲线 → 充电模型 → 显示」这条链路
//       完整独立实现一遍，直接读真机，并与 G HUB 网页版同源数据逐项对照。
//
// 编译（Windows 自带 .NET Framework 4.x，无需 SDK）：
//   %WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:exe /platform:x64 ^
//     /codepage:65001 /out:bin\GpwBatteryProbe.exe tools\GpwBatteryProbe.cs
//
// 运行：
//   GpwBatteryProbe.exe                 取一次读数
//   GpwBatteryProbe.exe --watch         每 5 秒刷新一次
//   GpwBatteryProbe.exe --watch --interval 30 --count 20
//   GpwBatteryProbe.exe --json          输出单行 JSON（便于比对脚本）
//
// 退出码：0 读到电量；1 没找到设备或设备无 0x1001；2 参数错误。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace GpwProbe
{
    // ======================================================================
    // 1. 电池模型数据（取自 G HUB 网页版 /depots/mice/pro-wireless/battery.xml）
    //    编码："电压mV:值;..."，按电压升序。
    //    放电：值 = 剩余电量 ×10（能量曲线归一后的结果）
    //    充电：值 = 百分比；时间表：键=分钟，值=百分比
    // ======================================================================
    internal static class Model
    {
        public const int Pid = 0x4079;
        public const string Name = "PRO WIRELESS";
        public const double StableMv = 4100;     // stable_state_detection_voltage
        public static readonly double GapFill = 0.835;     // gap_fill_speed（%/30 秒）

        public const string Discharge = "3479:0;3493:3;3500:4;3506:5;3513:6;3525:9;3543:13;3548:14;3554:16;3559:17;3569:20;3579:22;3583:24;3588:25;3596:28;3604:30;3608:32;3612:33;3615:34;3619:36;3625:38;3631:41;3635:44;3643:49;3644:50;3646:52;3650:57;3651:58;3652:60;3654:62;3654:62;3655:65;3657:68;3657:68;3658:71;3658:71;3659:73;3659:73;3661:77;3661:77;3662:80;3662:80;3662:80;3663:84;3663:84;3665:88;3665:88;3666:91;3666:91;3667:94;3667:94;3669:98;3669:98;3671:102;3671:102;3673:104;3673:104;3674:107;3675:108;3676:110;3677:111;3677:111;3679:115;3680:117;3682:119;3683:121;3683:121;3686:126;3686:126;3688:130;3688:130;3690:134;3690:134;3692:138;3692:138;3693:141;3693:141;3695:145;3695:145;3696:148;3697:149;3697:149;3697:149;3699:155;3699:155;3701:159;3701:159;3702:161;3702:161;3704:166;3704:166;3705:168;3705:168;3706:171;3706:171;3707:174;3707:174;3709:178;3709:178;3709:178;3711:183;3711:183;3712:186;3712:186;3713:189;3713:189;3714:191;3714:191;3715:194;3715:194;3715:194;3717:200;3717:200;3717:200;3718:204;3718:204;3719:207;3719:207;3720:209;3720:209;3720:209;3721:213;3721:213;3722:216;3722:216;3722:216;3724:222;3724:222;3724:222;3725:226;3725:226;3725:226;3726:230;3726:230;3728:234;3728:234;3728:234;3729:238;3729:238;3730:241;3730:241;3731:244;3731:244;3732:246;3732:246;3733:249;3733:249;3734:252;3734:252;3735:255;3735:255;3736:257;3736:257;3736:257;3737:262;3738:263;3738:263;3739:266;3739:266;3739:266;3740:270;3741:271;3741:271;3742:274;3742:274;3743:277;3743:277;3744:279;3744:279;3744:279;3745:284;3745:284;3747:288;3747:288;3748:290;3748:290;3749:293;3749:293;3749:293;3750:297;3750:297;3751:300;3751:300;3751:300;3752:304;3752:304;3752:304;3753:308;3753:308;3754:311;3754:311;3754:311;3755:315;3755:315;3756:318;3756:318;3757:321;3757:321;3757:321;3758:325;3758:325;3758:325;3759:329;3759:329;3759:329;3760:333;3760:333;3761:336;3761:336;3761:336;3762:340;3762:340;3762:340;3763:344;3763:344;3763:344;3764:349;3764:349;3764:349;3765:353;3765:353;3765:353;3766:357;3766:357;3766:357;3767:361;3767:361;3768:364;3768:364;3768:364;3769:368;3769:368;3770:371;3770:371;3770:371;3771:375;3771:375;3771:375;3772:379;3772:379;3773:382;3773:382;3773:382;3774:386;3774:386;3774:386;3775:390;3775:390;3776:393;3776:393;3777:396;3777:396;3777:396;3778:400;3778:400;3778:400;3779:404;3779:404;3780:407;3780:407;3780:407;3781:411;3781:411;3781:411;3782:415;3782:415;3783:418;3783:418;3784:421;3784:421;3784:421;3786:427;3786:427;3787:429;3787:429;3787:429;3788:433;3788:433;3789:436;3789:436;3789:436;3790:440;3790:440;3791:443;3791:443;3792:446;3792:446;3793:449;3793:449;3793:449;3794:453;3794:453;3795:456;3796:457;3796:457;3796:457;3797:461;3798:463;3798:463;3798:463;3799:467;3799:467;3800:470;3800:470;3801:473;3801:473;3803:477;3803:477;3804:480;3804:480;3805:482;3805:482;3806:485;3806:485;3807:488;3807:488;3808:491;3808:491;3809:494;3809:494;3811:498;3811:498;3811:498;3813:503;3813:503;3814:506;3814:506;3815:509;3815:509;3817:513;3817:513;3818:516;3818:516;3819:519;3819:519;3821:523;3821:523;3823:527;3823:527;3824:530;3824:530;3825:533;3826:534;3826:534;3828:539;3828:539;3829:541;3829:541;3831:546;3831:546;3833:550;3833:550;3834:553;3835:554;3835:554;3836:557;3837:558;3837:558;3838:561;3839:563;3839:563;3842:568;3842:568;3843:571;3844:573;3846:575;3846:575;3848:580;3848:580;3849:582;3851:585;3851:585;3852:588;3854:591;3855:592;3855:592;3857:595;3857:595;3859:599;3859:599;3861:604;3863:607;3865:609;3865:609;3868:615;3868:615;3870:618;3870:618;3874:625;3874:625;3875:628;3877:629;3877:629;3879:632;3879:632;3881:637;3886:644;3887:645;3887:645;3889:648;3890:649;3890:649;3892:654;3893:655;3894:657;3896:660;3899:664;3899:664;3901:667;3901:667;3903:671;3904:672;3907:677;3909:680;3911:683;3913:685;3914:687;3918:693;3919:694;3919:694;3922:700;3924:703;3926:706;3929:710;3931:713;3931:713;3933:716;3933:716;3935:720;3936:721;3937:723;3938:724;3941:729;3942:730;3943:732;3944:733;3946:736;3948:739;3950:742;3951:743;3953:746;3956:751;3957:752;3961:758;3961:758;3962:761;3964:762;3966:765;3967:767;3967:767;3969:770;3971:773;3973:775;3974:777;3975:778;3976:780;3977:781;3978:783;3980:786;3982:789;3983:790;3986:795;3987:796;3989:799;3991:800;3991:800;3993:803;3993:803;3995:806;4000:814;4001:815;4004:820;4005:821;4007:823;4008:824;4009:825;4011:828;4013:831;4015:833;4018:837;4019:839;4021:840;4021:840;4022:843;4024:845;4026:848;4027:849;4028:851;4030:852;4031:854;4033:857;4034:858;4036:860;4036:860;4038:863;4040:866;4042:867;4044:870;4045:872;4046:873;4048:874;4052:880;4054:882;4056:885;4057:886;4059:888;4061:891;4062:892;4064:894;4067:898;4069:900;4070:901;4071:903;4073:904;4074:906;4075:907;4077:910;4079:912;4081:915;4082:916;4086:919;4088:922;4090:924;4091:925;4092:927;4094:928;4096:932;4098:933;4099:935;4100:936;4102:938;4103:939;4105:941;4106:942;4108:944;4110:947;4112:948;4113:950;4115:951;4117:954;4121:957;4122:959;4124:960;4125:962;4127:963;4128:965;4130:966;4131:968;4135:971;4136:972;4138:974;4139:976;4143:979;4144:980;4146:982;4148:983;4152:986;4153:988;4156:989;4157:991;4163:994;4166:995;4172:997;4186:1000";
        public const string Charge = "3271:2;3668:3;3786:5;3859:7;3908:8;3929:10;3942:12;3954:13;3965:15;3976:17;3988:18;3998:20;4007:22;4015:23;4023:25;4029:27;4036:28;4042:30;4048:32;4054:33;4061:35;4066:37;4073:38;4079:40;4085:42;4091:43;4098:45;4104:47;4111:48;4118:50;4125:52;4132:53;4139:55;4147:57;4155:58;4163:60;4172:62;4181:63;4190:65;4197:67;4200:68;4200:70;4200:71;4200:73;4200:74;4200:76;4200:77;4200:78;4200:80;4200:81;4200:82;4200:83;4200:84;4200:85;4200:86;4200:87;4200:88;4200:88;4200:89;4200:90;4200:91;4200:91;4200:92;4200:93;4200:93;4200:94;4200:94;4200:95;4200:95;4200:96;4200:96;4200:97;4200:97;4200:97;4200:98;4200:98;4200:98;4200:99;4200:99;4200:99;4200:100;4200:100;4200:100";
        public const string Slots = "1:66;2:68;3:70;5:72;8:74;11:75;14:76;18:79;20:80;23:81;25:82;28:83;30:85;32:86;34:87;36:88;38:89;40:90;44:92;50:93;55:94;60:95;68:96;75:97;80:98;88:99";

        // 旧的通用锂电曲线：1.4.0 用的就是这条，留在这里只为并排对比。
        private static readonly double[] GenericU = { 0.0, 0.11, 0.22, 0.33, 0.44, 0.56, 0.67, 0.78, 0.89, 1.0 };
        private static readonly double[] GenericP = { 0, 5, 10, 20, 35, 50, 65, 80, 90, 100 };

        public static double[] DisMv;
        public static double[] DisPct;
        public static double[] ChgMv;
        public static double[] ChgPct;
        public static double[] SlotMin;
        public static double[] SlotPct;

        public static void Load()
        {
            Parse(Discharge, 10.0, out DisMv, out DisPct);
            Parse(Charge, 1.0, out ChgMv, out ChgPct);
            Parse(Slots, 1.0, out SlotMin, out SlotPct);
        }

        private static void Parse(string encoded, double divisor, out double[] x, out double[] y)
        {
            string[] parts = encoded.Split(';');
            List<double> xs = new List<double>(parts.Length);
            List<double> ys = new List<double>(parts.Length);
            for (int i = 0; i < parts.Length; i++)
            {
                int c = parts[i].IndexOf(':');
                if (c <= 0) continue;
                double a, b;
                if (!double.TryParse(parts[i].Substring(0, c), NumberStyles.Float, CultureInfo.InvariantCulture, out a)) continue;
                if (!double.TryParse(parts[i].Substring(c + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out b)) continue;
                xs.Add(a);
                ys.Add(b / divisor);
            }
            x = xs.ToArray();
            y = ys.ToArray();
        }

        /// <summary>放电：电压 → 剩余电量（0..100）。</summary>
        public static double SocFromVoltage(double mv)
        {
            return Interp(DisMv, DisPct, mv);
        }

        /// <summary>充电曲线：电压 → 百分比。</summary>
        public static double SocFromChargeVoltage(double mv)
        {
            return Interp(ChgMv, ChgPct, mv);
        }

        /// <summary>充电时间表：进入平台区后的分钟数 → 百分比。</summary>
        public static double SocFromChargeMinutes(double minutes)
        {
            return Interp(SlotMin, SlotPct, minutes);
        }

        /// <summary>1.4.0 的通用曲线，用于对比（满 4200 / 空 3300）。</summary>
        public static int LegacyPercent(double mv, double fullMv, double emptyMv)
        {
            double u = (mv - emptyMv) / (fullMv - emptyMv);
            if (u < 0) u = 0;
            if (u > 1) u = 1;
            double pct = 100;
            for (int i = 0; i < GenericU.Length - 1; i++)
            {
                if (u <= GenericU[i + 1])
                {
                    double span = GenericU[i + 1] - GenericU[i];
                    double t = span <= 0 ? 0 : (u - GenericU[i]) / span;
                    pct = GenericP[i] + (GenericP[i + 1] - GenericP[i]) * t;
                    break;
                }
            }
            return (int)Math.Round(pct);
        }

        /// <summary>升序数组的分段线性插值，端点外夹取。</summary>
        public static double Interp(double[] xs, double[] ys, double x)
        {
            if (xs == null || xs.Length < 2) return 0;
            int n = xs.Length;
            if (x <= xs[0]) return ys[0];
            if (x >= xs[n - 1]) return ys[n - 1];
            for (int i = 1; i < n; i++)
            {
                if (xs[i] >= x)
                {
                    double span = xs[i] - xs[i - 1];
                    if (span <= 0) return ys[i];
                    double t = (x - xs[i - 1]) / span;
                    return ys[i - 1] + (ys[i] - ys[i - 1]) * t;
                }
            }
            return ys[n - 1];
        }
    }

    // ======================================================================
    // 2. 显示平滑（对应 GpwBattery.Feed 的放电分支）
    //    中值(3) → EMA(0.25) → 滞回 2% → 连续两次偏离才刷新，放电只降不升
    // ======================================================================
    internal sealed class DisplayState
    {
        private const int MedianWindow = 3;
        private const double Alpha = 0.25;
        private const double Hysteresis = 2.0;
        private const double RiseTolerance = 50.0;

        private readonly List<double> _window = new List<double>();
        private double _ema = -1;
        private int _pending = -1;
        private int _pendingCount;
        private double _lowMv = -1;
        private int _riseCount;

        public int Displayed = -1;
        public double SmoothedMv;
        public DateTime StableSinceUtc = DateTime.MinValue;

        public void Reset()
        {
            _window.Clear();
            _ema = -1;
            _pending = -1;
            _pendingCount = 0;
            _lowMv = -1;
            _riseCount = 0;
            StableSinceUtc = DateTime.MinValue;
        }

        /// <summary>放电分支：返回应当显示的百分比；-1 表示暂无。</summary>
        public int FeedDischarge(double mv)
        {
            _window.Add(mv);
            while (_window.Count > MedianWindow) _window.RemoveAt(0);
            double median = Median(_window);

            if (_ema < 0) _ema = median;
            else _ema = _ema + Alpha * (median - _ema);
            SmoothedMv = _ema;

            int candidate = (int)Math.Round(Model.SocFromVoltage(_ema));
            if (candidate < 0) candidate = 0;
            if (candidate > 100) candidate = 100;

            if (Displayed < 0)
            {
                Displayed = candidate;
                if (_lowMv < 0 || mv < _lowMv) _lowMv = mv;
                return candidate;
            }

            if (_lowMv < 0 || mv < _lowMv) _lowMv = mv;
            if (candidate > Displayed)
            {
                double rise = mv - _lowMv;
                if (rise >= RiseTolerance)
                {
                    _riseCount++;
                    if (_riseCount >= 2) { _riseCount = 0; _lowMv = mv; }
                    else return Displayed;
                }
                else
                {
                    _riseCount = 0;
                    return Displayed;
                }
            }

            int delta = Math.Abs(candidate - Displayed);
            if (delta < Hysteresis)
            {
                _pending = -1;
                _pendingCount = 0;
                return Displayed;
            }

            if (_pending >= 0 && Math.Abs(candidate - _pending) <= 1) _pendingCount++;
            else { _pending = candidate; _pendingCount = 1; }

            if (_pendingCount >= 2)
            {
                Displayed = candidate;
                _pending = -1;
                _pendingCount = 0;
            }
            return Displayed;
        }

        /// <summary>充电分支：平台区以下用充电曲线，平台区以上用时间表 + gap fill 限速。</summary>
        public int FeedCharge(double mv, DateTime now)
        {
            double pct;
            if (mv >= Model.StableMv && Model.SlotMin.Length > 0)
            {
                // 没有放电基准（例如插着电启动）时平台区电压没有信息量，不编造数字，和应用一致。
                if (Displayed < 0) return -1;
                if (StableSinceUtc == DateTime.MinValue) StableSinceUtc = now;
                double minutes = (now - StableSinceUtc).TotalMinutes;
                if (minutes < 0) minutes = 0;
                double byTime = Model.SocFromChargeMinutes(minutes);
                if (Model.GapFill > 0)
                {
                    double basePct = Displayed;
                    pct = Math.Min(byTime, basePct + Model.GapFill * (minutes / 0.5));
                }
                else pct = byTime;
            }
            else
            {
                StableSinceUtc = DateTime.MinValue;
                pct = Model.SocFromChargeVoltage(mv);
            }

            int v = (int)Math.Round(pct);
            if (v < 0) v = 0;
            if (v > 100) v = 100;
            if (v > Displayed) Displayed = v;
            return Displayed;
        }

        private static double Median(List<double> values)
        {
            int n = values.Count;
            if (n == 0) return 0;
            double[] copy = values.ToArray();
            Array.Sort(copy);
            return copy[n / 2];
        }
    }

    // ======================================================================
    // 3. HID 底层（hid.dll / setupapi.dll），重叠 I/O + 超时
    // ======================================================================
    internal static class Hid
    {
        public const int DIGCF_PRESENT = 0x02;
        public const int DIGCF_DEVICEINTERFACE = 0x10;
        public const uint GENERIC_READ = 0x80000000;
        public const uint GENERIC_WRITE = 0x40000000;
        public const uint FILE_SHARE_READ = 0x1;
        public const uint FILE_SHARE_WRITE = 0x2;
        public const uint OPEN_EXISTING = 3;
        public const uint FILE_FLAG_OVERLAPPED = 0x40000000;
        public const int ERROR_IO_PENDING = 997;
        public const uint WAIT_OBJECT_0 = 0;

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDD_ATTRIBUTES
        {
            public int Size;
            public ushort VendorID;
            public ushort ProductID;
            public ushort VersionNumber;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct OVERLAPPED
        {
            public IntPtr Internal;
            public IntPtr InternalHigh;
            public int Offset;
            public int OffsetHigh;
            public IntPtr hEvent;
        }

        [DllImport("hid.dll")] public static extern void HidD_GetHidGuid(out Guid guid);
        [DllImport("hid.dll")] public static extern bool HidD_GetAttributes(IntPtr handle, ref HIDD_ATTRIBUTES attrs);
        [DllImport("hid.dll")] public static extern bool HidD_GetPreparsedData(IntPtr handle, out IntPtr preparsed);
        [DllImport("hid.dll")] public static extern bool HidD_FreePreparsedData(IntPtr preparsed);
        [DllImport("hid.dll")] public static extern int HidP_GetCaps(IntPtr preparsed, out HIDP_CAPS caps);

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDP_CAPS
        {
            public ushort Usage;
            public ushort UsagePage;
            public ushort InputReportByteLength;
            public ushort OutputReportByteLength;
            public ushort FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes;
            public ushort NumberInputButtonCaps;
            public ushort NumberInputValueCaps;
            public ushort NumberInputDataIndices;
            public ushort NumberOutputButtonCaps;
            public ushort NumberOutputValueCaps;
            public ushort NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps;
            public ushort NumberFeatureValueCaps;
            public ushort NumberFeatureDataIndices;
        }

        [DllImport("setupapi.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        public static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, int flags);
        [DllImport("setupapi.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        public static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid, int memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);
        [DllImport("setupapi.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        public static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, IntPtr deviceInterfaceDetailData, int deviceInterfaceDetailDataSize, out int requiredSize, IntPtr deviceInfoData);
        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern IntPtr CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool WriteFile(IntPtr handle, byte[] buffer, int numberOfBytesToWrite, out int bytesWritten, ref OVERLAPPED overlapped);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ReadFile(IntPtr handle, byte[] buffer, int numberOfBytesToRead, out int bytesRead, ref OVERLAPPED overlapped);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetOverlappedResult(IntPtr handle, ref OVERLAPPED overlapped, out int bytesTransferred, bool wait);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr CreateEvent(IntPtr securityAttributes, bool manualReset, bool initialState, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CancelIo(IntPtr handle);

        /// <summary>调试用：枚举过程中的关键步骤，--debug 时打印。</summary>
        public static readonly List<string> Log = new List<string>();

        public sealed class Interface
        {
            public string Path;
            public ushort Vid;
            public ushort Pid;
            public int InputLen;
            public int OutputLen;
        }

        /// <summary>枚举指定 VID 下所有 HID 接口（含接收器的多个集合）。</summary>
        public static List<Interface> Enumerate(ushort vid)
        {
            List<Interface> list = new List<Interface>();
            Guid guid;
            HidD_GetHidGuid(out guid);
            IntPtr set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            Log.Add("SetupDiGetClassDevs -> " + set.ToInt64() + " (err=" + Marshal.GetLastWin32Error() + ")");
            if (set == new IntPtr(-1)) return list;

            try
            {
                SP_DEVICE_INTERFACE_DATA did = new SP_DEVICE_INTERFACE_DATA();
                did.cbSize = Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA));
                for (int i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref did); i++)
                {
                    int need = 0;
                    SetupDiGetDeviceInterfaceDetail(set, ref did, IntPtr.Zero, 0, out need, IntPtr.Zero);
                    if (need <= 0) continue;
                    IntPtr buf = Marshal.AllocHGlobal(need);
                    try
                    {
                        // SP_DEVICE_INTERFACE_DETAIL_DATA.cbSize：x64 为 8，x86 为 6；路径从偏移 4 开始。
                        Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                        int got;
                        if (!SetupDiGetDeviceInterfaceDetail(set, ref did, buf, need, out got, IntPtr.Zero)) continue;
                        string path = Marshal.PtrToStringAnsi(new IntPtr(buf.ToInt64() + 4));
                        Interface it = Describe(path);
                        Log.Add("  device[" + i + "] " + (it == null ? "open/failed" : ("VID_" + it.Vid.ToString("X4") + " PID_" + it.Pid.ToString("X4") + " in=" + it.InputLen + " out=" + it.OutputLen)) + "  " + path);
                        if (it != null && it.Vid == vid) list.Add(it);
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return list;
        }

        private static Interface Describe(string path)
        {
            IntPtr h = CreateFile(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h == new IntPtr(-1)) { Log.Add("    CreateFile failed err=" + Marshal.GetLastWin32Error()); return null; }
            try
            {
                HIDD_ATTRIBUTES attrs = new HIDD_ATTRIBUTES();
                attrs.Size = Marshal.SizeOf(typeof(HIDD_ATTRIBUTES));
                if (!HidD_GetAttributes(h, ref attrs)) return null;

                Interface it = new Interface();
                it.Path = path;
                it.Vid = attrs.VendorID;
                it.Pid = attrs.ProductID;

                IntPtr preparsed;
                if (HidD_GetPreparsedData(h, out preparsed))
                {
                    try
                    {
                        HIDP_CAPS caps;
                        // HidP_GetCaps 返回 NTSTATUS：HIDP_STATUS_SUCCESS = 0x00110000，判 <0 才是失败。
                        if (HidP_GetCaps(preparsed, out caps) >= 0)
                        {
                            it.InputLen = caps.InputReportByteLength;
                            it.OutputLen = caps.OutputReportByteLength;
                        }
                    }
                    finally { HidD_FreePreparsedData(preparsed); }
                }
                return it;
            }
            finally { CloseHandle(h); }
        }
    }

    // ======================================================================
    // 4. HID++ 2.0 通信
    // ======================================================================
    internal sealed class HidppLink : IDisposable
    {
        public const int FeatureRoot = 0x0000;
        public const int FeatureDeviceInfo = 0x0003;
        public const int FeatureBatteryVoltage = 0x1001;
        public const int FeatureUnifiedBattery = 0x1004;

        private readonly string _path;
        private IntPtr _handle = new IntPtr(-1);
        private IntPtr _event;
        private int _swId;
        private readonly int _inLen;
        private readonly int _outLen;

        public readonly bool UsingLongReport;
        private int _deviceIndex;

        public HidppLink(Hid.Interface it, int deviceIndex)
        {
            _path = it.Path;
            _inLen = it.InputLen > 0 ? it.InputLen : 20;
            _outLen = it.OutputLen > 0 ? it.OutputLen : 20;
            _deviceIndex = deviceIndex;
            UsingLongReport = _outLen >= 20;

            _handle = Hid.CreateFile(_path, Hid.GENERIC_READ | Hid.GENERIC_WRITE,
                Hid.FILE_SHARE_READ | Hid.FILE_SHARE_WRITE, IntPtr.Zero, Hid.OPEN_EXISTING, Hid.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            _event = Hid.CreateEvent(IntPtr.Zero, true, false, null);
        }

        /// <summary>接收器背后的设备槽位（1..6，0xFF 为接收器自身）；同一句柄上可切换。</summary>
        public int DeviceIndex
        {
            get { return _deviceIndex; }
            set { _deviceIndex = value; }
        }

        public bool IsOpen { get { return _handle != new IntPtr(-1); } }

        public byte ReportId { get { return UsingLongReport ? (byte)0x11 : (byte)0x10; } }

        /// <summary>发一条请求并等应答；超时返回 null。parameters 可为 null。</summary>
        public byte[] Call(int featureIndex, int function, byte[] parameters, int timeoutMs)
        {
            if (!IsOpen) return null;

            _swId++;
            if (_swId > 15) _swId = 1;
            byte address = (byte)(((function & 0x0F) << 4) | _swId);

            byte[] req = new byte[_outLen];
            req[0] = ReportId;
            req[1] = (byte)_deviceIndex;
            req[2] = (byte)featureIndex;
            req[3] = address;
            if (parameters != null)
            {
                for (int i = 0; i < parameters.Length && 4 + i < _outLen; i++) req[4 + i] = parameters[i];
            }

            // 不预先清空输入队列：软件号每轮自增，旧应答的地址字节对不上，会被下面的过滤条件丢掉。
            if (!Write(req, 500)) return null;

            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                int left = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                if (left <= 0) break;
                byte[] r = Read(left > 60 ? left : 60);
                if (r == null) break;
                if (r.Length >= 4 && r[0] == req[0] && r[1] == req[1] && r[2] == req[2]
                    && (r[3] == address || (r[3] & 0x80) != 0))
                    return r;
            }
            return null;
        }

        /// <summary>0x0000 的 getFeature：返回特性索引，0 表示设备没有这个特性。</summary>
        public int GetFeatureIndex(int featureId)
        {
            byte[] p = new byte[] { (byte)(featureId >> 8), (byte)(featureId & 0xFF) };
            byte[] r = Call(0x00, 0x00, p, 400);
            if (r == null || r.Length < 7) return 0;
            if ((r[3] & 0x80) != 0) return 0;
            return r[4];
        }

        /// <summary>0x0003 function 0：设备 PID（modelId）。</summary>
        public int ReadModelId()
        {
            int idx = GetFeatureIndex(FeatureDeviceInfo);
            if (idx <= 0) return -1;
            byte[] r = Call(idx, 0x00, null, 400);
            Hid.Log.Add("0x0003 idx=" + idx + " reply=" + Hex(r));
            if (r == null || r.Length < 8) return -1;

            // 规范位置（v0）：entityCnt, unitId|transport, modelId(BE), extendedModelId(BE), caps
            int primary = (r[6] << 8) + r[7];
            Hid.Log.Add("  modelId(r6,r7)=0x" + primary.ToString("X4"));
            if (primary == Model.Pid) return primary;

            // 新固件（0x0003 v4）在 capabilities 之后追加了无线/有线 Product ID，
            // 规范位拿到的可能不是设备 PID；这里在整段载荷里找能对上的那个。
            for (int i = 4; i + 1 < r.Length; i++)
            {
                int v = (r[i] << 8) + r[i + 1];
                if (v == Model.Pid) { Hid.Log.Add("  命中 Product ID @byte" + i + "=0x" + v.ToString("X4")); return v; }
            }
            return primary >= 0x1000 ? primary : -1;
        }

        /// <summary>0x1001 function 0：返回电压 mV 与是否外部供电。</summary>
        public bool ReadVoltage(int featureIndex, out int mv, out bool externalPower)
        {
            mv = 0;
            externalPower = false;
            byte[] r = Call(featureIndex, 0x00, null, 400);
            if (r == null || r.Length < 6) return false;
            mv = (r[4] << 8) + r[5];
            if (mv < 1000 || mv > 6000) return false;
            if (r.Length > 6) externalPower = (r[6] & 0x80) != 0;
            return true;
        }

        private static string Hex(byte[] b)
        {
            if (b == null) return "null";
            StringBuilder sb = new StringBuilder(b.Length * 3);
            for (int i = 0; i < b.Length; i++) sb.Append(b[i].ToString("X2")).Append(' ');
            return sb.ToString();
        }

        private bool Write(byte[] buf, int timeoutMs)
        {
            Hid.OVERLAPPED ov = new Hid.OVERLAPPED();
            ov.hEvent = _event;
            int written;
            if (Hid.WriteFile(_handle, buf, buf.Length, out written, ref ov)) return true;
            if (Marshal.GetLastWin32Error() != Hid.ERROR_IO_PENDING) return false;
            if (Hid.WaitForSingleObject(_event, (uint)timeoutMs) != Hid.WAIT_OBJECT_0)
            {
                Hid.CancelIo(_handle);
                int junk;
                // 等取消真正落地，否则事件残留会让下一次等待立刻返回。
                Hid.GetOverlappedResult(_handle, ref ov, out junk, true);
                return false;
            }
            return Hid.GetOverlappedResult(_handle, ref ov, out written, false);
        }

        private byte[] Read(int timeoutMs)
        {
            byte[] buf = new byte[_inLen];
            Hid.OVERLAPPED ov = new Hid.OVERLAPPED();
            ov.hEvent = _event;
            int read;
            if (!Hid.ReadFile(_handle, buf, buf.Length, out read, ref ov))
            {
                if (Marshal.GetLastWin32Error() != Hid.ERROR_IO_PENDING) return null;
                if (Hid.WaitForSingleObject(_event, (uint)timeoutMs) != Hid.WAIT_OBJECT_0)
                {
                    Hid.CancelIo(_handle);
                    int junk;
                    Hid.GetOverlappedResult(_handle, ref ov, out junk, true);
                    return null;
                }
                if (!Hid.GetOverlappedResult(_handle, ref ov, out read, false)) return null;
            }
            if (read <= 0) return null;
            byte[] r = new byte[read];
            Array.Copy(buf, r, read);
            return r;
        }

        public void Dispose()
        {
            if (_handle != new IntPtr(-1)) { Hid.CloseHandle(_handle); _handle = new IntPtr(-1); }
            if (_event != IntPtr.Zero) { Hid.CloseHandle(_event); _event = IntPtr.Zero; }
        }
    }

    // ======================================================================
    // 5. 主流程
    // ======================================================================
    internal static class Program
    {
        private const ushort LogitechVid = 0x046D;
        private static bool _debug;

        private static int Main(string[] args)
        {
            bool watch = false, json = false;
            int intervalSec = 5;
            int count = 1;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                if (a == "--watch") watch = true;
                else if (a == "--debug") _debug = true;
                else if (a == "--json") json = true;
                else if (a == "--interval" && i + 1 < args.Length) { int.TryParse(args[++i], out intervalSec); }
                else if (a == "--count" && i + 1 < args.Length) { int.TryParse(args[++i], out count); }
                else if (a == "--help" || a == "-h") { PrintUsage(); return 0; }
                else { Console.Error.WriteLine("未知参数: " + args[i]); PrintUsage(); return 2; }
            }
            if (intervalSec < 1) intervalSec = 1;
            if (count < 1) count = 1;
            if (!watch) count = 1;

            try { Console.OutputEncoding = Encoding.UTF8; }
            catch { }

            Model.Load();

            DisplayState display = new DisplayState();
            bool firstRound = true;
            bool lastExternal = false;
            int rounds = 0;

            while (rounds < count)
            {
                rounds++;
                Sample s = ReadOnce();
                if (_debug)
                {
                    for (int k = 0; k < Hid.Log.Count; k++) Console.WriteLine("[dbg] " + Hid.Log[k]);
                }
                if (s == null)
                {
                    if (!json) Console.WriteLine("未找到设备：请把 Lightspeed 接收器插在 USB 口上，并确认鼠标已开机。");
                    return 1;
                }

                // 电源状态切换时清空滤波历史（拔插线后表面电荷会造成跳变）
                if (!firstRound && s.ExternalPower != lastExternal) display.Reset();
                lastExternal = s.ExternalPower;

                int shown;
                if (s.ExternalPower) shown = display.FeedCharge(s.VoltageMv, s.When);
                else shown = display.FeedDischarge(s.VoltageMv);

                if (json) Console.WriteLine(ToJson(s, shown, display));
                else PrintHuman(s, shown, display, firstRound);

                firstRound = false;

                if (rounds < count)
                {
                    if (!json) Console.WriteLine();
                    Thread.Sleep(intervalSec * 1000);
                }
            }
            return 0;
        }

        private static void PrintUsage()
        {
            Console.WriteLine("GpwBatteryProbe —— GPW 电量读取与换算验证");
            Console.WriteLine("  --watch              持续刷新（默认 5 秒一轮）");
            Console.WriteLine("  --interval <秒>      刷新间隔，默认 5");
            Console.WriteLine("  --count <次数>       采样轮数，默认 1");
            Console.WriteLine("  --json               输出单行 JSON");
            Console.WriteLine("  --debug              打印枚举与逐槽位探测细节");
            Console.WriteLine("  --help               显示本帮助");
        }

        private sealed class Sample
        {
            public int DeviceIndex;
            public int Pid;
            public int VoltageMv;
            public bool ExternalPower;
            public bool HasUnifiedBattery;
            public DateTime When;
            public ushort ReceiverPid;
        }

        private static Sample ReadOnce()
        {
            Hid.Log.Clear();
            List<Hid.Interface> ifaces = Hid.Enumerate(LogitechVid);
            if (ifaces.Count == 0) return null;

            // 长报文（20 字节）优先：HID++ 的长报文才能承载特性参数
            ifaces.Sort(delegate(Hid.Interface a, Hid.Interface b)
            {
                return b.OutputLen.CompareTo(a.OutputLen);
            });

            for (int i = 0; i < ifaces.Count; i++)
            {
                Hid.Interface it = ifaces[i];
                if (it.OutputLen < 7) continue;

                // 同一个接口只开一次句柄，靠切换设备槽位去找配对设备（反复开同一路径会互相干扰）。
                using (HidppLink link = new HidppLink(it, 1))
                {
                    if (!link.IsOpen)
                    {
                        if (_debug) Console.WriteLine("[dbg] 打开失败: " + it.Path);
                        continue;
                    }

                    // 槽位顺序：先试 1..6（接收器上配对的设备），最后才试 0xFF（接收器自身）。
                    int[] order = new int[] { 1, 2, 3, 4, 5, 6, 0xFF };
                    for (int slot = 0; slot < order.Length; slot++)
                    {
                        link.DeviceIndex = order[slot];
                        int voltFeature = link.GetFeatureIndex(HidppLink.FeatureBatteryVoltage);
                        if (_debug)
                            Console.WriteLine("[dbg] out=" + it.OutputLen + " slot=0x" + order[slot].ToString("X2")
                                + " 0x1001 idx=" + voltFeature);
                        if (voltFeature <= 0) continue;

                        int mv;
                        bool ext;
                        if (!link.ReadVoltage(voltFeature, out mv, out ext)) continue;

                        Sample s = new Sample();
                        s.DeviceIndex = order[slot];
                        s.VoltageMv = mv;
                        s.ExternalPower = ext;
                        s.When = DateTime.Now;
                        s.ReceiverPid = it.Pid;
                        s.Pid = link.ReadModelId();
                        s.HasUnifiedBattery = link.GetFeatureIndex(HidppLink.FeatureUnifiedBattery) > 0;
                        return s;
                    }
                }
            }
            return null;
        }

        private static void PrintHuman(Sample s, int shown, DisplayState display, bool first)
        {
            if (first)
            {
                Console.WriteLine("GpwBatteryProbe · 模型 " + Model.Name + " (0x" + Model.Pid.ToString("X4") + ")");
                Console.WriteLine(new string('-', 72));
            }

            string pidText = s.Pid > 0 ? "0x" + s.Pid.ToString("X4") : "未读到";
            bool modelHit = s.Pid == Model.Pid;

            Console.WriteLine("时间        " + s.When.ToString("HH:mm:ss"));
            Console.WriteLine("接收器      " + "VID_046D&PID_" + s.ReceiverPid.ToString("X4") + "  设备槽位 " + s.DeviceIndex);
            Console.WriteLine("设备 PID    " + pidText + (modelHit ? "  → 命中设备曲线" : "  → 未命中，回落到通用曲线"));
            Console.WriteLine("电压        " + s.VoltageMv + " mV" + (s.ExternalPower ? "   外部供电（充电中）" : "   放电中"));
            Console.WriteLine();

            if (modelHit)
            {
                double raw = Model.SocFromVoltage(s.VoltageMv);
                Console.WriteLine("曲线原始值  " + raw.ToString("0.0", CultureInfo.InvariantCulture) + "%   （设备曲线，与 G HUB 同源）");
            }
            Console.WriteLine("通用曲线     " + Model.LegacyPercent(s.VoltageMv, 4200, 3300) + "%   （1.4.0 的旧口径，仅对比用）");
            Console.WriteLine("显示值      " + (shown < 0 ? "—" : shown + "%") + "   （滤波后，与实际卡片一致）");
            if (display.SmoothedMv > 0 && !s.ExternalPower)
                Console.WriteLine("平滑电压    " + display.SmoothedMv.ToString("0", CultureInfo.InvariantCulture) + " mV");
            if (s.HasUnifiedBattery)
                Console.WriteLine("提示        该设备还提供 0x1004 统一电池特性，可直接取百分比（本工具只用 0x1001 做对照）");
        }

        private static string ToJson(Sample s, int shown, DisplayState display)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("{\"time\":\"").Append(s.When.ToString("HH:mm:ss")).Append("\"");
            sb.Append(",\"receiverPid\":\"0x").Append(s.ReceiverPid.ToString("X4")).Append("\"");
            sb.Append(",\"deviceIndex\":").Append(s.DeviceIndex);
            sb.Append(",\"devicePid\":").Append(s.Pid > 0 ? s.Pid : 0);
            sb.Append(",\"voltageMv\":").Append(s.VoltageMv);
            sb.Append(",\"externalPower\":").Append(s.ExternalPower ? "true" : "false");
            sb.Append(",\"socDeviceCurve\":").Append(Model.SocFromVoltage(s.VoltageMv).ToString("0.0", CultureInfo.InvariantCulture));
            sb.Append(",\"socLegacyCurve\":").Append(Model.LegacyPercent(s.VoltageMv, 4200, 3300));
            sb.Append(",\"shown\":").Append(shown);
            sb.Append(",\"smoothedMv\":").Append(display.SmoothedMv.ToString("0", CultureInfo.InvariantCulture));
            sb.Append("}");
            return sb.ToString();
        }
    }
}
