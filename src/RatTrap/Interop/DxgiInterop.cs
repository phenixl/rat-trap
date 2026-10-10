using System.Runtime.InteropServices;

namespace RatTrap.Interop;

// =================================================================================
//  DXGI / D3D11 COM interop — Desktop Duplication API
// =================================================================================
//
//  WHY RAW VTABLE CALLS INSTEAD OF [GeneratedComInterface]
//  --------------------------------------------------------------------------------
//  Under Native AOT the built-in COM interop machinery ([ComImport], runtime stubs,
//  Marshal.GetObjectForIUnknown) is unavailable, so the only supported route is the COM
//  interface source generator. That generator's managed-wrapper ownership rules
//  (whether ConvertToManaged takes a reference, and when the wrapper releases it) are
//  not something to guess at in code whose failure mode is a silent wrong-vtable call.
//
//  So this file drives the vtables directly:
//
//    * A COM interface pointer points at a pointer to an array of function pointers.
//    * Method at index N is slot N of that array, ALWAYS counted from the start of the
//      vtable — so slot numbers include the 3 IUnknown methods.
//
//  ---------------------------------------------------------------------------------
//  VTABLE INDEX AUDIT TABLE (absolute indices, IUnknown included)
//  ---------------------------------------------------------------------------------
//    0  IUnknown::QueryInterface        1  IUnknown::AddRef        2  IUnknown::Release
//    3  IDXGIObject::SetPrivateData     4  SetPrivateDataInterface
//    5  IDXGIObject::GetPrivateData     6  IDXGIObject::GetParent
//
//    IDXGIDevice           (starts at 7, 5 methods)
//      7  GetAdapter
//    IDXGIAdapter          (starts at 7, 3 methods)
//      7  EnumOutputs
//    IDXGIOutput           (starts at 7, 12 methods)
//      7  GetDesc
//    IDXGIOutput1          (starts at 19 = 7 + 12, 4 methods)
//      19 GetDisplayModeList1    20 FindClosestMatchingMode1
//      21 GetDisplaySurfaceData1 22 DuplicateOutput
//    IDXGIOutputDuplication (starts at 7, 8 methods)
//      7  GetDesc                8  AcquireNextFrame
//      9  GetFrameDirtyRects     10 GetFrameMoveRects
//      11 GetFramePointerShape   12 MapDesktopSurface
//      13 UnMapDesktopSurface    14 ReleaseFrame
//    ID3D11Device          (inherits IUnknown directly, starts at 3)
//      3  CreateBuffer  4  CreateTexture1D  5  CreateTexture2D
//
//    ID3D11DeviceContext   (inherits ID3D11DeviceChild, NOT IUnknown: starts at 7)
//      ID3D11DeviceChild contributes 4 slots at 3..6:
//        3 GetDevice  4 GetPrivateData  5 SetPrivateData  6 SetPrivateDataInterface
//      7  VSSetConstantBuffers      8  PSSetShaderResources   9  PSSetShader
//      10 PSSetSamplers             11 VSSetShader            12 DrawIndexed
//      13 Draw                      14 Map                    15 Unmap
//      ...                         46 CopySubresourceRegion   47 CopyResource
//
//  ---------------------------------------------------------------------------------
//  TWO MEASURED CORRECTIONS, KEPT AS EVIDENCE
//  ---------------------------------------------------------------------------------
//  1. The first version counted method indices *within* each interface instead of within
//     the vtable, a 3-slot error that turns GetAdapter into SetPrivateDataInterface. It
//     produced a garbage interface pointer and an access violation inside IUnknown::Release.
//  2. The second version assumed ID3D11DeviceContext derived from IUnknown, putting Map at
//     slot 10. Slot 10 is really PSSetSamplers, a void method, so the call returned leftover
//     register garbage (measured: hr=0x794F9D00, which is not a valid HRESULT shape) and a
//     null mapped pointer. ID3D11Device genuinely does derive from IUnknown - that is
//     confirmed by CreateTexture2D working at slot 5 - but ID3D11DeviceContext does not.
//     The lesson is recorded here because both mistakes are invisible at compile time and
//     were caught only by the `rattrap probe` step-by-step diagnosis.
//
//  This process is x64-only (RuntimeIdentifier=win-x64, PlatformTarget=x64), where every
//  Win32 calling convention collapses to the single x64 convention, so `unmanaged[Stdcall]`
//  is correct throughout.
// =================================================================================

