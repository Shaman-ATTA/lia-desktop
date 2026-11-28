using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace LiaDesktop.Services;

/// <summary>Host-wide CPU / RAM / net / ping — not Lia process counters.</summary>
public sealed class SystemTelemetry
{
    readonly object _gate = new();
    ulong _idle0, _kern0, _user0;
    bool _cpuSeeded;
    long _netRecv0, _netSent0;
    DateTime _netSampleUtc = DateTime.MinValue;
    double _downMbps, _upMbps;
    int _pingMs = -1;
    DateTime _lastPingUtc = DateTime.MinValue;
    readonly Process _self = Process.GetCurrentProcess();
    TimeSpan _selfCpu;
    DateTime _selfSampleUtc = DateTime.MinValue;
    double _liaCpuPct;

    public double SystemCpuPct { get; private set; }
    public double GpuPct { get; private set; } = -1; // -1 = unavailable
    public double RamUsedGb { get; private set; }
    public double RamTotalGb { get; private set; }
    public double DownMbps => _downMbps;
    public double UpMbps => _upMbps;
    public int PingMs => _pingMs;
    public double LiaCpuPct => _liaCpuPct;
    public double LiaRamMb { get; private set; }

    public void Sample()
    {
        SampleCpu();
        SampleRam();
        SampleNet();
        SampleLia();
        SampleGpuPdh();
        _ = SamplePingAsync();
    }

    void SampleCpu()
    {
        if (!GetSystemTimes(out var idle, out var kern, out var user)) return;
        var idleT = FileTimeToUlong(idle);
        var kernT = FileTimeToUlong(kern);
        var userT = FileTimeToUlong(user);
        lock (_gate)
        {
            if (!_cpuSeeded)
            {
                _idle0 = idleT; _kern0 = kernT; _user0 = userT;
                _cpuSeeded = true;
                return;
            }
            var di = idleT - _idle0;
            var dk = kernT - _kern0;
            var du = userT - _user0;
            _idle0 = idleT; _kern0 = kernT; _user0 = userT;
            var total = dk + du;
            if (total == 0) return;
            // Kernel includes idle time on Windows — busy = (kern+user-idle)/ (kern+user)
            var busy = total > di ? total - di : 0;
            SystemCpuPct = Math.Clamp(100.0 * busy / total, 0, 100);
        }
    }

