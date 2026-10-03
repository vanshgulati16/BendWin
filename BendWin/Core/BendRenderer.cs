using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace BendWin.Core;

// Owns the D3D11 device and renders the bend effect to an offscreen staging texture
// that the overlay host reads back as BGRA bytes for display.
public sealed class BendRenderer : IDisposable
{
    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }

    private readonly FrameStore _store;

    // Pipelines
    private ID3D11VertexShader? _vsMain;
    private ID3D11PixelShader?  _psGaussH;
    private ID3D11PixelShader?  _psGaussV;
    private ID3D11PixelShader?  _psBend;

    // Constant buffers
    private ID3D11Buffer? _cbBend;
    private ID3D11Buffer? _cbBlur;

    // Blur intermediate textures (original + 5 levels)
    private ID3D11Texture2D?[]         _blurTextures  = new ID3D11Texture2D[7];
    private ID3D11RenderTargetView?[]  _blurRTVs      = new ID3D11RenderTargetView[7];
    private ID3D11ShaderResourceView?[] _blurSRVs     = new ID3D11ShaderResourceView[7];

    // Output staging texture (CPU-readable BGRA)
    private ID3D11Texture2D?          _stagingTex;
    private ID3D11RenderTargetView?   _outputRTV;
    private ID3D11ShaderResourceView? _outputSRV;
    private ID3D11Texture2D?          _readbackTex;

    private ID3D11SamplerState?  _sampler;
    private ID3D11RasterizerState? _rasterState;

    private int _width, _height;
    private float _lastBlurParam = -1f;
    private bool _disposed;
    private long _lastFrameTimestamp;

    // Current render parameters (thread-safe write via Interlocked on floats isn't possible,
    // so we use a simple struct copy under a lock).
    private BendParamsCpu _params;
    private readonly object _paramLock = new();

    [StructLayout(LayoutKind.Sequential, Pack = 16)]
    private struct BendParamsCpu
    {
        public float Progress;
        public float Perspective;
        public float Blur;
        public float Shadow;
        public float Style;
        float _p0, _p1, _p2;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 16)]
    private struct BlurParamsCpu
    {
        public float TexelX;
        public float TexelY;
        public float Sigma;
        float _pad;
    }

    public BendRenderer(FrameStore store)
    {
        _store = store;
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.None,
            [FeatureLevel.Level_11_0, FeatureLevel.Level_10_1],
            out var device, out var context);
        Device  = device!;
        Context = context!;
    }

    public void Initialize(int width, int height)
    {
        _width  = width;
        _height = height;

        CompileShaders();
        CreateConstantBuffers();
        CreateBlurTextures(width, height);
        CreateOutputTextures(width, height);
        CreateSampler();
        CreateRasterizerState();
    }

    public void SetParams(float progress, float perspective, float blur, float shadow, float style)
    {
        lock (_paramLock)
        {
            _params.Progress    = progress;
            _params.Perspective = perspective;
            _params.Blur        = blur;
            _params.Shadow      = shadow;
            _params.Style       = style;
        }
    }

    // Returns a byte[] (BGRA, width×height×4) for the overlay to display,
    // or null if no new frame is available.
    public byte[]? Draw()
    {
        var (srcTex, ts) = _store.Get(_lastFrameTimestamp);
        if (srcTex == null) return null;
        _lastFrameTimestamp = ts;

        BendParamsCpu p;
        lock (_paramLock) { p = _params; }

        if (p.Progress < 0.005f) return null;

        // Create SRV for the source frame
        using var srcSRV = Device.CreateShaderResourceView(srcTex);

        // Blur pass (only recompute if blur param changed significantly)
        bool needsBlur = Math.Abs(p.Blur - _lastBlurParam) > 0.01f;
        if (needsBlur || _blurSRVs[0] == null)
        {
            _lastBlurParam = p.Blur;
            RunBlurPipeline(srcSRV, p.Blur);
        }
        else
        {
            // Always refresh level 0 (original)
            Context.CopyResource(_blurTextures[0]!, srcTex);
        }

        // Bend pass
        UpdateBendCB(p);
        RunBendPass();

        return ReadbackPixels();
    }

    // ── Blur pipeline ─────────────────────────────────────────────────────────

    private void RunBlurPipeline(ID3D11ShaderResourceView srcSRV, float blurStrength)
    {
        // Level 0 = original
        Context.CopyResource(_blurTextures[0]!, (ID3D11Resource)srcSRV.Resource!);

        // Levels 1-5 with increasing sigma
        float[] sigmas = [1f, 2f, 4f, 8f, 16f];

        for (int level = 0; level < 5; level++)
        {
            float sigma = sigmas[level] * (blurStrength * 1.5f + 0.5f);
            int halfW = Math.Max(1, _width  >> (level / 2));
            int halfH = Math.Max(1, _height >> (level / 2));

            // --- Horizontal pass: levels[level] → levels[6] (temp) ---
            UpdateBlurCB(_width, _height, sigma, horizontal: true);
            SetFullScreenPipeline(_psGaussH!, _blurRTVs[6]!, _blurSRVs[level]!, _width, _height);
            Context.Draw(3, 0);

            // --- Vertical pass: levels[6] → levels[level+1] ---
            UpdateBlurCB(_width, _height, sigma, horizontal: false);
            SetFullScreenPipeline(_psGaussV!, _blurRTVs[level + 1]!, _blurSRVs[6]!, _width, _height);
            Context.Draw(3, 0);
        }

        Context.PixelShaderSetShaderResources(0, null, null, null, null, null, null);
        Context.OMSetRenderTargets(null, (ID3D11DepthStencilView?)null);
    }

    private void SetFullScreenPipeline(ID3D11PixelShader ps, ID3D11RenderTargetView rtv,
        ID3D11ShaderResourceView srv, int w, int h)
    {
        Context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        Context.VSSetShader(_vsMain);
        Context.PSSetShader(ps);
        Context.PSSetShaderResources(0, srv);
        Context.PSSetSamplers(0, _sampler);
        Context.PSSetConstantBuffers(0, _cbBlur);
        Context.OMSetRenderTargets(rtv);
        Context.RSSetViewport(new Viewport(0, 0, w, h));
        Context.RSSetState(_rasterState);
    }

    private void RunBendPass()
    {
        Context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        Context.VSSetShader(_vsMain);
        Context.PSSetShader(_psBend);
        Context.PSSetShaderResources(0,
            _blurSRVs[0], _blurSRVs[1], _blurSRVs[2],
            _blurSRVs[3], _blurSRVs[4], _blurSRVs[5]);
        Context.PSSetSamplers(0, _sampler);
        Context.PSSetConstantBuffers(0, _cbBend);
        Context.OMSetRenderTargets(_outputRTV);
        Context.RSSetViewport(new Viewport(0, 0, _width, _height));
        Context.RSSetState(_rasterState);
        Context.Draw(3, 0);
        Context.PixelShaderSetShaderResources(0, null, null, null, null, null, null);
        Context.OMSetRenderTargets(null, (ID3D11DepthStencilView?)null);
    }

    private byte[] ReadbackPixels()
    {
        if (_readbackTex == null) return [];
        Context.CopyResource(_readbackTex, (ID3D11Resource)_outputRTV!.Resource!);
        var mapped = Context.Map(_readbackTex, 0, MapMode.Read, MapFlags.None);
        int bytes = _height * _width * 4;
        var buf = new byte[bytes];
        unsafe
        {
            for (int row = 0; row < _height; row++)
                Marshal.Copy(mapped.DataPointer + row * mapped.RowPitch, buf, row * _width * 4, _width * 4);
        }
        Context.Unmap(_readbackTex, 0);
        return buf;
    }

    // ── Setup helpers ─────────────────────────────────────────────────────────

    private void CompileShaders()
    {
        string shaderDir = Path.Combine(AppContext.BaseDirectory, "Shaders");
        _vsMain  = CompileVS(Path.Combine(shaderDir, "BendVertex.hlsl"));
        _psGaussH = CompilePS(Path.Combine(shaderDir, "GaussianH.hlsl"));
        _psGaussV = CompilePS(Path.Combine(shaderDir, "GaussianV.hlsl"));
        _psBend  = CompilePS(Path.Combine(shaderDir, "BendPixel.hlsl"));
    }

    private ID3D11VertexShader CompileVS(string path)
    {
        var src = File.ReadAllText(path);
        Compiler.Compile(src, null, null, "main", Path.GetFileName(path), "vs_5_0", 0, 0,
            out var blob, out var err);
        if (blob == null) throw new Exception($"VS compile error: {err?.ConvertToString()}");
        return Device.CreateVertexShader(blob.GetBytes());
    }

    private ID3D11PixelShader CompilePS(string path)
    {
        var src = File.ReadAllText(path);
        Compiler.Compile(src, null, null, "main", Path.GetFileName(path), "ps_5_0", 0, 0,
            out var blob, out var err);
        if (blob == null) throw new Exception($"PS compile error: {err?.ConvertToString()}");
        return Device.CreatePixelShader(blob.GetBytes());
    }

    private void CreateConstantBuffers()
    {
        _cbBend = Device.CreateBuffer(new BufferDescription(
            32, BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
        _cbBlur = Device.CreateBuffer(new BufferDescription(
            16, BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
    }

    private void CreateBlurTextures(int w, int h)
    {
        var desc = new Texture2DDescription
        {
            Width = w, Height = h, MipLevels = 1, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
        };

        for (int i = 0; i < 7; i++)
        {
            _blurTextures[i] = Device.CreateTexture2D(desc);
            _blurRTVs[i]     = Device.CreateRenderTargetView(_blurTextures[i]!);
            _blurSRVs[i]     = Device.CreateShaderResourceView(_blurTextures[i]!);
        }
    }

    private void CreateOutputTextures(int w, int h)
    {
        var desc = new Texture2DDescription
        {
            Width = w, Height = h, MipLevels = 1, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        };
        var outTex = Device.CreateTexture2D(desc);
        _outputRTV = Device.CreateRenderTargetView(outTex);
        _outputSRV = Device.CreateShaderResourceView(outTex);

        // CPU-readable staging texture
        var staging = desc with
        {
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
        };
        _readbackTex = Device.CreateTexture2D(staging);
    }

    private void CreateSampler()
    {
        _sampler = Device.CreateSamplerState(new SamplerDescription
        {
            Filter         = Filter.MinMagMipLinear,
            AddressU       = TextureAddressMode.Clamp,
            AddressV       = TextureAddressMode.Clamp,
            AddressW       = TextureAddressMode.Clamp,
            ComparisonFunc = ComparisonFunction.Never,
            MaxLOD         = float.MaxValue,
        });
    }

    private void CreateRasterizerState()
    {
        _rasterState = Device.CreateRasterizerState(new RasterizerDescription
        {
            FillMode = FillMode.Solid,
            CullMode = CullMode.None,
        });
    }

    private void UpdateBendCB(BendParamsCpu p)
    {
        var mapped = Context.Map(_cbBend!, 0, MapMode.WriteDiscard, MapFlags.None);
        unsafe { Marshal.StructureToPtr(p, mapped.DataPointer, false); }
        Context.Unmap(_cbBend!, 0);
    }

    private void UpdateBlurCB(int w, int h, float sigma, bool horizontal)
    {
        var cp = new BlurParamsCpu
        {
            TexelX = horizontal ? 1f / w : 0f,
            TexelY = horizontal ? 0f : 1f / h,
            Sigma  = sigma,
        };
        var mapped = Context.Map(_cbBlur!, 0, MapMode.WriteDiscard, MapFlags.None);
        unsafe { Marshal.StructureToPtr(cp, mapped.DataPointer, false); }
        Context.Unmap(_cbBlur!, 0);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var t in _blurTextures) t?.Dispose();
        foreach (var v in _blurRTVs)    v?.Dispose();
        foreach (var v in _blurSRVs)    v?.Dispose();
        _outputRTV?.Dispose(); _outputSRV?.Dispose(); _readbackTex?.Dispose();
        _vsMain?.Dispose(); _psGaussH?.Dispose(); _psGaussV?.Dispose(); _psBend?.Dispose();
        _cbBend?.Dispose(); _cbBlur?.Dispose();
        _sampler?.Dispose(); _rasterState?.Dispose();
        Context.Dispose(); Device.Dispose();
    }
}