internal static partial class Dxgi
{
    // --- Vtable slot constants, one per call this program makes --------------------
    public const int SlotQueryInterface = 0;
    public const int SlotAddRef = 1;
    public const int SlotRelease = 2;

    public const int SlotIdxgiDeviceGetAdapter = 7;
    public const int SlotIdxgiAdapterEnumOutputs = 7;
    public const int SlotIdxgiOutputGetDesc = 7;
    public const int SlotIdxgiOutput1DuplicateOutput = 22;
    public const int SlotIdxgiOutputDuplicationGetDesc = 7;
    public const int SlotIdxgiOutputDuplicationAcquireNextFrame = 8;
    public const int SlotIdxgiOutputDuplicationGetFramePointerShape = 11;
    public const int SlotIdxgiOutputDuplicationMapDesktopSurface = 12;
    public const int SlotIdxgiOutputDuplicationUnMapDesktopSurface = 13;
    public const int SlotIdxgiOutputDuplicationReleaseFrame = 14;
    public const int SlotId3d11DeviceCreateTexture2D = 5;
    // ID3D11DeviceContext inherits ID3D11DeviceChild (4 extra slots at 3..6), so its own
    // methods start at 7 and Map is the 8th of them.
    public const int SlotId3d11DeviceContextFirstMethod = 7;
    public const int SlotId3d11DeviceContextMap = 14;
    public const int SlotId3d11DeviceContextUnmap = 15;
    public const int SlotId3d11DeviceContextCopySubresourceRegion = 46;

    // --- HRESULTs -----------------------------------------------------------------
    public const int S_OK = 0;
    public const int E_INVALIDARG = unchecked((int)0x80070057);
    public const int DXGI_ERROR_INVALID_CALL = unchecked((int)0x887A0001);
    public const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);
    public const int DXGI_ERROR_UNSUPPORTED = unchecked((int)0x887A0004);
    public const int DXGI_ERROR_DEVICE_REMOVED = unchecked((int)0x887A0005);
    public const int DXGI_ERROR_NOT_CURRENTLY_AVAILABLE = unchecked((int)0x887A0022);
    public const int DXGI_ERROR_ACCESS_LOST = unchecked((int)0x887A0026);
    public const int DXGI_ERROR_WAIT_TIMEOUT = unchecked((int)0x887A0027);

    // --- enum values used as plain ints -------------------------------------------
    public const uint DXGI_FORMAT_B8G8R8A8_UNORM = 87;
    public const int DXGI_MODE_ROTATION_IDENTITY = 1;
    public const int DXGI_MODE_ROTATION_ROTATE90 = 2;
    public const int DXGI_MODE_ROTATION_ROTATE180 = 3;
    public const int DXGI_MODE_ROTATION_ROTATE270 = 4;

    // DXGI_OUTDUPL_POINTER_SHAPE_TYPE
    public const uint POINTER_SHAPE_MONOCHROME = 1;
    public const uint POINTER_SHAPE_COLOR = 2;
    public const uint POINTER_SHAPE_MASKED_COLOR = 4;

    // D3D11
    public const uint D3D11_SDK_VERSION = 7;
    public const int D3D_DRIVER_TYPE_UNKNOWN = 0;
    public const int D3D_DRIVER_TYPE_HARDWARE = 1;
    public const int D3D_DRIVER_TYPE_WARP = 5;
    public const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;
    public const int D3D11_USAGE_STAGING = 3;
    public const uint D3D11_CPU_ACCESS_READ = 0x20000;
    public const uint D3D11_MAP_READ = 1;

    /// <summary>
    /// Upper bound used to sanity-check geometry that came back through a vtable call, so
    /// that a wrong slot fails loudly instead of quietly producing nonsense.
    /// </summary>
    public const uint MaxPlausibleImageDimension = 32768;

    [LibraryImport("d3d11.dll", EntryPoint = "D3D11CreateDevice")]
    public static partial int D3D11CreateDevice(
        IntPtr pAdapter,
        int driverType,
        IntPtr software,
        uint flags,
        IntPtr pFeatureLevels,
        uint featureLevels,
        uint sdkVersion,
        out IntPtr ppDevice,
        out int pFeatureLevel,
        out IntPtr ppImmediateContext);

    public static bool Succeeded(int hr) => hr >= 0;
    public static bool Failed(int hr) => hr < 0;
    public static string Hex(int hr) => $"0x{hr:X8}";
}

