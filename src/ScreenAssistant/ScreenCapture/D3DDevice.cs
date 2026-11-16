using System.Runtime.InteropServices;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace ScreenAssistant.ScreenCapture;

internal static class D3DDevice
{
    private static readonly Guid DxgiDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    private const uint BgraSupport = 0x20;
    private const uint SdkVersion = 7;
    private const uint DriverHardware = 1;
    private const uint DriverWarp = 5;

    public static IDirect3DDevice Create()
    {
        var hr = D3D11CreateDevice(0, DriverHardware, 0, BgraSupport, 0, 0,
            SdkVersion, out var d3d, 0, 0);
        if (hr < 0)
            hr = D3D11CreateDevice(0, DriverWarp, 0, BgraSupport, 0, 0,
                SdkVersion, out d3d, 0, 0);
        Marshal.ThrowExceptionForHR(hr);

        try
        {
            var iid = DxgiDevice;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(d3d, in iid, out var dxgi));
            try
            {
                Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out var inspectable));
                try
                {
                    return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
                }
                finally
                {
                    Marshal.Release(inspectable);
                }
            }
            finally
            {
                Marshal.Release(dxgi);
            }
        }
        finally
        {
            Marshal.Release(d3d);
        }
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(nint adapter, uint driverType, nint software,
        uint flags, nint featureLevels, uint featureLevelCount, uint sdkVersion,
        out nint device, nint featureLevel, nint immediateContext);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgi, out nint device);
}
