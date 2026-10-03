using System.Runtime.InteropServices;
using System.Text;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace BendWin.Core;

public sealed class BendRenderer : IDisposable
{
    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }

    private readonly FrameStore _store;

    private ID3D11VertexShader? _vsMain;
    private ID3D11PixelShader?  _psGaussH;
    private ID3D11PixelShader?  _psGaussV;
    private ID3D11PixelShader?  _psBend;

    private ID3D11Buffer? _cbBend;
    private ID3D11Buffer? _cbBlur;

    // Index 0 = original; 1-5 = blur levels; 6 = horizontal-pass temp
    private readonly ID3D11Texture2D?[]          _blurTex  = new ID3D11Texture2D[7];
    private readonly ID3D11RenderTargetView?[]   _blurRTV  = new ID3D11RenderTargetView[7];
    private readonly ID3D11ShaderResourceView?[] _blurSRV  = new ID3D11ShaderResourceView[7];

    private ID3D11RenderTargetView?   _outputRTV;
    private ID3D11Texture2D?          _readbackTex;
    private ID3D11SamplerState?       _sampler;
    private ID3D11RasterizerState?    _rasterState;

    private int   _width, _height;
    private float _lastBlurParam = -1f;
    private long  _lastFrameTs;
    private bool  _disposed;

    private BendParamsCpu _params;
    private readonly object _paramLock = new();

    [StructLayout(LayoutKind.Sequential, Pack = 16)]
    private struct BendParamsCpu
    {
        public float Progress, Perspective, Blur, Shadow, Style;
        float _p0, _p1, _p2;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 16)]
    private struct BlurParamsCpu
    {
        public float TexelX, TexelY, Sigma;
        float _pad;
    }

    public BendRenderer(FrameStore store)
    {
        _store = store;
        // Use explicit out types so the compiler picks the right overload
        D3D11.D3D11CreateDevice(
            adapter: null,
            driverType: DriverType.Hardware,
            flags: DeviceCreationFlags.None,
            featureLevels: new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_1 },
            device: out ID3D11Device device,
            immediateContext: out ID3D11DeviceContext context);
        Device  = device;
        Context = context;
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

    public byte[]? Draw()
    {
        var (srcTex, ts) = _store.Get(_lastFrameTs);
        if (srcTex == null) return null;
        _lastFrameTs = ts;

        BendParamsCpu p;
        lock (_paramLock) { p = _params; }
        if (p.Progress < 0.005f) return null;

        using var srcSRV = Device.CreateShaderResourceView(srcTex);

        bool needsBlur = Math.Abs(p.Blur - _lastBlurParam) > 0.01f;
        if (needsBlur || _blurSRV[0] == null)
        {
            _lastBlurParam = p.Blur;
            RunBlurPipeline(srcTex, p.Blur);
        }
        else
        {
            Context.CopyResource(_blurTex[0]!, srcTex);
        }

        UpdateBendCB(p);
        RunBendPass();
        return ReadbackPixels();
    }

    // ── Blur pipeline ─────────────────────────────────────────────────────────

    private void RunBlurPipeline(ID3D11Texture2D srcTex, float blurStrength)
    {
        Context.CopyResource(_blurTex[0]!, srcTex);

        float[] sigmas = [1f, 2f, 4f, 8f, 16f];
        for (int level = 0; level < 5; level++)
        {
            float sigma = sigmas[level] * (blurStrength * 1.5f + 0.5f);

            UpdateBlurCB(_width, _height, sigma, horizontal: true);
            SetupFullScreenPipeline(_psGaussH!, _blurRTV[6]!, _blurSRV[level]!, _width, _height);
            Context.Draw(3, 0);

            UpdateBlurCB(_width, _height, sigma, horizontal: false);
            SetupFullScreenPipeline(_psGaussV!, _blurRTV[level + 1]!, _blurSRV[6]!, _width, _height);
            Context.Draw(3, 0);
        }

        UnbindPipeline();
    }

    private void SetupFullScreenPipeline(ID3D11PixelShader ps, ID3D11RenderTargetView rtv,
        ID3D11ShaderResourceView srv, int w, int h)
    {
        Context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        Context.VSSetShader(_vsMain);
        Context.PSSetShader(ps);
        Context.PSSetShaderResources(0, new[] { srv });
        Context.PSSetSamplers(0, new[] { _sampler! });
        Context.PSSetConstantBuffers(0, new[] { _cbBlur! });
        Context.OMSetRenderTargets(rtv);
        Context.RSSetViewport(new Viewport(0, 0, w, h));
        Context.RSSetState(_rasterState);
    }

    private void RunBendPass()
    {
        Context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        Context.VSSetShader(_vsMain);
        Context.PSSetShader(_psBend);
        Context.PSSetShaderResources(0, new[]
        {
            _blurSRV[0]!, _blurSRV[1]!, _blurSRV[2]!,
            _blurSRV[3]!, _blurSRV[4]!, _blurSRV[5]!
        });
        Context.PSSetSamplers(0, new[] { _sampler! });
        Context.PSSetConstantBuffers(0, new[] { _cbBend! });
        Context.OMSetRenderTargets(_outputRTV!);
        Context.RSSetViewport(new Viewport(0, 0, _width, _height));
        Context.RSSetState(_rasterState);
        Context.Draw(3, 0);
        UnbindPipeline();
    }

    private void UnbindPipeline()
    {
        Context.PSSetShaderResources(0, new ID3D11ShaderResourceView?[6]);
        Context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
    }

    private unsafe byte[] ReadbackPixels()
    {
        Context.CopyResource(_readbackTex!, (ID3D11Resource)_outputRTV!.Resource!);
        var mapped = Context.Map(_readbackTex!, 0, MapMode.Read, D3D11MapFlags.None);
        int stride = _width * 4;
        var buf = new byte[_height * stride];
        for (int row = 0; row < _height; row++)
            Marshal.Copy(mapped.DataPointer + row * mapped.RowPitch, buf, row * stride, stride);
        Context.Unmap(_readbackTex!, 0);
        return buf;
    }

    // ── Setup helpers ─────────────────────────────────────────────────────────

    private void CompileShaders()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "Shaders");
        _vsMain   = CompileVS(Path.Combine(dir, "BendVertex.hlsl"));
        _psGaussH = CompilePS(Path.Combine(dir, "GaussianH.hlsl"));
        _psGaussV = CompilePS(Path.Combine(dir, "GaussianV.hlsl"));
        _psBend   = CompilePS(Path.Combine(dir, "BendPixel.hlsl"));
    }

    private ID3D11VertexShader CompileVS(string path)
    {
        Compiler.Compile(File.ReadAllText(path), null, null, "main",
            Path.GetFileName(path), "vs_5_0", 0, 0, out var blob, out var err);
        if (blob == null)
            throw new Exception($"VS compile '{path}': {BlobToString(err)}");
        var bytes = BlobToBytes(blob);
        blob.Dispose(); err?.Dispose();
        return Device.CreateVertexShader(bytes);
    }

    private ID3D11PixelShader CompilePS(string path)
    {
        Compiler.Compile(File.ReadAllText(path), null, null, "main",
            Path.GetFileName(path), "ps_5_0", 0, 0, out var blob, out var err);
        if (blob == null)
            throw new Exception($"PS compile '{path}': {BlobToString(err)}");
        var bytes = BlobToBytes(blob);
        blob.Dispose(); err?.Dispose();
        return Device.CreatePixelShader(bytes);
    }

    // Use dynamic so we don't need to name the exact Blob type (varies across Vortice versions).
    private static byte[] BlobToBytes(dynamic blob)
    {
        int size = (int)blob.BufferSize;
        var buf  = new byte[size];
        Marshal.Copy((IntPtr)blob.BufferPointer, buf, 0, size);
        return buf;
    }

    private static string BlobToString(dynamic? blob)
    {
        if (blob == null) return "(no error blob)";
        int size = (int)blob.BufferSize;
        var buf  = new byte[size];
        Marshal.Copy((IntPtr)blob.BufferPointer, buf, 0, size);
        return Encoding.UTF8.GetString(buf);
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
            _blurTex[i] = Device.CreateTexture2D(desc);
            _blurRTV[i] = Device.CreateRenderTargetView(_blurTex[i]!);
            _blurSRV[i] = Device.CreateShaderResourceView(_blurTex[i]!);
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

        _readbackTex = Device.CreateTexture2D(desc with
        {
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
        });
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
        var m = Context.Map(_cbBend!, 0, MapMode.WriteDiscard, D3D11MapFlags.None);
        Marshal.StructureToPtr(p, m.DataPointer, false);
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
        var m = Context.Map(_cbBlur!, 0, MapMode.WriteDiscard, D3D11MapFlags.None);
        Marshal.StructureToPtr(cp, m.DataPointer, false);
        Context.Unmap(_cbBlur!, 0);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        for (int i = 0; i < 7; i++) { _blurTex[i]?.Dispose(); _blurRTV[i]?.Dispose(); _blurSRV[i]?.Dispose(); }
        _outputRTV?.Dispose(); _readbackTex?.Dispose();
        _vsMain?.Dispose(); _psGaussH?.Dispose(); _psGaussV?.Dispose(); _psBend?.Dispose();
        _cbBend?.Dispose(); _cbBlur?.Dispose();
        _sampler?.Dispose(); _rasterState?.Dispose();
        Context.Dispose(); Device.Dispose();
    }
}
