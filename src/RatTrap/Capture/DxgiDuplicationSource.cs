using System.Runtime.InteropServices;
using RatTrap.Imaging;
using RatTrap.Interop;

namespace RatTrap.Capture;

/// <summary>Thrown when a backend cannot be created, so the factory can fall back.</summary>
public sealed class CaptureBackendUnavailableException : Exception
{
    public CaptureBackendUnavailableException(string message, int hresult = 0)
        : base(message)
    {
        HResult = hresult;
    }

    public CaptureBackendUnavailableException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Screen capture through the DXGI Desktop Duplication API.
/// </summary>
/// <remarks>
/// <para>
/// This is the backend that satisfies the "fullscreen game" requirement: it is the same
/// mechanism most desktop capture tools use, and it keeps working when an application has
/// taken over the display and desktop composition is bypassed, which is exactly where GDI
/// <c>BitBlt</c> returns stale or black pixels.
/// </para>
/// <para>
/// Critically, the duplication image contains the desktop <b>without</b> the cursor; the
/// compositor hands the cursor over separately through
/// <c>GetFramePointerShape</c>. That single fact is what makes this backend both:
/// </para>
/// <list type="bullet">
///   <item>a source of an exact, already-scaled pointer bitmap (see <see cref="TryGetPointerShape"/>), and</item>
///   <item>a source of a clean background plate <c>B</c> for the reconstruction solver,
///     because its pixels are the composite with the pointer removed.</item>
/// </list>
/// <para>
/// Each backend owns its own D3D11 device. A duplication session is exclusive per output,
/// so only one <see cref="DxgiDuplicationSource"/> may exist per monitor at a time; the OS
/// allows a small number of duplications per output across the whole system.
/// </para>
/// </remarks>
public sealed class DxgiDuplicationSource : ICaptureSource, IPointerShapeProvider
{
    private readonly object _sync = new();
    private readonly MonitorDescriptor _monitor;

    private IntPtr _device;
    private IntPtr _context;
    private IntPtr _adapter;
    private IntPtr _output1;
    private IntPtr _duplication;

    private IntPtr _stagingTexture;
    private int _stagingWidth;
    private int _stagingHeight;

    private DXGI_OUTDUPL_DESC _duplicationDesc;
    private bool _useMapDesktopSurface;

    private byte[] _shapeBuffer = new byte[4096];
    private PointerShapeSnapshot _lastShape;
    private bool _hasShape;

    private POINT _pointerPositionInOutput;
    private bool _pointerVisible;
    private bool _pointerPositionKnown;

    // Desktop Duplication only signals a frame when something actually changed, so on a
    // still screen AcquireNextFrame times out indefinitely. Sampling needs an observation
    // even when nothing moved, and "the screen is unchanged" is itself valid information,
    // so the last region read is cached and replayed (flagged IsCached=true so callers can
    // tell a replayed observation from a fresh one).
    private byte[]? _cachedPixels;
    private RECT _cachedRegion;
    private bool _hasValidCache;

    private bool _disposed;

    /// <summary>
    /// True when the compositor masked this frame because the content is protected.
    /// </summary>
    /// <remarks>
    /// Duplication reports this instead of delivering pixels, and it is worth surfacing rather than
    /// silently returning a black rectangle: "the output is showing protected content" is a completely
    /// different problem from "the cursor was not found", and only one of them has a workaround.
    /// </remarks>
    public bool LastFrameProtectedContentMasked { get; private set; }

    public DxgiDuplicationSource(MonitorDescriptor monitor)
    {
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        try
        {
            CreateResources();
        }
        catch
        {
            ReleaseResources();
            throw;
        }
    }

    public string Name => "DXGI Desktop Duplication";

    public MonitorDescriptor Monitor => _monitor;

    /// <inheritdoc />
    /// <remarks>
    /// False by design: the duplication image has the pointer removed, so this backend is
    /// the background-plate source rather than the composite source.
    /// </remarks>
    public bool IncludesPointer => false;

    /// <summary>The duplication session description, as a plain data record.</summary>
    public DuplicationInfo DuplicationDesc => new(
        Width: _duplicationDesc.ModeDesc.Width,
        Height: _duplicationDesc.ModeDesc.Height,
        Rotation: _duplicationDesc.Rotation,
        DesktopImageInSystemMemory: _duplicationDesc.DesktopImageInSystemMemory != 0);

