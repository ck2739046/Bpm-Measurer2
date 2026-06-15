using System.Runtime.InteropServices;

namespace BpmMeasurer.Wpf;

public static class FftwNative
{
    /// <summary>FFTW_ESTIMATE — quick heuristic plan, no runtime measurement.</summary>
    public const uint FFTW_ESTIMATE = 1U << 6;

    [DllImport("libfftw3f-3.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr fftwf_malloc(UIntPtr n);

    [DllImport("libfftw3f-3.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern void fftwf_free(IntPtr p);

    /// <summary>1D real-to-complex FFT plan (float precision).</summary>
    [DllImport("libfftw3f-3.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr fftwf_plan_dft_r2c_1d(int n, IntPtr input, IntPtr output, uint flags);

    [DllImport("libfftw3f-3.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern void fftwf_execute(IntPtr plan);

    /// <summary>
    /// Thread-safe "new-array execute" for r2c. Safe to call from multiple threads
    /// with different input/output arrays, provided the plan was created with FFTW_ESTIMATE.
    /// https://www.fftw.org/fftw3_doc/New_002darray-Execute-Functions.html
    /// </summary>
    [DllImport("libfftw3f-3.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern void fftwf_execute_dft_r2c(IntPtr plan, IntPtr input, IntPtr output);

    [DllImport("libfftw3f-3.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern void fftwf_destroy_plan(IntPtr plan);
}