// ---------------------------------------------------------------------------------
// Blittable structures. Every one of these is passed by pointer to native code, so a
// wrong layout is a memory-corruption bug: the sizes are asserted in the self-test.
// ---------------------------------------------------------------------------------

[StructLayout(LayoutKind.Sequential)]
internal struct DXGI_RATIONAL
{
    public uint Numerator;
    public uint Denominator;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DXGI_MODE_DESC
{
    public uint Width;
    public uint Height;
    public DXGI_RATIONAL RefreshRate;
    public uint Format;
    public int ScanlineOrdering;
    public int Scaling;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DXGI_OUTPUT_DESC
{
    /// <summary>WCHAR[32]. Never read: outputs are matched by rectangle instead.</summary>
    private unsafe fixed byte _deviceName[64];
    public RECT DesktopCoordinates;
    public int AttachedToDesktop;
    public int Rotation;
    public IntPtr Monitor;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DXGI_OUTDUPL_POINTER_POSITION
{
    public POINT Position;
    public int Visible;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DXGI_OUTDUPL_FRAME_INFO
{
    public long LastPresentTime;
    public long LastMouseUpdateTime;
    public uint AccumulatedFrames;
    public int RectsCoalesced;
    public int ProtectedContentMaskedOut;
    public DXGI_OUTDUPL_POINTER_POSITION PointerPosition;
    public uint TotalMetadataBufferSize;
    public uint PointerShapeBufferSize;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DXGI_OUTDUPL_DESC
{
    public DXGI_MODE_DESC ModeDesc;
    public int Rotation;
    public int DesktopImageInSystemMemory;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DXGI_OUTDUPL_POINTER_SHAPE_INFO
{
    public uint Type;
    public uint Width;
    public uint Height;
    public uint Pitch;
    public POINT HotSpot;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DXGI_MAPPED_RECT
{
    public int Pitch;
    public IntPtr pBits;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DXGI_SAMPLE_DESC
{
    public uint Count;
    public uint Quality;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11_TEXTURE2D_DESC
{
    public uint Width;
    public uint Height;
    public uint MipLevels;
    public uint ArraySize;
    public uint Format;
    public DXGI_SAMPLE_DESC SampleDesc;
    public int Usage;
    public uint BindFlags;
    public uint CPUAccessFlags;
    public uint MiscFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11_MAPPED_SUBRESOURCE
{
    public IntPtr pData;
    public uint RowPitch;
    public uint DepthPitch;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11_BOX
{
    public uint left;
    public uint top;
    public uint front;
    public uint right;
    public uint bottom;
    public uint back;
}

// ---------------------------------------------------------------------------------
// Well-known interface IDs
// ---------------------------------------------------------------------------------

internal static class Guids
{
    public static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");
    public static readonly Guid IID_IDXGIDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    public static readonly Guid IID_IDXGIAdapter = new("2411e7e1-12ac-4ccf-bd14-9798e8534dc0");
    public static readonly Guid IID_IDXGIOutput = new("ae02eedb-c735-4690-8d52-5a8dc20213aa");
    public static readonly Guid IID_IDXGIOutput1 = new("00cddea8-939b-4b83-a340-a685226666cc");
    public static readonly Guid IID_IDXGIOutputDuplication = new("191cfac3-a341-470d-b26e-a864f428319c");
    public static readonly Guid IID_IDXGIResource = new("035f3ab4-482e-4e50-b41f-8a7f8bd8960b");
    public static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    public static readonly Guid IID_ID3D11Resource = new("dc8e63f3-d12b-4952-b47b-5e45026a862d");
    public static readonly Guid IID_ID3D11Device = new("db6f6ddb-ac77-4e88-8253-819df9bbf140");
    public static readonly Guid IID_ID3D11DeviceContext = new("cc86fabe-da55-401d-85e7-e3c9de2877e9");
}

// ---------------------------------------------------------------------------------
// IUnknown
// ---------------------------------------------------------------------------------

internal static unsafe class ComPtr
{
    /// <summary>IUnknown::QueryInterface — slot 0.</summary>
    public static int QueryInterface(IntPtr punk, in Guid iid, out IntPtr ppv)
    {
        ppv = IntPtr.Zero;
        if (punk == IntPtr.Zero)
        {
            return Dxgi.E_INVALIDARG;
        }

        var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, void**, int>)(*(void***)punk)[Dxgi.SlotQueryInterface];
        fixed (Guid* pIid = &iid)
        {
            void* result = null;
            int hr = fn(punk, pIid, &result);
            ppv = (IntPtr)result;
            return hr;
        }
    }

    /// <summary>IUnknown::AddRef — slot 1.</summary>
    public static uint AddRef(IntPtr punk)
    {
        if (punk == IntPtr.Zero) return 0;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint>)(*(void***)punk)[Dxgi.SlotAddRef];
        return fn(punk);
    }

    /// <summary>IUnknown::Release — slot 2.</summary>
    public static uint Release(IntPtr punk)
    {
        if (punk == IntPtr.Zero) return 0;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint>)(*(void***)punk)[Dxgi.SlotRelease];
        return fn(punk);
    }
}

// ---------------------------------------------------------------------------------
// Per-interface method callers
// ---------------------------------------------------------------------------------

/// <summary>IDXGIDevice.</summary>
internal static unsafe class IDXGIDeviceMethods
{
    /// <summary>IDXGIDevice::GetAdapter — slot 7.</summary>
    public static int GetAdapter(IntPtr self, out IntPtr ppAdapter)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, void**, int>)(*(void***)self)[Dxgi.SlotIdxgiDeviceGetAdapter];
        void* adapter = null;
        int hr = fn(self, &adapter);
        ppAdapter = (IntPtr)adapter;
        return hr;
    }
}

/// <summary>IDXGIAdapter.</summary>
internal static unsafe class IDXGIAdapterMethods
{
    /// <summary>IDXGIAdapter::EnumOutputs — slot 7.</summary>
    public static int EnumOutputs(IntPtr self, uint output, out IntPtr ppOutput)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint, void**, int>)(*(void***)self)[Dxgi.SlotIdxgiAdapterEnumOutputs];
        void* result = null;
        int hr = fn(self, output, &result);
        ppOutput = (IntPtr)result;
        return hr;
    }
}

/// <summary>IDXGIOutput.</summary>
internal static unsafe class IDXGIOutputMethods
{
    /// <summary>IDXGIOutput::GetDesc — slot 7.</summary>
    public static int GetDesc(IntPtr self, out DXGI_OUTPUT_DESC desc)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, DXGI_OUTPUT_DESC*, int>)(*(void***)self)[Dxgi.SlotIdxgiOutputGetDesc];
        DXGI_OUTPUT_DESC tmp = default;
        int hr = fn(self, &tmp);
        desc = tmp;
        return hr;
    }
}

/// <summary>IDXGIOutput1.</summary>
internal static unsafe class IDXGIOutput1Methods
{
    /// <summary>IDXGIOutput1::DuplicateOutput — slot 22.</summary>
    public static int DuplicateOutput(IntPtr self, IntPtr pDevice, out IntPtr ppOutputDuplication)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, void**, int>)(*(void***)self)[Dxgi.SlotIdxgiOutput1DuplicateOutput];
        void* result = null;
        int hr = fn(self, pDevice, &result);
        ppOutputDuplication = (IntPtr)result;
        return hr;
    }
}