    /// <summary>True when the desktop image was available in system memory and mapping is used.</summary>
    public bool UsesSystemMemoryMapping => _useMapDesktopSurface;

    /// <summary>Pointer position in output image coordinates, when the OS has reported one.</summary>
    public bool TryGetPointerPositionInOutput(out POINT position, out bool visible)
    {
        position = _pointerPositionInOutput;
        visible = _pointerVisible;
        return _pointerPositionKnown;
    }

    public bool TryGetPointerShape(out PointerShapeSnapshot snapshot)
    {
        snapshot = _lastShape;
        return _hasShape;
    }

    // -----------------------------------------------------------------------------
    // Resource lifecycle
    // -----------------------------------------------------------------------------

    private void CreateResources()
    {
        int hr = Dxgi.D3D11CreateDevice(IntPtr.Zero, Dxgi.D3D_DRIVER_TYPE_HARDWARE, IntPtr.Zero,
            Dxgi.D3D11_CREATE_DEVICE_BGRA_SUPPORT, IntPtr.Zero, 0, Dxgi.D3D11_SDK_VERSION,
            out _device, out _, out _context);

        if (Dxgi.Failed(hr))
        {
            int hardwareHr = hr;
            // WARP keeps the tool usable in a VM, over RDP, or on a machine whose display
            // adapter driver refuses a hardware device.
            hr = Dxgi.D3D11CreateDevice(IntPtr.Zero, Dxgi.D3D_DRIVER_TYPE_WARP, IntPtr.Zero,
                Dxgi.D3D11_CREATE_DEVICE_BGRA_SUPPORT, IntPtr.Zero, 0, Dxgi.D3D11_SDK_VERSION,
                out _device, out _, out _context);

            if (Dxgi.Failed(hr))
            {
                throw new CaptureBackendUnavailableException(
                    $"D3D11CreateDevice failed for both hardware ({Dxgi.Hex(hardwareHr)}) and WARP ({Dxgi.Hex(hr)}).",
                    hardwareHr);
            }
        }

        hr = ComPtr.QueryInterface(_device, Guids.IID_IDXGIDevice, out IntPtr dxgiDevice);
        if (Dxgi.Failed(hr))
        {
            throw new CaptureBackendUnavailableException(
                $"ID3D11Device does not expose IDXGIDevice ({Dxgi.Hex(hr)}).", hr);
        }

        try
        {
            hr = IDXGIDeviceMethods.GetAdapter(dxgiDevice, out _adapter);
            if (Dxgi.Failed(hr))
            {
                throw new CaptureBackendUnavailableException($"IDXGIDevice::GetAdapter failed ({Dxgi.Hex(hr)}).", hr);
            }
        }
        finally
        {
            ComPtr.Release(dxgiDevice);
        }

        _output1 = FindOutputForMonitor();

        hr = IDXGIOutput1Methods.DuplicateOutput(_output1, _device, out _duplication);
        if (Dxgi.Failed(hr))
        {
            string reason = hr switch
            {
                Dxgi.DXGI_ERROR_NOT_CURRENTLY_AVAILABLE =>
                    "the per-output duplication limit was reached (another capture tool already " +
                    "holds this monitor, or the session limit for this output is exhausted)",
                Dxgi.DXGI_ERROR_UNSUPPORTED =>
                    "the output does not support duplication (typical for some virtual/indirect display drivers, " +
                    "HDR-only modes, or protected output)",
                Dxgi.DXGI_ERROR_ACCESS_LOST => "access to the output was lost during setup",
                _ => "the output refused a duplication session",
            };
            throw new CaptureBackendUnavailableException(
                $"DuplicateOutput failed on {_monitor.DeviceName}: {reason} ({Dxgi.Hex(hr)}).", hr);
        }

        hr = IDXGIOutputDuplicationMethods.GetDescChecked(_duplication, out _duplicationDesc, out string? descError);
        if (Dxgi.Failed(hr))
        {
            throw new CaptureBackendUnavailableException($"IDXGIOutputDuplication::GetDesc failed ({Dxgi.Hex(hr)}).", hr);
        }
        if (descError is not null)
        {
            // A wrong vtable slot would surface here as impossible geometry rather than as
            // an error, so refuse to continue on suspicion.
            throw new CaptureBackendUnavailableException(descError);
        }

        if (_duplicationDesc.Rotation != Dxgi.DXGI_MODE_ROTATION_IDENTITY)
        {
            // A rotated output returns the image rotated, which would need a coordinate
            // transform in every consumer. Rather than half-support it, refuse here so the
            // factory falls back to GDI (which always returns an upright image), and the
            // caller can still obtain the pointer shape from a temporary duplication.
            throw new CaptureBackendUnavailableException(
                $"Output {_monitor.DeviceName} reports rotation {_duplicationDesc.Rotation}; " +
                "rotated outputs are not supported by the duplication backend yet.");
        }

        _useMapDesktopSurface = _duplicationDesc.DesktopImageInSystemMemory != 0;
    }

