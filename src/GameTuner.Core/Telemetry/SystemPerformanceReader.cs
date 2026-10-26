using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace GameTuner.Core.Telemetry;

public sealed record GpuSnapshot(
    double Utilization,
    double MemoryUsedMb,
    double MemoryTotalMb,
    double TemperatureC,
    double PowerW);

public sealed record CpuSnapshot(
    double AverageUtilization,
    double MaxCoreUtilization,
    int LogicalCoreCount);

public sealed class NvidiaSmiReader
{
    private readonly string _executablePath;

    public NvidiaSmiReader(string? executablePath = null)
    {
        _executablePath = executablePath ?? FindNvidiaSmi();
    }

    public async Task<GpuSnapshot?> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_executablePath))
        {
            return null;
        }

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(_executablePath)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    Arguments = "--query-gpu=utilization.gpu,memory.used,memory.total,temperature.gpu,power.draw --format=csv,noheader,nounits"
                }
            };
            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
            {
                return null;
            }

            var values = output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault()?
                .Split(',', StringSplitOptions.TrimEntries);
            if (values is null || values.Length < 5)
            {
                return null;
            }

            return new GpuSnapshot(
                ParseNumber(values[0]),
                ParseNumber(values[1]),
                ParseNumber(values[2]),
                ParseNumber(values[3]),
                ParseNumber(values[4]));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static double ParseNumber(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;

    private static string FindNvidiaSmi()
    {
        var known = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "nvidia-smi.exe");
        return File.Exists(known)
            ? known
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "NVIDIA Corporation",
                "NVSMI",
                "nvidia-smi.exe");
    }
}

public sealed class CpuPerformanceReader
{
    private const int SystemProcessorPerformanceInformation = 8;
    private CpuTimes[]? _previous;

    public CpuSnapshot Read()
    {
        var count = Math.Max(1, Environment.ProcessorCount);
        var size = Marshal.SizeOf<CpuTimes>();
        var buffer = Marshal.AllocHGlobal(size * count);
        try
        {
            var status = NtQuerySystemInformation(
                SystemProcessorPerformanceInformation,
                buffer,
                size * count,
                out var returned);
            if (status < 0)
            {
                return new CpuSnapshot(0, 0, count);
            }

            var actualCount = Math.Min(count, returned / size);
            var current = new CpuTimes[actualCount];
            for (var i = 0; i < actualCount; i++)
            {
                current[i] = Marshal.PtrToStructure<CpuTimes>(buffer + (i * size));
            }

            var previous = _previous;
            _previous = current;
            if (previous is null || previous.Length != current.Length)
            {
                return new CpuSnapshot(0, 0, count);
            }

            var totalBusy = 0d;
            var totalElapsed = 0d;
            var maxBusy = 0d;
            for (var i = 0; i < current.Length; i++)
            {
                var kernel = current[i].KernelTime - previous[i].KernelTime;
                var user = current[i].UserTime - previous[i].UserTime;
                var idle = current[i].IdleTime - previous[i].IdleTime;
                var elapsed = kernel + user;
                var busy = Math.Max(0, elapsed - idle);
                totalBusy += busy;
                totalElapsed += elapsed;
                if (elapsed > 0)
                {
                    maxBusy = Math.Max(maxBusy, 100d * busy / elapsed);
                }
            }

            return new CpuSnapshot(
                totalElapsed <= 0 ? 0 : Math.Clamp(100d * totalBusy / totalElapsed, 0, 100),
                Math.Clamp(maxBusy, 0, 100),
                count);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CpuTimes
    {
        public long IdleTime;
        public long KernelTime;
        public long UserTime;
        public long DpcTime;
        public long InterruptTime;
        public uint InterruptCount;
        private readonly uint _padding;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(
        int systemInformationClass,
        IntPtr systemInformation,
        int systemInformationLength,
        out int returnLength);
}