    void SampleRam()
    {
        var st = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref st)) return;
        RamTotalGb = st.ullTotalPhys / (1024.0 * 1024 * 1024);
        var used = st.ullTotalPhys - st.ullAvailPhys;
        RamUsedGb = used / (1024.0 * 1024 * 1024);
    }

    void SampleNet()
    {
        long recv = 0, sent = 0;
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;
                var stats = ni.GetIPStatistics();
                recv += stats.BytesReceived;
                sent += stats.BytesSent;
            }
        }
        catch { return; }

        var now = DateTime.UtcNow;
        if (_netSampleUtc != DateTime.MinValue)
        {
            var dt = (now - _netSampleUtc).TotalSeconds;
            if (dt >= 0.4)
            {
                var dRecv = Math.Max(0, recv - _netRecv0);
                var dSent = Math.Max(0, sent - _netSent0);
                _downMbps = dRecv * 8.0 / dt / 1_000_000.0;
                _upMbps = dSent * 8.0 / dt / 1_000_000.0;
            }
        }
        _netRecv0 = recv;
        _netSent0 = sent;
        _netSampleUtc = now;
    }

    void SampleLia()
    {
        try
        {
            _self.Refresh();
            LiaRamMb = _self.WorkingSet64 / (1024.0 * 1024.0);
            var t = _self.TotalProcessorTime;
            var now = DateTime.UtcNow;
            if (_selfSampleUtc != DateTime.MinValue)
            {
                var wall = (now - _selfSampleUtc).TotalMilliseconds;
                if (wall > 400)
                {
                    var cpuDt = (t - _selfCpu).TotalMilliseconds;
                    var cores = Math.Max(1, Environment.ProcessorCount);
                    _liaCpuPct = Math.Clamp(cpuDt / wall / cores * 100.0, 0, 100);
                }
            }
            _selfCpu = t;
            _selfSampleUtc = now;
        }
        catch { /* */ }
    }

    async Task SamplePingAsync()
    {
        if ((DateTime.UtcNow - _lastPingUtc).TotalSeconds < 4) return;
        _lastPingUtc = DateTime.UtcNow;
        try
        {
            using var p = new Ping();
            var reply = await p.SendPingAsync("1.1.1.1", 800);
            if (reply.Status == IPStatus.Success)
                _pingMs = (int)reply.RoundtripTime;
        }
        catch
        {
            try
            {
                using var p = new Ping();
                var reply = await p.SendPingAsync("8.8.8.8", 800);
                if (reply.Status == IPStatus.Success)
                    _pingMs = (int)reply.RoundtripTime;
            }
            catch { /* leave previous */ }
        }
    }

    /// <summary>Best-effort GPU via PDH «\GPU Engine(*)\Utilization Percentage» — may stay -1 on WARP/CI.</summary>
    void SampleGpuPdh()
    {
        try
        {
            // Lightweight: skip PDH counter open each tick — use previously cached or probe once.
            if (_gpuProbeFailed) return;
            if (_gpuQuery == IntPtr.Zero)
            {
                if (PdhOpenQuery(null, IntPtr.Zero, out _gpuQuery) != 0)
                {
                    _gpuProbeFailed = true;
                    return;
                }
                // Aggregate all engines
                if (PdhAddEnglishCounter(_gpuQuery, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _gpuCounter) != 0)
                {
                    PdhCloseQuery(_gpuQuery);
                    _gpuQuery = IntPtr.Zero;
                    _gpuProbeFailed = true;
                    return;
                }
                PdhCollectQueryData(_gpuQuery); // prime
                return;
            }
            if (PdhCollectQueryData(_gpuQuery) != 0) return;
            var size = 0;
            var count = 0;
            var status = PdhGetFormattedCounterArray(_gpuCounter, PdhFmtDouble, ref size, ref count, IntPtr.Zero);
            if (status != 0x800007D2 /* PDH_MORE_DATA */ && status != 0) return;
            if (size <= 0 || count <= 0) return;
            var buf = Marshal.AllocHGlobal(size);
            try
            {
                status = PdhGetFormattedCounterArray(_gpuCounter, PdhFmtDouble, ref size, ref count, buf);
                if (status != 0 || count <= 0) return;
                double max = 0;
                var stride = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM>();
                for (var i = 0; i < count; i++)
                {
                    var item = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM>(buf + i * stride);
                    if (item.FmtValue.CStatus == 0)
                        max = Math.Max(max, item.FmtValue.DoubleValue);
                }
                GpuPct = Math.Clamp(max, 0, 100);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch
        {
            _gpuProbeFailed = true;
            GpuPct = -1;
        }
    }

    IntPtr _gpuQuery, _gpuCounter;
    bool _gpuProbeFailed;
    const uint PdhFmtDouble = 0x00000200;

    public void Dispose()
    {
        if (_gpuQuery != IntPtr.Zero)
        {
            PdhCloseQuery(_gpuQuery);
            _gpuQuery = IntPtr.Zero;
        }
    }

    static ulong FileTimeToUlong(FILETIME ft) =>
        ((ulong)ft.dwHighDateTime << 32) | ft.dwLowDateTime;

    [StructLayout(LayoutKind.Sequential)]
    struct FILETIME { public uint dwLowDateTime, dwHighDateTime; }

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PDH_FMT_COUNTERVALUE
    {
        public uint CStatus;
        public double DoubleValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PDH_FMT_COUNTERVALUE_ITEM
    {
        public IntPtr szName;
        public PDH_FMT_COUNTERVALUE FmtValue;
    }

    [DllImport("kernel32.dll")]
    static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    static extern int PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    static extern int PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    static extern int PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    static extern int PdhCloseQuery(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    static extern int PdhGetFormattedCounterArray(IntPtr counter, uint format, ref int bufferSize, ref int itemCount, IntPtr itemBuffer);
}