    private IntPtr FindOutputForMonitor()
    {
        for (uint i = 0; ; i++)
        {
            int hr = IDXGIAdapterMethods.EnumOutputs(_adapter, i, out IntPtr output);
            if (hr == Dxgi.DXGI_ERROR_NOT_FOUND)
            {
                break;
            }
            if (Dxgi.Failed(hr))
            {
                throw new CaptureBackendUnavailableException(
                    $"IDXGIAdapter::EnumOutputs({i}) failed ({Dxgi.Hex(hr)}).", hr);
            }

            DXGI_OUTPUT_DESC desc;
            hr = IDXGIOutputMethods.GetDesc(output, out desc);
            if (Dxgi.Failed(hr))
            {
                ComPtr.Release(output);
                continue;
            }

            bool exact = desc.DesktopCoordinates.Left == _monitor.Rect.Left &&
                         desc.DesktopCoordinates.Top == _monitor.Rect.Top &&
                         desc.DesktopCoordinates.Right == _monitor.Rect.Right &&
                         desc.DesktopCoordinates.Bottom == _monitor.Rect.Bottom;

            bool centreInside = !exact &&
                _monitor.Contains(
                    (desc.DesktopCoordinates.Left + desc.DesktopCoordinates.Right) / 2,
                    (desc.DesktopCoordinates.Top + desc.DesktopCoordinates.Bottom) / 2);

            if (!exact && !centreInside)
            {
                ComPtr.Release(output);
                continue;
            }

            hr = ComPtr.QueryInterface(output, Guids.IID_IDXGIOutput1, out IntPtr output1);
            ComPtr.Release(output);
            if (Dxgi.Failed(hr))
            {
                continue;
            }
            return output1;
        }

        throw new CaptureBackendUnavailableException(
            $"No DXGI output matches monitor {_monitor.DeviceName} {_monitor.Rect}.");
    }

    private void ReleaseResources()
    {
        if (_duplication != IntPtr.Zero) { ComPtr.Release(_duplication); _duplication = IntPtr.Zero; }
        if (_output1 != IntPtr.Zero) { ComPtr.Release(_output1); _output1 = IntPtr.Zero; }
        if (_adapter != IntPtr.Zero) { ComPtr.Release(_adapter); _adapter = IntPtr.Zero; }
        if (_context != IntPtr.Zero) { ComPtr.Release(_context); _context = IntPtr.Zero; }
        if (_device != IntPtr.Zero) { ComPtr.Release(_device); _device = IntPtr.Zero; }
        ReleaseStaging();
    }

    private void ReleaseStaging()
    {
        if (_stagingTexture != IntPtr.Zero)
        {
            ComPtr.Release(_stagingTexture);
            _stagingTexture = IntPtr.Zero;
        }
        _stagingWidth = 0;
        _stagingHeight = 0;
    }