/// <summary>IDXGIOutputDuplication.</summary>
internal static unsafe class IDXGIOutputDuplicationMethods
{
    /// <summary>IDXGIOutputDuplication::GetDesc — slot 7.</summary>
    public static int GetDesc(IntPtr self, out DXGI_OUTDUPL_DESC desc)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, DXGI_OUTDUPL_DESC*, int>)(*(void***)self)[Dxgi.SlotIdxgiOutputDuplicationGetDesc];
        DXGI_OUTDUPL_DESC tmp = default;
        int hr = fn(self, &tmp);
        desc = tmp;
        return hr;
    }

    /// <summary>
    /// GetDesc plus a plausibility check on the result. A wrong vtable slot is far more
    /// likely to yield nonsense geometry than an error code, so this is the cheapest place
    /// to catch a slot mistake.
    /// </summary>
    public static int GetDescChecked(IntPtr self, out DXGI_OUTDUPL_DESC desc, out string? validationError)
    {
        int hr = GetDesc(self, out desc);
        validationError = null;
        if (Dxgi.Failed(hr))
        {
            return hr;
        }

        if (desc.ModeDesc.Width is 0 or > Dxgi.MaxPlausibleImageDimension ||
            desc.ModeDesc.Height is 0 or > Dxgi.MaxPlausibleImageDimension)
        {
            validationError = $"GetDesc returned implausible image geometry " +
                              $"{desc.ModeDesc.Width}x{desc.ModeDesc.Height}; vtable slot " +
                              $"{Dxgi.SlotIdxgiOutputDuplicationGetDesc} is probably wrong.";
        }
        else if (desc.Rotation is < Dxgi.DXGI_MODE_ROTATION_IDENTITY or > Dxgi.DXGI_MODE_ROTATION_ROTATE270)
        {
            validationError = $"GetDesc returned rotation {desc.Rotation}, outside the documented 1..4 " +
                              "range; the vtable slot is probably wrong.";
        }

        return hr;
    }

    /// <summary>IDXGIOutputDuplication::AcquireNextFrame — slot 8.</summary>
    public static int AcquireNextFrame(IntPtr self, uint timeoutMs, out DXGI_OUTDUPL_FRAME_INFO frameInfo, out IntPtr ppDesktopResource)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint, DXGI_OUTDUPL_FRAME_INFO*, void**, int>)(*(void***)self)[Dxgi.SlotIdxgiOutputDuplicationAcquireNextFrame];
        DXGI_OUTDUPL_FRAME_INFO info = default;
        void* resource = null;
        int hr = fn(self, timeoutMs, &info, &resource);
        frameInfo = info;
        ppDesktopResource = (IntPtr)resource;
        return hr;
    }

    /// <summary>IDXGIOutputDuplication::GetFramePointerShape — slot 11.</summary>
    public static int GetFramePointerShape(IntPtr self, uint bufferSize, IntPtr buffer, out uint requiredSize, out DXGI_OUTDUPL_POINTER_SHAPE_INFO shapeInfo)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint, void*, uint*, DXGI_OUTDUPL_POINTER_SHAPE_INFO*, int>)(*(void***)self)[Dxgi.SlotIdxgiOutputDuplicationGetFramePointerShape];
        uint required = 0;
        DXGI_OUTDUPL_POINTER_SHAPE_INFO info = default;
        int hr = fn(self, bufferSize, (void*)buffer, &required, &info);
        requiredSize = required;
        shapeInfo = info;
        return hr;
    }

    /// <summary>IDXGIOutputDuplication::MapDesktopSurface — slot 12.</summary>
    public static int MapDesktopSurface(IntPtr self, out DXGI_MAPPED_RECT lockedRect)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, DXGI_MAPPED_RECT*, int>)(*(void***)self)[Dxgi.SlotIdxgiOutputDuplicationMapDesktopSurface];
        DXGI_MAPPED_RECT tmp = default;
        int hr = fn(self, &tmp);
        lockedRect = tmp;
        return hr;
    }

    /// <summary>IDXGIOutputDuplication::UnMapDesktopSurface — slot 13.</summary>
    public static int UnMapDesktopSurface(IntPtr self)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, int>)(*(void***)self)[Dxgi.SlotIdxgiOutputDuplicationUnMapDesktopSurface];
        return fn(self);
    }

    /// <summary>IDXGIOutputDuplication::ReleaseFrame — slot 14.</summary>
    public static int ReleaseFrame(IntPtr self)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, int>)(*(void***)self)[Dxgi.SlotIdxgiOutputDuplicationReleaseFrame];
        return fn(self);
    }
}

/// <summary>ID3D11Device. Only CreateTexture2D is used.</summary>
internal static unsafe class ID3D11DeviceMethods
{
    /// <summary>ID3D11Device::CreateTexture2D — slot 5.</summary>
    public static int CreateTexture2D(IntPtr self, in D3D11_TEXTURE2D_DESC desc, IntPtr initialData, out IntPtr ppTexture2D)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, D3D11_TEXTURE2D_DESC*, void*, void**, int>)(*(void***)self)[Dxgi.SlotId3d11DeviceCreateTexture2D];
        void* texture = null;
        fixed (D3D11_TEXTURE2D_DESC* pDesc = &desc)
        {
            int hr = fn(self, pDesc, (void*)initialData, &texture);
            ppTexture2D = (IntPtr)texture;
            return hr;
        }
    }
}

/// <summary>
/// ID3D11DeviceContext. Inherits ID3D11DeviceChild, so slots 3..6 are GetDevice,
/// GetPrivateData, SetPrivateData and SetPrivateDataInterface, and the context's own
/// methods begin at slot 7.
/// </summary>
/// <remarks>
/// The 7 Set*/Draw* calls at slots 7..13 are never invoked; only their COUNT matters for
/// reaching Map (14), Unmap (15) and CopySubresourceRegion (46). Order per d3d11.h:
/// VSSetConstantBuffers, PSSetShaderResources, PSSetShader, PSSetSamplers, VSSetShader,
/// DrawIndexed, Draw, Map, Unmap, PSSetConstantBuffers, IASetInputLayout,
/// IASetVertexBuffers, IASetIndexBuffer, DrawIndexedInstanced, DrawInstanced,
/// GSSetConstantBuffers, GSSetShader, IASetPrimitiveTopology, VSSetShaderResources,
/// VSSetSamplers, Begin, End, GetData, SetPredication, GSSetShaderResources,
/// GSSetSamplers, OMSetRenderTargets, OMSetRenderTargetsAndUnorderedAccessViews,
/// OMSetBlendState, OMSetDepthStencilState, SOSetTargets, DrawAuto,
/// DrawIndexedInstancedIndirect, DrawInstancedIndirect, Dispatch, DispatchIndirect,
/// RSSetState, RSSetViewports, RSSetScissorRects, CopySubresourceRegion, CopyResource.
/// </remarks>
internal static unsafe class ID3D11DeviceContextMethods
{
    /// <summary>ID3D11DeviceContext::Map — slot 14.</summary>
    public static int Map(IntPtr self, IntPtr resource, uint subresource, uint mapType, uint mapFlags, out D3D11_MAPPED_SUBRESOURCE mapped)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, D3D11_MAPPED_SUBRESOURCE*, int>)(*(void***)self)[Dxgi.SlotId3d11DeviceContextMap];
        D3D11_MAPPED_SUBRESOURCE tmp = default;
        int hr = fn(self, resource, subresource, mapType, mapFlags, &tmp);
        mapped = tmp;
        return hr;
    }

    /// <summary>ID3D11DeviceContext::Unmap — slot 15.</summary>
    public static void Unmap(IntPtr self, IntPtr resource, uint subresource)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)(*(void***)self)[Dxgi.SlotId3d11DeviceContextUnmap];
        fn(self, resource, subresource);
    }

    /// <summary>ID3D11DeviceContext::CopySubresourceRegion — slot 46.</summary>
    public static void CopySubresourceRegion(IntPtr self, IntPtr dst, uint dstSubresource, uint dstX, uint dstY, uint dstZ,
                                             IntPtr src, uint srcSubresource, in D3D11_BOX srcBox)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, uint, IntPtr, uint, D3D11_BOX*, void>)(*(void***)self)[Dxgi.SlotId3d11DeviceContextCopySubresourceRegion];
        fixed (D3D11_BOX* pBox = &srcBox)
        {
            fn(self, dst, dstSubresource, dstX, dstY, dstZ, src, srcSubresource, pBox);
        }
    }
}