    public void Reinitialize()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ReleaseResources();
            _hasShape = false;
            _pointerPositionKnown = false;
            InvalidateCache();
            CreateResources();
        }
    }

    // -----------------------------------------------------------------------------
    // Capture
    // -----------------------------------------------------------------------------

    public CaptureResult Capture(RECT region, ArgbBitmap destination, int timeoutMs)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            CaptureSupport.ValidateRegion(_monitor, region, destination);

            int hr = IDXGIOutputDuplicationMethods.AcquireNextFrame(_duplication, (uint)Math.Max(0, timeoutMs),
                out DXGI_OUTDUPL_FRAME_INFO frameInfo, out IntPtr desktopResource);

            if (hr == Dxgi.DXGI_ERROR_WAIT_TIMEOUT)
            {
                // The desktop did not change. Replay the cached pixels when they cover
                // exactly the requested rectangle.
                if (TryReplayCache(region, destination))
                {
                    return new CaptureResult(CaptureStatus.Captured, null, IsCached: true);
                }
                return CaptureResult.NoNewFrame;
            }
            if (hr == Dxgi.DXGI_ERROR_ACCESS_LOST)
            {
                return new CaptureResult(CaptureStatus.NeedsReinitialize,
                    "DXGI access lost (display mode change, fullscreen transition, or the desktop switched).");
            }
            if (Dxgi.Failed(hr))
            {
                return new CaptureResult(CaptureStatus.Failed, $"AcquireNextFrame failed ({Dxgi.Hex(hr)}).");
            }

            try
            {
                if (frameInfo.PointerShapeBufferSize > 0)
                {
                    CapturePointerShape(frameInfo.PointerShapeBufferSize);
                }

                LastFrameProtectedContentMasked = frameInfo.ProtectedContentMaskedOut != 0;

                if (frameInfo.LastMouseUpdateTime != 0)
                {
                    _pointerPositionInOutput = frameInfo.PointerPosition.Position;
                    _pointerVisible = frameInfo.PointerPosition.Visible != 0;
                    _pointerPositionKnown = true;
                }

                if (desktopResource == IntPtr.Zero)
                {
                    return CaptureResult.NoNewFrame;
                }

                string? error = CopyRegion(region, destination, desktopResource);
                if (error is null)
                {
                    StoreCache(region, destination);
                    return new CaptureResult(CaptureStatus.Captured);
                }
                return new CaptureResult(CaptureStatus.Failed, error);
            }
            finally
            {
                if (desktopResource != IntPtr.Zero)
                {
                    ComPtr.Release(desktopResource);
                }

                // ReleaseFrame must happen on every successful AcquireNextFrame, including
                // early returns, or the duplicator stalls and stops delivering frames.
                IDXGIOutputDuplicationMethods.ReleaseFrame(_duplication);
            }
        }
    }

    private void CapturePointerShape(uint frameShapeBufferSize)
    {
        // The frame info reports the exact size for this frame, but the pointer can grow
        // between frames (accessibility pointer size, DPI change), so grow-and-retry is
        // still needed. The shape must be copied out before ReleaseFrame invalidates it.
        int required = (int)frameShapeBufferSize;

        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (required <= 0)
            {
                return;
            }
            if (_shapeBuffer.Length < required)
            {
                Array.Resize(ref _shapeBuffer, required);
            }

            IntPtr buffer = Marshal.AllocHGlobal(_shapeBuffer.Length);
            try
            {
                int hr = IDXGIOutputDuplicationMethods.GetFramePointerShape(_duplication,
                    (uint)_shapeBuffer.Length, buffer, out uint reportedRequired, out DXGI_OUTDUPL_POINTER_SHAPE_INFO info);

                if (Dxgi.Failed(hr))
                {
                    // Only an undersized buffer is worth retrying. DXGI reports it either
                    // as HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER) or by returning the
                    // needed size, so accept either signal once.
                    if (reportedRequired > _shapeBuffer.Length)
                    {
                        required = (int)reportedRequired;
                        continue;
                    }
                    return;
                }

                int length = ComputeShapeByteLength(info);
                if (length <= 0 || length > _shapeBuffer.Length)
                {
                    return;
                }

                var copy = new byte[length];
                Marshal.Copy(buffer, copy, 0, length);

                _lastShape = new PointerShapeSnapshot(
                    ShapeType: info.Type,
                    Width: (int)info.Width,
                    Height: (int)info.Height,
                    Pitch: (int)info.Pitch,
                    HotspotX: info.HotSpot.X,
                    HotspotY: info.HotSpot.Y,
                    Buffer: copy,
                    TimestampMs: Environment.TickCount64,
                    Visible: _pointerVisible);
                _hasShape = true;
                return;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    /// <summary>Byte length of a pointer shape payload, derived from its declared type.</summary>
    internal static int ComputeShapeByteLength(in DXGI_OUTDUPL_POINTER_SHAPE_INFO info)
    {
        checked
        {
            int pitch = (int)info.Pitch;
            int height = (int)info.Height;
            return info.Type switch
            {
                // 32bpp BGRA.
                Dxgi.POINTER_SHAPE_COLOR => pitch * height,
                // 32bpp BGRA whose colours are XORed into the desktop; alpha is 0 or 255.
                Dxgi.POINTER_SHAPE_MASKED_COLOR => pitch * height,
                // 1bpp AND mask followed by a 1bpp XOR mask, each pitch x height.
                Dxgi.POINTER_SHAPE_MONOCHROME => 2 * pitch * height,
                _ => 0,
            };
        }
    }

    private string? CopyRegion(RECT region, ArgbBitmap destination, IntPtr desktopResource)
    {
        int offsetX = region.Left - _monitor.Rect.Left;
        int offsetY = region.Top - _monitor.Rect.Top;

        if (_useMapDesktopSurface)
        {
            string? viaMap = TryCopyViaSystemMemory(region, destination, offsetX, offsetY);
            if (viaMap is null)
            {
                return null;
            }
            // Mapping is advertised but failed: fall through to the GPU path permanently.
            _useMapDesktopSurface = false;
            _ = viaMap;
        }

        return CopyViaStagingTexture(region, destination, offsetX, offsetY, desktopResource);
    }

    /// <summary>
    /// Fast path: the desktop image is already in system memory, so read the rectangle
    /// straight out of the mapped surface with no GPU copy at all.
    /// </summary>
    private string? TryCopyViaSystemMemory(RECT region, ArgbBitmap destination, int offsetX, int offsetY)
    {
        int hr = IDXGIOutputDuplicationMethods.MapDesktopSurface(_duplication, out DXGI_MAPPED_RECT locked);
        if (Dxgi.Failed(hr))
        {
            return $"MapDesktopSurface failed ({Dxgi.Hex(hr)}).";
        }

        try
        {
            if (locked.pBits == IntPtr.Zero || locked.Pitch <= 0)
            {
                return "MapDesktopSurface returned an empty surface.";
            }

            unsafe
            {
                byte* basePtr = (byte*)locked.pBits;
                fixed (byte* dest = destination.Pixels)
                {
                    for (int y = 0; y < region.Height; y++)
                    {
                        byte* source = basePtr + ((long)(offsetY + y) * locked.Pitch) + (offsetX * 4);
                        Buffer.MemoryCopy(source, dest + ((long)y * destination.Stride), destination.Stride, destination.Stride);
                    }
                }
            }

            CaptureSupport.ForceOpaque(destination);
            return null;
        }
        finally
        {
            IDXGIOutputDuplicationMethods.UnMapDesktopSurface(_duplication);
        }
    }

    /// <summary>
    /// General path: copy just the requested rectangle from the desktop texture into a
    /// small staging texture, then map that. Only the rectangle crosses the bus.
    /// </summary>
    private string? CopyViaStagingTexture(RECT region, ArgbBitmap destination, int offsetX, int offsetY, IntPtr desktopResource)
    {
        int hr = ComPtr.QueryInterface(desktopResource, Guids.IID_ID3D11Texture2D, out IntPtr desktopTexture);
        if (Dxgi.Failed(hr))
        {
            return $"Acquired frame does not expose ID3D11Texture2D ({Dxgi.Hex(hr)}).";
        }

        try
        {
            if (!EnsureStaging(region.Width, region.Height, out string? stagingError))
            {
                return stagingError;
            }

            var box = new D3D11_BOX
            {
                left = (uint)offsetX,
                top = (uint)offsetY,
                front = 0,
                right = (uint)(offsetX + region.Width),
                bottom = (uint)(offsetY + region.Height),
                back = 1,
            };

            ID3D11DeviceContextMethods.CopySubresourceRegion(_context, _stagingTexture, 0, 0, 0, 0,
                desktopTexture, 0, box);

            hr = ID3D11DeviceContextMethods.Map(_context, _stagingTexture, 0, Dxgi.D3D11_MAP_READ, 0, out D3D11_MAPPED_SUBRESOURCE mapped);
            if (Dxgi.Failed(hr))
            {
                return $"ID3D11DeviceContext::Map on the staging texture failed ({Dxgi.Hex(hr)}).";
            }

            try
            {
                if (mapped.pData == IntPtr.Zero)
                {
                    return "Map returned a null data pointer.";
                }

                if (mapped.RowPitch < (uint)(region.Width * 4))
                {
                    // Guards against a wrong Map vtable slot returning unrelated memory.
                    return $"Map reported a row pitch of {mapped.RowPitch} bytes, which cannot hold " +
                           $"{region.Width} pixels; vtable slot {Dxgi.SlotId3d11DeviceContextMap} is probably wrong.";
                }

                unsafe
                {
                    byte* source = (byte*)mapped.pData;
                    fixed (byte* dest = destination.Pixels)
                    {
                        for (int y = 0; y < region.Height; y++)
                        {
                            Buffer.MemoryCopy(source + ((long)y * mapped.RowPitch), dest + ((long)y * destination.Stride),
                                destination.Stride, destination.Stride);
                        }
                    }
                }
            }
            finally
            {
                ID3D11DeviceContextMethods.Unmap(_context, _stagingTexture, 0);
            }

            CaptureSupport.ForceOpaque(destination);
            return null;
        }
        finally
        {
            ComPtr.Release(desktopTexture);
        }
    }

    private bool EnsureStaging(int width, int height, out string? error)
    {
        error = null;
        if (_stagingTexture != IntPtr.Zero && _stagingWidth == width && _stagingHeight == height)
        {
            return true;
        }

        ReleaseStaging();

        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Dxgi.DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
            Usage = Dxgi.D3D11_USAGE_STAGING,
            BindFlags = 0,
            CPUAccessFlags = Dxgi.D3D11_CPU_ACCESS_READ,
            MiscFlags = 0,
        };

        int hr = ID3D11DeviceMethods.CreateTexture2D(_device, desc, IntPtr.Zero, out _stagingTexture);
        if (Dxgi.Failed(hr))
        {
            error = $"CreateTexture2D for the {width}x{height} staging texture failed ({Dxgi.Hex(hr)}).";
            return false;
        }

        _stagingWidth = width;
        _stagingHeight = height;
        return true;
    }

    private void StoreCache(RECT region, ArgbBitmap source)
    {
        int length = source.Pixels.Length;
        if (_cachedPixels is null || _cachedPixels.Length != length)
        {
            _cachedPixels = new byte[length];
        }
        source.Pixels.AsSpan().CopyTo(_cachedPixels);
        _cachedRegion = region;
        _hasValidCache = true;
    }

    private bool TryReplayCache(RECT region, ArgbBitmap destination)
    {
        if (!_hasValidCache || _cachedPixels is null)
        {
            return false;
        }
        if (_cachedRegion.Left != region.Left || _cachedRegion.Top != region.Top ||
            _cachedRegion.Width != region.Width || _cachedRegion.Height != region.Height)
        {
            // A different rectangle was asked for and no fresh frame arrived, so there is
            // nothing honest to return.
            return false;
        }
        if (_cachedPixels.Length != destination.Pixels.Length)
        {
            return false;
        }

        _cachedPixels.AsSpan().CopyTo(destination.Pixels);
        return true;
    }

    private void InvalidateCache()
    {
        _hasValidCache = false;
        _cachedPixels = null;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            ReleaseResources();
        }
    }

    // -----------------------------------------------------------------------------
    // Diagnostic hook
    // -----------------------------------------------------------------------------

    /// <summary>
    /// Walks the pixel-readback path one native call at a time and reports each result.
    /// </summary>
    /// <remarks>
    /// Exists because the failure modes of hand-written vtable calls are not error codes:
    /// a wrong slot usually returns success and leaves the output parameter untouched. Being
    /// able to see "CreateTexture2D returned 0x00000000 and a null pointer" is the difference
    /// between a five-minute fix and an afternoon of guessing. Used by <c>rattrap probe</c>.
    /// </remarks>
    public IReadOnlyList<string> DiagnoseReadback(RECT region, int timeoutMs)
    {
        lock (_sync)
        {
            var lines = new List<string>();
            void Report(string label, int hr, string extra = "")
                => lines.Add($"  {label,-46} hr={Dxgi.Hex(hr)} {extra}".TrimEnd());

            lines.Add($"  device=0x{_device:X} context=0x{_context:X} adapter=0x{_adapter:X} " +
                      $"output1=0x{_output1:X} duplication=0x{_duplication:X}");

            int hr = IDXGIOutputDuplicationMethods.AcquireNextFrame(_duplication, (uint)timeoutMs,
                out DXGI_OUTDUPL_FRAME_INFO frameInfo, out IntPtr desktopResource);
            Report("AcquireNextFrame", hr, $"shapeBytes={frameInfo.PointerShapeBufferSize} resource=0x{desktopResource:X}");
            if (Dxgi.Failed(hr))
            {
                return lines;
            }

            try
            {
                hr = ComPtr.QueryInterface(desktopResource, Guids.IID_ID3D11Texture2D, out IntPtr desktopTexture);
                Report("QI(ID3D11Texture2D)", hr, $"texture=0x{desktopTexture:X}");
                if (Dxgi.Failed(hr))
                {
                    return lines;
                }

                try
                {
                    var desc = new D3D11_TEXTURE2D_DESC
                    {
                        Width = (uint)region.Width,
                        Height = (uint)region.Height,
                        MipLevels = 1,
                        ArraySize = 1,
                        Format = Dxgi.DXGI_FORMAT_B8G8R8A8_UNORM,
                        SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
                        Usage = Dxgi.D3D11_USAGE_STAGING,
                        BindFlags = 0,
                        CPUAccessFlags = Dxgi.D3D11_CPU_ACCESS_READ,
                        MiscFlags = 0,
                    };
                    lines.Add($"  staging desc: {desc.Width}x{desc.Height} fmt={desc.Format} " +
                              $"mips={desc.MipLevels} array={desc.ArraySize} usage={desc.Usage} " +
                              $"cpu={desc.CPUAccessFlags} bind={desc.BindFlags} misc={desc.MiscFlags}");

                    hr = ID3D11DeviceMethods.CreateTexture2D(_device, desc, IntPtr.Zero, out IntPtr staging);
                    Report("CreateTexture2D", hr, $"texture=0x{staging:X}");
                    if (Dxgi.Failed(hr) || staging == IntPtr.Zero)
                    {
                        lines.Add("  => CreateTexture2D did not produce a texture; the readback cannot proceed.");
                        return lines;
                    }

                    try
                    {
                        var box = new D3D11_BOX
                        {
                            left = (uint)(region.Left - _monitor.Rect.Left),
                            top = (uint)(region.Top - _monitor.Rect.Top),
                            front = 0,
                            right = (uint)(region.Right - _monitor.Rect.Left),
                            bottom = (uint)(region.Bottom - _monitor.Rect.Top),
                            back = 1,
                        };
                        lines.Add($"  CopySubresourceRegion box = ({box.left},{box.top})-({box.right},{box.bottom})");

                        ID3D11DeviceContextMethods.CopySubresourceRegion(_context, staging, 0, 0, 0, 0, desktopTexture, 0, box);
                        lines.Add("  CopySubresourceRegion returned (void call; no HRESULT to report)");

                        hr = ID3D11DeviceContextMethods.Map(_context, staging, 0, Dxgi.D3D11_MAP_READ, 0, out D3D11_MAPPED_SUBRESOURCE mapped);
                        Report("Map(staging, D3D11_MAP_READ)", hr,
                            $"pData=0x{mapped.pData:X} rowPitch={mapped.RowPitch} depthPitch={mapped.DepthPitch}");

                        if (Dxgi.Succeeded(hr) && mapped.pData != IntPtr.Zero && mapped.RowPitch > 0)
                        {
                            lines.Add($"  => readback works; expected minimum row pitch = {region.Width * 4}");
                        }
                        else
                        {
                            lines.Add("  => readback FAILED at Map.");
                        }

                        if (Dxgi.Succeeded(hr))
                        {
                            ID3D11DeviceContextMethods.Unmap(_context, staging, 0);
                        }
                    }
                    finally
                    {
                        ComPtr.Release(staging);
                    }
                }
                finally
                {
                    ComPtr.Release(desktopTexture);
                }
            }
            finally
            {
                IDXGIOutputDuplicationMethods.ReleaseFrame(_duplication);
            }

            return lines;
        }
    }
}

